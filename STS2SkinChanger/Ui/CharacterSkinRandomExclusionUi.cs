using System.Runtime.CompilerServices;
using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// Reuse each selector's existing themed list. Right-click changes neither its selected
// row nor the loaded skin, and never rebuilds the list (so its scroll position stays put).
internal static class CharacterSkinRandomExclusionUi
{
    private sealed record Binding(ItemList List, Func<string?> Group);
    private static readonly ConditionalWeakTable<OptionButton, Binding> Bindings = new();

    internal static void Attach(OptionButton picker, ItemList list, Func<string?> group)
    {
        if (Bindings.TryGetValue(picker, out _)) { Refresh(picker); return; }
        Bindings.Add(picker, new(list, group));
        list.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } mouse) return;
            var index = list.GetItemAtPosition(mouse.Position, true);
            var groupId = group();
            if (groupId == null || index < 0 || index >= picker.ItemCount || picker.IsItemDisabled(index)) return;
            var option = picker.GetItemMetadata(index).AsString();
            if (!RandomCharacterSkinPolicy.IsCandidate(option)) return;
            list.AcceptEvent();
            if (SkinService.ToggleCharacterSkinRandomExclusion(groupId, option)) Refresh(picker);
        };
        picker.GetPopup().AboutToPopup += () => Refresh(picker);
        ModThemeRuntime.Bind(list, "random_exclusions", _ => Refresh(picker));
    }

    internal static Color ChoiceColor(Color normal, bool excluded) =>
        excluded ? new Color(normal, normal.A * .5f) : normal;

    internal static void Refresh(OptionButton picker)
    {
        if (!Bindings.TryGetValue(picker, out var binding) || binding.Group() is not { } group) return;
        var list = binding.List;
        for (var i = 0; i < Math.Min(list.ItemCount, picker.ItemCount); i++)
        {
            var option = picker.GetItemMetadata(i).AsString();
            var color = SkinOptionStylePolicy.IsAccented(option) ? ModThemeRuntime.Accent : ModThemeRuntime.Text;
            list.SetItemCustomFgColor(i, ChoiceColor(color, SkinService.IsCharacterSkinExcludedFromRandom(group, option)));
        }
    }
}
