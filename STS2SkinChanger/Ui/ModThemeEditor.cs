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
    private Button _save = null!;
    private readonly ThemeSaveFeedback _saveFeedback = new();
    private Label _status = null!;
    private readonly List<Action<ModThemeSettings>> _readValues = [];
    private bool _reading;
    private bool _collapsed;
    private bool _dragging;
    private Vector2 _dragOffset;
    private bool _initialized;
    private bool _connected;
    private Window? _window;

    public ModThemeEditor()
    {
        // This DLL has no Godot-generated virtual-method bridge. Use native signals for
        // re-entry/cleanup, and initialize explicitly before the first toggle.
        TreeEntered += Connect;
        TreeExiting += Disconnect;
    }

    public static ModThemeEditor Ensure(Node context)
    {
        var root = context.GetTree().Root;
        var editor = root.GetNodeOrNull<ModThemeEditor>(NodeName);
        if (editor == null)
        {
            editor = new ModThemeEditor { Name = NodeName, Layer = 110, ProcessMode = ProcessModeEnum.Always };
            root.AddChild(editor);
        }
        try
        {
            editor.Initialize();
            return editor;
        }
        catch
        {
            // Do not leave a half-built singleton behind after a failed construction.
            root.RemoveChild(editor);
            editor.QueueFree();
            throw;
        }
    }

    private void Initialize()
    {
        if (!_initialized)
        {
            BuildUi();
            _initialized = true;
        }
        Connect();
    }

    public static void Toggle(Node context) => Ensure(context).Toggle();
    private void Toggle()
    {
        _panel.Visible = !_panel.Visible;
        _dragging = false;
        CancelSaveFeedback();
        CancelPresetDelete();
        if (_panel.Visible) { ReadValues(); ClampPanel(); }
        ModLog.Info($"主题调节窗口：visible={_panel.Visible}, size={_panel.Size}, position={_panel.Position}");
    }

    private void BuildUi()
    {
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        _panel = new PanelContainer { Position = new Vector2(36, 100), Size = new Vector2(540, 750),
            Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        ModThemeRuntime.Panel(_panel);
        _root.AddChild(_panel);
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 16);
        _panel.AddChild(margin);
        var content = new VBoxContainer(); content.AddThemeConstantOverride("separation", 12); margin.AddChild(content);
        var header = new HBoxContainer(); content.AddChild(header);
        var grip = MakeButton(() => ModThemeLocalization.Get(ThemeText.Theme));
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
        BuildPresetSection(rows);
        var section = Section(rows, ThemeText.Panel);
        ColorRow(section, ThemeText.Color, s => s.PanelColor, (s, v) => s with { PanelColor = v });
        NumberRow(section, ThemeText.Opacity, 0, 100, 1, s => s.PanelOpacity * 100, (s, v) => s with { PanelOpacity = (float)v / 100 }, "%");
        NumberRow(section, ThemeText.Blur, 0, 5, .1, s => s.PanelBlur, (s, v) => s with { PanelBlur = (float)v });
        section = Section(rows, ThemeText.Selection);
        ColorRow(section, ThemeText.Color, s => s.SelectionColor, (s, v) => s with { SelectionColor = v });
        NumberRow(section, ThemeText.Opacity, 0, 100, 1, s => s.SelectionOpacity * 100, (s, v) => s with { SelectionOpacity = (float)v / 100 }, "%");
        NumberRow(section, ThemeText.Blur, 0, 5, .1, s => s.SelectionBlur, (s, v) => s with { SelectionBlur = (float)v });
        ColorRow(section, ThemeText.HoverColor, s => s.SelectionHoverColor, (s, v) => s with { SelectionHoverColor = v });
        NumberRow(section, ThemeText.HoverOpacity, 0, 100, 1, s => s.SelectionHoverOpacity * 100, (s, v) => s with { SelectionHoverOpacity = (float)v / 100 }, "%");
        NumberRow(section, ThemeText.HoverBlur, 0, 5, .1, s => s.SelectionHoverBlur, (s, v) => s with { SelectionHoverBlur = (float)v });
        section = Section(rows, ThemeText.Buttons);
        ColorRow(section, ThemeText.Color, s => s.ButtonColor, (s, v) => s with { ButtonColor = v });
        NumberRow(section, ThemeText.Opacity, 0, 100, 1, s => s.ButtonOpacity * 100, (s, v) => s with { ButtonOpacity = (float)v / 100 }, "%");
        NumberRow(section, ThemeText.Blur, 0, 5, .1, s => s.ButtonBlur, (s, v) => s with { ButtonBlur = (float)v });
        ColorRow(section, ThemeText.HoverColor, s => s.HoverColor, (s, v) => s with { HoverColor = v });
        section = Section(rows, ThemeText.Dropdown);
        ColorRow(section, ThemeText.Color, s => s.DropdownColor, (s, v) => s with { DropdownColor = v });
        NumberRow(section, ThemeText.Opacity, 0, 100, 1, s => s.DropdownOpacity * 100, (s, v) => s with { DropdownOpacity = (float)v / 100 }, "%");
        NumberRow(section, ThemeText.Blur, 0, 5, .1, s => s.DropdownBlur, (s, v) => s with { DropdownBlur = (float)v });
        ColorRow(section, ThemeText.HoverColor, s => s.DropdownHoverColor, (s, v) => s with { DropdownHoverColor = v });
        NumberRow(section, ThemeText.HoverOpacity, 0, 100, 1, s => s.DropdownHoverOpacity * 100, (s, v) => s with { DropdownHoverOpacity = (float)v / 100 }, "%");
        ColorRow(section, ThemeText.SelectionColor, s => s.DropdownSelectionColor, (s, v) => s with { DropdownSelectionColor = v });
        NumberRow(section, ThemeText.SelectionOpacity, 0, 100, 1, s => s.DropdownSelectionOpacity * 100, (s, v) => s with { DropdownSelectionOpacity = (float)v / 100 }, "%");
        ColorRow(section, ThemeText.SelectedHoverColor, s => s.DropdownSelectionHoverColor, (s, v) => s with { DropdownSelectionHoverColor = v });
        NumberRow(section, ThemeText.SelectedHoverOpacity, 0, 100, 1, s => s.DropdownSelectionHoverOpacity * 100, (s, v) => s with { DropdownSelectionHoverOpacity = (float)v / 100 }, "%");
        ColorRow(section, ThemeText.BorderColor, s => s.DropdownBorderColor, (s, v) => s with { DropdownBorderColor = v });
        NumberRow(section, ThemeText.BorderWidth, 0, 5, 1, s => s.DropdownBorderWidth, (s, v) => s with { DropdownBorderWidth = (int)v });
        NumberRow(section, ThemeText.Radius, 0, 24, 1, s => s.DropdownCornerRadius, (s, v) => s with { DropdownCornerRadius = (int)v });
        section = Section(rows, ThemeText.TextBorder);
        ColorRow(section, ThemeText.TextColor, s => s.TextColor, (s, v) => s with { TextColor = v });
        ColorRow(section, ThemeText.AccentColor, s => s.AccentColor, (s, v) => s with { AccentColor = v });
        ColorRow(section, ThemeText.BorderColor, s => s.BorderColor, (s, v) => s with { BorderColor = v });
        NumberRow(section, ThemeText.BorderWidth, 0, 5, 1, s => s.BorderWidth, (s, v) => s with { BorderWidth = (int)v });
        NumberRow(section, ThemeText.Radius, 0, 24, 1, s => s.CornerRadius, (s, v) => s with { CornerRadius = (int)v });
        NumberRow(section, ThemeText.FontScale, 75, 150, 1, s => s.FontScale * 100, (s, v) => s with { FontScale = (float)v / 100 }, "%");
        NumberRow(section, ThemeText.Outline, 0, 8, 1, s => s.TextOutline, (s, v) => s with { TextOutline = (int)v });
        section = Section(rows, ThemeText.Shadow);
        var shadow = new CheckButton();
        ModLocalization.Bind(shadow, () => shadow.Text = ModThemeLocalization.Get(ThemeText.EnableShadow));
        ModThemeRuntime.Button(shadow, 18);
        if (ContextualSkinControls.GameFont is { } shadowFont) shadow.AddThemeFontOverride("font", shadowFont);
        shadow.Toggled += enabled => { if (!_reading) ModThemeRuntime.Session.Preview(ModThemeRuntime.Current with { TextShadowEnabled = enabled }); };
        _readValues.Add(theme => shadow.SetPressedNoSignal(theme.TextShadowEnabled));
        section.AddChild(shadow);
        ColorRow(section, ThemeText.Color, s => s.TextShadowColor, (s, v) => s with { TextShadowColor = v });
        NumberRow(section, ThemeText.Opacity, 0, 100, 1, s => s.TextShadowOpacity * 100, (s, v) => s with { TextShadowOpacity = (float)v / 100 }, "%");
        NumberRow(section, ThemeText.OffsetX, -12, 12, 1, s => s.TextShadowOffsetX, (s, v) => s with { TextShadowOffsetX = (int)v });
        NumberRow(section, ThemeText.OffsetY, -12, 12, 1, s => s.TextShadowOffsetY, (s, v) => s with { TextShadowOffsetY = (int)v });
        NumberRow(section, ThemeText.ShadowSize, 0, 8, 1, s => s.TextShadowSize, (s, v) => s with { TextShadowSize = (int)v });
        var footer = new HBoxContainer(); _body.AddChild(footer);
        var reset = MakeButton(() => ModLocalization.Get(ModText.Reset)); reset.Pressed += ModThemeRuntime.Session.Reset; footer.AddChild(reset);
        var revert = MakeButton(() => ModThemeLocalization.Get(ThemeText.Revert)); revert.Pressed += ModThemeRuntime.Session.Revert; footer.AddChild(revert);
        _save = MakeButton(SaveCaption);
        _save.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _save.Pressed += SaveTheme;
        footer.AddChild(_save);
        _status = MakeLabel(() => "");
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.Hide(); _body.AddChild(_status);
        _root.Resized += ClampPanel;
        ReadValues();
    }

    private void Connect()
    {
        if (!_initialized || _connected || !IsInsideTree()) return;
        _window = GetWindow();
        _window.WindowInput += HandleInput;
        ModThemeRuntime.Session.Changed += ReadValues;
        _connected = true;
        ReadValues();
    }

    private void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
        _dragging = false;
        _saveFeedback.Cancel();
        CancelPresetDelete();
        ModThemeRuntime.Session.Changed -= ReadValues;
        if (GodotObject.IsInstanceValid(_window)) _window!.WindowInput -= HandleInput;
        _window = null;
    }

    private void HandleInput(InputEvent input)
    {
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
        try
        {
            CancelSaveFeedback();
            foreach (var read in _readValues) read(ModThemeRuntime.Current);
            if (_status != null) { _status.Text = ""; _status.Hide(); }
        }
        finally { _reading = false; }
    }

    private string SaveCaption() => _saveFeedback.Active
        ? ModThemeLocalization.Get(ThemeText.Saved) + "✓" : ModLocalization.Get(ModText.SaveCharacterSkinMerge);

    private void RefreshSaveButton()
    {
        if (!GodotObject.IsInstanceValid(_save)) return;
        _save.Text = SaveCaption();
        ModThemeRuntime.TextControl(_save, 18, accent: _saveFeedback.Active);
    }

    private void CancelSaveFeedback()
    {
        _saveFeedback.Cancel();
        RefreshSaveButton();
    }

    private void SaveTheme()
    {
        CancelSaveFeedback();
        try
        {
            ModThemeRuntime.Session.Save(ModThemeRuntime.Path);
            _status.Hide();
            var revision = _saveFeedback.Begin();
            RefreshSaveButton();
            // Real time, including paused gameplay; old saves cannot expire a newer one.
            GetTree().CreateTimer(ThemeSaveFeedback.DurationSeconds, true, false, true).Timeout += () =>
            {
                if (!GodotObject.IsInstanceValid(this) || !IsInsideTree() || IsQueuedForDeletion()) return;
                if (_saveFeedback.Expire(revision)) RefreshSaveButton();
            };
        }
        catch (Exception e) { ShowThemeError(e); }
    }

    private void ShowThemeError(Exception error)
    {
        CancelSaveFeedback();
        _status.Text = ModThemeLocalization.Get(error is ArgumentException ? ThemeText.InvalidPresetName : ThemeText.SaveFailed);
        _status.Show();
        ModLog.Error("主题操作失败：" + error);
    }

    private VBoxContainer Section(VBoxContainer rows, ThemeText title)
    {
        var group = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        group.AddThemeConstantOverride("separation", 8);
        rows.AddChild(group);
        var expanded = false;
        var header = MakeButton(() => (expanded ? "▼ " : "▶ ") + ModThemeLocalization.Get(title));
        header.ToggleMode = true;
        header.Alignment = HorizontalAlignment.Left;
        header.ClipText = true;
        header.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        header.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        group.AddChild(header);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 8);
        body.Hide();
        group.AddChild(body);
        header.Toggled += value =>
        {
            expanded = value;
            body.Visible = value;
            header.Text = (expanded ? "▼ " : "▶ ") + ModThemeLocalization.Get(title);
            Callable.From(ClampPanel).CallDeferred();
        };
        return body;
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
        ModThemeRuntime.Button(picker, 18);
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
        ModThemeRuntime.Input(number.GetLineEdit(), 18);
        ThemeSlider(slider);
        void Change(double value) { if (!_reading) ModThemeRuntime.Session.Preview(set(ModThemeRuntime.Current, value)); }
        slider.ValueChanged += Change; number.ValueChanged += Change;
        _readValues.Add(theme => { slider.SetValueNoSignal(get(theme)); number.SetValueNoSignal(get(theme)); });
    }

    private static Label MakeLabel(Func<string> text)
    {
        var label = new Label { VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        ModThemeRuntime.TextControl(label, 18);
        if (ContextualSkinControls.GameFont is { } font) label.AddThemeFontOverride("font", font);
        ModLocalization.Bind(label, () => label.Text = text()); return label;
    }

    private static Button MakeButton(Func<string> text)
    {
        var button = new Button { CustomMinimumSize = new Vector2(62, 36), FocusMode = Control.FocusModeEnum.None };
        ModThemeRuntime.Button(button, 18);
        if (ContextualSkinControls.GameFont is { } font) button.AddThemeFontOverride("font", font);
        ModLocalization.Bind(button, () => button.Text = text()); return button;
    }

    private static void ThemeSlider(HSlider slider)
    {
        var track = new StyleBoxFlat { ContentMarginTop = 2, ContentMarginBottom = 2 };
        var fill = new StyleBoxFlat { ContentMarginTop = 2, ContentMarginBottom = 2 };
        var hover = new StyleBoxFlat { ContentMarginTop = 2, ContentMarginBottom = 2 };
        slider.AddThemeStyleboxOverride("slider", track);
        slider.AddThemeStyleboxOverride("grabber_area", fill);
        slider.AddThemeStyleboxOverride("grabber_area_highlight", hover);
        ModThemeRuntime.Bind(slider, "slider", theme =>
        {
            track.BgColor = ModThemeRuntime.Tint(theme.ButtonColor, theme.ButtonOpacity);
            fill.BgColor = new Color(theme.AccentColor);
            hover.BgColor = new Color(theme.HoverColor);
        });
    }
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
