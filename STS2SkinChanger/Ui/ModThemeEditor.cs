using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class ModThemeEditor : CanvasLayer
{
    private const string NodeName = "SkinChangerThemeEditor";
    private Control _root = null!;
    private PanelContainer _panel = null!;
    private VBoxContainer _body = null!;
    private ScrollContainer _scroll = null!;
    private Button _collapse = null!;
    private Label _status = null!;
    private readonly List<Action<ModThemeSettings>> _readValues = [];
    private bool _reading;
    private bool _collapsed;
    private bool _dragging;
    private Vector2 _dragOffset;

    public static ModThemeEditor Ensure(Node context)
    {
        var root = context.GetTree().Root;
        var editor = root.GetNodeOrNull<ModThemeEditor>(NodeName);
        if (editor != null) return editor;
        editor = new ModThemeEditor { Name = NodeName, Layer = 110, ProcessMode = ProcessModeEnum.Always };
        root.AddChild(editor);
        return editor;
    }

    public static void Toggle(Node context) => Ensure(context).Toggle();
    private void Toggle()
    {
        _panel.Visible = !_panel.Visible;
        _dragging = false;
        if (_panel.Visible) { ReadValues(); ClampPanel(); }
    }

    public override void _Ready()
    {
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        _panel = new PanelContainer { Position = new Vector2(36, 100), Size = new Vector2(540, 750),
            Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        // The editor deliberately stays readable if the draft is made invisible or low contrast.
        _panel.AddThemeStyleboxOverride("panel", FixedStyle(new Color(1, 1, 1, .94f)));
        _root.AddChild(_panel);
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 16);
        _panel.AddChild(margin);
        var content = new VBoxContainer(); content.AddThemeConstantOverride("separation", 12); margin.AddChild(content);
        var header = new HBoxContainer(); content.AddChild(header);
        var grip = MakeButton(() => ModThemeLocalization.Get(ThemeText.Editor));
        grip.Flat = true; grip.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        grip.MouseDefaultCursorShape = Control.CursorShape.Move;
        grip.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left } click) return;
            _dragging = click.Pressed;
            _dragOffset = _root.GetLocalMousePosition() - _panel.Position;
            grip.AcceptEvent();
        };
        header.AddChild(grip);
        _collapse = MakeButton(() => ModThemeLocalization.Get(_collapsed ? ThemeText.Expand : ThemeText.Collapse));
        _collapse.Pressed += () =>
        {
            _collapsed = !_collapsed; _body.Visible = !_collapsed;
            _collapse.Text = ModThemeLocalization.Get(_collapsed ? ThemeText.Expand : ThemeText.Collapse);
            _panel.Size = new Vector2(540, _collapsed ? 72 : Math.Min(750, _root.Size.Y - 40));
            ClampPanel();
            Callable.From(ClampPanel).CallDeferred();
        };
        header.AddChild(_collapse);
        var close = MakeButton(() => ModLocalization.Get(ModText.Close)); close.Pressed += Toggle; header.AddChild(close);
        _body = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 12); content.AddChild(_body);
        _scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _body.AddChild(_scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 8); _scroll.AddChild(rows);
        Section(rows, ThemeText.Panel);
        ColorRow(rows, ThemeText.Color, s => s.PanelColor, (s, v) => s with { PanelColor = v });
        NumberRow(rows, ThemeText.Opacity, 0, 100, 1, s => s.PanelOpacity * 100, (s, v) => s with { PanelOpacity = (float)v / 100 }, "%");
        NumberRow(rows, ThemeText.Blur, 0, 5, .1, s => s.PanelBlur, (s, v) => s with { PanelBlur = (float)v });
        Section(rows, ThemeText.Selection);
        ColorRow(rows, ThemeText.Color, s => s.SelectionColor, (s, v) => s with { SelectionColor = v });
        NumberRow(rows, ThemeText.Opacity, 0, 100, 1, s => s.SelectionOpacity * 100, (s, v) => s with { SelectionOpacity = (float)v / 100 }, "%");
        NumberRow(rows, ThemeText.Blur, 0, 5, .1, s => s.SelectionBlur, (s, v) => s with { SelectionBlur = (float)v });
        Section(rows, ThemeText.Buttons);
        ColorRow(rows, ThemeText.Color, s => s.ButtonColor, (s, v) => s with { ButtonColor = v });
        NumberRow(rows, ThemeText.Opacity, 0, 100, 1, s => s.ButtonOpacity * 100, (s, v) => s with { ButtonOpacity = (float)v / 100 }, "%");
        ColorRow(rows, ThemeText.HoverColor, s => s.HoverColor, (s, v) => s with { HoverColor = v });
        Section(rows, ThemeText.TextBorder);
        ColorRow(rows, ThemeText.TextColor, s => s.TextColor, (s, v) => s with { TextColor = v });
        ColorRow(rows, ThemeText.AccentColor, s => s.AccentColor, (s, v) => s with { AccentColor = v });
        ColorRow(rows, ThemeText.BorderColor, s => s.BorderColor, (s, v) => s with { BorderColor = v });
        NumberRow(rows, ThemeText.BorderWidth, 0, 5, 1, s => s.BorderWidth, (s, v) => s with { BorderWidth = (int)v });
        NumberRow(rows, ThemeText.Radius, 0, 24, 1, s => s.CornerRadius, (s, v) => s with { CornerRadius = (int)v });
        NumberRow(rows, ThemeText.FontScale, 75, 150, 1, s => s.FontScale * 100, (s, v) => s with { FontScale = (float)v / 100 }, "%");
        NumberRow(rows, ThemeText.Outline, 0, 8, 1, s => s.TextOutline, (s, v) => s with { TextOutline = (int)v });
        var footer = new HBoxContainer(); _body.AddChild(footer);
        var reset = MakeButton(() => ModLocalization.Get(ModText.Reset)); reset.Pressed += ModThemeRuntime.Session.Reset; footer.AddChild(reset);
        var revert = MakeButton(() => ModThemeLocalization.Get(ThemeText.Revert)); revert.Pressed += ModThemeRuntime.Session.Revert; footer.AddChild(revert);
        var save = MakeButton(() => ModLocalization.Get(ModText.SaveCharacterSkinMerge));
        save.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        save.Pressed += () =>
        {
            try { ModThemeRuntime.Session.Save(ModThemeRuntime.Path); _status.Text = ModThemeLocalization.Get(ThemeText.Saved); }
            catch (Exception e) { _status.Text = ModThemeLocalization.Get(ThemeText.SaveFailed); ModLog.Error("保存界面主题失败：" + e); }
        };
        footer.AddChild(save);
        _status = MakeLabel(() => ""); _body.AddChild(_status);
        ModThemeRuntime.Session.Changed += ReadValues;
        _root.Resized += ClampPanel;
        ReadValues();
    }

    public override void _ExitTree() => ModThemeRuntime.Session.Changed -= ReadValues;

    public override void _Input(InputEvent input)
    {
        if (input is InputEventKey { Pressed: true, Echo: false, ShiftPressed: true } key &&
            (key.CtrlPressed || key.MetaPressed) && (key.Keycode == Key.T || key.PhysicalKeycode == Key.T))
        { Toggle(); GetViewport().SetInputAsHandled(); return; }
        if (!_panel.Visible || !_dragging) return;
        if (input is InputEventMouseMotion)
        {
            if (!Input.IsMouseButtonPressed(MouseButton.Left) || !_root.GetWindow().HasFocus()) { _dragging = false; return; }
            _panel.Position = _root.GetLocalMousePosition() - _dragOffset; ClampPanel(); GetViewport().SetInputAsHandled();
        }
        else if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        { _dragging = false; } // Let the captured title button also receive its release.
    }

    private void ClampPanel()
    {
        if (_root.Size.X <= 0 || _root.Size.Y <= 0) return;
        _panel.Size = new Vector2(Math.Min(540, _root.Size.X - 24), _collapsed ? 72 : Math.Min(750, _root.Size.Y - 24));
        _panel.Position = new Vector2(Math.Clamp(_panel.Position.X, 12, Math.Max(12, _root.Size.X - _panel.Size.X - 12)),
            Math.Clamp(_panel.Position.Y, 12, Math.Max(12, _root.Size.Y - _panel.Size.Y - 12)));
    }

    private void ReadValues()
    {
        _reading = true;
        try { foreach (var read in _readValues) read(ModThemeRuntime.Current); if (_status != null) _status.Text = ""; }
        finally { _reading = false; }
    }

    private void Section(VBoxContainer rows, ThemeText title)
    {
        rows.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8), MouseFilter = Control.MouseFilterEnum.Ignore });
        var label = MakeLabel(() => ModThemeLocalization.Get(title)); label.AddThemeFontSizeOverride("font_size", 23); rows.AddChild(label);
    }

    private HBoxContainer Row(VBoxContainer rows, ThemeText title)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 38) }; row.AddThemeConstantOverride("separation", 10); rows.AddChild(row);
        var label = MakeLabel(() => ModThemeLocalization.Get(title));
        label.CustomMinimumSize = new Vector2(168, 0); label.ClipText = true; row.AddChild(label); return row;
    }

    private void ColorRow(VBoxContainer rows, ThemeText title, Func<ModThemeSettings, string> get,
        Func<ModThemeSettings, string, ModThemeSettings> set)
    {
        var row = Row(rows, title);
        var picker = new ColorPickerButton { EditAlpha = false, CustomMinimumSize = new Vector2(64, 32) }; row.AddChild(picker);
        var hex = MakeLabel(() => get(ModThemeRuntime.Current)); row.AddChild(hex);
        picker.ColorChanged += value => { if (!_reading) ModThemeRuntime.Session.Preview(set(ModThemeRuntime.Current, "#" + value.ToHtml(false))); };
        _readValues.Add(theme => { picker.Color = new Color(get(theme)); hex.Text = get(theme); });
    }

    private void NumberRow(VBoxContainer rows, ThemeText title, double min, double max, double step,
        Func<ModThemeSettings, double> get, Func<ModThemeSettings, double, ModThemeSettings> set, string suffix = "")
    {
        var row = Row(rows, title);
        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(120, 24), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        row.AddChild(slider);
        var number = new SpinBox { MinValue = min, MaxValue = max, Step = step, Suffix = suffix, CustomMinimumSize = new Vector2(102, 32) };
        row.AddChild(number);
        void Change(double value) { if (!_reading) ModThemeRuntime.Session.Preview(set(ModThemeRuntime.Current, value)); }
        slider.ValueChanged += Change; number.ValueChanged += Change;
        _readValues.Add(theme => { slider.SetValueNoSignal(get(theme)); number.SetValueNoSignal(get(theme)); });
    }

    private static Label MakeLabel(Func<string> text)
    {
        var label = new Label { VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", 18); label.AddThemeColorOverride("font_color", new Color("18222c"));
        if (ContextualSkinControls.GameFont is { } font) label.AddThemeFontOverride("font", font);
        ModLocalization.Bind(label, () => label.Text = text()); return label;
    }

    private static Button MakeButton(Func<string> text)
    {
        var button = new Button { CustomMinimumSize = new Vector2(62, 36), FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", 18); button.AddThemeColorOverride("font_color", new Color("18222c"));
        button.AddThemeColorOverride("font_hover_color", new Color("18222c"));
        button.AddThemeStyleboxOverride("normal", FixedStyle(new Color("e6ebef")));
        button.AddThemeStyleboxOverride("hover", FixedStyle(new Color("d1deea")));
        if (ContextualSkinControls.GameFont is { } font) button.AddThemeFontOverride("font", font);
        ModLocalization.Bind(button, () => button.Text = text()); return button;
    }

    private static StyleBoxFlat FixedStyle(Color color) => new()
    {
        BgColor = color, CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
        CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8, ContentMarginLeft = 8, ContentMarginRight = 8
    };
}

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
internal static class ModThemeEditorReadyPatch
{
    private static void Postfix(NMainMenu __instance) => Callable.From(() =>
    {
        if (!GodotObject.IsInstanceValid(__instance) || !__instance.IsInsideTree()) return;
        try { ModThemeEditor.Ensure(__instance); }
        catch (Exception e) { ModLog.Warn("创建主题调节入口失败：" + e.GetBaseException().Message); }
    }).CallDeferred();
}
