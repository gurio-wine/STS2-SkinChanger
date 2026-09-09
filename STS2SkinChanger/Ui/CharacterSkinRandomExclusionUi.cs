using System.Runtime.CompilerServices;
using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// Reuse each selector's existing themed list. Right-click changes neither its selected
// row nor the loaded skin, and never rebuilds the list (so its scroll position stays put).
internal static class CharacterSkinRandomExclusionUi
{
    private sealed record Binding(ItemList List, Func<string?> Group)
    {
        public int Hovered { get; set; } = -1;
    }
    private static readonly ConditionalWeakTable<OptionButton, Binding> Bindings = new();

    internal static void Attach(OptionButton picker, ItemList list, Func<string?> group)
    {
        if (Bindings.TryGetValue(picker, out _)) { Refresh(picker); return; }
        var binding = new Binding(list, group);
        Bindings.Add(picker, binding);
        list.GuiInput += input =>
        {
            if (input is InputEventMouseMotion motion)
            {
                // Track the same exact row as ItemList's native mouse-motion handler.
                var hovered = list.GetItemAtPosition(motion.Position, true);
                if (binding.Hovered != hovered)
                {
                    binding.Hovered = hovered;
                    RefreshInteractionColors(picker, binding);
                }
            }
            if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } mouse) return;
            var index = list.GetItemAtPosition(mouse.Position, true);
            var groupId = group();
            if (groupId == null || index < 0 || index >= picker.ItemCount || picker.IsItemDisabled(index)) return;
            var option = picker.GetItemMetadata(index).AsString();
            if (!RandomCharacterSkinPolicy.IsCandidate(option)) return;
            list.AcceptEvent();
            if (SkinService.ToggleCharacterSkinRandomExclusion(groupId, option)) Refresh(picker);
        };
        list.MouseExited += () =>
        {
            binding.Hovered = -1;
            RefreshInteractionColors(picker, binding);
        };
        list.ItemSelected += _ => RefreshInteractionColors(picker, binding);
        picker.GetPopup().AboutToPopup += () => Refresh(picker);
        ModThemeRuntime.Bind(list, "random_exclusions", _ => Refresh(picker));
    }

    internal static Color ChoiceColor(Color normal, bool excluded) =>
        excluded ? new Color(normal, normal.A * .5f) : normal;

    internal static void ApplyInteractionColors(string? group, string? hoveredOption, string? selectedOption,
        Color normal, Color accent, Action<string, Color> apply)
    {
        Color For(string? option) => ChoiceColor(
            option != null && SkinOptionStylePolicy.IsAccented(option) ? accent : normal,
            group != null && option != null && SkinService.IsCharacterSkinExcludedFromRandom(group, option));
        var hovered = For(hoveredOption);
        // Native ItemList ignores custom_fg for these three states. Single-selection lists
        // have only one row in each state, so style them independently, without disabling rows.
        apply("font_hovered_color", hovered);
        apply("font_selected_color", For(selectedOption));
        apply("font_hovered_selected_color", hovered);
    }

    private static void RefreshInteractionColors(OptionButton picker, Binding binding)
    {
        var list = binding.List;
        string? Option(int index) => index >= 0 && index < Math.Min(picker.ItemCount, list.ItemCount)
            ? picker.GetItemMetadata(index).AsString() : null;
        var selected = list.GetSelectedItems();
        ApplyInteractionColors(binding.Group(), Option(binding.Hovered),
            Option(selected.Length == 0 ? -1 : selected[0]), ModThemeRuntime.Text, ModThemeRuntime.Accent,
            (name, color) => list.AddThemeColorOverride(name, color));
    }

    internal static void Refresh(OptionButton picker)
    {
        if (!Bindings.TryGetValue(picker, out var binding)) return;
        var group = binding.Group();
        var list = binding.List;
        for (var i = 0; i < Math.Min(list.ItemCount, picker.ItemCount); i++)
        {
            var option = picker.GetItemMetadata(i).AsString();
            var color = SkinOptionStylePolicy.IsAccented(option) ? ModThemeRuntime.Accent : ModThemeRuntime.Text;
            list.SetItemCustomFgColor(i, ChoiceColor(color, group != null && SkinService.IsCharacterSkinExcludedFromRandom(group, option)));
        }
        RefreshInteractionColors(picker, binding);
    }
}
