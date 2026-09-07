namespace STS2SkinChanger.Core;

internal static class WorkshopLoadTags
{
    public static readonly string[] FilterOptions = ["restart", "hot"];
    public static string Classify(bool knownRestart, WorkshopTextKey? state, WorkshopLoadReason reason)
    {
        // Errors retain their separate, specific message; they aren't load-mode tags.
        if (state == WorkshopTextKey.Ready) return "hot";
        return state == WorkshopTextKey.Restart || knownRestart ? "restart" : "hot";
    }
    public static bool Matches(string filter, string tag) => filter.Length == 0 || filter == tag;
    public static string Name(string tag) => tag switch
    {
        "restart" => WorkshopText.Get(WorkshopTextKey.Restart),
        "hot" => WorkshopCommunityText.Get(WorkshopCommunityTextKey.Hot),
        _ => throw new ArgumentOutOfRangeException(nameof(tag))
    };
}
