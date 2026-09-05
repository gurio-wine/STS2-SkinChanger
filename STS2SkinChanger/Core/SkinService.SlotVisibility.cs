namespace STS2SkinChanger.Core;

internal static partial class SkinService
{
    internal static SlotVisibilitySelection[] GetSlotVisibilitySelections(string groupId, string providerId)
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
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
