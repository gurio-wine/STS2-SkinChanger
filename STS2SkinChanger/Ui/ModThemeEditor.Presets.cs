using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class ModThemeEditor
{
    private ModThemePresets _presets = null!;
    private OptionButton _presetList = null!;
    private LineEdit _presetName = null!;
    private Button _presetOverwrite = null!;
    private Button _presetRename = null!;
    private Button _presetDelete = null!;
    private string _presetId = ModThemePresets.DefaultId;
    private string? _deletePresetId;

    private void BuildPresetSection(VBoxContainer rows)
    {
        _presets = new ModThemePresets(System.IO.Path.Combine(OS.GetUserDataDir(), "skin_changer_theme_presets.json"));
        var section = Section(rows, ThemeText.Presets);
        _presetList = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FitToLongestItem = false, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            CustomMinimumSize = new Vector2(0, 36), FocusMode = Control.FocusModeEnum.None };
        ModThemeRuntime.Button(_presetList, 18);
        if (ContextualSkinControls.GameFont is { } font) _presetList.AddThemeFontOverride("font", font);
        ModThemeRuntime.Popup(_presetList.GetPopup());
        section.AddChild(_presetList);
        _presetName = new LineEdit { MaxLength = 100, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        ModThemeRuntime.Input(_presetName, 18);
        ModLocalization.Bind(_presetName, () => _presetName.PlaceholderText = ModLocalization.Get(ModText.CardPresetName));
        section.AddChild(_presetName);
        _presetName.TextChanged += _ => CancelPresetDelete();

        var createRow = new HBoxContainer(); section.AddChild(createRow);
        var create = PresetButton(createRow, ModText.SaveCurrentPreset);
        var apply = PresetButton(createRow, ModText.ApplyCardPreset);
        var editRow = new HBoxContainer(); section.AddChild(editRow);
        _presetOverwrite = PresetButton(editRow, ModText.OverwriteCardPreset);
        _presetRename = PresetButton(editRow, ModText.RenameCardPreset);
        _presetDelete = PresetButton(editRow, ModText.DeleteCardPreset);
        ModLocalization.Bind(_presetDelete, RefreshPresetDelete);

        create.Pressed += () => PresetAction(() =>
        {
            _presetId = _presets.Create(_presetName.Text, ModThemeRuntime.Current);
            RefreshPresetList();
            FillPresetName();
        });
        apply.Pressed += () => PresetAction(() =>
        {
            var selected = _presets.Presets.FirstOrDefault(p => p.Id == _presetId);
            if (selected != null) ModThemeRuntime.Session.Preview(selected.Settings);
        });
        _presetOverwrite.Pressed += () => PresetAction(() => _presets.Overwrite(_presetId, ModThemeRuntime.Current));
        _presetRename.Pressed += () => PresetAction(() =>
        {
            _presets.Rename(_presetId, _presetName.Text);
            RefreshPresetList();
            FillPresetName();
        });
        _presetDelete.Pressed += () =>
        {
            if (_presetId == ModThemePresets.DefaultId) return;
            if (_deletePresetId != _presetId) { _deletePresetId = _presetId; RefreshPresetDelete(); return; }
            PresetAction(() =>
            {
                _presets.Delete(_presetId);
                _presetId = ModThemePresets.DefaultId;
                RefreshPresetList();
                FillPresetName();
            });
        };
        _presetList.ItemSelected += index =>
        {
            _presetId = _presetList.GetItemMetadata((int)index).AsString();
            CancelPresetDelete();
            RefreshPresetActions();
            FillPresetName();
        };
        // Rebuild only for CRUD/language changes, not for every slider movement.
        ModLocalization.Bind(_presetList, RefreshPresetList);
        FillPresetName();
    }

    private static Button PresetButton(HBoxContainer row, ModText text)
    {
        var button = MakeButton(() => ModLocalization.Get(text));
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        button.ClipText = true;
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        row.AddChild(button);
        return button;
    }

    private void RefreshPresetList()
    {
        var presets = _presets.Presets;
        if (!presets.Any(p => p.Id == _presetId)) _presetId = ModThemePresets.DefaultId;
        _presetList.Clear();
        foreach (var preset in presets)
        {
            var index = _presetList.ItemCount;
            _presetList.AddItem(preset.Id == ModThemePresets.DefaultId ? ModLocalization.Get(ModText.DefaultVariant) : preset.Name);
            _presetList.SetItemMetadata(index, preset.Id);
            if (preset.Id == _presetId) _presetList.Select(index);
        }
        RefreshPresetActions();
    }

    private void FillPresetName() => _presetName.Text = _presets.Presets.FirstOrDefault(p => p.Id == _presetId)?.Name ?? "";

    private void RefreshPresetActions()
    {
        var readOnly = _presetId == ModThemePresets.DefaultId;
        _presetOverwrite.Disabled = _presetRename.Disabled = _presetDelete.Disabled = readOnly;
    }

    private void CancelPresetDelete()
    {
        _deletePresetId = null;
        if (GodotObject.IsInstanceValid(_presetDelete)) RefreshPresetDelete();
    }

    private void RefreshPresetDelete()
    {
        var confirming = _deletePresetId == _presetId;
        _presetDelete.Text = ModLocalization.Get(confirming ? ModText.ConfirmDeleteCardPreset : ModText.DeleteCardPreset);
        ModThemeRuntime.TextControl(_presetDelete, 18);
        if (confirming)
            foreach (var key in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                _presetDelete.AddThemeColorOverride(key, new Color("ff7272"));
    }

    private void PresetAction(Action action)
    {
        CancelPresetDelete();
        try { action(); _status.Hide(); }
        catch (Exception e) { ShowThemeError(e); }
    }
}
