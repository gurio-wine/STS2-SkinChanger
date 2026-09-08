using System.Text.Json;
using System.Net.Http;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Nodes;
using Steamworks;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal sealed class WorkshopDownload
{
    public WorkshopTextKey State = WorkshopTextKey.Waiting;
    public string Error = "";
    public bool Busy;
    public WorkshopLoadReason Reason;
    public bool BrowserSubscription;
    public bool HotLoadVerified;
    public bool Removing;
    public bool Unsubscribed;
}

// Permanent, explicitly requested subscriptions. Never calls OnlineSkinCache or removes Steam files.
internal static partial class SkinWorkshopService
{
    private static readonly Lazy<WorkshopCatalogItem[]> Items = new(() =>
    {
        using var stream = typeof(Entry).Assembly.GetManifestResourceStream("STS2SkinChanger.Data.workshop-catalog.json")!;
        using var reader = new StreamReader(stream);
        return WorkshopCatalogPolicy.Parse(reader.ReadToEnd());
    });
    public static IReadOnlyList<WorkshopCatalogItem> Catalog => Items.Value;
    private static readonly Dictionary<ulong, WorkshopDownload> Downloads = [];
    private static readonly Dictionary<ulong, TaskCompletionSource<EResult>> Waiters = [];
    private static readonly WorkshopSessionCache<Dictionary<ulong, WorkshopDetails>> SessionDetails = new();
    private static Callback<DownloadItemResult_t>? _downloadCallback;
    private static readonly System.Net.Http.HttpClient Images = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim ImageGate = new(3);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SkinCatalog, HashSet<ulong>> ActiveItems = new();
    private static string CacheRoot => System.IO.Path.Combine(OS.GetUserDataDir(), "skin_changer_workshop");
    public static WorkshopDetails? CachedDetails(ulong id) => SessionDetails.TryGet(WorkshopText.SteamLanguage, out var details) ? details.GetValueOrDefault(id) : null;
    private static readonly Dictionary<ulong, WorkshopDetails> EmptyDetails = [];
    public static IReadOnlyDictionary<ulong, WorkshopDetails> CachedMetadata =>
        SessionDetails.TryGet(WorkshopText.SteamLanguage, out var details) ? details : EmptyDetails;

    public static async Task<Dictionary<ulong, WorkshopDetails>> Query(IReadOnlyList<ulong> ids, CancellationToken token)
    {
        var requested = ids.Where(id => Catalog.Any(item => item.Id == id)).Distinct().Take(12).ToArray();
        if (requested.Length == 0) return [];
        var language = WorkshopText.SteamLanguage;
        var details = await SessionDetails.Get(language, () => RefreshDetails(language), token);
        return requested.Where(details.ContainsKey).ToDictionary(id => id, id => details[id]);
    }

