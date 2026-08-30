using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Nodes;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;
using Steamworks;

namespace STS2SkinChanger.Core;

internal sealed record OnlineSkinSource(
    string ProviderId,
    ulong WorkshopItemId,
    string SafeResourceFingerprint,
    string SafeResourceManifest,
    string SafeResourceBindings);

internal enum OnlineSkinDescriptionState
{
    Unavailable,
    Preparing,
    Ready,
    Failed
}

[HarmonyPatch(typeof(ModManager), "OnSteamWorkshopItemInstalled")]
internal static class OnlineSkinWorkshopInstallPatch
{
    private static bool Prefix(ItemInstalled_t ev)
    {
        var workshopItemId = ev.m_nPublishedFileId.m_PublishedFileId;
        if (!OnlineSkinCache.ShouldSuppressRuntimeWorkshopInstall(workshopItemId))
        {
            return true;
        }

        ModLog.Info(
            $"已拦截联机临时皮肤 {workshopItemId} 的运行时 Mod 安装通知；" +
            "只会读取安全资源子包，不要求重启游戏。 ");
        return false;
    }
}

internal sealed record OnlineSkinCacheFailure(
    string Key,
    string ProviderId,
    string Detail);

internal enum OnlineSkinCacheStage
{
    Idle,
    Preparing,
    WaitingForReady,
    CheckingWorkshop,
    Downloading,
    Verifying,
    Applying,
    Complete,
    Failed
}

internal readonly record struct OnlineSkinCacheProgress(
    OnlineSkinCacheStage Stage,
    string ProviderId,
    ulong WorkshopItemId,
    ulong DownloadedBytes,
    ulong TotalBytes,
    string Detail)
{
    internal bool IsVisible => Stage != OnlineSkinCacheStage.Idle;
}

internal static partial class OnlineSkinCache
{
    private const ulong GameAppId = 2868840;
    private const int MaxArchiveEntries = 100_000;
    private const int MaxSafeFiles = 2_048;
    private const ulong MaxSafeFileSize = 64UL * 1024 * 1024;
    private const ulong MaxSafePackageSize = 128UL * 1024 * 1024;
    private const int MaxTextResourceSize = 2 * 1024 * 1024;

