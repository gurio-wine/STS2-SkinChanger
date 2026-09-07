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

internal sealed record WorkshopDetails(string Title, string PreviewUrl);
internal sealed class WorkshopDownload
{
    public WorkshopTextKey State = WorkshopTextKey.Waiting;
    public string Error = "";
    public bool Busy;
}

// Permanent, explicitly requested subscriptions. Never calls OnlineSkinCache or removes Steam files.
internal static class SkinWorkshopService
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
    private static readonly Dictionary<ulong, Mod> DeferredNotices = [];
    private static Callback<DownloadItemResult_t>? _downloadCallback;
    private static readonly System.Net.Http.HttpClient Images = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim ImageGate = new(3);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SkinCatalog, HashSet<ulong>> ActiveItems = new();
    private static string CacheRoot => System.IO.Path.Combine(OS.GetUserDataDir(), "skin_changer_workshop");

    public static async Task<Dictionary<ulong, WorkshopDetails>> Query(IReadOnlyList<ulong> ids, CancellationToken token)
    {
        var requested = ids.Where(id => Catalog.Any(item => item.Id == id)).Distinct().Take(12).ToArray();
        var cache = System.IO.Path.Combine(CacheRoot, WorkshopText.SteamLanguage);
        var result = new Dictionary<ulong, WorkshopDetails>();
        foreach (var id in requested)
        {
            try
            {
                var path = System.IO.Path.Combine(cache, id + ".json");
                if (File.Exists(path) && new FileInfo(path).Length < 32768)
                    if (JsonSerializer.Deserialize<WorkshopDetails>(await File.ReadAllTextAsync(path, token)) is { } cached) result[id] = cached;
            }
            catch (IOException) { }
            catch (JsonException) { }
        }
        if (requested.Length == 0) return result;
        var handle = UGCQueryHandle_t.Invalid;
        try
        {
            handle = SteamUGC.CreateQueryUGCDetailsRequest(requested.Select(id => new PublishedFileId_t(id)).ToArray(), (uint)requested.Length);
            SteamUGC.SetLanguage(handle, WorkshopText.SteamLanguage);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var call = new SteamCallResult<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(handle), timeout.Token);
            var completed = await call.Task;
            if (completed.m_eResult != EResult.k_EResultOK) return result;
            for (uint i = 0; i < completed.m_unNumResultsReturned; i++)
            {
                if (!SteamUGC.GetQueryUGCResult(handle, i, out var item) || item.m_eResult != EResult.k_EResultOK ||
                    item.m_nConsumerAppID.m_AppId != WorkshopCatalogPolicy.AppId || !requested.Contains(item.m_nPublishedFileId.m_PublishedFileId)) continue;
                SteamUGC.GetQueryUGCPreviewURL(handle, i, out var preview, 4096);
                var details = new WorkshopDetails(item.m_rgchTitle, preview ?? "");
                var id = item.m_nPublishedFileId.m_PublishedFileId;
                result[id] = details;
                try
                {
                    Directory.CreateDirectory(cache);
                    await File.WriteAllTextAsync(System.IO.Path.Combine(cache, id + ".json"), JsonSerializer.Serialize(details), token);
                }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        { ModLog.Info("工坊详情暂不可用，保留缓存：" + ex.Message); }
        finally { if (handle != UGCQueryHandle_t.Invalid) SteamUGC.ReleaseQueryUGCRequest(handle); }
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
    internal static bool IsHotRegisteredResourceMod(Mod mod) => mod.manifest?.hasDll == false &&
        Downloads.GetValueOrDefault(SkinCatalog.WorkshopSourceId(mod.path))?.State == WorkshopTextKey.Ready;
    public static bool IsSubscribed(ulong id) => ((EItemState)SteamUGC.GetItemState(new(id)) & EItemState.k_EItemStateSubscribed) != 0;
    public static double? Progress(ulong id) => SteamUGC.GetItemDownloadInfo(new(id), out var bytes, out var total) && total > 0
        ? Math.Clamp(100d * bytes / total, 0, 100) : null;

    public static async Task Subscribe(ulong id)
    {
        if (!Catalog.Any(item => item.Id == id) || Downloads.GetValueOrDefault(id)?.Busy == true) return;
        var status = new WorkshopDownload { Busy = true };
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
            var available = IsActive(id) || await SkinService.TryRegisterWorkshopResources(directory);
            status.State = available ? WorkshopTextKey.Ready : WorkshopTextKey.Restart;
            ModLog.Info($"工坊皮肤 {id}：{status.State}；安装目录 {directory}");
        }
        catch (Exception ex)
        {
            status.State = status.State == WorkshopTextKey.Checking ? WorkshopTextKey.Restart : WorkshopTextKey.Failed;
            status.Error = ex.Message;
            ModLog.Error($"工坊皮肤 {id} 下载/检查失败：{ex}");
        }
        finally
        {
            status.Busy = false;
            Waiters.Remove(id);
            if (DeferredNotices.Remove(id, out var mod) && status.State != WorkshopTextKey.Ready && GodotObject.IsInstanceValid(NGame.Instance))
            {
                try { AccessTools.Method(typeof(NGame), "OnNewModDetected")?.Invoke(NGame.Instance, [mod]); }
                catch (Exception ex) { ModLog.Warn("恢复工坊重启提示失败，面板保留重启状态：" + ex.GetBaseException().Message); }
            }
        }
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
        if (Downloads.GetValueOrDefault(id) is not { } status) return false;
        if (status.Busy) { DeferredNotices[id] = mod; return true; }
        return status.State == WorkshopTextKey.Ready;
    }
}

[HarmonyPatch]
internal static class WorkshopRuntimeNoticePatch
{
    private static System.Reflection.MethodBase? TargetMethod() => AccessTools.Method(typeof(NGame), "OnNewModDetected");
    private static bool Prepare() => TargetMethod() != null;
    private static bool Prefix(Mod mod) => !SkinWorkshopService.DeferNativeNotice(mod);
}
