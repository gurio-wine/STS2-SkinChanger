using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using Steamworks;

namespace STS2SkinChanger.Core;

internal static partial class SkinWorkshopService
{
    private static readonly WorkshopSessionCache<WorkshopIntroduction> Introductions = new();
    private static readonly SemaphoreSlim IntroductionGate = new(2);

    public static Task<WorkshopIntroduction> Introduction(ulong id, CancellationToken token)
    {
        if (!CanReadMetadata(id)) return Task.FromResult(new WorkshopIntroduction("", [], false));
        var language = WorkshopText.SteamLanguage;
        return Introductions.Get(language + ":" + id, () => LoadIntroduction(id, language), token);
    }

    private static async Task<WorkshopIntroduction> LoadIntroduction(ulong id, string language)
    {
        await IntroductionGate.WaitAsync();
        var handle = UGCQueryHandle_t.Invalid;
        try
        {
            handle = SteamUGC.CreateQueryUGCDetailsRequest([new(id)], 1);
            if (handle == UGCQueryHandle_t.Invalid || !SteamUGC.SetLanguage(handle, language) ||
                !SteamUGC.SetReturnLongDescription(handle, true) || !SteamUGC.SetReturnAdditionalPreviews(handle, true))
                return new("", [], false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var call = new SteamCallResult<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(handle), timeout.Token);
            var result = await call.Task;
            if (result.m_eResult != EResult.k_EResultOK || result.m_unNumResultsReturned != 1 ||
                !SteamUGC.GetQueryUGCResult(handle, 0, out var item) || item.m_eResult != EResult.k_EResultOK ||
                item.m_nPublishedFileId.m_PublishedFileId != id ||
                item.m_nConsumerAppID.m_AppId != WorkshopCatalogPolicy.AppId && !CommunityIssues.Any(issue => issue.Id == id))
                return new("", [], false);
            var images = new List<string>();
            var count = Math.Min(64U, SteamUGC.GetQueryUGCNumAdditionalPreviews(handle, 0));
            for (uint i = 0; i < count; i++)
                if (SteamUGC.GetQueryUGCAdditionalPreview(handle, 0, i, out var url, 4096, out _, 1024, out var kind) &&
                    kind == EItemPreviewType.k_EItemPreviewType_Image) images.Add(url);
            SteamUGC.GetQueryUGCPreviewURL(handle, 0, out var cover, 4096);
            return new(WorkshopIntroductionPolicy.Summary(item.m_rgchDescription), WorkshopIntroductionPolicy.Images(cover ?? "", images));
        }
        catch (Exception ex)
        {
            ModLog.Info($"工坊皮肤 {id} 简介暂不可用：{ex.Message}");
            return new("", [], false);
        }
        finally
        {
            if (handle != UGCQueryHandle_t.Invalid) SteamUGC.ReleaseQueryUGCRequest(handle);
            IntroductionGate.Release();
        }
    }
}
