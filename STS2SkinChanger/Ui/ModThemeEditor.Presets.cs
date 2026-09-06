using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class ModThemeEditor
{
    private ModThemePresets? _presets;
    private PanelContainer? _presetPanel;
    private VBoxContainer _presetContent = null!;
    private LineEdit _presetNewName = null!;
    private Label _presetStatus = null!;
    private readonly Dictionary<string, string> _presetNameDrafts = [];
    private readonly List<Action> _presetStates = [];
    private string _activePresetId = ModThemePresets.DefaultId;
    private string? _deletePresetId;
    private bool _positioningPresets;

    private void TogglePresetPanel()
    {
        if (_presetPanel?.Visible == true) { HidePresetPanel(); return; }
        try
        {
            if (_presetPanel == null) BuildPresetPanel();
            BuildPresetRows();
            _presetPanel!.Show();
            _presetPanel.MoveToFront();
            PositionPresetPanel();
            Callable.From(PositionPresetPanel).CallDeferred();
        }
        catch (Exception e)
        {
            _panel.ItemRectChanged -= PositionPresetPanel;
            _presetStates.Clear();
            if (GodotObject.IsInstanceValid(_presetPanel))
            {
                _presetPanel!.GetParent()?.RemoveChild(_presetPanel);
                _presetPanel.QueueFree();
            }
            _presetPanel = null;
            _presetNewName = null!;
            ShowThemeError(e);
        }
    }

    private void BuildPresetPanel()
    {
        _presets = new ModThemePresets(System.IO.Path.Combine(OS.GetUserDataDir(), "skin_changer_theme_presets.json"));
        _presetPanel = new PanelContainer { Name = "ThemePresets", Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop };
        ModThemeRuntime.Panel(_presetPanel);
        _root.AddChild(_presetPanel); // Sibling, never a child of the theme's scroll/container.
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right" }) margin.AddThemeConstantOverride("margin_" + side, 20);
        foreach (var side in new[] { "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 18);
        _presetPanel.AddChild(margin);
        _presetContent = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _presetContent.AddThemeConstantOverride("separation", 10);
        margin.AddChild(_presetContent);
        _panel.ItemRectChanged += PositionPresetPanel;
        _presetPanel.Resized += PositionPresetPanel;
    }

    private void HidePresetPanel()
    {
        if (GodotObject.IsInstanceValid(_presetPanel)) _presetPanel!.Hide();
        CancelPresetDelete();
    }

    private void PositionPresetPanel()
    {
        if (_positioningPresets || !GodotObject.IsInstanceValid(_presetPanel) || !_presetPanel!.Visible ||
            !GodotObject.IsInstanceValid(_root) || _root.Size.X <= 0 || _root.Size.Y <= 0) return;
        _positioningPresets = true;
        try
        {
            var (themePosition, preset) = ThemePresetPanelLayout.Place(_root.Size,
                new Rect2(_panel.Position, _panel.Size), new Vector2(820, Math.Max(480, _presetPanel.GetCombinedMinimumSize().Y)));
            _panel.Position = themePosition;
            _presetPanel.Size = preset.Size;
            _presetPanel.Position = preset.Position;
        }
        finally { _positioningPresets = false; }
    }

    private void BuildPresetRows()
    {
        var newNameDraft = GodotObject.IsInstanceValid(_presetNewName) ? _presetNewName.Text : "";
        _presetStates.Clear();
        _deletePresetId = null;
        // Same retained-scroll pattern as the card presets. The header/new-name row
        // and close button never scroll; CRUD does not reset the list to its top.
        var scroll = ScrollListRebuild.Begin(_presetContent, "theme-presets");
        var title = MakeLabel(() => ModThemeLocalization.Get(ThemeText.Presets));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        ModThemeRuntime.TextControl(title, 25, accent: true);
        _presetContent.AddChild(title);

        var createRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        createRow.AddThemeConstantOverride("separation", 10);
        _presetContent.AddChild(createRow);
        _presetNewName = new LineEdit { Text = newNameDraft, MaxLength = 100,
            CustomMinimumSize = new Vector2(0, 40), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        ModThemeRuntime.Input(_presetNewName, 18);
        ModLocalization.Bind(_presetNewName, () => _presetNewName.PlaceholderText = ModLocalization.Get(ModText.CardPresetName));
        createRow.AddChild(_presetNewName);
        var save = PresetButton(() => ModLocalization.Get(ModText.SaveCurrentPreset), 138);
        createRow.AddChild(save);
        save.Pressed += () =>
        {
            var requestedName = _presetNewName.Text;
            QueueThemePresetAction(() =>
            {
                _activePresetId = _presets!.Create(requestedName, ModThemeRuntime.Current);
                if (_presetNewName.Text == requestedName) _presetNewName.Text = "";
            });
        };

        scroll.CustomMinimumSize = new Vector2(0, 96);
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Auto;
        ScrollListRebuild.PlaceAfterHeader(scroll);
        var rows = new VBoxContainer { CustomMinimumSize = new Vector2(744, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 7);
        scroll.AddChild(rows);
        foreach (var preset in _presets!.Presets) BuildPresetRow(rows, preset);

        _presetStatus = MakeLabel(() => "");
        _presetStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _presetStatus.Hide();
        _presetContent.AddChild(_presetStatus);
        var close = PresetButton(() => ModLocalization.Get(ModText.Close), 180);
        close.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        close.Pressed += HidePresetPanel;
        _presetContent.AddChild(close);
        RefreshPresetStates();
        Callable.From(PositionPresetPanel).CallDeferred();
    }

    private void BuildPresetRow(VBoxContainer rows, ModThemePreset preset)
    {
        var readOnly = preset.Id == ModThemePresets.DefaultId;
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 46),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        rows.AddChild(row);
        var active = MakeLabel(() => _activePresetId == preset.Id ? "●" : "");
        active.CustomMinimumSize = new Vector2(24, 38);
        active.HorizontalAlignment = HorizontalAlignment.Center;
        ModThemeRuntime.AccentText(active);
        row.AddChild(active);

        var name = new LineEdit { Text = readOnly ? ModLocalization.Get(ModText.DefaultVariant) :
                _presetNameDrafts.GetValueOrDefault(preset.Id, preset.Name),
            Editable = !readOnly, MaxLength = 100, CustomMinimumSize = new Vector2(0, 38),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        ModThemeRuntime.Input(name, 18, accent: readOnly);
        if (readOnly) ModLocalization.Bind(name, () => name.Text = ModLocalization.Get(ModText.DefaultVariant));
        name.TextChanged += value => { _presetNameDrafts[preset.Id] = value; CancelPresetDelete(); };
        row.AddChild(name);
        var apply = PresetButton(() => ModLocalization.Get(_activePresetId == preset.Id
            ? ModText.ActiveCardPreset : ModText.ApplyCardPreset), 108);
        apply.Pressed += () => QueueThemePresetAction(() =>
        {
            _activePresetId = preset.Id;
            ModThemeRuntime.Session.Preview(preset.Settings);
        }, rebuild: false);
        row.AddChild(apply);

        var overwrite = PresetButton(() => ModLocalization.Get(ModText.OverwriteCardPreset), 100);
        overwrite.Disabled = readOnly;
        overwrite.Pressed += () => QueueThemePresetAction(() =>
        {
            _presets!.Overwrite(preset.Id, ModThemeRuntime.Current);
            _activePresetId = preset.Id;
        });
        row.AddChild(overwrite);
        var rename = PresetButton(() => ModLocalization.Get(ModText.RenameCardPreset), 100);
        rename.Disabled = readOnly;
        rename.Pressed += () =>
        {
            var requestedName = name.Text;
            QueueThemePresetAction(() =>
            {
                _presets!.Rename(preset.Id, requestedName);
                _presetNameDrafts.Remove(preset.Id);
            });
        };
        row.AddChild(rename);
        var delete = PresetButton(() => ModLocalization.Get(_deletePresetId == preset.Id
            ? ModText.ConfirmDeleteCardPreset : ModText.DeleteCardPreset), 108);
        delete.Disabled = readOnly;
        delete.Pressed += () =>
        {
            if (readOnly) return;
            if (_deletePresetId != preset.Id) { _deletePresetId = preset.Id; RefreshPresetStates(); return; }
            QueueThemePresetAction(() => { _presets!.Delete(preset.Id); _presetNameDrafts.Remove(preset.Id); });
        };
        row.AddChild(delete);
        _presetStates.Add(() =>
        {
            var selected = _activePresetId == preset.Id;
            active.Text = selected ? "●" : "";
            apply.Text = ModLocalization.Get(selected ? ModText.ActiveCardPreset : ModText.ApplyCardPreset);
            apply.Disabled = selected;
            var confirming = _deletePresetId == preset.Id;
            delete.Text = ModLocalization.Get(confirming ? ModText.ConfirmDeleteCardPreset : ModText.DeleteCardPreset);
            ModThemeRuntime.TextControl(delete, 18);
            if (confirming)
                foreach (var key in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                    delete.AddThemeColorOverride(key, new Color("ff7272"));
        });
    }

    private static Button PresetButton(Func<string> text, float width)
    {
        var button = MakeButton(text);
        button.CustomMinimumSize = new Vector2(width, 38);
        button.ClipText = true;
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return button;
    }

    private void RefreshPresetStates()
    {
        if (_presets == null || !GodotObject.IsInstanceValid(_presetPanel)) return;
        _activePresetId = _presets.FindMatchingId(ModThemeRuntime.Current, _activePresetId) ?? "";
        foreach (var refresh in _presetStates) refresh();
    }

    private void CancelPresetDelete()
    {
        _deletePresetId = null;
        RefreshPresetStates();
    }

    private void QueueThemePresetAction(Action action, bool rebuild = true)
    {
        // Row buttons can be destroyed by a rebuild; never free/rebuild inside their signal.
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(this) || !IsInsideTree() || IsQueuedForDeletion()) return;
            CancelPresetDelete();
            try
            {
                action();
                _presetStatus.Hide();
                if (rebuild) BuildPresetRows();
                else RefreshPresetStates();
            }
            catch (Exception e) { ShowThemeError(e, _presetStatus); }
        }).CallDeferred();
    }
}
