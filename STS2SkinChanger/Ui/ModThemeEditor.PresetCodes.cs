using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class ModThemeEditor
{
    private Button _presetCreate = null!;
    private Button _copyPresetCode = null!;
    private readonly ThemeSaveFeedback _copyFeedback = new();
    // Remember the actual selection even when live edits no longer match a preset.
    // Copy must export that preset's saved parameters, never an unrelated draft.
    private string _selectedPresetId = ModThemePresets.DefaultId;

    private string PresetCreateCaption() => ThemePresetCode.LooksLikeCode(_presetNewName.Text)
        ? ModThemeLocalization.Get(ThemeText.ImportPreset) : ModLocalization.Get(ModText.SaveCurrentPreset);

    private void SaveOrImportThemePreset()
    {
        var input = _presetNewName.Text;
        var current = ModThemeRuntime.Current;
        QueueThemePresetAction(() =>
        {
            if (ThemePresetCode.LooksLikeCode(input)) ImportThemePreset(input);
            else _activePresetId = _selectedPresetId = _presets!.Create(input, current);
            if (_presetNewName.Text == input) _presetNewName.Text = "";
        });
    }

    private void ImportThemePreset(string code)
    {
        var preset = _presets!.ImportCode(code, ModThemeLocalization.Get(ThemeText.ImportPreset));
        _activePresetId = _selectedPresetId = preset.Id;
        // Only apply after the new preset was successfully persisted.
        ModThemeRuntime.Session.Preview(preset.Settings);
    }

    private string CopyPresetCaption() => _copyFeedback.Active
        ? ModThemeLocalization.Get(ThemeText.Copied) + "✓" : ModThemeLocalization.Get(ThemeText.CopyPresetCode);

    private void CopyThemePreset()
    {
        CancelCopyFeedback();
        try
        {
            var code = _presets!.ExportCode(_selectedPresetId);
            DisplayServer.ClipboardSet(code);
            _presetStatus.Hide();
            var revision = _copyFeedback.Begin();
            RefreshCopyButton();
            GetTree().CreateTimer(ThemeSaveFeedback.DurationSeconds, true, false, true).Timeout += () =>
            {
                if (!GodotObject.IsInstanceValid(this) || !IsInsideTree() || IsQueuedForDeletion()) return;
                if (_copyFeedback.Expire(revision)) RefreshCopyButton();
            };
        }
        catch (Exception e) { ShowThemeError(e, _presetStatus, ThemeText.CopyFailed); }
    }

    private void CancelCopyFeedback() { _copyFeedback.Cancel(); RefreshCopyButton(); }

    private void RefreshCopyButton()
    {
        if (!GodotObject.IsInstanceValid(_copyPresetCode)) return;
        _copyPresetCode.Text = CopyPresetCaption();
        ModThemeRuntime.TextControl(_copyPresetCode, 18, accent: _copyFeedback.Active);
    }
}
