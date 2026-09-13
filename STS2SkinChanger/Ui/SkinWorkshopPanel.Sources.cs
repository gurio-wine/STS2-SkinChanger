using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// The second source lives inside the same browser: the identity space, the rows and the
// type/region/target filters are shared, while subscription and download state stay Steam-only.
internal partial class SkinWorkshopPanel
{
    private const string InstallAll = "";
    private string _source = SkinSources.Steam;
    private string _installFilter = InstallAll;
    private OptionButton _sourcePicker = null!;
    private WorkshopPagedChoice _installPicker = null!;
    private OptionButton _githubSortPicker = null!;
    private Button _githubRefresh = null!;
    // Stars first: the topic is small, and the most starred repository is what a player
    // looking for a skin is after.
    private GitHubSort _githubSort = GitHubSort.Stars;
    private int _githubRevision = -1;
    private static readonly string[] InstallOptions = ["missing", "installed", "updatable"];

    private bool GitHubMode => _source == SkinSources.GitHub;

    private static string GitHubText(GitHubSkinTextKey key) =>
        GitHubSkinText.ForLanguage(ModLocalization.CurrentLanguage, key);

    private static string InstallName(string value) => GitHubText(value switch
    {
        "missing" => GitHubSkinTextKey.NotInstalled,
        "installed" => GitHubSkinTextKey.Installed,
        "updatable" => GitHubSkinTextKey.Updatable,
        _ => GitHubSkinTextKey.All
    });

    // A GitHub skin has no Workshop subscription, so "installed" has to ask the install
    // state of its own source; the Workshop answer is always false for a synthetic id.
    private static bool Installed(ulong id) => SkinSourceIdentity.IsGitHub(id)
        ? GitHubSkinService.InstallState(id) != GitHubInstallState.NotInstalled
        : SkinWorkshopService.IsInstalled(id);

    private void BuildSourceFilters(HFlowContainer filters)
    {
        // Exactly two entries, Steam first, each labelled by its own source. The paged choice
        // used by the other filters appends the current value as an extra entry, which for a
        // two-value switch showed a third entry wearing a duplicate label.
        _sourcePicker = new OptionButton { CustomMinimumSize = new Vector2(215, 42), FitToLongestItem = false,
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        ContextualSkinControls.ApplyGameTheme(_sourcePicker);
        for (var index = 0; index < SkinSources.Options.Length; index++)
            _sourcePicker.AddItem(SkinSources.Name(ModLocalization.CurrentLanguage, SkinSources.Options[index]), index);
        _sourcePicker.Select(SkinSources.IndexOf(_source));
        _sourcePicker.ItemSelected += index => SelectSource(SkinSources.OptionAt((int)index));
        filters.AddChild(_sourcePicker); KeepHoverBelow(_sourcePicker);
        // Steam's own filter follows the source picker; GitHub swaps it for its install filter.
        filters.AddChild(_subscriptionPicker.Picker); KeepHoverBelow(_subscriptionPicker.Picker);
        _installPicker = new(InstallName, value =>
        { _installFilter = value; _page = 0; _listScroll.ScrollVertical = 0; RefreshFilters(); Rebuild(); });
        filters.AddChild(_installPicker.Picker); KeepHoverBelow(_installPicker.Picker);
    }

    private void SelectSource(string value)
    {
        _source = SkinSources.Normalize(value);
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
            "missing" => !Installed(item.Id),
            "installed" => Installed(item.Id),
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
