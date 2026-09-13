namespace STS2SkinChanger.Core;

// The GitHub source shares the Workshop browser's list but not its subscription state, so it
// filters by what the local machine can see: the install state of the folder this mod wrote,
// and whether the repository published a code at all.
internal static class GitHubSkinFilter
{
    public const string All = "";
    public const string Missing = "missing";
    public const string Installed = "installed";
    public const string Updatable = "updatable";
    public const string Unrecognized = "unrecognized";

    public static readonly string[] Options = [Missing, Installed, Updatable, Unrecognized];

    // The player opening this source is looking for something to install, so the default view
    // is "not installed" rather than the whole topic.
    public const string DefaultFilter = Missing;

    // A repository without a recognized code cannot be installed, but it is still not
    // installed, so it stays visible in the default view - that is where its scan button is.
    public static bool Matches(string filter, bool recognized, GitHubInstallState state) => filter switch
    {
        Missing => state == GitHubInstallState.NotInstalled,
        Installed => state != GitHubInstallState.NotInstalled,
        Updatable => state == GitHubInstallState.Updatable,
        Unrecognized => !recognized,
        _ => true
    };

    public static bool IsOption(string? filter) => filter != null && Options.Contains(filter);
}
