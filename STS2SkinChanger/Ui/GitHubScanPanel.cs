using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// The scan result lives in this window only: the codes are shown, copied and then forgotten,
// so a repository keeps the state the panel shows for it - not installed, not recognized -
// until its author publishes the code as sc.info in the repository itself.
internal partial class GitHubScanPanel : Control
{
    private CanvasLayer _layer = null!;
    private Window _window = null!;
    private Node _origin = null!;
    private Action _closedCallback = null!;
    private bool _closed;
    private bool Alive => !_closed && GodotObject.IsInstanceValid(this) && IsInsideTree();

    internal static GitHubScanPanel Show(Node origin, string title, string details, GitHubScanResult result, Action closed)
    {
        var layer = new CanvasLayer { Name = "SCGitHubScanLayer", Layer = 101, ProcessMode = ProcessModeEnum.Always };
        var screen = new GitHubScanPanel { _layer = layer, _origin = origin, _closedCallback = closed };
        try
        {
            origin.GetTree().Root.AddChild(layer); layer.AddChild(screen);
            screen.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); screen.Build(title, details, result); return screen;
        }
        catch { screen.Cleanup(); layer.QueueFree(); throw; }
    }

    private void Build(string title, string details, GitHubScanResult result)
    {
        MouseFilter = MouseFilterEnum.Stop; ProcessMode = ProcessModeEnum.Always;
        _window = GetWindow(); _window.WindowInput += OnInput; _origin.TreeExited += Close;
        TreeExiting += Cleanup;
        var modalTimer = new Godot.Timer { WaitTime = .1, Autostart = true, ProcessMode = ProcessModeEnum.Always };
        modalTimer.Timeout += () =>
        {
            if (Alive) _layer.Visible = MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer.Instance?.OpenModal == null;
        };
        AddChild(modalTimer);
        var mask = new ColorRect { Color = new Color(0, 0, 0, .5f), MouseFilter = MouseFilterEnum.Stop };
        AddChild(mask); mask.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var panel = new PanelContainer { AnchorLeft = .12f, AnchorRight = .88f, AnchorTop = .07f, AnchorBottom = .93f, MouseFilter = MouseFilterEnum.Stop };
        AddChild(panel); ModThemeRuntime.Panel(panel);
        var margin = new MarginContainer(); foreach (var edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 16);
        panel.AddChild(margin);
        var content = new VBoxContainer(); content.AddThemeConstantOverride("separation", 12); margin.AddChild(content);
        var heading = new Label { Text = title, MouseFilter = MouseFilterEnum.Ignore };
        if (ContextualSkinControls.GameFont is { } font) heading.AddThemeFontOverride("font", font);
        ModThemeRuntime.TextControl(heading, 27, true); content.AddChild(heading);
        var status = new Label { MouseFilter = MouseFilterEnum.Ignore, ClipText = true };
        status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        if (ContextualSkinControls.GameFont is { } statusFont) status.AddThemeFontOverride("font", statusFont);
        ModThemeRuntime.TextControl(status, 20); content.AddChild(status);
        var hint = new Label { MouseFilter = MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        if (ContextualSkinControls.GameFont is { } hintFont) hint.AddThemeFontOverride("font", hintFont);
        ModThemeRuntime.TextControl(hint, 18); content.AddChild(hint);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        content.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6); scroll.AddChild(list);
        var footer = new HBoxContainer(); footer.AddThemeConstantOverride("separation", 10); content.AddChild(footer);
        footer.AddChild(Button(ModLocalization.Get(ModText.Close), Close));
        footer.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        footer.AddChild(Button(T(GitHubSkinTextKey.OpenRepo), () => OpenRepo(title)));
        if (result.Error.Length > 0)
        {
            status.Text = T(GitHubSkinTextKey.ScanFailed);
            status.TooltipText = result.Error;
            hint.Hide();
            return;
        }
        status.Text = GitHubSkinText.Format(ModLocalization.CurrentLanguage, GitHubSkinTextKey.ScanResult, details, result.Codes.Length);
        status.TooltipText = details;
        hint.Text = T(GitHubSkinTextKey.ScanHint);
        for (var index = 0; index < result.Codes.Length; index++)
        {
            var code = result.Codes[index];
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 10); list.AddChild(row);
            var number = new Label { Text = (index + 1).ToString(), VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
            if (ContextualSkinControls.GameFont is { } numberFont) number.AddThemeFontOverride("font", numberFont);
            ModThemeRuntime.TextControl(number, 20, true); row.AddChild(number);
            var field = new LineEdit { Text = code, Editable = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            ModThemeRuntime.Input(field, 20); row.AddChild(field);
            Button? copy = null;
            copy = Button(WorkshopSubmissionText.Get(SubmissionText.Copy), () => Copy(copy!, code));
            row.AddChild(copy);
        }
    }

    private static void OpenRepo(string fullName)
    {
        if (!GitHubSkinPolicy.TryParseFullName(fullName, out var repo)) return;
        OS.ShellOpen(repo.Url);
    }

    private void Copy(Button button, string code)
    {
        try
        {
            DisplayServer.ClipboardSet(code);
            button.Text = WorkshopSubmissionText.Get(SubmissionText.Copied);
            ModThemeRuntime.TextControl(button, 20, true);
            GetTree().CreateTimer(1, true, false, true).Timeout += () =>
            {
                if (!Alive || !GodotObject.IsInstanceValid(button) || button.IsQueuedForDeletion()) return;
                button.Text = WorkshopSubmissionText.Get(SubmissionText.Copy);
                ModThemeRuntime.TextControl(button, 20);
            };
        }
        catch (Exception ex) { ModLog.Warn("复制信息码失败：" + ex.Message); }
    }

    private void OnInput(InputEvent ev)
    {
        if (Alive && MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer.Instance?.OpenModal == null &&
            ev.IsActionPressed("ui_cancel") && !ev.IsEcho()) { GetViewport().SetInputAsHandled(); Close(); }
    }

    internal void Close()
    { if (_closed) return; _closed = true; _layer.Hide(); _layer.QueueFree(); _closedCallback(); }

    private void Cleanup()
    {
        _closed = true;
        if (GodotObject.IsInstanceValid(_window)) _window.WindowInput -= OnInput;
        if (GodotObject.IsInstanceValid(_origin)) _origin.TreeExited -= Close;
    }

    private static string T(GitHubSkinTextKey key) => GitHubSkinText.ForLanguage(ModLocalization.CurrentLanguage, key);

    private static Button Button(string text, Action pressed)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(100, 42), MouseDefaultCursorShape = CursorShape.PointingHand };
        ContextualSkinControls.ApplyGameTheme(button); button.Pressed += pressed; return button;
    }
}
