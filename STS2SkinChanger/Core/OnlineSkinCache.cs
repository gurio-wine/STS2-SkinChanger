using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;
using Steamworks;

namespace STS2SkinChanger.Core;

internal sealed record OnlineSkinSource(
    string ProviderId,
    ulong WorkshopItemId,
    string SafeResourceFingerprint);

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
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".webp", ".jpg", ".jpeg", ".ctex",
        ".spatlas", ".spskel", ".atlas", ".skel",
        ".remap", ".import", ".tres"
    };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".remap", ".import", ".tres", ".atlas"
    };

    private static Callback<DownloadItemResult_t>? _downloadCallback;
    private static CancellationTokenSource? _sessionCancellation;
    private static string? _sessionDirectory;
    private static int _sessionGeneration;
    private static bool _processing;

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
            Pending.Clear();
            PendingKeys.Clear();
            DeclinedKeys.Clear();
            providers = Providers.Values.ToArray();
            Providers.Clear();
            directory = _sessionDirectory;
            _sessionDirectory = null;
            _processing = false;
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

    internal static void Tick()
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
            if (_processing || Pending.Count == 0 || _sessionCancellation == null ||
                NRun.Instance?.CombatRoom != null ||
                NModalContainer.Instance is not { OpenModal: null })
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

    internal static void QueueMissingSelection(SkinChangerNetMessage message)
    {
        if (message.WorkshopItemId == 0 ||
            string.IsNullOrWhiteSpace(message.ProviderId) ||
            string.IsNullOrWhiteSpace(message.SafeResourceFingerprint) ||
            !FingerprintRegex().IsMatch(message.SafeResourceFingerprint) ||
            message.OptionId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var providerKey = ProviderKey(message);
        lock (Sync)
        {
            if (Providers.ContainsKey(providerKey))
            {
                return;
            }

            var requestKey = RequestKey(message);
            if (_sessionCancellation == null || DeclinedKeys.Contains(providerKey) ||
                !PendingKeys.Add(requestKey))
            {
                return;
            }
            Pending.Enqueue(message);
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

    internal static bool TryDescribeLocalSelection(
        string groupId,
        string optionId,
        out OnlineSkinSource source)
    {
        source = null!;
        var catalog = SkinService.Catalog;
        if (catalog == null ||
            !catalog.TryGetVisualProviderId(groupId, optionId, out var providerId) ||
            !optionId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var mod = ModManager.GetLoadedMods().FirstOrDefault(candidate =>
            candidate.manifest?.id?.Equals(providerId, StringComparison.OrdinalIgnoreCase) == true);
        if (mod?.manifest?.hasPck != true ||
            !TryGetWorkshopItemId(mod, out var workshopItemId))
        {
            return false;
        }

        var pckPath = Path.Combine(mod.path, providerId + ".pck");
        if (!File.Exists(pckPath))
        {
            return false;
        }

        var info = new FileInfo(pckPath);
        var cacheKey = pckPath + "\n" + groupId + "\n" + optionId;
        lock (Sync)
        {
            if (LocalSources.TryGetValue(cacheKey, out var cached) &&
                cached.Length == info.Length && cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                source = cached.Source;
                return true;
            }
            if (LocalSourceFailures.TryGetValue(cacheKey, out var failed) &&
                failed.Length == info.Length && failed.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                return false;
            }

            if (!LocalSourceBuilds.Add(cacheKey))
            {
                return false;
            }
        }

        _ = Task.Run(() =>
        {
            try
            {
                var package = BuildSafePackage(pckPath, groupId, outputPath: null);
                var discovered = new OnlineSkinSource(
                    providerId,
                    workshopItemId,
                    package.Fingerprint);
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
            }
            catch (Exception exception)
            {
                lock (Sync)
                {
                    LocalSourceFailures[cacheKey] = new LocalSourceFailureCacheEntry(
                        info.Length,
                        info.LastWriteTimeUtc);
                }
                LocalSourceReports.Enqueue(
                    $"{providerId} 不支持安全联机缓存，将让缺少该皮肤的玩家显示原皮：" +
                    exception.GetBaseException().Message);
            }
            finally
            {
                lock (Sync)
                {
                    LocalSourceBuilds.Remove(cacheKey);
                }
            }
        });
        return false;
    }

    private static async Task ProcessRequest(
        SkinChangerNetMessage request,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
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

            var details = await QueryWorkshopItem(request.WorkshopItemId, cancellationToken);
            if (!IsCurrentSession(generation, cancellationToken))
            {
                return;
            }

            if (NRun.Instance?.CombatRoom != null)
            {
                Requeue(request);
                return;
            }

            var accepted = await ShowPermissionPrompt(request, details);
            if (!accepted || !IsCurrentSession(generation, cancellationToken))
            {
                lock (Sync)
                {
                    DeclinedKeys.Add(ProviderKey(request));
                }
                return;
            }

            var workshopDirectory = await EnsureWorkshopItemAvailable(
                request.WorkshopItemId,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePck = FindProviderPck(workshopDirectory, request.ProviderId);
            var outputDirectory = Path.Combine(
                _sessionDirectory ?? throw new OperationCanceledException(),
                request.WorkshopItemId.ToString(),
                BuildSessionOptionId(request));
            Directory.CreateDirectory(outputDirectory);
            var outputPck = Path.Combine(outputDirectory, "safe-resources.pck");
            var package = await Task.Run(
                () => BuildSafePackage(sourcePck, request.GroupId, outputPck),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentSession(generation, cancellationToken))
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
            if (!SkinService.TryRegisterOnlineSessionProvider(
                    sessionOptionId,
                    displayName + OnlineCacheSuffix(),
                    outputPck,
                    request.GroupId,
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
                $"共 {package.TotalBytes / 1024d:F1} KiB；未加载 DLL、脚本、Shader 或自定义场景。 ");
            MultiplayerSkinSync.RetryCachedSelection(request, sessionOptionId);
        }
        catch (OperationCanceledException)
        {
            // Leaving the room cancels pending queries/downloads without surfacing an error.
        }
        catch (Exception exception)
        {
            lock (Sync)
            {
                if (generation == _sessionGeneration)
                {
                    DeclinedKeys.Add(ProviderKey(request));
                }
            }
            ModLog.Warn(
                $"无法在线缓存 {request.ProviderId}，远程玩家继续显示原皮：" +
                exception.GetBaseException().Message);
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

    private static async Task<bool> ShowPermissionPrompt(
        SkinChangerNetMessage request,
        WorkshopDetails details)
    {
        var container = NModalContainer.Instance;
        var popup = NGenericPopup.Create();
        if (container == null || popup == null || container.OpenModal != null)
        {
            Requeue(request);
            throw new OperationCanceledException("等待可用的游戏弹窗容器。 ");
        }

        container.Add(popup);
        var confirmation = popup.WaitForConfirmation(
            new MegaCrit.Sts2.Core.Localization.LocString(
                "main_menu_ui",
                "MOD_NOT_LOADED_POPUP.description"),
            new MegaCrit.Sts2.Core.Localization.LocString(
                "main_menu_ui",
                "MOD_NOT_LOADED_POPUP.title"),
            new MegaCrit.Sts2.Core.Localization.LocString(
                "main_menu_ui",
                "GENERIC_POPUP.cancel"),
            new MegaCrit.Sts2.Core.Localization.LocString(
                "main_menu_ui",
                "GENERIC_POPUP.confirm"));
        var vertical = popup.GetNodeOrNull<NVerticalPopup>("VerticalPopup");
        if (vertical == null)
        {
            popup.QueueFree();
            throw new InvalidOperationException("在线皮肤授权弹窗缺少 VerticalPopup 节点。");
        }

        var text = OnlinePromptTexts.Get();
        var title = string.IsNullOrWhiteSpace(details.Title)
            ? request.ProviderId
            : EscapeBbCode(details.Title);
        var size = FormatBytes(details.Size);
        vertical.SetText(
            text.Title,
            text.Body.Replace("{0}", title, StringComparison.Ordinal)
                .Replace("{1}", size, StringComparison.Ordinal));
        vertical.YesButton.SetText(text.AllowOnce);
        vertical.NoButton.SetText(text.Decline);
        return await confirmation;
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
        string? outputPath)
    {
        using var archive = PckArchive.Open(sourcePck, (uint)MaxArchiveEntries);
        if (archive.Paths.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException("资源包文件数量异常。 ");
        }

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (var path in archive.Paths.Where(path =>
                     AllowedExtensions.Contains(Path.GetExtension(path)) &&
                     SkinCatalog.IsSafeOnlineResourceRootForGroup(path, groupId)))
        {
            Enqueue(path);
        }

        while (queue.TryDequeue(out var path))
        {
            ValidateFile(path);
            if (!TextExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            var bytes = archive.ReadFile(path);
            if (bytes.Length > MaxTextResourceSize)
            {
                throw new InvalidDataException($"文本资源过大：{path}。");
            }
            var text = Encoding.UTF8.GetString(bytes);
            if (ContainsForbiddenContent(text))
            {
                throw new InvalidDataException($"资源含脚本、Shader 或可执行引用：{path}。");
            }

            foreach (Match match in ResourcePathRegex().Matches(text))
            {
                var dependency = NormalizeResourcePath(match.Value);
                if (SkinService.IsBaseGameResource(dependency))
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

                throw new InvalidDataException($"资源依赖未包含在同一工坊包中：{dependency}。");
            }

            if (Path.GetExtension(path).Equals(".spatlas", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".atlas", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var page in AtlasPageRegex().Matches(text).Cast<Match>()
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
            var size = archive.GetFileSize(path);
            totalBytes = checked(totalBytes + size);
            if (size > MaxSafeFileSize || totalBytes > MaxSafePackageSize)
            {
                throw new InvalidDataException("安全资源缓存超过大小限制。 ");
            }
            var hash = Convert.ToHexString(SHA256.HashData(archive.ReadFile(path)));
            fingerprints.Add(path.ToLowerInvariant() + "\n" + hash);
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", fingerprints))));
        if (outputPath != null)
        {
            var files = selected.ToDictionary(
                path => path,
                path => (archive, path),
                StringComparer.OrdinalIgnoreCase);
            PckArchive.WriteFromArchives(outputPath, files);
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

    private static bool TryGetWorkshopItemId(Mod mod, out ulong workshopItemId)
    {
        workshopItemId = 0;
        var field = mod.GetType().GetField("workshopId");
        if (field?.GetValue(mod) is ulong direct && direct != 0)
        {
            workshopItemId = direct;
            return true;
        }

        var normalized = mod.path.Replace('\\', '/');
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

    private static string EscapeBbCode(string value) => value
        .Replace('[', '(')
        .Replace(']', ')')
        .Replace('\r', ' ')
        .Replace('\n', ' ');

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1024UL * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d:F2} GiB",
        >= 1024UL * 1024 => $"{bytes / 1024d / 1024d:F1} MiB",
        >= 1024UL => $"{bytes / 1024d:F1} KiB",
        _ => $"{bytes} B"
    };

    private static string OnlineCacheSuffix() => ModLocalization.CurrentLanguage switch
    {
        "zhs" => " · 联机缓存",
        "zht" => " · 連線快取",
        "jpn" => " · オンラインキャッシュ",
        "kor" => " · 온라인 캐시",
        _ => " · Online cache"
    };

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

    private sealed record WorkshopDetails(string Title, ulong Size);
    private sealed record SafePackage(string Fingerprint, int FileCount, ulong TotalBytes);
    private sealed record CachedProvider(string OptionId, string Fingerprint, string PckPath);
    private sealed record LocalSourceCacheEntry(
        long Length,
        DateTime LastWriteTimeUtc,
        OnlineSkinSource Source);
    private sealed record LocalSourceFailureCacheEntry(long Length, DateTime LastWriteTimeUtc);
}

internal sealed record OnlinePromptLanguagePack(
    string Title,
    string Body,
    string AllowOnce,
    string Decline,
    string UnknownSize);

internal static class OnlinePromptTexts
{
    private static readonly OnlinePromptLanguagePack English = new(
        "Online skin cache",
        "Another player is using [b]{0}[/b] ({1}), which is not installed locally.\n\nSkin Changer can ask Steam to temporarily download it and will extract only verified static images, atlases, and skeleton data. DLLs, scripts, shaders, and custom scenes will never be loaded. The Workshop item may contain adult content; Steam account and region restrictions still apply.\n\nAllow this download for the current room?",
        "Allow this time",
        "Use original skin",
        "size unknown");

    private static readonly IReadOnlyDictionary<string, OnlinePromptLanguagePack> Packs =
        new Dictionary<string, OnlinePromptLanguagePack>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = English,
            ["zhs"] = new(
                "联机皮肤缓存",
                "另一名玩家正在使用你本地未安装的 [b]{0}[/b]（{1}）。\n\n皮肤切换器可以让 Steam 临时下载该工坊物品，但只会提取经过校验的静态贴图、图集和骨骼数据，绝不会加载 DLL、脚本、Shader 或自定义场景。工坊物品可能含成人内容，并仍受 Steam 账号与地区限制。\n\n是否仅在当前房间允许本次下载？",
                "本次允许",
                "使用原皮",
                "大小未知"),
            ["zht"] = new(
                "連線外觀快取",
                "另一名玩家正在使用本機未安裝的 [b]{0}[/b]（{1}）。\n\nSkin Changer 可讓 Steam 暫時下載，但只提取已驗證的靜態圖片、圖集與骨骼資料，絕不載入 DLL、腳本、Shader 或自訂場景。物品可能含成人內容，且仍受 Steam 帳號與地區限制。\n\n僅允許目前房間的這次下載？",
                "允許這次",
                "使用原版",
                "大小未知"),
            ["deu"] = new("Online-Skin-Cache", "Ein anderer Spieler nutzt [b]{0}[/b] ({1}), das lokal fehlt. Steam kann es temporär laden; Skin Changer übernimmt nur geprüfte Bilder, Atlanten und Skelettdaten, niemals DLLs, Skripte, Shader oder eigene Szenen. Der Inhalt kann nicht jugendfrei sein. Für diesen Raum erlauben?", "Diesmal erlauben", "Original verwenden", "Größe unbekannt"),
            ["esp"] = new("Caché de aspectos en línea", "Otro jugador usa [b]{0}[/b] ({1}) y no está instalado. Steam puede descargarlo temporalmente; Skin Changer solo extraerá imágenes, atlas y esqueletos verificados, nunca DLL, scripts, shaders ni escenas personalizadas. Puede incluir contenido adulto. ¿Permitirlo en esta sala?", "Permitir esta vez", "Usar original", "Tamaño desconocido"),
            ["fra"] = new("Cache de skins en ligne", "Un autre joueur utilise [b]{0}[/b] ({1}), absent localement. Steam peut le télécharger temporairement ; Skin Changer n’extrait que les images, atlas et squelettes vérifiés, jamais les DLL, scripts, shaders ou scènes personnalisées. Le contenu peut être adulte. Autoriser pour cette salle ?", "Autoriser cette fois", "Utiliser l’original", "Taille inconnue"),
            ["ita"] = new("Cache skin online", "Un altro giocatore usa [b]{0}[/b] ({1}), non installato localmente. Steam può scaricarlo temporaneamente; Skin Changer estrae solo immagini, atlanti e scheletri verificati, mai DLL, script, shader o scene personalizzate. Può contenere materiale per adulti. Consentire per questa stanza?", "Consenti questa volta", "Usa originale", "Dimensione sconosciuta"),
            ["jpn"] = new("オンラインスキンキャッシュ", "別のプレイヤーが未導入の [b]{0}[/b]（{1}）を使用しています。Steam から一時取得できますが、検証済みの画像・アトラス・スケルトンだけを抽出し、DLL・スクリプト・Shader・独自シーンは読み込みません。成人向け内容を含む場合があります。このルームで許可しますか？", "今回のみ許可", "原版を使用", "サイズ不明"),
            ["kor"] = new("온라인 스킨 캐시", "다른 플레이어가 로컬에 없는 [b]{0}[/b] ({1})을 사용 중입니다. Steam에서 임시 다운로드할 수 있으며 검증된 이미지, 아틀라스, 스켈레톤만 추출합니다. DLL, 스크립트, 셰이더, 사용자 장면은 로드하지 않습니다. 성인 콘텐츠가 포함될 수 있습니다. 이 방에서 허용할까요?", "이번만 허용", "원본 사용", "크기 알 수 없음"),
            ["pol"] = new("Pamięć skórek online", "Inny gracz używa [b]{0}[/b] ({1}), którego nie masz. Steam może pobrać go tymczasowo; Skin Changer wyodrębni tylko sprawdzone obrazy, atlasy i szkielety, nigdy DLL, skrypty, shadery ani własne sceny. Zawartość może być dla dorosłych. Zezwolić w tym pokoju?", "Zezwól tym razem", "Użyj oryginału", "Rozmiar nieznany"),
            ["ptb"] = new("Cache de visuais online", "Outro jogador usa [b]{0}[/b] ({1}), que não está instalado. A Steam pode baixá-lo temporariamente; o Skin Changer extrai apenas imagens, atlas e esqueletos verificados, nunca DLLs, scripts, shaders ou cenas personalizadas. Pode conter conteúdo adulto. Permitir nesta sala?", "Permitir desta vez", "Usar original", "Tamanho desconhecido"),
            ["rus"] = new("Онлайн-кэш обликов", "Другой игрок использует [b]{0}[/b] ({1}), которого нет локально. Steam может временно загрузить его; Skin Changer извлечёт только проверенные изображения, атласы и скелеты, но не DLL, скрипты, шейдеры или пользовательские сцены. Возможен контент для взрослых. Разрешить для этой комнаты?", "Разрешить один раз", "Использовать оригинал", "Размер неизвестен"),
            ["spa"] = new("Caché de aspectos en línea", "Otro jugador usa [b]{0}[/b] ({1}) y no está instalado. Steam puede descargarlo temporalmente; Skin Changer solo extraerá imágenes, atlas y esqueletos verificados, nunca DLL, scripts, shaders ni escenas personalizadas. Puede incluir contenido adulto. ¿Permitirlo en esta sala?", "Permitir esta vez", "Usar original", "Tamaño desconocido"),
            ["tha"] = new("แคชสกินออนไลน์", "ผู้เล่นอื่นใช้ [b]{0}[/b] ({1}) ที่เครื่องนี้ไม่ได้ติดตั้ง Steam สามารถดาวน์โหลดชั่วคราวได้ โดย Skin Changer จะนำมาเฉพาะรูปภาพ แอตลาส และข้อมูลโครงกระดูกที่ตรวจสอบแล้ว และจะไม่โหลด DLL สคริปต์ Shader หรือฉากกำหนดเอง อาจมีเนื้อหาสำหรับผู้ใหญ่ อนุญาตสำหรับห้องนี้หรือไม่", "อนุญาตครั้งนี้", "ใช้ต้นฉบับ", "ไม่ทราบขนาด"),
            ["tur"] = new("Çevrimiçi görünüm önbelleği", "Başka bir oyuncu yerelde kurulu olmayan [b]{0}[/b] ({1}) kullanıyor. Steam geçici olarak indirebilir; Skin Changer yalnızca doğrulanmış görselleri, atlasları ve iskelet verilerini çıkarır, DLL, betik, shader veya özel sahne yüklemez. Yetişkin içerik bulunabilir. Bu oda için izin verilsin mi?", "Bu kez izin ver", "Orijinali kullan", "Boyut bilinmiyor")
        };

    public static OnlinePromptLanguagePack Get() =>
        Packs.GetValueOrDefault(ModLocalization.CurrentLanguage) ?? English;
}
