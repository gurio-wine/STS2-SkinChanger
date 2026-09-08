using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using Steamworks;

namespace STS2SkinChanger.Core;

internal static partial class SkinWorkshopService
{
    private static readonly WorkshopCommunityCatalog Community = new();
    private static WorkshopCatalogItem[]? _combinedItems;
    private static bool _communityStarted;
    private static bool _communityBusy;
    private static int _communityRevision;
    private static string _communityProgress = "";
    private static bool _communityFailed;
    public static int CommunityRevision => _communityRevision;
    public static bool CommunityBusy => _communityBusy;
    public static bool CommunityFailed => _communityFailed;
    public static string CommunityProgress => _communityProgress;

    internal static void StartCommunityRefresh()
    {
        if (_communityStarted) return;
        _communityStarted = true;
        _ = RefreshCommunity(restore: true);
    }
    public static async Task RefreshCommunity(bool restore = false)
    {
        if (_communityBusy) return;
        _communityBusy = true; _communityFailed = false; _communityProgress = "";
        try
        {
            var path = System.IO.Path.Combine(CacheRoot, "community-catalog.json");
            if (restore)
            {
                try { await Community.Restore(path); PublishCommunity(); }
                catch (Exception ex) { ModLog.Warn("社区清单缓存不可用：" + ex.Message); }
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var progress = new Progress<(int Current, int Total)>(p => _communityProgress = $"{p.Current}/{p.Total}");
            await Community.Replace(
                () => Task.Run(() => WorkshopDiscussionSource.ReadAll(WorkshopDiscussionSource.Fetch, progress, timeout.Token), timeout.Token),
                items => VerifyCommunity(items, timeout.Token), path);
            PublishCommunity();
            ModLog.Info($"工坊投稿帖完整刷新成功：{Community.Items.Length} 项，合并后 {Catalog.Count} 项；未订阅或执行任何投稿内容。");
        }
        catch (Exception ex)
        {
            _communityFailed = true;
            ModLog.Warn("工坊投稿帖刷新失败，保留上次完整清单：" + ex.GetBaseException().Message);
        }
        finally { _communityBusy = false; _communityProgress = ""; }
    }
    private static void PublishCommunity()
    {
        var next = WorkshopSubmissionCode.Merge(Items.Value, Community.Items);
        var current = _combinedItems ?? Items.Value;
        if (current.Length == next.Length && current.Zip(next).All(p => p.First.Id == p.Second.Id &&
            p.First.RestartRequired == p.Second.RestartRequired && p.First.Targets.SequenceEqual(p.Second.Targets))) return;
        _combinedItems = next;
        _communityRevision++;
    }
    private static async Task<WorkshopCatalogItem[]> VerifyCommunity(WorkshopCatalogItem[] items, CancellationToken token)
    {
        var valid = new HashSet<ulong>();
        foreach (var chunk in items.Select(i => i.Id).Distinct().Chunk(100))
        {
            token.ThrowIfCancellationRequested();
            var handle = SteamUGC.CreateQueryUGCDetailsRequest(chunk.Select(id => new PublishedFileId_t(id)).ToArray(), (uint)chunk.Length);
            if (handle == UGCQueryHandle_t.Invalid) throw new IOException("Steam item verification unavailable.");
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                using var call = new MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamCallResult<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(handle), timeout.Token);
                var result = await call.Task;
                if (result.m_eResult != EResult.k_EResultOK) throw new IOException("Steam item verification: " + result.m_eResult);
                // The callback counts returned items, not requested IDs. Deleted/private IDs
                // may be absent; one unavailable submission must not block all other Mods.
                if (result.m_unNumResultsReturned > chunk.Length) throw new IOException("Unexpected Steam item verification results.");
                for (uint index = 0; index < result.m_unNumResultsReturned; index++)
                {
                    if (!SteamUGC.GetQueryUGCResult(handle, index, out var detail)) throw new IOException("Incomplete Steam item verification.");
                    if (detail.m_eResult is not (EResult.k_EResultOK or EResult.k_EResultFileNotFound or EResult.k_EResultAccessDenied or EResult.k_EResultInvalidParam))
                        throw new IOException("Transient Steam item verification failure: " + detail.m_eResult);
                    if (detail.m_eResult == EResult.k_EResultOK && detail.m_nConsumerAppID.m_AppId == WorkshopCatalogPolicy.AppId &&
                        chunk.Contains(detail.m_nPublishedFileId.m_PublishedFileId)) valid.Add(detail.m_nPublishedFileId.m_PublishedFileId);
                }
            }
            finally { SteamUGC.ReleaseQueryUGCRequest(handle); }
        }
        return items.Where(i => valid.Contains(i.Id)).ToArray();
    }

    public static async Task<WorkshopLocalSubmission[]> SubmissionCandidates(CancellationToken token)
    {
        // Including Steam-locally-disabled subscriptions is read-only; never enable them here.
        var counter = typeof(SteamUGC).GetMethod("GetNumSubscribedItems", [typeof(bool)]);
        var count = counter != null ? (uint)counter.Invoke(null, [true])! : SteamUGC.GetNumSubscribedItems();
        if (count > 20000) throw new InvalidDataException("Too many subscribed items.");
        var ids = new PublishedFileId_t[count];
        var listing = typeof(SteamUGC).GetMethod("GetSubscribedItems", [typeof(PublishedFileId_t[]), typeof(uint), typeof(bool)]);
        var returned = listing != null ? (uint)listing.Invoke(null, [ids, count, true])! : SteamUGC.GetSubscribedItems(ids, count);
        var installed = new List<(ulong Id, string Path)>();
        var candidates = ids.Take((int)returned).Select(i => i.m_PublishedFileId)
            .Concat(ModManager.Mods.Select(m => STS2SkinChanger.Catalog.SkinCatalog.WorkshopSourceId(m.path))).Where(i => i > 0).Distinct();
        foreach (var source in candidates)
        {
            var id = new PublishedFileId_t(source);
            token.ThrowIfCancellationRequested();
            if (id.m_PublishedFileId == 3787302680) continue;
            if (IsSubscribed(source) && TryInstalled(source, out var directory)) installed.Add((source, directory));
        }
        return await Task.Run(() =>
        {
            var candidates = new List<WorkshopLocalSubmission>();
            foreach (var item in installed)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var mods = WorkshopSubmissionScanner.ReadDescriptors(item.Path);
                    if (mods.Length > 0) candidates.Add(new(item.Id, string.Join(" / ", mods.Select(m => m.Name).Distinct()), item.Path));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { ModLog.Info($"投稿候选 {item.Id} 跳过：{ex.Message}"); }
            }
            return candidates.OrderBy(c => c.Name, StringComparer.CurrentCulture).ToArray();
        }, token);
    }

    public static async Task<(string[] Codes, int Accepted, int Rejected)> ScanSubmissions(WorkshopLocalSubmission[] selected,
        IProgress<(int Current, int Total, string Name)> progress, CancellationToken token)
    {
        var snapshot = SkinService.CaptureWorkshopScanContext();
        var gameVersion = AccessTools.Field(typeof(ModManager), "_gameVersion")?.GetValue(null)?.ToString() ?? "";
        var dependencies = ModManager.Mods.Where(m => m.state == ModLoadState.Loaded && m.manifest?.id != null)
            .GroupBy(m => m.manifest!.id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().manifest!.version ?? "", StringComparer.OrdinalIgnoreCase);
        var installed = selected.Where(s => IsSubscribed(s.Id) && TryInstalled(s.Id, out var root) &&
            System.IO.Path.GetFullPath(root).Equals(System.IO.Path.GetFullPath(s.Directory), StringComparison.OrdinalIgnoreCase)).ToArray();
        return await SkinService.RunWorkshopScan(() =>
        {
            var items = new List<WorkshopCatalogItem>();
            var rejected = selected.Length - installed.Length;
            for (var i = 0; i < installed.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var local = installed[i];
                progress.Report((i, installed.Length, local.Name));
                var result = WorkshopSubmissionScanner.Scan(local, snapshot.GamePack, snapshot.Cards, gameVersion, dependencies, snapshot.Baselines);
                if (result.Item != null) items.Add(result.Item);
                else { rejected++; ModLog.Info($"投稿扫描 {local.Id} 未识别：{result.Error}"); }
                progress.Report((i + 1, installed.Length, local.Name));
            }
            token.ThrowIfCancellationRequested();
            return (WorkshopSubmissionCode.Encode(items, Entry.InternalTestVersion, gameVersion), items.Count, rejected);
        }, token);
    }
}
