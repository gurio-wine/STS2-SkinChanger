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
    public static IReadOnlyList<WorkshopCodeIssue> CommunityIssues => Community.State.Issues;

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
            // SCM1's cache has no group completeness proof. Keep the grouped cache format.
            var path = System.IO.Path.Combine(CacheRoot, "community-catalog-v2.json");
            if (restore)
            {
                try { await Community.Restore(path); PublishCommunity(); }
                catch (Exception ex) { ModLog.Warn("社区清单缓存不可用：" + ex.Message); }
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var progress = new Progress<(int Current, int Total)>(p => _communityProgress = $"{p.Current}/{p.Total}");
            await Community.Replace(async () =>
            {
                var read=await Task.Run(async ()=>WorkshopSubmissionCodec.Read(await WorkshopDiscussionSource.ReadAllPosts(
                    WorkshopDiscussionSource.Fetch,progress,timeout.Token)),timeout.Token);
                var identities=await QuerySubmissionIdentities(read.Candidates.Select(c=>c.Payload.Item.Id).Concat(read.Issues.Select(i=>i.Id)),timeout.Token);
                return WorkshopSubmissionIntegrity.Verify(read,identities,Community.State);
            }, path, SkinChangerPaths.WriteCache);
            PublishCommunity();
            ModLog.Info($"工坊投稿帖完整刷新成功：{Community.Items.Length} 项，码错误 {CommunityIssues.Count} 项，合并后 {Catalog.Count} 项；未订阅或执行任何投稿内容。");
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
        _combinedItems = next;
        // Errors can change without valid membership changing; the error filter must refresh too.
        _communityRevision++;
    }
    private static async Task<Dictionary<ulong,WorkshopIdentity>> QuerySubmissionIdentities(IEnumerable<ulong> ids, CancellationToken token)
    {
        var valid = new Dictionary<ulong,WorkshopIdentity>();
        foreach (var chunk in ids.Where(id=>id>0).Distinct().Chunk(100))
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
                    if (detail.m_eResult == EResult.k_EResultOK && chunk.Contains(detail.m_nPublishedFileId.m_PublishedFileId))
                        valid[detail.m_nPublishedFileId.m_PublishedFileId]=new(detail.m_nConsumerAppID.m_AppId,detail.m_rgchTitle??"");
                }
            }
            finally { SteamUGC.ReleaseQueryUGCRequest(handle); }
        }
        return valid;
    }

    public static async Task<WorkshopLocalSubmission[]> SubmissionCandidates(CancellationToken token)
    {
        // Reuse already recognized resources. Opening this panel must not scan every subscription.
        var recognized = SkinService.Catalog?.ExportWorkshopCatalog() ?? [];
        var pending = WorkshopSubmissionCandidatePolicy.EligibleIds(recognized, Catalog);
        var installed = new List<(ulong Id, string Path)>();
        foreach (var source in pending)
        {
            token.ThrowIfCancellationRequested();
            if (IsSubscribed(source) && TryInstalled(source, out var directory)) installed.Add((source, directory));
        }
        var result = await Task.Run(() =>
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
        // A community refresh may have finished while the manifests were being read.
        return FilterSubmissionCandidates(result);
    }

    internal static WorkshopLocalSubmission[] FilterSubmissionCandidates(IEnumerable<WorkshopLocalSubmission> candidates) =>
        WorkshopSubmissionCandidatePolicy.Filter(candidates, SkinService.Catalog?.ExportWorkshopCatalog() ?? [], Catalog);

    public static async Task<(string[] Codes, int Accepted, int Rejected)> ScanSubmissions(WorkshopLocalSubmission[] selected,
        IProgress<(int Current, int Total, string Name)> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var installed = FilterSubmissionCandidates(selected).Where(s => IsSubscribed(s.Id) && TryInstalled(s.Id, out var root) &&
            System.IO.Path.GetFullPath(root).Equals(System.IO.Path.GetFullPath(s.Directory), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (installed.Length == 0) return ([], 0, selected.Length);
        var snapshot = SkinService.CaptureWorkshopScanContext();
        var gameVersion = AccessTools.Field(typeof(ModManager), "_gameVersion")?.GetValue(null)?.ToString() ?? "";
        var dependencies = ModManager.Mods.Where(m => m.state == ModLoadState.Loaded && m.manifest?.id != null)
            .GroupBy(m => m.manifest!.id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().manifest!.version ?? "", StringComparer.OrdinalIgnoreCase);
        return await SkinService.RunWorkshopScan(() =>
        {
            var items = new List<WorkshopCatalogItem>();
            var codes=new List<string>();
            var rejected = selected.Length - installed.Length;
            for (var i = 0; i < installed.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var local = installed[i];
                progress.Report((i, installed.Length, local.Name));
                var result = WorkshopSubmissionScanner.Scan(local, snapshot.GamePack, snapshot.Cards, gameVersion, dependencies, snapshot.Baselines);
                if (result.Item != null)
                {
                    try
                    {
                        codes.AddRange(WorkshopSubmissionCodec.Encode(result.Item,Entry.InternalTestVersion,gameVersion));
                        items.Add(result.Item);
                    }
                    catch(Exception ex) when(ex is not OperationCanceledException)
                    { rejected++; ModLog.Warn($"投稿编码 {local.Id} 失败：{ex.GetBaseException().Message}"); }
                }
                else { rejected++; ModLog.Info($"投稿扫描 {local.Id} 未识别：{result.Error}"); }
                progress.Report((i + 1, installed.Length, local.Name));
            }
            token.ThrowIfCancellationRequested();
            return (codes.ToArray(), items.Count, rejected);
        }, token);
    }
}
