namespace STS2SkinChanger.Core;

internal static partial class SkinService
{
    internal static SlotVisibilitySelection[] GetSlotVisibilitySelections(string groupId, string providerId)
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            if (GetSelectedManualCharacterVariant(groupId) is { State.IsStatic: false } mode &&
                GetSelectedRuntimeProvider(groupId)?.Equals(providerId, StringComparison.OrdinalIgnoreCase) == true)
                return [new(groupId, providerId, mode.State.Id, mode.Value, mode.State.SourceSlots)];
            return SlotVisibilityPolicy.GetSelections(Config, groupId, providerId);
        }
    }

    internal static void SaveSlotVisibilitySelection(SlotVisibilitySelection selection)
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            Config.SlotVisibilitySelections.RemoveAll(state =>
                state.GroupId.Equals(selection.GroupId, StringComparison.OrdinalIgnoreCase) &&
                state.ProviderId.Equals(selection.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                state.ToggleId == selection.ToggleId);
            Config.SlotVisibilitySelections.Add(selection);
            Config.Save(ConfigPath);
        }
    }
}