    private static readonly object Sync = new();
    private static readonly Queue<SkinChangerNetMessage> Pending = new();
    private static readonly HashSet<string> PendingKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DeclinedKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, OnlineSkinCacheFailure> BlockingFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AcknowledgedFailureKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CachedProvider> Providers =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LocalSourceCacheEntry> LocalSources =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LocalSourceBuilds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LocalSourceFailureCacheEntry> LocalSourceFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<(string GroupId, string OptionId)> LocalSourcesReady = new();
    private static readonly ConcurrentQueue<string> LocalSourceReports = new();
    private static readonly Dictionary<ulong, TaskCompletionSource<EResult>> DownloadWaiters = [];
    private static readonly HashSet<ulong> SessionWorkshopInstalls = [];
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".webp", ".jpg", ".jpeg", ".ctex",
        ".spatlas", ".spskel", ".atlas", ".skel",
        ".remap", ".import", ".tres", ".res", ".scn", ".tscn"
    };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".remap", ".import", ".tres", ".atlas", ".spatlas", ".tscn"
    };
    private static readonly HashSet<string> BinaryResourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".res", ".scn"
    };

    private static Callback<DownloadItemResult_t>? _downloadCallback;
    private static CancellationTokenSource? _sessionCancellation;
    private static string? _sessionDirectory;
    private static int _sessionGeneration;
    private static bool _processing;
    private static OnlineSkinCacheStage _progressStage;
    private static string _progressProviderId = string.Empty;
    private static ulong _progressWorkshopItemId;
    private static string _progressDetail = string.Empty;
    private static DateTime _progressExpiresAtUtc;

    internal static void BeginSession()
    {
        EndSession();
        lock (Sync)
        {
            _sessionGeneration++;
            _sessionCancellation = new CancellationTokenSource();
            _sessionDirectory = Path.Combine(
                Path.GetTempPath(),
                "Gurio.SkinChanger",
                "online",
                $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{_sessionGeneration:D3}");
            Directory.CreateDirectory(_sessionDirectory);
            try
            {
                EnsureDownloadCallback();
            }
            catch (Exception exception)
            {
                ModLog.Info(
                    "当前联机方式无法使用 Steam 工坊临时缓存；缺少的皮肤将显示原皮：" +
                    exception.GetBaseException().Message);
            }
        }
        CleanupOldSessionDirectories();
    }

    internal static void EndSession()
    {
        CachedProvider[] providers;
        string? directory;
        lock (Sync)
        {
            _sessionCancellation?.Cancel();
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
            foreach (var waiter in DownloadWaiters.Values)
            {
                waiter.TrySetCanceled();
            }
            DownloadWaiters.Clear();
            SessionWorkshopInstalls.Clear();
            Pending.Clear();
            PendingKeys.Clear();
            DeclinedKeys.Clear();
            BlockingFailures.Clear();
            AcknowledgedFailureKeys.Clear();
            providers = Providers.Values.ToArray();
            Providers.Clear();
            directory = _sessionDirectory;
            _sessionDirectory = null;
            _processing = false;
            ResetProgressLocked();
        }

        foreach (var provider in providers.Reverse())
        {
            try
            {
                SkinService.RemoveOnlineSessionProvider(provider.OptionId);
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"清理联机缓存皮肤 {provider.OptionId} 失败：" +
                    exception.GetBaseException().Message);
            }
        }

        TryDeleteDirectory(directory);
    }

    internal static void Tick(bool allowDownloads)
    {
        while (LocalSourcesReady.TryDequeue(out var ready))
        {
            MultiplayerSkinSync.OnLocalOnlineMetadataReady(ready.GroupId, ready.OptionId);
        }
        while (LocalSourceReports.TryDequeue(out var report))
        {
            ModLog.Info(report);
        }

        SkinChangerNetMessage request;
        int generation;
        CancellationToken cancellationToken;
        lock (Sync)
        {
            if (!allowDownloads || !SkinService.ShouldLoadOtherPlayersCustomSkins() ||
                _processing || Pending.Count == 0 || _sessionCancellation == null ||
                NRun.Instance?.CombatRoom != null)
            {
                return;
            }

            request = Pending.Dequeue();
            PendingKeys.Remove(RequestKey(request));
            _processing = true;
            generation = _sessionGeneration;
            cancellationToken = _sessionCancellation.Token;
        }

        TaskHelper.RunSafely(ProcessRequest(request, generation, cancellationToken));
    }

    internal static bool HasPendingWork()
    {
        var allowDownloads = SkinService.ShouldLoadOtherPlayersCustomSkins();
        lock (Sync)
        {
            return LocalSourceBuilds.Count > 0 ||
                   (allowDownloads && (_processing || Pending.Count > 0 || BlockingFailures.Count > 0));
        }
    }

    internal static OnlineSkinCacheProgress GetProgress()
    {
        OnlineSkinCacheStage stage;
        string providerId;
        ulong workshopItemId;
        string detail;
        lock (Sync)
        {
            if (_progressStage is OnlineSkinCacheStage.Complete or OnlineSkinCacheStage.Failed &&
                DateTime.UtcNow >= _progressExpiresAtUtc)
            {
                ResetProgressLocked();
            }

            stage = _progressStage;
            providerId = _progressProviderId;
            workshopItemId = _progressWorkshopItemId;
            detail = _progressDetail;
        }

        ulong downloadedBytes = 0;
        ulong totalBytes = 0;
        if (stage == OnlineSkinCacheStage.Downloading && workshopItemId != 0)
        {
            try
            {
                SteamUGC.GetItemDownloadInfo(
                    new PublishedFileId_t(workshopItemId),
                    out downloadedBytes,
                    out totalBytes);
            }
            catch
            {
                // Steam download progress is optional; the stage text remains useful.
            }
        }

        return new OnlineSkinCacheProgress(
            stage,
            providerId,
            workshopItemId,
            downloadedBytes,
            totalBytes,
            detail);
    }

    internal static void OnRemoteSkinLoadingPreferenceChanged(bool enabled)
    {
        if (enabled)
        {
            return;
        }

        lock (Sync)
        {
            Pending.Clear();
            PendingKeys.Clear();
            BlockingFailures.Clear();
            ResetProgressLocked();
        }
    }

    internal static void DiscardPendingSelectionsForPlayer(ulong playerNetId)
    {
        lock (Sync)
        {
            var retained = Pending
                .Where(message => message.PlayerNetId != playerNetId)
                .ToArray();
            Pending.Clear();
            PendingKeys.Clear();
            foreach (var message in retained)
            {
                Pending.Enqueue(message);
                PendingKeys.Add(RequestKey(message));
            }

            var failureKeys = BlockingFailures
                .Where(pair => pair.Key.StartsWith($"remote:{playerNetId}:", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var failureKey in failureKeys)
            {
                BlockingFailures.Remove(failureKey);
            }
        }
    }

    internal static bool QueueMissingSelection(SkinChangerNetMessage message)
    {
        if (!SkinService.ShouldLoadOtherPlayersCustomSkins() ||
            !HasDownloadMetadata(message) ||
            message.OptionId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var providerKey = ProviderKey(message);
        lock (Sync)
        {
            if (Providers.ContainsKey(providerKey))
            {
                return false;
            }

            var requestKey = RequestKey(message);
            if (_sessionCancellation == null || DeclinedKeys.Contains(providerKey) ||
                !PendingKeys.Add(requestKey))
            {
                return false;
            }
            Pending.Enqueue(message);
            SetProgressLocked(
                OnlineSkinCacheStage.WaitingForReady,
                message.ProviderId,
                message.WorkshopItemId);
        }

        ModLog.Info(
            $"检测到联机玩家使用本地缺少的皮肤 {message.ProviderId}，" +
            "已记录并等待所有玩家准备后自动安全缓存。");
        return true;
    }

    internal static bool HasDownloadMetadata(SkinChangerNetMessage message) =>
        message.WorkshopItemId != 0 &&
        !string.IsNullOrWhiteSpace(message.ProviderId) &&
        !string.IsNullOrWhiteSpace(message.SafeResourceFingerprint) &&
        FingerprintRegex().IsMatch(message.SafeResourceFingerprint) &&
        IsValidSafeResourceManifest(message.SafeResourceManifest) &&
        TryParseSafeResourceBindings(
            message.SafeResourceBindings,
            message.GroupId,
            out _);

    internal static bool IsWaitingForDownloadMetadata(SkinChangerNetMessage message) =>
        message.WorkshopItemId != 0 &&
        !string.IsNullOrWhiteSpace(message.ProviderId) &&
        string.IsNullOrWhiteSpace(message.SafeResourceFingerprint) &&
        string.IsNullOrWhiteSpace(message.OnlineFailure);

    internal static void ReportMissingMetadata(SkinChangerNetMessage message)
    {
        var providerId = string.IsNullOrWhiteSpace(message.ProviderId)
            ? message.OptionId
            : message.ProviderId;
        var detail = string.IsNullOrWhiteSpace(message.OnlineFailure)
            ? "对方没有提供可下载的工坊资源信息。"
            : message.OnlineFailure;
        AddBlockingFailure(
            $"remote:{message.PlayerNetId}:{message.GroupId}:{message.OptionId}",
            providerId,
            detail);
        SetProgress(
            OnlineSkinCacheStage.Failed,
            providerId,
            message.WorkshopItemId,
            detail,
            TimeSpan.FromDays(1));
    }

    internal static void ReportWaitingForMetadata(SkinChangerNetMessage message)
    {
        SetProgress(
            OnlineSkinCacheStage.Preparing,
            message.ProviderId,
            message.WorkshopItemId);
    }

    internal static void ReportLocalDescriptionFailure(
        string groupId,
        string optionId,
        string providerId,
        ulong workshopItemId,
        string detail)
    {
        if (!MultiplayerSkinSync.IsReadyGateActiveForOnlineCache())
        {
            return;
        }

        // The selected skin is already available to its owner. A local packaging failure only
        // means peers that do not own it must fall back to the base skin, so do not show the
        // owner the receiver-facing "could not load" blocking dialog. The failure still travels
        // with the advertisement and is surfaced on machines that actually lack the skin.
        SetProgress(
            OnlineSkinCacheStage.Failed,
            providerId,
            workshopItemId,
            detail,
            TimeSpan.FromDays(1));
    }

    internal static bool TryPeekBlockingFailure(out OnlineSkinCacheFailure failure)
    {
        lock (Sync)
        {
            failure = BlockingFailures.Values.FirstOrDefault()!;
            return failure != null;
        }
    }

    internal static void AcknowledgeBlockingFailure(string key)
    {
        lock (Sync)
        {
            BlockingFailures.Remove(key);
            AcknowledgedFailureKeys.Add(key);
            if (BlockingFailures.Count == 0 && _progressStage == OnlineSkinCacheStage.Failed)
            {
                ResetProgressLocked();
            }
        }
    }

    internal static void ClearBlockingFailures()
    {
        lock (Sync)
        {
            BlockingFailures.Clear();
            if (_progressStage == OnlineSkinCacheStage.Failed)
            {
                ResetProgressLocked();
            }
        }
    }

    private static void AddBlockingFailure(string key, string providerId, string detail)
    {
        lock (Sync)
        {
            if (AcknowledgedFailureKeys.Contains(key))
            {
                return;
            }
            BlockingFailures[key] = new OnlineSkinCacheFailure(key, providerId, detail);
        }
    }

    internal static bool TryGetCachedOption(
        SkinChangerNetMessage message,
        out string optionId)
    {
        lock (Sync)
        {
            if (Providers.TryGetValue(ProviderKey(message), out var provider))
            {
                optionId = provider.OptionId;
                return true;
            }
        }

        optionId = string.Empty;
        return false;
    }

    internal static OnlineSkinDescriptionState TryDescribeLocalSelection(
        string groupId,
        string optionId,
        out OnlineSkinSource source,
        out string failureDetail)
    {
        source = null!;
        failureDetail = string.Empty;
        var catalog = SkinService.Catalog;
        if (catalog == null)
        {
            failureDetail = "皮肤目录尚未初始化。";
            return OnlineSkinDescriptionState.Unavailable;
        }
        if (!catalog.TryGetVisualProviderSource(
                groupId,
                optionId,
                out var providerId,
                out var pckPath,
                out var safeResourceRoots,
                out var resourceBindings))
        {
            catalog.TryGetVisualProviderId(groupId, optionId, out providerId);
            providerId = string.IsNullOrWhiteSpace(providerId) ? optionId : providerId;
            failureDetail = "找不到该皮肤实际所属的 PCK 资源包。";
            source = new OnlineSkinSource(providerId, 0, string.Empty, string.Empty, string.Empty);
            return OnlineSkinDescriptionState.Unavailable;
        }
        if (!TryGetWorkshopItemId(pckPath, out var workshopItemId))
        {
            failureDetail = "该皮肤不是从 Steam 创意工坊目录加载的，无法自动下载。";
            source = new OnlineSkinSource(providerId, 0, string.Empty, string.Empty, string.Empty);
            return OnlineSkinDescriptionState.Unavailable;
        }

        var manifestRoots = FilterLocalSafeResourceRoots(
            safeResourceRoots,
            out var ignoredRootCount);
        if (ignoredRootCount > 0)
        {
            ModLog.Info(
                $"{providerId} 的联机资源清单已忽略 {ignoredRootCount} 个非静态/不可共享附带文件；" +
                "这些文件不会影响皮肤拥有者本机使用。 ");
        }

        var safeResourceManifest = string.Join('\n', manifestRoots);
        var filteredBindings = FilterLocalSafeResourceBindings(
            resourceBindings,
            manifestRoots,
            groupId);
        var safeResourceBindings = SerializeSafeResourceBindings(filteredBindings);
        if (!IsValidSafeResourceManifest(safeResourceManifest) ||
            !TryParseSafeResourceBindings(
                safeResourceBindings,
                groupId,
                out _))
        {
            failureDetail = "该皮肤的安全资源清单过大或格式无效。";
            source = new OnlineSkinSource(
                providerId,
                workshopItemId,
                string.Empty,
                string.Empty,
                string.Empty);
            return OnlineSkinDescriptionState.Failed;
        }

        var info = new FileInfo(pckPath);
        var cacheKey = pckPath + "\n" + groupId + "\n" + optionId;
        lock (Sync)
        {
            if (LocalSources.TryGetValue(cacheKey, out var cached) &&
                cached.Length == info.Length && cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                source = cached.Source;
                return OnlineSkinDescriptionState.Ready;
            }
            if (LocalSourceFailures.TryGetValue(cacheKey, out var failed) &&
                failed.Length == info.Length && failed.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                failureDetail = failed.Detail;
                source = new OnlineSkinSource(
                    providerId,
                    workshopItemId,
                    string.Empty,
                    safeResourceManifest,
                    safeResourceBindings);
                return OnlineSkinDescriptionState.Failed;
            }

            if (!LocalSourceBuilds.Add(cacheKey))
            {
                source = new OnlineSkinSource(
                    providerId,
                    workshopItemId,
                    string.Empty,
                    safeResourceManifest,
                    safeResourceBindings);
                return OnlineSkinDescriptionState.Preparing;
            }
            SetLocalPreparationProgressLocked(
                OnlineSkinCacheStage.Preparing,
                providerId,
                workshopItemId);
        }

        _ = Task.Run(() =>
        {
            try
            {
                var package = BuildSafePackage(
                    pckPath,
                    groupId,
                    manifestRoots,
                    filteredBindings,
                    outputPath: null);
                var discovered = new OnlineSkinSource(
                    providerId,
                    workshopItemId,
                    package.Fingerprint,
                    safeResourceManifest,
                    safeResourceBindings);
                lock (Sync)
                {
                    LocalSources[cacheKey] = new LocalSourceCacheEntry(
                        info.Length,
                        info.LastWriteTimeUtc,
                        discovered);
                }
                LocalSourcesReady.Enqueue((groupId, optionId));
                LocalSourceReports.Enqueue(
                    $"{providerId} 已准备联机安全资源指纹；缺少该皮肤的玩家可选择临时缓存静态素材。");
                SetLocalPreparationProgress(
                    OnlineSkinCacheStage.Complete,
                    providerId,
                    workshopItemId,
                    string.Empty,
                    TimeSpan.FromSeconds(2.5));
            }
            catch (Exception exception)
            {
                var detail = exception.GetBaseException().Message;
                lock (Sync)
                {
                    LocalSourceFailures[cacheKey] = new LocalSourceFailureCacheEntry(
                        info.Length,
                        info.LastWriteTimeUtc,
                        detail);
                }
                LocalSourcesReady.Enqueue((groupId, optionId));
                LocalSourceReports.Enqueue(
                    $"{providerId} 不支持安全联机缓存，将让缺少该皮肤的玩家显示原皮：" +
                    detail);
                SetLocalPreparationProgress(
                    OnlineSkinCacheStage.Failed,
                    providerId,
                    workshopItemId,
                    detail,
                    TimeSpan.FromDays(1));
            }
            finally
            {
                lock (Sync)
                {
                    LocalSourceBuilds.Remove(cacheKey);
                }
            }
        });
        source = new OnlineSkinSource(
            providerId,
            workshopItemId,
            string.Empty,
            safeResourceManifest,
            safeResourceBindings);
        return OnlineSkinDescriptionState.Preparing;
    }

    private static async Task ProcessRequest(
        SkinChangerNetMessage request,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!SkinService.ShouldLoadOtherPlayersCustomSkins())
            {
                return;
            }

            CachedProvider? alreadyCached;
            lock (Sync)
            {
                Providers.TryGetValue(ProviderKey(request), out alreadyCached);
            }
            if (alreadyCached != null)
            {
                MultiplayerSkinSync.RetryCachedSelection(request, alreadyCached.OptionId);
                return;
            }

            SetProgress(
                OnlineSkinCacheStage.CheckingWorkshop,
                request.ProviderId,
                request.WorkshopItemId);
            var details = await QueryWorkshopItem(request.WorkshopItemId, cancellationToken);
            if (!IsCurrentSession(generation, cancellationToken))
            {
                return;
            }

            if (!SkinService.ShouldLoadOtherPlayersCustomSkins())
            {
                return;
            }

            if (NRun.Instance?.CombatRoom != null)
            {
                Requeue(request);
                return;
            }

            SetProgress(
                OnlineSkinCacheStage.Downloading,
                request.ProviderId,
                request.WorkshopItemId);
            var workshopDirectory = await EnsureWorkshopItemAvailable(
                request.WorkshopItemId,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!SkinService.ShouldLoadOtherPlayersCustomSkins())
            {
                return;
            }
            var sourcePck = FindProviderPck(workshopDirectory, request.ProviderId);
            var outputDirectory = Path.Combine(
                _sessionDirectory ?? throw new OperationCanceledException(),
                request.WorkshopItemId.ToString(),
                BuildSessionOptionId(request));
            Directory.CreateDirectory(outputDirectory);
            var outputPck = Path.Combine(outputDirectory, "safe-resources.pck");
            if (!TryParseSafeResourceBindings(
                    request.SafeResourceBindings,
                    request.GroupId,
                    out var resourceBindings))
            {
                throw new InvalidDataException("对方提供的角色资源映射格式无效。 ");
            }
            SetProgress(
                OnlineSkinCacheStage.Verifying,
                request.ProviderId,
                request.WorkshopItemId);
            var package = await Task.Run(
                () => BuildSafePackage(
                    sourcePck,
                    request.GroupId,
                    ParseSafeResourceManifest(request.SafeResourceManifest),
                    resourceBindings,
                    outputPck),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentSession(generation, cancellationToken))
            {
                return;
            }
            if (!SkinService.ShouldLoadOtherPlayersCustomSkins())
            {
                return;
            }
            if (!package.Fingerprint.Equals(
                    request.SafeResourceFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "发送方与 Steam 当前资源的安全指纹不同，可能是版本不一致。");
            }

            var displayName = string.IsNullOrWhiteSpace(details.Title)
                ? request.ProviderId
                : details.Title;
            var sessionOptionId = BuildSessionOptionId(request);
            SetProgress(
                OnlineSkinCacheStage.Applying,
                displayName,
                request.WorkshopItemId);
            if (!SkinService.TryRegisterOnlineSessionProvider(
                    sessionOptionId,
                    displayName + OnlineCacheSuffix(),
                    outputPck,
                    request.GroupId,
                    resourceBindings,
                    out var error))
            {
                throw new InvalidDataException(error);
            }

            lock (Sync)
            {
                Providers[ProviderKey(request)] = new CachedProvider(
                    sessionOptionId,
                    request.SafeResourceFingerprint,
                    outputPck);
            }
            ModLog.Info(
                $"已为联机玩家缓存 {displayName} 的 {package.FileCount} 个安全资源，" +
                $"共 {package.TotalBytes / 1024d:F1} KiB；未加载 DLL、自定义脚本或 Shader。 ");
            MultiplayerSkinSync.RetryCachedSelection(request, sessionOptionId);
            SetProgress(
                OnlineSkinCacheStage.Complete,
                displayName,
                request.WorkshopItemId,
                string.Empty,
                TimeSpan.FromSeconds(3));
        }
        catch (OperationCanceledException)
        {
            // Leaving the room cancels pending queries/downloads without surfacing an error.
        }
        catch (Exception exception)
        {
            var detail = exception.GetBaseException().Message;
            lock (Sync)
            {
                if (generation == _sessionGeneration)
                {
                    DeclinedKeys.Add(ProviderKey(request));
                }
            }
            ModLog.Warn(
                $"无法在线缓存 {request.ProviderId}，远程玩家继续显示原皮：" +
                detail);
            AddBlockingFailure(
                $"remote:{request.PlayerNetId}:{request.GroupId}:{request.OptionId}",
                request.ProviderId,
                detail);
            SetProgress(
                OnlineSkinCacheStage.Failed,
                request.ProviderId,
                request.WorkshopItemId,
                detail,
                TimeSpan.FromDays(1));
        }
        finally
        {
            lock (Sync)
            {
                if (generation == _sessionGeneration)
                {
                    _processing = false;
                }
            }
        }
    }

    private static void Requeue(SkinChangerNetMessage request)
    {
        lock (Sync)
        {
            var key = RequestKey(request);
            if (_sessionCancellation != null && PendingKeys.Add(key))
            {
                Pending.Enqueue(request);
            }
        }
    }

    private static bool IsCurrentSession(int generation, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && generation == _sessionGeneration;

    private static async Task<WorkshopDetails> QueryWorkshopItem(
        ulong workshopItemId,
        CancellationToken cancellationToken)
    {
        var itemId = new PublishedFileId_t(workshopItemId);
        var handle = SteamUGC.CreateQueryUGCDetailsRequest([itemId], 1);
        try
        {
            using var result = new SteamCallResult<SteamUGCQueryCompleted_t>(
                SteamUGC.SendQueryUGCRequest(handle),
                cancellationToken);
            var completed = await result.Task;
            if (completed.m_eResult != EResult.k_EResultOK ||
                !SteamUGC.GetQueryUGCResult(completed.m_handle, 0, out var details))
            {
                throw new IOException(
                    $"Steam 无法验证该工坊物品：{completed.m_eResult}。");
            }

            if ((ulong)details.m_nConsumerAppID.m_AppId != GameAppId)
            {
                throw new InvalidDataException("该工坊物品不属于杀戮尖塔 2。");
            }

            return new WorkshopDetails(details.m_rgchTitle, checked((ulong)details.m_nFileSize));
        }
        finally
        {
            SteamUGC.ReleaseQueryUGCRequest(handle);
        }
    }

    private static async Task<string> EnsureWorkshopItemAvailable(
        ulong workshopItemId,
        CancellationToken cancellationToken)
    {
        var itemId = new PublishedFileId_t(workshopItemId);
        var state = (EItemState)SteamUGC.GetItemState(itemId);
        if ((state & EItemState.k_EItemStateInstalled) != 0 &&
            (state & EItemState.k_EItemStateNeedsUpdate) == 0 &&
            TryGetInstallDirectory(itemId, out var installed))
        {
            return installed;
        }

        TaskCompletionSource<EResult> waiter;
        lock (Sync)
        {
            waiter = new TaskCompletionSource<EResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            DownloadWaiters[workshopItemId] = waiter;
            SessionWorkshopInstalls.Add(workshopItemId);
        }

        if (!SteamUGC.DownloadItem(itemId, true))
        {
            lock (Sync)
            {
                DownloadWaiters.Remove(workshopItemId);
            }
            throw new IOException("Steam 拒绝开始下载该工坊物品。");
        }

        var result = await waiter.Task.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
        if (result != EResult.k_EResultOK || !TryGetInstallDirectory(itemId, out installed))
        {
            throw new IOException($"Steam 下载失败：{result}。");
        }
        return installed;
    }

    internal static bool ShouldSuppressRuntimeWorkshopInstall(ulong workshopItemId)
    {
        lock (Sync)
        {
            return SessionWorkshopInstalls.Contains(workshopItemId);
        }
    }

    private static void EnsureDownloadCallback()
    {
        _downloadCallback ??= Callback<DownloadItemResult_t>.Create(OnDownloadCompleted);
    }

    private static void OnDownloadCompleted(DownloadItemResult_t result)
    {
        if ((ulong)result.m_unAppID.m_AppId != GameAppId)
        {
            return;
        }

        TaskCompletionSource<EResult>? waiter;
        lock (Sync)
        {
            DownloadWaiters.Remove(result.m_nPublishedFileId.m_PublishedFileId, out waiter);
        }
        waiter?.TrySetResult(result.m_eResult);
    }

    private static bool TryGetInstallDirectory(PublishedFileId_t itemId, out string directory)
    {
        directory = string.Empty;
        return SteamUGC.GetItemInstallInfo(
                   itemId,
                   out _,
                   out directory,
                   4096,
                   out _) &&
               Directory.Exists(directory);
    }

    private static SafePackage BuildSafePackage(
        string sourcePck,
        string groupId,
        IReadOnlyCollection<string> explicitRoots,
        IReadOnlyDictionary<string, VisualResourceBinding> resourceBindings,
        string? outputPath)
    {
        using var archive = PckArchive.Open(sourcePck, (uint)MaxArchiveEntries);
        if (archive.Paths.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException("资源包文件数量异常。 ");
        }

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        var preparedFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var boundSources = resourceBindings
            .SelectMany(binding => binding.Value.Files.Select(resourcePath => new
            {
                ResourcePath = NormalizeResourcePath(resourcePath),
                SourcePath = NormalizeResourcePath(binding.Key)
            }))
            .GroupBy(binding => binding.ResourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().SourcePath,
                StringComparer.OrdinalIgnoreCase);
        foreach (var path in archive.Paths.Where(path =>
                     AllowedExtensions.Contains(Path.GetExtension(path)) &&
                     SkinCatalog.IsSafeOnlineResourceRootForGroup(path, groupId)))
        {
            Enqueue(path);
        }
        foreach (var path in explicitRoots)
        {
            var normalized = NormalizeResourcePath(path);
            if (!archive.Contains(normalized))
            {
                throw new InvalidDataException($"安全资源清单引用了工坊包中不存在的文件：{normalized}。");
            }
            Enqueue(normalized);
        }

        while (queue.TryDequeue(out var path))
        {
            ValidateFile(path);
            var extension = Path.GetExtension(path);
            var bytes = archive.ReadFile(path);
            if (extension.Equals(".tscn", StringComparison.OrdinalIgnoreCase))
            {
                if (!boundSources.TryGetValue(path, out var sourcePath))
                {
                    throw new InvalidDataException($"在线场景缺少角色资源目标映射：{path}。");
                }
                bytes = SanitizeOnlineTextScene(sourcePath, path, bytes);
            }
            preparedFiles[path] = bytes;
            if (!TextExtensions.Contains(extension) &&
                !BinaryResourceExtensions.Contains(extension))
            {
                continue;
            }

            if (TextExtensions.Contains(extension) && bytes.Length > MaxTextResourceSize)
            {
                throw new InvalidDataException($"文本资源过大：{path}。");
            }
            // Godot binary resources still store resource paths as UTF-8 strings among binary
            // fields. UTF-8 decoding preserves non-ASCII asset names; invalid binary bytes are
            // replaced but do not affect the res:// dependency tokens we scan.
            var text = Encoding.UTF8.GetString(bytes);
            if (TextExtensions.Contains(extension) &&
                !extension.Equals(".tscn", StringComparison.OrdinalIgnoreCase) &&
                ContainsForbiddenContent(text))
            {
                throw new InvalidDataException($"资源含脚本、Shader 或可执行引用：{path}。");
            }
            if (BinaryResourceExtensions.Contains(extension) &&
                ForbiddenBinaryResourceRegex().IsMatch(text))
            {
                throw new InvalidDataException($"二进制场景内嵌了可执行脚本或扩展：{path}。");
            }

            foreach (Match match in ResourcePathRegex().Matches(text))
            {
                var dependency = NormalizeResourcePath(match.Value);
                if (SkinService.IsBaseGameResource(dependency) ||
                    SkinService.IsBaseGameResource(dependency + ".remap") ||
                    SkinService.IsBaseGameResource(dependency + ".import"))
                {
                    continue;
                }

                if (archive.Contains(dependency))
                {
                    Enqueue(dependency);
                    continue;
                }
                if (archive.Contains(dependency + ".remap"))
                {
                    Enqueue(dependency + ".remap");
                    continue;
                }
                if (archive.Contains(dependency + ".import"))
                {
                    Enqueue(dependency + ".import");
                    continue;
                }
                if (extension.Equals(".spatlas", StringComparison.OrdinalIgnoreCase) &&
                    dependency.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase))
                {
                    // Godot's exported SpineAtlasResource embeds atlas_data and retains the
                    // editor source_path only as metadata; the source .atlas is not exported.
                    continue;
                }

                throw new InvalidDataException($"资源依赖未包含在同一工坊包中：{dependency}。");
            }

            if (extension.Equals(".spatlas", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".atlas", StringComparison.OrdinalIgnoreCase))
            {
                var atlasText = extension.Equals(".spatlas", StringComparison.OrdinalIgnoreCase)
                    ? text.Replace("\\n", "\n", StringComparison.Ordinal)
                    : text;
                foreach (var page in AtlasPageRegex().Matches(atlasText).Cast<Match>()
                             .Select(match => match.Groups[1].Value))
                {
                    var directory = path[..(path.LastIndexOf('/') + 1)];
                    var candidate = NormalizeResourcePath(directory + page);
                    if (archive.Contains(candidate))
                    {
                        Enqueue(candidate);
                    }
                    else if (archive.Contains(candidate + ".remap"))
                    {
                        Enqueue(candidate + ".remap");
                    }
                    else if (archive.Contains(candidate + ".import"))
                    {
                        Enqueue(candidate + ".import");
                    }
                }
            }
        }

        if (selected.Count == 0 || selected.Count > MaxSafeFiles)
        {
            throw new InvalidDataException("没有可安全隔离的静态角色资源，或资源数量超限。 ");
        }

        ulong totalBytes = 0;
        var fingerprints = new List<string>(selected.Count);
        foreach (var path in selected.Order(StringComparer.OrdinalIgnoreCase))
        {
            var bytes = preparedFiles[path];
            var size = checked((ulong)bytes.LongLength);
            totalBytes = checked(totalBytes + size);
            if (size > MaxSafeFileSize || totalBytes > MaxSafePackageSize)
            {
                throw new InvalidDataException("安全资源缓存超过大小限制。 ");
            }
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            fingerprints.Add(path.ToLowerInvariant() + "\n" + hash);
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", fingerprints))));
        if (outputPath != null)
        {
            PckArchive.Write(outputPath, preparedFiles);
        }
        return new SafePackage(fingerprint, selected.Count, totalBytes);

        void Enqueue(string path)
        {
            var normalized = NormalizeResourcePath(path);
            if (selected.Add(normalized))
            {
                queue.Enqueue(normalized);
            }
        }

        void ValidateFile(string path)
        {
            var extension = Path.GetExtension(path);
            if (!AllowedExtensions.Contains(extension))
            {
                throw new InvalidDataException($"不允许在线加载此资源类型：{path}。");
            }
            if (!archive.Contains(path))
            {
                throw new InvalidDataException($"资源包缺少文件：{path}。");
            }
            if (archive.GetFileSize(path) > MaxSafeFileSize)
            {
                throw new InvalidDataException($"单个资源过大：{path}。");
            }
        }
    }

    private static bool IsValidSafeResourceManifest(string? manifest)
    {
        if (manifest == null || manifest.Length > 64 * 1024)
        {
            return false;
        }

        var paths = ParseSafeResourceManifest(manifest);
        return paths.Count <= MaxSafeFiles && paths.All(path =>
            path.StartsWith("res://", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("..", StringComparison.Ordinal) &&
            path.Length <= 512 &&
            AllowedExtensions.Contains(Path.GetExtension(path)));
    }

    private static IReadOnlyList<string> FilterLocalSafeResourceRoots(
        IEnumerable<string> roots,
        out int ignoredCount)
    {
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ignoredCount = 0;
        foreach (var root in roots)
        {
            var normalized = NormalizeResourcePath(root);
            if (!normalized.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("..", StringComparison.Ordinal) ||
                normalized.Length > 512 ||
                !AllowedExtensions.Contains(Path.GetExtension(normalized)))
            {
                ignoredCount++;
                continue;
            }

            accepted.Add(normalized);
        }

        return accepted.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyDictionary<string, VisualResourceBinding> FilterLocalSafeResourceBindings(
        IReadOnlyDictionary<string, VisualResourceBinding> bindings,
        IReadOnlyCollection<string> acceptedRoots,
        string groupId)
    {
        var accepted = acceptedRoots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, VisualResourceBinding>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            var targetPath = NormalizeResourcePath(binding.Key);
            var sourcePath = NormalizeResourcePath(binding.Value.SourcePath);
            if (targetPath.Contains("..", StringComparison.Ordinal) ||
                targetPath.Length > 512 ||
                sourcePath.Contains("..", StringComparison.Ordinal) ||
                sourcePath.Length > 512 ||
                !sourcePath.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
                !SkinCatalog.IsSafeOnlineResourceRootForGroup(targetPath, groupId))
            {
                continue;
            }

            var resourcePaths = binding.Value.Files
                .Select(NormalizeResourcePath)
                .Where(path => accepted.Contains(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (resourcePaths.Length > 0)
            {
                result[targetPath] = new VisualResourceBinding(sourcePath, resourcePaths);
            }
        }
        return result;
    }

    private static string SerializeSafeResourceBindings(
        IReadOnlyDictionary<string, VisualResourceBinding> bindings) =>
        string.Join('\n', bindings
            .OrderBy(binding => binding.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(binding => binding.Value.Files.Select(resourcePath =>
                binding.Key + "\t" + binding.Value.SourcePath + "\t" + resourcePath)));

    private static bool TryParseSafeResourceBindings(
        string? manifest,
        string groupId,
        out IReadOnlyDictionary<string, VisualResourceBinding> bindings)
    {
        bindings = new Dictionary<string, VisualResourceBinding>(
            StringComparer.OrdinalIgnoreCase);
        if (manifest == null || manifest.Length > 64 * 1024)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(manifest))
        {
            return true;
        }

        var parsed = new Dictionary<string, (string SourcePath, HashSet<string> Files)>(
            StringComparer.OrdinalIgnoreCase);
        var lines = manifest.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > MaxSafeFiles)
        {
            return false;
        }
        foreach (var line in lines)
        {
            var fields = line.Split('\t');
            if (fields.Length != 3)
            {
                return false;
            }

            var targetPath = fields[0];
            var sourcePath = fields[1];
            var resourcePath = fields[2];
            if (!targetPath.Equals(
                    NormalizeResourcePath(targetPath),
                    StringComparison.Ordinal) ||
                !sourcePath.Equals(
                    NormalizeResourcePath(sourcePath),
                    StringComparison.Ordinal) ||
                !resourcePath.Equals(
                    NormalizeResourcePath(resourcePath),
                    StringComparison.Ordinal) ||
                targetPath.Contains("..", StringComparison.Ordinal) ||
                sourcePath.Contains("..", StringComparison.Ordinal) ||
                resourcePath.Contains("..", StringComparison.Ordinal) ||
                targetPath.Length > 512 ||
                sourcePath.Length > 512 ||
                resourcePath.Length > 512 ||
                !sourcePath.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
                !AllowedExtensions.Contains(Path.GetExtension(resourcePath)) ||
                !SkinCatalog.IsSafeOnlineResourceRootForGroup(targetPath, groupId))
            {
                return false;
            }

            if (!parsed.TryGetValue(targetPath, out var binding))
            {
                binding = (sourcePath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                parsed[targetPath] = binding;
            }
            else if (!binding.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            binding.Files.Add(resourcePath);
        }

        bindings = parsed.ToDictionary(
            pair => pair.Key,
            pair => new VisualResourceBinding(
                pair.Value.SourcePath,
                pair.Value.Files
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()),
            StringComparer.OrdinalIgnoreCase);
        return true;
    }

    private static byte[] SanitizeOnlineTextScene(
        string sourcePath,
        string providerPath,
        byte[] providerBytes)
    {
        if (!SkinService.TryReadBaseGameResource(sourcePath, out var baselineBytes))
        {
            throw new InvalidDataException(
                $"在线场景的角色目标不属于游戏原始资源：{sourcePath}。");
        }

        var providerText = Encoding.UTF8.GetString(providerBytes);
        var baselineText = Encoding.UTF8.GetString(baselineBytes);
        var baselineScripts = SceneExecutableResourceRegex().Matches(baselineText)
            .Cast<Match>()
            .Where(match => match.Groups[1].Value.Equals(
                "Script",
                StringComparison.OrdinalIgnoreCase))
            .Select(match => match.Groups[2].Value)
            .Where(SkinService.IsBaseGameResource)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var replacementIndex = 0;
        providerText = SceneExecutableResourceRegex().Replace(providerText, match =>
        {
            var type = match.Groups[1].Value;
            var path = match.Groups[2].Value;
            if (SkinService.IsBaseGameResource(path))
            {
                return match.Value;
            }
            if (!type.Equals("Script", StringComparison.OrdinalIgnoreCase) ||
                replacementIndex >= baselineScripts.Length)
            {
                throw new InvalidDataException(
                    $"在线场景含无法替换的自定义 {type}：{providerPath} -> {path}。");
            }

            var replacement = baselineScripts[replacementIndex++];
            return match.Value.Replace(path, replacement, StringComparison.Ordinal);
        });

        if (UnsafeTextSceneTokenRegex().IsMatch(providerText) ||
            SceneExecutableResourceRegex().Matches(providerText)
                .Cast<Match>()
                .Any(match => !SkinService.IsBaseGameResource(match.Groups[2].Value)))
        {
            throw new InvalidDataException(
                $"在线场景仍含不可安全加载的脚本、Shader 或扩展：{providerPath}。");
        }

        return Encoding.UTF8.GetBytes(providerText);
    }

    private static IReadOnlyList<string> ParseSafeResourceManifest(string? manifest) =>
        string.IsNullOrWhiteSpace(manifest)
            ? []
            : manifest
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static bool ContainsForbiddenContent(string text) =>
        ForbiddenTextRegex().IsMatch(text);

    private static string FindProviderPck(string workshopDirectory, string providerId)
    {
        var matches = Directory.EnumerateFiles(workshopDirectory, "*.pck", SearchOption.TopDirectoryOnly)
            .ToArray();
        var matchingIds = matches.Where(path => Path.GetFileNameWithoutExtension(path).Equals(
                providerId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var selected = matchingIds.Length == 1
            ? matchingIds[0]
            : matches.Length == 1
                ? matches[0]
                : null;
        if (selected == null)
        {
            throw new FileNotFoundException("下载内容中找不到唯一的皮肤 PCK。", providerId);
        }

        if ((File.GetAttributes(selected) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("在线皮肤 PCK 不能是符号链接或重解析点。 ");
        }
        return selected;
    }

    private static bool TryGetWorkshopItemId(string path, out ulong workshopItemId)
    {
        workshopItemId = 0;
        var normalized = path.Replace('\\', '/');
        var match = WorkshopPathRegex().Match(normalized);
        return match.Success && ulong.TryParse(match.Groups[1].Value, out workshopItemId);
    }

    private static string NormalizeResourcePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var relative = normalized.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
            ? normalized[6..]
            : normalized;
        while (relative.Contains("//", StringComparison.Ordinal))
        {
            relative = relative.Replace("//", "/", StringComparison.Ordinal);
        }
        return "res://" + relative.TrimStart('/');
    }

    private static string ProviderKey(SkinChangerNetMessage message) =>
        message.WorkshopItemId + "\n" + message.GroupId + "\n" + message.OptionId + "\n" +
        message.SafeResourceFingerprint;

    private static string RequestKey(SkinChangerNetMessage message) =>
        message.PlayerNetId + "\n" + ProviderKey(message);

    private static string BuildSessionOptionId(SkinChangerNetMessage message)
    {
        var identity = Encoding.UTF8.GetBytes(
            message.WorkshopItemId + "\n" + message.GroupId + "\n" +
            message.SafeResourceFingerprint);
        return "__online_" + Convert.ToHexString(SHA256.HashData(identity))[..24];
    }

    private static string OnlineCacheSuffix() => ModLocalization.CurrentLanguage switch
    {
        "zhs" => " · 联机缓存",
        "zht" => " · 連線快取",
        "jpn" => " · オンラインキャッシュ",
        "kor" => " · 온라인 캐시",
        _ => " · Online cache"
    };

    private static void SetProgress(
        OnlineSkinCacheStage stage,
        string providerId,
        ulong workshopItemId,
        string detail = "",
        TimeSpan? lifetime = null)
    {
        lock (Sync)
        {
            SetProgressLocked(stage, providerId, workshopItemId, detail, lifetime);
        }
    }

    private static void SetLocalPreparationProgress(
        OnlineSkinCacheStage stage,
        string providerId,
        ulong workshopItemId,
        string detail = "",
        TimeSpan? lifetime = null)
    {
        lock (Sync)
        {
            SetLocalPreparationProgressLocked(
                stage,
                providerId,
                workshopItemId,
                detail,
                lifetime);
        }
    }

    private static void SetLocalPreparationProgressLocked(
        OnlineSkinCacheStage stage,
        string providerId,
        ulong workshopItemId,
        string detail = "",
        TimeSpan? lifetime = null)
    {
        if (_progressStage is OnlineSkinCacheStage.WaitingForReady or
            OnlineSkinCacheStage.CheckingWorkshop or
            OnlineSkinCacheStage.Downloading or
            OnlineSkinCacheStage.Verifying or
            OnlineSkinCacheStage.Applying)
        {
            return;
        }

        SetProgressLocked(stage, providerId, workshopItemId, detail, lifetime);
    }

    private static void SetProgressLocked(
        OnlineSkinCacheStage stage,
        string providerId,
        ulong workshopItemId,
        string detail = "",
        TimeSpan? lifetime = null)
    {
        _progressStage = stage;
        _progressProviderId = providerId;
        _progressWorkshopItemId = workshopItemId;
        _progressDetail = detail;
        _progressExpiresAtUtc = lifetime.HasValue
            ? DateTime.UtcNow + lifetime.Value
            : DateTime.MaxValue;
    }

    private static void ResetProgressLocked()
    {
        _progressStage = OnlineSkinCacheStage.Idle;
        _progressProviderId = string.Empty;
        _progressWorkshopItemId = 0;
        _progressDetail = string.Empty;
        _progressExpiresAtUtc = DateTime.MinValue;
    }

    private static void CleanupOldSessionDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "Gurio.SkinChanger", "online");
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (var directory in Directory.EnumerateDirectories(root)
                     .Where(path => !path.Equals(_sessionDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            TryDeleteDirectory(directory);
        }
    }

    private static void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception)
        {
            ModLog.Info("联机皮肤缓存仍被游戏资源系统占用，将在下次启动时清理：" +
                        exception.GetBaseException().Message);
        }
    }

    [GeneratedRegex("^[0-9A-Fa-f]{64}$")]
    private static partial Regex FingerprintRegex();

    [GeneratedRegex("/workshop/content/2868840/([0-9]+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex WorkshopPathRegex();

    [GeneratedRegex("res://[^\\x00\\\"'\\r\\n\\t \\]\\[(){}<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex ResourcePathRegex();

    [GeneratedRegex("(?im)^([^\\r\\n]+\\.(?:png|webp|jpe?g))\\s*$")]
    private static partial Regex AtlasPageRegex();

    [GeneratedRegex(
        "(?i)(?:ext_resource[^\\r\\n]*(?:Script|Shader)|script\\s*=|shader\\s*=|ShaderMaterial|GDExtension|type\\s*=\\s*[\\\"'](?:Shader|GDScript|CSharpScript|GDExtension|PackedScene)[\\\"']|\\.(?:dll|cs|gd|gdc|gdshader)(?:\\\"|'|\\s|$))")]
    private static partial Regex ForbiddenTextRegex();

    [GeneratedRegex("(?i)(?:GDScript|CSharpScript|GDExtension|ExtensionLibrary|local://Shader_)")]
    private static partial Regex ForbiddenBinaryResourceRegex();

    [GeneratedRegex(
        "(?im)^\\[ext_resource[^\\]]*type=\\\"(Script|Shader)\\\"[^\\]]*path=\\\"(res://[^\\\"]+)\\\"[^\\]]*\\]$")]
    private static partial Regex SceneExecutableResourceRegex();

    [GeneratedRegex(
        "(?i)(?:GDExtension|ExtensionLibrary|type\\s*=\\s*\\\"(?:GDScript|CSharpScript|Shader)\\\"|shader/code\\s*=|(?m)^code\\s*=)")]
    private static partial Regex UnsafeTextSceneTokenRegex();

    private sealed record WorkshopDetails(string Title, ulong Size);
    private sealed record SafePackage(string Fingerprint, int FileCount, ulong TotalBytes);
    private sealed record CachedProvider(string OptionId, string Fingerprint, string PckPath);
    private sealed record LocalSourceCacheEntry(
        long Length,
        DateTime LastWriteTimeUtc,
        OnlineSkinSource Source);
    private sealed record LocalSourceFailureCacheEntry(
        long Length,
        DateTime LastWriteTimeUtc,
        string Detail);
}
