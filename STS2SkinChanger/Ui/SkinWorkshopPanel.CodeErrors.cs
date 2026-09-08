using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class SkinWorkshopPanel
{
    private void RebuildCodeErrors(int generation, CancellationToken token)
    {
        var problems = WorkshopCodeDiagnostics.Group(SkinWorkshopService.CommunityIssues);
        var pages = Math.Max(1, (problems.Length + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1); _pageLabel.Text = $"{_page + 1} / {pages}";
        _previous.Disabled = _page == 0; _next.Disabled = _page + 1 >= pages;
        _filteredIds = [];
        _emptyLabel!.Text = WorkshopCodeErrorText.Get(WorkshopCodeUi.Empty);
        _emptyLabel.Visible = problems.Length == 0;
        var visible = problems.Skip(_page * PageSize).Take(PageSize).ToArray();

        // Share the ordinary row pool, cover, marquee, hover and theme. Slot keys also
        // permit malformed codes without an ID; only WorkshopId may reach Steam.
        PrepareRows(Enumerable.Range(1, visible.Length).Select(i => (ulong)i).ToArray());
        var snapshots = new Dictionary<ulong, ItemVisual>();
        for (var i = 0; i < visible.Length; i++)
        {
            var view = _pageRows!.Slots[i].View;
            var problem = visible[i];
            view.WorkshopId = problem.Id;
            view.Title.Text = SkinWorkshopService.CachedDetails(problem.Id)?.Title ??
                (problem.Name.Length > 0 ? problem.Name : problem.Id > 0 ? "…" : WorkshopCodeErrorText.Get(WorkshopCodeUi.Unknown));
            view.Tags.Hide(); view.Metric.Hide();
            view.Status.Text = string.Join(" · ", problem.Issues.Select(issue => issue.Error).Distinct().Select(WorkshopCodeErrorText.Error));
            view.Status.TooltipText = view.Status.Text;
            view.Action.Text = WorkshopCodeErrorText.Get(WorkshopCodeUi.CopyDetails);
            view.Cancel.Text = WorkshopCodeErrorText.Get(WorkshopCodeUi.Locate);
            view.Action.Show(); view.Cancel.Show(); view.Action.Disabled = false;
            var source = problem.Sources.FirstOrDefault(s => WorkshopCodeDiagnostics.SafeSource(s.Url));
            view.Cancel.Disabled = source == null;
            view.PrimaryAction = () =>
            {
                try
                {
                    DisplayServer.ClipboardSet(WorkshopCodeDiagnostics.Report(problem, ModLocalization.CurrentLanguage));
                    view.Action.Text = WorkshopSubmissionText.Get(SubmissionText.Copied);
                }
                catch (Exception ex) { view.Status.TooltipText = ex.Message; ModLog.Warn("复制投稿错误失败：" + ex.Message); }
            };
            view.SecondaryAction = () =>
            {
                if (source == null) return;
                try { WorkshopCommunityLinks.OpenSubmissionSource(source.Url); }
                catch (Exception ex) { view.Status.TooltipText = ex.Message; ModLog.Warn("投稿来源打开失败：" + ex.Message); }
            };
            if (problem.Id == 0) continue;
            snapshots[problem.Id] = view.Snapshot();
            _hoverItems.Add(new(problem.Id, view.Slot, view.Panel));
        }
        _ = LoadDetails(snapshots.Keys.ToArray(), snapshots, generation, token);
    }
}
