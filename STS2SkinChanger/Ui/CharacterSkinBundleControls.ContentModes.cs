using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static partial class CharacterSkinBundleControls
{
    private static void AddContentSection(EditorState state, VBoxContainer fields, bool monsters)
    {
        var categories = monsters ? state.MonsterCategories : state.CardCategories;
        if (categories.Count == 0) return;
        fields.AddChild(new Control { CustomMinimumSize = new Vector2(0f, 8f), MouseFilter = Control.MouseFilterEnum.Ignore });
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        fields.AddChild(header);
        var label = CreateLabel(monsters ? ModLocalization.BundleMonsters : ModLocalization.BundleCards, 21);
        label.AddThemeColorOverride("font_color", new Color("efc850"));
        label.CustomMinimumSize = new Vector2(250f, 42f);
        header.AddChild(label);
        var mode = CreateOptions();
        mode.Name = monsters ? "BundleMonsterMode" : "BundleCardMode";
        mode.AddItem(ModLocalization.BundleMultiplePresets, (int)BundleContentMode.MultiplePresets);
        mode.AddItem(ModLocalization.BundleModPriority, (int)BundleContentMode.ModPriority);
        var selected = monsters ? state.Draft.MonsterMode : state.Draft.CardMode;
        mode.Select(mode.GetItemIndex((int)selected));
        mode.ItemSelected += index =>
        {
            var value = (BundleContentMode)mode.GetItemId((int)index);
            if (monsters) state.Draft.MonsterMode = value;
            else state.Draft.CardMode = value;
            MarkDirty(state);
            BuildEditor(state);
        };
        header.AddChild(mode);
        if (selected == BundleContentMode.ModPriority)
            AddModPriorityRows(state, fields, monsters);
        else
            AddPresetRows(state, fields, categories,
                monsters ? state.Draft.MonsterPresetNames : state.Draft.CardPresetNames);
    }

    private static void AddModPriorityRows(EditorState state, VBoxContainer fields, bool monsters)
    {
        // Metadata only: opening/editing a bundle must not load or apply any skin resources.
        var sources = SkinService.GetBundleModSources(monsters)
            .ToDictionary(source => source.OptionId, StringComparer.OrdinalIgnoreCase);
        var entries = SkinService.GetBundleModPriority(state.Draft, monsters);
        if (monsters) state.Draft.MonsterModPriority = entries;
        else state.Draft.CardModPriority = entries;
        // Keep unavailable sources in the draft so temporary Mod disabling doesn't erase them.
        var visible = entries.Where(entry => sources.ContainsKey(entry.OptionId)).ToArray();
        for (var rowIndex = 0; rowIndex < visible.Length; rowIndex++)
        {
            var visibleIndex = rowIndex;
            var id = visible[rowIndex].OptionId;
            var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 8);
            fields.AddChild(row);
            var enabled = new CheckBox
            {
                Text = ModLocalization.Get(ModText.EnabledForCategory),
                ButtonPressed = visible[rowIndex].Enabled,
                CustomMinimumSize = new Vector2(88f, 38f)
            };
            ContextualSkinControls.ApplyGameTheme(enabled);
            enabled.AddThemeFontSizeOverride("font_size", 17);
            enabled.Toggled += value =>
            {
                var index = entries.FindIndex(entry => entry.OptionId.Equals(id, StringComparison.OrdinalIgnoreCase));
                entries[index] = entries[index] with { Enabled = value };
                MarkDirty(state);
            };
            row.AddChild(enabled);
            var name = CreateLabel(ModLocalization.DisplayOptionName(sources[id].Name), 19);
            name.ClipText = true;
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(name);
            void Move(int direction)
            {
                var index = entries.FindIndex(entry => entry.OptionId.Equals(id, StringComparison.OrdinalIgnoreCase));
                var neighbor = entries.FindIndex(entry => entry.OptionId.Equals(
                    visible[visibleIndex + direction].OptionId, StringComparison.OrdinalIgnoreCase));
                (entries[index], entries[neighbor]) = (entries[neighbor], entries[index]);
                MarkDirty(state);
                BuildEditor(state);
            }
            foreach (var direction in new[] { -1, 1 })
            {
                var button = new Button
                {
                    Text = direction < 0 ? "↑" : "↓",
                    CustomMinimumSize = new Vector2(42f, 38f),
                    Disabled = direction < 0 ? visibleIndex == 0 : visibleIndex == visible.Length - 1,
                    FocusMode = Control.FocusModeEnum.None
                };
                ContextualSkinControls.ApplyGameTheme(button);
                button.AddThemeFontSizeOverride("font_size", 19);
                button.Pressed += () => Move(direction);
                row.AddChild(button);
            }
        }
    }
}
