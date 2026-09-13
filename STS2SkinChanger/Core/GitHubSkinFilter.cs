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

    // A repository without a code has no skin to install, so it lives in its own bucket and is
    // never listed as installed or not installed: those two views only hold repositories the
    // browser can actually install.
    public static bool Matches(string filter, bool recognized, GitHubInstallState state) => filter switch
    {
        Missing => recognized && state == GitHubInstallState.NotInstalled,
        Installed => recognized && state != GitHubInstallState.NotInstalled,
        Updatable => recognized && state == GitHubInstallState.Updatable,
        Unrecognized => !recognized,
        _ => true
    };

    // The unrecognized view lists repositories that carry no kind at all, so a kind or target
    // left over from the Steam source must not empty it: an unrecognized repository can never
    // match one, and this is the only view where its scan action lives.
    public static bool IgnoresKindFilter(string filter) => filter == Unrecognized;

    public static bool IsOption(string? filter) => filter != null && Options.Contains(filter);
}
