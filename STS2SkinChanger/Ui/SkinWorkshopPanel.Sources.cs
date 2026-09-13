using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// The second source lives inside the same browser: the identity space, the rows and the
// type/region/target filters are shared, while subscription and download state stay Steam-only.
internal partial class SkinWorkshopPanel
{
    private const string SourceSteam = "steam";
    private const string SourceGitHub = "github";
    private const string InstallAll = "";
    private string _source = SourceSteam;
    private string _installFilter = InstallAll;
    private WorkshopPagedChoice _sourcePicker = null!;
    private WorkshopPagedChoice _installPicker = null!;
    private OptionButton _githubSortPicker = null!;
    private Button _githubRefresh = null!;
    private GitHubSort _githubSort = GitHubSort.Updated;
    private int _githubRevision = -1;
    // Only "GitHub" is an explicit option: the picker's built-in empty entry carries the Steam
    // label, so the row reads exactly "Steam | GitHub" without a meaningless "all sources".
    private static readonly string[] SourceOptions = [SourceGitHub];
    private static readonly string[] InstallOptions = ["missing", "installed", "updatable"];

    private bool GitHubMode => _source == SourceGitHub;

    private static string GitHubText(GitHubSkinTextKey key) =>
        GitHubSkinText.ForLanguage(ModLocalization.CurrentLanguage, key);

    private static string InstallName(string value) => GitHubText(value switch
    {
        "missing" => GitHubSkinTextKey.NotInstalled,
        "installed" => GitHubSkinTextKey.Installed,
        "updatable" => GitHubSkinTextKey.Updatable,
        _ => GitHubSkinTextKey.All
    });

    private void BuildSourceFilters(HFlowContainer filters)
    {
        _sourcePicker = new(_ => GitHubText(GitHubSkinTextKey.GitHub), SelectSource,
            () => GitHubText(GitHubSkinTextKey.Steam));
        filters.AddChild(_sourcePicker.Picker); KeepHoverBelow(_sourcePicker.Picker);
        // Steam's own filter follows the source picker; GitHub swaps it for its install filter.
        filters.AddChild(_subscriptionPicker.Picker); KeepHoverBelow(_subscriptionPicker.Picker);
        _installPicker = new(InstallName, value =>
        { _installFilter = value; _page = 0; _listScroll.ScrollVertical = 0; RefreshFilters(); Rebuild(); });
        filters.AddChild(_installPicker.Picker); KeepHoverBelow(_installPicker.Picker);
    }

    private void SelectSource(string value)
    {
        _source = value.Length == 0 ? SourceSteam : value;
        _page = 0;
        _listScroll.ScrollVertical = 0;
        // First switch to GitHub restores the cached catalog and then refreshes it in the
        // background; the list itself never waits for a download.
        if (GitHubMode && GitHubSkinService.Revision == 0) GitHubSkinService.StartRefresh();
        RefreshFilters();
        Rebuild();
    }

    private IEnumerable<WorkshopCatalogItem> SourceFiltered(IEnumerable<WorkshopCatalogItem> items) => items
        .Where(item => GitHubMode ? SkinSourceIdentity.IsGitHub(item.Id) : SkinSourceRoutingPolicy.IsSteamItem(item.Id))
        .Where(item => !GitHubMode || _installFilter.Length == 0 || _installFilter switch
        {
            // "Installed" covers both an up-to-date install and one with an update waiting.
            "missing" => !SkinWorkshopService.IsInstalled(item.Id),
            "installed" => SkinWorkshopService.IsInstalled(item.Id),
            "updatable" => GitHubSkinService.InstallState(item.Id) == GitHubInstallState.Updatable,
            _ => true
        });

    private string GitHubMetric(ulong id) => GitHubSortPolicy.Metric(GitHubSkinService.Entry(id), _githubSort);

    private static string GitHubStatus(GitHubSkinEntry? entry, GitHubInstallState state) => entry is null ? "" :
        entry.Recognition switch
        {
            GitHubRecognition.Pending => GitHubText(GitHubSkinTextKey.Refreshing),
            GitHubRecognition.Recognized => GitHubText(state switch
            {
                GitHubInstallState.Updatable => GitHubSkinTextKey.Updatable,
                GitHubInstallState.Installed => GitHubSkinTextKey.Installed,
                _ => GitHubSkinTextKey.NotInstalled
            }),
            _ => GitHubText(GitHubSkinTextKey.Unrecognized)
        };

    private void PollGitHub()
    {
        _communityRefresh.Visible = !GitHubMode;
        _submissionButton.Visible = !GitHubMode;
        _githubRefresh.Visible = GitHubMode && !CodeErrors;
        _githubRefresh.Disabled = GitHubSkinService.Busy;
        _githubRefresh.Text = GitHubText(GitHubSkinService.Busy ? GitHubSkinTextKey.Refreshing : GitHubSkinTextKey.Refresh);
        _githubRefresh.TooltipText = GitHubSkinService.RateLimited ? GitHubText(GitHubSkinTextKey.RateLimited)
            : GitHubSkinService.Failed ? GitHubText(GitHubSkinTextKey.RefreshFailed)
            : GitHubSkinService.Progress;
        if (_githubRevision == GitHubSkinService.Revision) return;
        _githubRevision = GitHubSkinService.Revision;
        if (!GitHubMode) return;
        RefreshFilters();
        Rebuild();
    }
}
