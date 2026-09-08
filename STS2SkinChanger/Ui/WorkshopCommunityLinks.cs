using Godot;
using Steamworks;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class WorkshopCommunityLinks
{
    public static void OpenItem(ulong id) => Open(WorkshopItemActions.ItemUrl(id, false), WorkshopItemActions.ItemUrl(id, true));
    public static void OpenDiscussion(bool presets) => Open(WorkshopCommunityPolicy.DiscussionUrl(presets, false), WorkshopCommunityPolicy.DiscussionUrl(presets, true));
    public static void OpenSubmissionSource(string url)
    {
        if(!WorkshopCodeDiagnostics.SafeSource(url))throw new InvalidDataException("Invalid submission source.");
        Open(url,"steam://openurl/"+url);
    }

    private static void Open(string webUrl, string clientUrl)
    {
        if (SteamUtils.IsOverlayEnabled()) SteamFriends.ActivateGameOverlayToWebPage(webUrl);
        else if (OS.ShellOpen(clientUrl) != Error.Ok)
            throw new IOException(WorkshopCommunityText.Get(WorkshopCommunityTextKey.OpenFailed));
    }
}
