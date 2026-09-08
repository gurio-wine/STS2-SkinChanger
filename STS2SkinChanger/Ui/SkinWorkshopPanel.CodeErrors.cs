using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class SkinWorkshopPanel
{
    private VBoxContainer _issueRows = null!;
    private readonly List<CodeErrorRow> _errorPool = [];
    private Label? _noCodeErrors;
    private sealed class CodeErrorRow
    {
        public PanelContainer Panel = null!;
        public Label Title = null!, Reason = null!, Group = null!;
        public OptionButton Source = null!;
        public WorkshopPagedChoice SourceChoice = null!;
        public int SourceIndex;
        public readonly WorkshopItemBinding Binding = new();
        public Button Locate = null!, Copy = null!;
        public WorkshopCodeProblem? Problem;
        public WorkshopCodeSource[] Sources = [];
    }
    private void RebuildCodeErrors()
    {
        const int count = 4;
        var problems = WorkshopCodeDiagnostics.Group(SkinWorkshopService.CommunityIssues);
        var pages = Math.Max(1, (problems.Length + count - 1) / count);
        _page = Math.Clamp(_page, 0, pages - 1); _pageLabel.Text = $"{_page + 1} / {pages}";
        _previous.Disabled = _page == 0; _next.Disabled = _page + 1 >= pages;
        if (_noCodeErrors == null) { _noCodeErrors = Text(WorkshopCodeErrorText.Get(WorkshopCodeUi.Empty)); _issueRows.AddChild(_noCodeErrors); }
        _noCodeErrors.Text = WorkshopCodeErrorText.Get(WorkshopCodeUi.Empty);
        _noCodeErrors.Visible = problems.Length == 0;
        var visible = problems.Skip(_page * count).Take(count).ToArray();
        while (_errorPool.Count < visible.Length) _errorPool.Add(CreateCodeErrorRow());
        for (var i = 0; i < _errorPool.Count; i++)
        {
            var row = _errorPool[i]; row.Panel.Visible = i < visible.Length; row.Problem = null;
            row.Binding.Bind(i < visible.Length ? (ulong)(i + 1) : 0);
            if (i >= visible.Length) continue;
            var problem = visible[i]; row.Problem = problem; row.Sources = problem.Sources;
            row.Title.Text = (problem.Name.Length > 0 ? problem.Name : WorkshopCodeErrorText.Get(WorkshopCodeUi.Unknown)) +
                (problem.Id > 0 ? $"  ·  ID {problem.Id}" : $"  ·  {problem.Issues[0].Key}");
            row.Title.TooltipText = row.Title.Text;
            row.Reason.Text = string.Join(" · ", problem.Issues.Select(issue => $"[SCM-{issue.Error}] " + WorkshopCodeErrorText.Error(issue.Error)).Distinct());
            row.Group.Text = string.Join("\n", problem.Issues.Select(issue =>
                (issue.Group.Length > 0 ? WorkshopCodeErrorText.Get(WorkshopCodeUi.Group, issue.Group) : WorkshopCodeErrorText.Get(WorkshopCodeUi.Code, issue.Key)) +
                (issue.Total > 0 ? "  ·  " + WorkshopCodeErrorText.Get(WorkshopCodeUi.Parts, issue.Sources.Where(s => s.Part > 0).Select(s => s.Part).Distinct().Count(), issue.Total) : "") +
                (issue.Missing.Length > 0 ? "  ·  " + WorkshopCodeErrorText.Get(WorkshopCodeUi.Missing, string.Join(", ", issue.Missing)) : "")).Distinct());
            row.SourceIndex = 0;
            row.SourceChoice.SetOptions(Enumerable.Range(1, Math.Max(0, row.Sources.Length - 1)).Select(n => n.ToString()), "");
            row.Source.Disabled = row.Sources.Length <= 1;
            row.Locate.Disabled = row.Sources.Length == 0;
            row.Copy.Text = WorkshopCodeErrorText.Get(WorkshopCodeUi.CopyDetails);
            row.Panel.TooltipText = "";
        }
    }
    private CodeErrorRow CreateCodeErrorRow()
    {
        var row = new CodeErrorRow(); row.Panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _issueRows.AddChild(row.Panel); ModThemeRuntime.Panel(row.Panel);
        var margin = new MarginContainer(); foreach (var edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 12);
        row.Panel.AddChild(margin); var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 6); margin.AddChild(box);
        row.Title = Text("", 23, true); row.Title.ClipText = true; row.Title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; box.AddChild(row.Title);
        row.Reason = Text("", 18, true); row.Group = Text("", 16);
        foreach (var label in new[] { row.Reason, row.Group }) { label.AutowrapMode = TextServer.AutowrapMode.WordSmart; label.MaxLinesVisible = 3; label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; label.SizeFlagsHorizontal = SizeFlags.ExpandFill; box.AddChild(label); }
        var actions = new HBoxContainer(); actions.AddThemeConstantOverride("separation", 10); box.AddChild(actions);
        string SourceName(string value)
        {
            if (row.Sources.Length == 0) return "—";
            var source = row.Sources[int.TryParse(value, out var n) ? Math.Clamp(n, 0, row.Sources.Length - 1) : 0];
            return (source.Reply == 0 ? WorkshopCodeErrorText.Get(WorkshopCodeUi.MainPost) : WorkshopCodeErrorText.Get(WorkshopCodeUi.Source, source.Page, source.Reply)) +
                $" · {WorkshopSubmissionCodec.Fingerprint(source.Code)}";
        }
        row.SourceChoice = new(SourceName, value => row.SourceIndex = int.TryParse(value, out var n) ? n : 0, () => SourceName(""));
        row.Source = row.SourceChoice.Picker; actions.AddChild(row.Source);
        row.Locate = BoundButton(row.Binding, WorkshopCodeErrorText.Get(WorkshopCodeUi.Locate), () =>
        {
            if (row.Problem == null || row.Sources.Length == 0) return;
            try { WorkshopCommunityLinks.OpenSubmissionSource(row.Sources[Math.Clamp(row.SourceIndex, 0, row.Sources.Length - 1)].Url); }
            catch (Exception ex) { row.Panel.TooltipText = ex.Message; ModLog.Warn("投稿来源打开失败：" + ex.Message); }
        }); actions.AddChild(row.Locate);
        row.Copy = BoundButton(row.Binding, WorkshopCodeErrorText.Get(WorkshopCodeUi.CopyDetails), () =>
        {
            if (row.Problem == null) return;
            try { DisplayServer.ClipboardSet(WorkshopCodeDiagnostics.Report(row.Problem, ModLocalization.CurrentLanguage)); row.Copy.Text = WorkshopSubmissionText.Get(SubmissionText.Copied); }
            catch (Exception ex) { row.Panel.TooltipText = ex.Message; }
        }); actions.AddChild(row.Copy);
        return row;
    }
}