    private static async Task<Dictionary<ulong, WorkshopDetails>> RefreshDetails(string language)
    {
        // Refresh the curated metadata once at the first open, not once per page.
        // UI cancellation must not dispose the Steam handle shared by later pages.
        var cache = System.IO.Path.Combine(CacheRoot, language);
        var result = new Dictionary<ulong, WorkshopDetails>();
        foreach (var id in Catalog.Select(item => item.Id))
        {
            try
            {
                var path = System.IO.Path.Combine(cache, id + ".json");
                if (File.Exists(path) && new FileInfo(path).Length < 32768)
                    if (JsonSerializer.Deserialize<WorkshopDetails>(await File.ReadAllTextAsync(path)) is { Title.Length: > 0 } cached) result[id] = cached;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        foreach (var requested in Catalog.Select(item => item.Id).Chunk(1000))
        {
            var handle = UGCQueryHandle_t.Invalid;
            try
            {
                handle = SteamUGC.CreateQueryUGCDetailsRequest(requested.Select(id => new PublishedFileId_t(id)).ToArray(), (uint)requested.Length);
                SteamUGC.SetLanguage(handle, language);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                using var call = new SteamCallResult<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(handle), timeout.Token);
                var completed = await call.Task;
                if (completed.m_eResult != EResult.k_EResultOK) continue;
                for (uint i = 0; i < completed.m_unNumResultsReturned; i++)
                {
                    if (!SteamUGC.GetQueryUGCResult(handle, i, out var item) || item.m_eResult != EResult.k_EResultOK ||
                        item.m_nConsumerAppID.m_AppId != WorkshopCatalogPolicy.AppId || !requested.Contains(item.m_nPublishedFileId.m_PublishedFileId)) continue;
                    SteamUGC.GetQueryUGCPreviewURL(handle, i, out var preview, 4096);
                    ulong? Statistic(EItemStatistic statistic) => SteamUGC.GetQueryUGCStatistic(handle, i, statistic, out var value) ? value : null;
                    var details = WorkshopMetadataReader.Read(item, preview ?? "", Statistic);
                    var id = item.m_nPublishedFileId.m_PublishedFileId;
                    result[id] = details;
                    try
                    {
                        Directory.CreateDirectory(cache);
                        await File.WriteAllTextAsync(System.IO.Path.Combine(cache, id + ".json"), JsonSerializer.Serialize(details));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex)
            { ModLog.Info("工坊详情暂不可用，保留缓存：" + ex.Message); }
            finally
            {
                if (handle != UGCQueryHandle_t.Invalid) SteamUGC.ReleaseQueryUGCRequest(handle);
            }
        }
        ModLog.Info($"工坊资料本次启动刷新结束：{language}，{result.Count}/{Catalog.Count} 项；之后筛选/翻页复用内存缓存。");
        return result;
    }

    public static async Task<byte[]?> Cover(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !(uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".steamusercontent.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".akamaihd.net", StringComparison.OrdinalIgnoreCase))) return null;
        await ImageGate.WaitAsync(token);
        try
        {
            var cache = System.IO.Path.Combine(CacheRoot, "covers");
            var cachePath = System.IO.Path.Combine(cache, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".img");
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length <= 2 * 1024 * 1024)
            {
                var cached = await File.ReadAllBytesAsync(cachePath, token);
                if (WorkshopCatalogPolicy.IsSafeCover(cached)) return cached;
            }
            using var response = await Images.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 2 * 1024 * 1024) return null;
            await using var input = await response.Content.ReadAsStreamAsync(token);
            using var output = new MemoryStream();
            var buffer = new byte[32768];
            int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                if (output.Length + count > 2 * 1024 * 1024) return null;
                output.Write(buffer, 0, count);
            }
            var bytes = output.ToArray();
            if (!WorkshopCatalogPolicy.IsSafeCover(bytes)) return null;
            try
            {
                Directory.CreateDirectory(cache);
                await File.WriteAllBytesAsync(cachePath, bytes, token);
                // Only our bounded preview cache, never subscribed mods or downloaded PCKs.
                var keep = new DirectoryInfo(cache).EnumerateFiles("*.img").OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
                foreach (var old in keep.Skip(128)) old.Delete();
            }
            catch (IOException) { }
            return bytes;
        }
        catch (HttpRequestException) { return null; }
        finally { ImageGate.Release(); }
    }

    public static bool IsActive(ulong id) => SkinService.Catalog is { } catalog &&
        ActiveItems.GetValue(catalog, c => c.ExportWorkshopCatalog().Select(item => item.Id).ToHashSet()).Contains(id);
    public static WorkshopDownload? DownloadState(ulong id) => Downloads.GetValueOrDefault(id);
    internal static bool IsHotRegisteredResourceMod(Mod mod) =>
        Downloads.GetValueOrDefault(SkinCatalog.WorkshopSourceId(mod.path))?.State == WorkshopTextKey.Ready;
    public static string LoadTag(WorkshopCatalogItem item)
    {
        var state = DownloadState(item.Id);
        // Ready also covers mods loaded at startup. Only a completed hot-register
        // check proves that future subscriptions can work without restarting.
        var checkedState = state is { State: WorkshopTextKey.Ready, HotLoadVerified: false } ? null : state?.State;
        return WorkshopLoadTags.Classify(item.RestartRequired, checkedState, state?.Reason ?? WorkshopLoadReason.None);
    }
    public static bool IsSubscribed(ulong id) => ((EItemState)SteamUGC.GetItemState(new(id)) & EItemState.k_EItemStateSubscribed) != 0;
    public static bool IsInstalled(ulong id) => TryInstalled(id, out _);
    public static double? Progress(ulong id) => SteamUGC.GetItemDownloadInfo(new(id), out var bytes, out var total) && total > 0
        ? Math.Clamp(100d * bytes / total, 0, 100) : null;

    public static async Task Subscribe(ulong id)
    {
        if (!Catalog.Any(item => item.Id == id) || Downloads.GetValueOrDefault(id)?.Busy == true) return;
        var status = new WorkshopDownload { Busy = true, BrowserSubscription = true };
        Downloads[id] = status;
        try
        {
            _downloadCallback ??= Callback<DownloadItemResult_t>.Create(ev =>
            {
                if (ev.m_unAppID.m_AppId == WorkshopCatalogPolicy.AppId && Waiters.TryGetValue(ev.m_nPublishedFileId.m_PublishedFileId, out var waiter))
                    waiter.TrySetResult(ev.m_eResult);
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var itemId = new PublishedFileId_t(id);
            using (var subscribed = new SteamCallResult<RemoteStorageSubscribePublishedFileResult_t>(SteamUGC.SubscribeItem(itemId), timeout.Token))
            {
                var result = await subscribed.Task;
                if (result.m_eResult != EResult.k_EResultOK || result.m_nPublishedFileId != itemId) throw new IOException(result.m_eResult.ToString());
            }
            if (!TryInstalled(id, out _))
            {
                var waiter = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                Waiters[id] = waiter;
                if (!SteamUGC.DownloadItem(itemId, false)) throw new IOException("Steam DownloadItem rejected.");
                if (await waiter.Task.WaitAsync(timeout.Token) != EResult.k_EResultOK) throw new IOException("Steam download failed.");
            }
            if (!TryInstalled(id, out var directory)) throw new IOException("Steam install is not complete.");
            status.State = WorkshopTextKey.Checking;
            var alreadyActive = IsActive(id);
            status.Reason = alreadyActive ? WorkshopLoadReason.None : await SkinService.TryRegisterWorkshopResources(directory);
            status.HotLoadVerified = !alreadyActive && status.Reason == WorkshopLoadReason.None;
            status.State = status.Reason == WorkshopLoadReason.None ? WorkshopTextKey.Ready :
                status.Reason is WorkshopLoadReason.Version or WorkshopLoadReason.InvalidPackage ? WorkshopTextKey.Failed : WorkshopTextKey.Restart;
            ModLog.Info($"工坊皮肤 {id}：{status.State}，原因={status.Reason}；安装目录 {directory}");
        }
        catch (Exception ex)
        {
            if (status.State == WorkshopTextKey.Checking) status.Reason = WorkshopLoadReason.IncompleteResources;
            status.State = status.State == WorkshopTextKey.Checking ? WorkshopTextKey.Restart : WorkshopTextKey.Failed;
            status.Error = ex.Message;
            ModLog.Error($"工坊皮肤 {id} 下载/检查失败：{ex}");
        }
        finally
        {
            status.Busy = false;
            Waiters.Remove(id);
            ModLog.Info($"工坊皮肤 {id} 结果保留在标签和操作按钮中；本面板订阅不弹重启提示。");
        }
    }

    public static async Task Unsubscribe(ulong id)
    {
        if (!Catalog.Any(item => item.Id == id) || Downloads.GetValueOrDefault(id)?.Busy == true) return;
        var status = Downloads.GetValueOrDefault(id) ?? new WorkshopDownload();
        Downloads[id] = status;
        status.Busy = true; status.Removing = true; status.Error = "";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var call = new SteamCallResult<RemoteStorageUnsubscribePublishedFileResult_t>(SteamUGC.UnsubscribeItem(new(id)), timeout.Token);
            var result = await call.Task;
            if (result.m_eResult != EResult.k_EResultOK || result.m_nPublishedFileId.m_PublishedFileId != id)
                throw new IOException(result.m_eResult.ToString());
            status.Unsubscribed = true;
            // Keep catalog, selections and mounted resources alive for this session.
            // Steam owns subscription files and removes them after the game exits.
            ModLog.Info($"已取消订阅工坊皮肤 {id}；本次已加载资源保留，文件移除由 Steam 在退出后处理。");
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
            ModLog.Warn($"取消订阅工坊皮肤 {id} 失败：{ex.Message}");
        }
        finally { status.Busy = false; status.Removing = false; }
    }

    private static bool TryInstalled(ulong id, out string directory)
    {
        var state = (EItemState)SteamUGC.GetItemState(new(id));
        directory = "";
        return WorkshopCatalogPolicy.CanUseInstalledFiles(state.HasFlag(EItemState.k_EItemStateInstalled), state.HasFlag(EItemState.k_EItemStateNeedsUpdate),
            state.HasFlag(EItemState.k_EItemStateDownloading), state.HasFlag(EItemState.k_EItemStateDownloadPending)) &&
            SteamUGC.GetItemInstallInfo(new(id), out _, out directory, 4096, out _) && Directory.Exists(directory);
    }

    internal static bool DeferNativeNotice(Mod mod)
    {
        var id = SkinCatalog.WorkshopSourceId(mod.path);
        // The browser owns feedback only for subscriptions explicitly initiated here.
        // Native notifications for unrelated subscriptions/Mods are left untouched.
        return Downloads.GetValueOrDefault(id)?.BrowserSubscription == true;
    }
}

[HarmonyPatch]
internal static class WorkshopRuntimeNoticePatch
{
    private static System.Reflection.MethodBase? TargetMethod() => AccessTools.Method(typeof(NGame), "OnNewModDetected");
    private static bool Prepare() => TargetMethod() != null;
    private static bool Prefix(Mod mod) => !SkinWorkshopService.DeferNativeNotice(mod);
}
