namespace STS2SkinChanger.Core;

internal static class WorkshopLoadTags
{
    public static string Classify(bool knownRestart, WorkshopTextKey? state, WorkshopLoadReason reason)
    {
        if (state == WorkshopTextKey.Failed && reason is WorkshopLoadReason.Version or WorkshopLoadReason.InvalidPackage) return "blocked";
        if (state == WorkshopTextKey.Ready) return "hot";
        return state == WorkshopTextKey.Restart || knownRestart ? "restart" : "unknown";
    }
    public static bool Matches(string filter, string tag) => filter.Length == 0 || filter == tag;
    public static string Name(string tag) => tag switch
    {
        "restart" => WorkshopText.Get(WorkshopTextKey.Restart),
        "hot" => WorkshopCommunityText.Get(WorkshopCommunityTextKey.Hot),
        "blocked" => WorkshopCommunityText.Get(WorkshopCommunityTextKey.Blocked),
        _ => WorkshopCommunityText.Get(WorkshopCommunityTextKey.Unknown)
    };
}
