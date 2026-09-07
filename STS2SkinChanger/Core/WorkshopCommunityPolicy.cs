namespace STS2SkinChanger.Core;

internal static class WorkshopCommunityPolicy
{
    public static string DiscussionUrl(bool presets, bool client)
    {
        var url = "https://steamcommunity.com/workshop/filedetails/discussion/3787302680/" +
            (presets ? "592940620292746467" : "592940620292752301");
        return client ? "steam://openurl/" + url : url;
    }
}
