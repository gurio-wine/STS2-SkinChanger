namespace STS2SkinChanger.Core;

internal static class WorkshopItemActions
{
    public static WorkshopTextKey? Primary(bool subscribed, bool active, bool installed, bool knownRestart, WorkshopTextKey? state, bool busy)
    {
        if (!subscribed) return WorkshopTextKey.Subscribe;
        if (busy) return WorkshopTextKey.Download;
        if (active || state == WorkshopTextKey.Ready) return null;
        if (installed && (state == WorkshopTextKey.Restart || state == null && knownRestart)) return WorkshopTextKey.Restart;
        return WorkshopTextKey.Download;
    }
    public static WorkshopTextKey? Status(WorkshopTextKey? state) => state is WorkshopTextKey.Ready or WorkshopTextKey.Restart ? null : state;
    public static string ItemUrl(ulong id, bool client) => client ? $"steam://url/CommunityFilePage/{id}" : $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";
}
