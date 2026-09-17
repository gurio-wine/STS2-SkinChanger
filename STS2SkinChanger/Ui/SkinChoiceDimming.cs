using System.Runtime.CompilerServices;
using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

/// <summary>
/// One row of a skin dropdown. A skin the group's skin-priority screen has switched off is drawn
/// with reduced opacity, so a list shows at a glance which skins the category actually uses.
/// Only the open list carries that state: the collapsed button is a single row showing the current
/// choice, and fading its title would read as a disabled control rather than as one option.
/// </summary>
internal static class SkinChoiceDimming
{
    private static readonly Func<string, bool> Never = _ => false;
    private static readonly ConditionalWeakTable<OptionButton, Func<string, bool>> Bindings = new();

    /// <summary>
    /// Registers which skins of this dropdown are switched off, from the same saved list that
    /// ordered it. Call before filling the dropdown in; null means the group has no priority
    /// screen, which is the normal case for characters, companions, Ancients and merchants.
    /// </summary>
    internal static void Bind(OptionButton picker, IReadOnlyList<SkinPriorityState>? states)
    {
        var lookup = states == null ? Never : SkinPriorityDisplayPolicy.UncheckedLookup(states);
        if (Bindings.TryGetValue(picker, out _)) Bindings.Remove(picker);
        Bindings.Add(picker, lookup);
    }

    internal static bool IsUnchecked(OptionButton? picker, string? optionId) =>
        picker != null && optionId != null &&
        Bindings.TryGetValue(picker, out var lookup) && lookup(optionId);

    /// <summary>The resting text color of one list row, faded when the category does not use that skin.</summary>
    internal static Color Row(Color color, OptionButton? picker, string? optionId) =>
        IsUnchecked(picker, optionId)
            ? new Color(color, color.A * SkinPriorityDisplayPolicy.UncheckedAlpha)
            : color;

    /// <summary>
    /// The same fade for a hovered or selected row. The caller applies one color per interaction
    /// state; the selected state belongs to the selected row and the other two to the hovered one.
    /// </summary>
    internal static Color InteractionRow(
        string stateKey,
        Color color,
        OptionButton picker,
        string? hoveredOption,
        string? selectedOption) =>
        Row(color, picker, stateKey == "font_selected_color" ? selectedOption : hoveredOption);
}
