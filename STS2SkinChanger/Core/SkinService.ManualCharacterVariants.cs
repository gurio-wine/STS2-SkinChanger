using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal static partial class SkinService
{
    internal static int MigrateLegacyManualSlots(SkinConfig config, SkinCatalog catalog)
    {
        var changed = 0;
        foreach (var legacy in config.SlotVisibilitySelections.ToArray())
        {
            var group = catalog.Groups.FirstOrDefault(group => group.Id.Equals(legacy.GroupId, StringComparison.OrdinalIgnoreCase));
            var options = group?.Options.Where(option => !option.IsComposition &&
                option.EffectiveProviderId.Equals(legacy.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                option.ManualCharacterVariant is { State.IsStatic: false } mode && mode.State.Id == legacy.ToggleId &&
                mode.State.SourceSlots.ToHashSet(StringComparer.Ordinal).SetEquals(legacy.SourceSlots)).ToArray() ?? [];
            var original = options.FirstOrDefault(option => option.ManualCharacterVariant!.Value == false);
            var target = options.FirstOrDefault(option => option.ManualCharacterVariant!.Value == legacy.Hidden);
            if (original == null || target == null) continue;
            if (config.GetSelection(legacy.GroupId).Equals(original.Id, StringComparison.OrdinalIgnoreCase))
                config.Selections[legacy.GroupId] = target.Id;
            // New manual variants store only the option ID. Consuming the old record prevents
            // an old hidden=true flag from undoing a later explicit choice of variant 1.
            config.SlotVisibilitySelections.Remove(legacy);
            changed++;
        }
        return changed;
    }

    internal static ManualCharacterVariant? GetSelectedManualCharacterVariant(string groupId)
    {
        lock (Sync)
            return Catalog?.Groups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
                .Options.FirstOrDefault(option => option.Id.Equals(GetVisualSelection(groupId), StringComparison.OrdinalIgnoreCase))?
                .ManualCharacterVariant;
    }

    internal static bool IsSameManualVariantSource(string groupId, string optionId)
    {
        lock (Sync)
        {
            var group = Catalog?.Groups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            var previous = group?.Options.FirstOrDefault(option => option.Id.Equals(Config.GetSelection(groupId), StringComparison.OrdinalIgnoreCase));
            var next = group?.Options.FirstOrDefault(option => option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase));
            return previous?.ManualCharacterVariant is { } before && next?.ManualCharacterVariant is { } after &&
                !previous.IsComposition && !next.IsComposition && previous.EffectiveProviderId == next.EffectiveProviderId &&
                before.State.Id == after.State.Id && ReferenceEquals(previous.Assets, next.Assets);
        }
    }

    private static string ManualVariantResourceSelection(string groupId, string selection)
    {
        var group = Catalog?.Groups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        var option = group?.Options.FirstOrDefault(option => option.Id.Equals(selection, StringComparison.OrdinalIgnoreCase));
        if (option?.ManualCharacterVariant is not { } variant || option.IsComposition) return selection;
        return group!.Options.FirstOrDefault(candidate => !candidate.IsComposition &&
            candidate.EffectiveProviderId == option.EffectiveProviderId && ReferenceEquals(candidate.Assets, option.Assets) &&
            candidate.ManualCharacterVariant is { Value: false } initial && initial.State.Id == variant.State.Id)?.Id ?? selection;
    }
}
