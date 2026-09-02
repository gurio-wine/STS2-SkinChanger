namespace STS2SkinChanger.Core;

internal static class CharacterIconSelectionPolicy
{
    public const string FollowCharacterSkinOptionId = "__follow_character_skin__";

    public static string ResolveResourceSelection(
        string? configuredSelection,
        string currentSkinSelection,
        string baseOptionId,
        IReadOnlySet<string> availableIconOptionIds,
        bool configuredSourceContainsResource)
    {
        if (string.IsNullOrWhiteSpace(configuredSelection) ||
            configuredSelection.Equals(
                FollowCharacterSkinOptionId,
                StringComparison.OrdinalIgnoreCase))
        {
            return currentSkinSelection;
        }

        if (configuredSelection.Equals(baseOptionId, StringComparison.OrdinalIgnoreCase))
        {
            return baseOptionId;
        }

        return availableIconOptionIds.Contains(configuredSelection) &&
               configuredSourceContainsResource
            ? configuredSelection
            : currentSkinSelection;
    }
}
