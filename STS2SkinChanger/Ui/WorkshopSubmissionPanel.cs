using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class WorkshopSubmissionPanel : Control
{
    private CanvasLayer _layer = null!;
    private Window _window = null!;
    private Node _origin = null!;
    private Action _closedCallback = null!;
    private readonly CancellationTokenSource _cancel = new();
    private readonly HashSet<ulong> _selected = [];
    private readonly Dictionary<ulong, Button> _choices = [];
    private WorkshopLocalSubmission[] _candidates = [];
    private VBoxContainer _list = null!;
    private HBoxContainer _tools = null!;
    private Button _scan = null!;
    private Button _back = null!;
    private Label _status = null!;
    private LineEdit _search = null!;
    private bool _busy;
    private bool _closed;
    private int _candidateRevision = -1;
    private bool Alive => !_closed && GodotObject.IsInstanceValid(this) && IsInsideTree();

    internal static WorkshopSubmissionPanel Show(Node origin, Action closed)
    {
        var layer = new CanvasLayer { Name = "SCWorkshopSubmissionLayer", Layer = 101, ProcessMode = ProcessModeEnum.Always };
        var screen = new WorkshopSubmissionPanel { _layer = layer, _origin = origin, _closedCallback = closed };
        try
        {
            origin.GetTree().Root.AddChild(layer); layer.AddChild(screen);
            screen.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); screen.Build(); return screen;
        }
        catch { screen.Cleanup(); layer.QueueFree(); throw; }
    }
    private void Build()
    {
        MouseFilter = MouseFilterEnum.Stop; ProcessMode = ProcessModeEnum.Always;
        _window = GetWindow(); _window.WindowInput += OnInput; _origin.TreeExited += Close;
        TreeExiting += Cleanup;
        var modalTimer = new Godot.Timer { WaitTime = .1, Autostart = true, ProcessMode = ProcessModeEnum.Always };
        modalTimer.Timeout += () =>
        {
            if (!Alive) return;
            _layer.Visible = MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer.Instance?.OpenModal == null;
            if (!_busy && _candidateRevision >= 0 && !_back.Visible &&
                _candidateRevision != SkinWorkshopService.CommunityRevision) ShowChoices();
        };
        AddChild(modalTimer);
        var mask = new ColorRect { Color = new Color(0, 0, 0, .5f), MouseFilter = MouseFilterEnum.Stop };
        AddChild(mask); mask.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var panel = new PanelContainer { AnchorLeft = .12f, AnchorRight = .88f, AnchorTop = .07f, AnchorBottom = .93f, MouseFilter = MouseFilterEnum.Stop };
        AddChild(panel); ModThemeRuntime.Panel(panel);
        var margin = new MarginContainer(); foreach (var edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 16);
        panel.AddChild(margin);
        var content = new VBoxContainer(); content.AddThemeConstantOverride("separation", 12); margin.AddChild(content);
        content.AddChild(Text(WorkshopCommunityText.Get(WorkshopCommunityTextKey.SubmitMod), 27, true));
        _tools = new HBoxContainer(); content.AddChild(_tools);
        _search = new LineEdit { PlaceholderText = T(SubmissionText.Search), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ModThemeRuntime.Input(_search, 20); _tools.AddChild(_search); _search.TextChanged += _ => Filter();
        _tools.AddChild(Button(T(SubmissionText.All), () => SelectVisible(true)));
        _tools.AddChild(Button(T(SubmissionText.Clear), () => SelectVisible(false)));
        _status = Text(T(SubmissionText.Reading)); _status.ClipText = true; _status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; content.AddChild(_status);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        content.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; _list.AddThemeConstantOverride("separation", 6); scroll.AddChild(_list);
        var footer = new HBoxContainer(); footer.AddThemeConstantOverride("separation", 10); content.AddChild(footer);
        footer.AddChild(Button(ModLocalization.Get(ModText.Close), Close));
        _back = Button(T(SubmissionText.Back), ShowChoices); _back.Visible = false; footer.AddChild(_back);
        footer.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        footer.AddChild(Button(T(SubmissionText.Open), () => { try { WorkshopCommunityLinks.OpenDiscussion(false); } catch (Exception ex) { Error(ex); } }));
        _scan = Button(T(SubmissionText.Scan), () => _ = Scan()); _scan.Disabled = true; footer.AddChild(_scan);
        _ = LoadCandidates();
    }
    private async Task LoadCandidates()
    {
        try { _candidates = await SkinWorkshopService.SubmissionCandidates(_cancel.Token); if (Alive) ShowChoices(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Alive) Error(ex); }
    }
    private void ClearRows()
    {
        foreach (var child in _list.GetChildren()) { _list.RemoveChild(child); child.QueueFree(); }
        _choices.Clear();
    }
    private void ShowChoices()
    {
        if (!Alive || _busy) return;
        _candidateRevision = SkinWorkshopService.CommunityRevision;
        _candidates = SkinWorkshopService.FilterSubmissionCandidates(_candidates);
        _selected.IntersectWith(_candidates.Select(item => item.Id));
        ClearRows(); _tools.Show(); _back.Hide(); _scan.Show();
        foreach (var local in _candidates)
        {
            var choice = Button(local.Name, () => { }); choice.ToggleMode = true; choice.Alignment = HorizontalAlignment.Left;
            choice.ClipText = true; choice.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            choice.TooltipText = local.Name; choice.ButtonPressed = _selected.Contains(local.Id);
            choice.Text = (choice.ButtonPressed ? "☑ " : "☐ ") + local.Name;
            choice.Toggled += pressed => { if (pressed) _selected.Add(local.Id); else _selected.Remove(local.Id); choice.Text = (pressed ? "☑ " : "☐ ") + local.Name; UpdateSelection(); };
            _choices[local.Id] = choice; _list.AddChild(choice);
        }
        Filter(); UpdateSelection();
    }
    private void Filter()
    {
        foreach (var item in _candidates) if (_choices.TryGetValue(item.Id, out var button))
                button.Visible = _search.Text.Length == 0 || item.Name.Contains(_search.Text, StringComparison.CurrentCultureIgnoreCase) || item.Id.ToString().Contains(_search.Text);
    }
    private void SelectVisible(bool selected)
    { if (_busy) return; foreach (var button in _choices.Values.Where(b => b.Visible)) button.ButtonPressed = selected; }
    private void UpdateSelection()
    {
        if (_busy) return;
        _status.Text = _candidates.Length == 0 ? T(SubmissionText.Empty) : T(SubmissionText.Select) + $"  {_selected.Count}/{_candidates.Length}";
        _scan.Disabled = _selected.Count == 0;
    }
    private async Task Scan()
    {
        if (_busy || _selected.Count == 0) return;
        _busy = true; _scan.Disabled = true;
        foreach (var choice in _choices.Values) choice.Disabled = true;
        var progress = new Progress<(int Current, int Total, string Name)>(p =>
        { if (Alive && _busy) _status.Text = T(SubmissionText.Progress, p.Current, p.Total, p.Name); });
        try
        {
            var result = await SkinWorkshopService.ScanSubmissions(_candidates.Where(c => _selected.Contains(c.Id)).ToArray(), progress, _cancel.Token);
            if (!Alive) return;
            ClearRows(); _tools.Hide(); _scan.Hide(); _back.Show();
            _status.Text = T(SubmissionText.Result, result.Accepted, result.Rejected, result.Codes.Length);
            for (var index = 0; index < result.Codes.Length; index++)
            {
                var code = result.Codes[index]; var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 10); _list.AddChild(row);
                row.AddChild(Text((index + 1).ToString(), 20, true));
                var field = new LineEdit { Text = code, Editable = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                ModThemeRuntime.Input(field, 20); row.AddChild(field);
                Button? copy = null; copy = Button(T(SubmissionText.Copy), () => Copy(copy!, code)); row.AddChild(copy);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Alive) Error(ex); }
        finally
        {
            _busy = false;
            if (Alive) { _scan.Disabled = _selected.Count == 0; foreach (var choice in _choices.Values) choice.Disabled = false; }
        }
    }
    private void Copy(Button button, string code)
    {
        try
        {
            DisplayServer.ClipboardSet(code); button.Text = T(SubmissionText.Copied); ModThemeRuntime.TextControl(button, 20, true);
            GetTree().CreateTimer(1, true, false, true).Timeout += () =>
            { if (Alive && GodotObject.IsInstanceValid(button) && !button.IsQueuedForDeletion()) { button.Text = T(SubmissionText.Copy); ModThemeRuntime.TextControl(button, 20); } };
        }
        catch (Exception ex) { Error(ex); }
    }
    private void OnInput(InputEvent ev)
    {
        if (Alive && MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer.Instance?.OpenModal == null &&
            ev.IsActionPressed("ui_cancel") && !ev.IsEcho()) { GetViewport().SetInputAsHandled(); Close(); }
    }
    internal void Close()
    { if (_closed) return; _closed = true; _cancel.Cancel(); _layer.Hide(); _layer.QueueFree(); _closedCallback(); }
    private void Cleanup()
    {
        _closed = true; _cancel.Cancel();
        if (GodotObject.IsInstanceValid(_window)) _window.WindowInput -= OnInput;
        if (GodotObject.IsInstanceValid(_origin)) _origin.TreeExited -= Close;
    }
    private void Error(Exception ex)
    { _status.Text = WorkshopText.Get(WorkshopTextKey.Failed); _status.TooltipText = ex.GetBaseException().Message; ModLog.Warn("工坊投稿：" + ex); }
    private static string T(SubmissionText key, params object[] values) => WorkshopSubmissionText.Get(key, values);
    private static Label Text(string text, int size = 20, bool accent = false)
    {
        var label = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
        if (ContextualSkinControls.GameFont is { } font) label.AddThemeFontOverride("font", font);
        ModThemeRuntime.TextControl(label, size, accent); return label;
    }
    private static Button Button(string text, Action pressed)
    { var button = new Button { Text = text, CustomMinimumSize = new Vector2(100, 42), MouseDefaultCursorShape = CursorShape.PointingHand }; ContextualSkinControls.ApplyGameTheme(button); button.Pressed += pressed; return button; }
}
