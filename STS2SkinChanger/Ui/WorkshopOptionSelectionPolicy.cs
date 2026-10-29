namespace STS2SkinChanger.Ui;

// Godot's OptionButton is indexed by insertion order while GetItemId carries the value the code
// actually compares. Selecting by index therefore shows a different label as soon as the two
// orders differ: the GitHub sort picker adds stars first while its enum starts at "updated",
// which left the picker reading "recently updated" while the list was ordered by stars.
internal static class WorkshopOptionSelectionPolicy
{
    // -1 when the value is absent: the caller keeps whatever the picker shows instead of
    // silently moving the player to an unrelated option.
    public static int IndexOfId(IReadOnlyList<int> itemIds, int id)
    {
        for (var index = 0; index < itemIds.Count; index++) if (itemIds[index] == id) return index;
        return -1;
    }
}
