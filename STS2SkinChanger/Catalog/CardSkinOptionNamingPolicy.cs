namespace STS2SkinChanger.Catalog;

internal static class CardSkinOptionNamingPolicy
{
    public static string Build(
        string modName,
        string? namedVariant,
        int ordinal,
        int optionCount)
    {
        if (!string.IsNullOrWhiteSpace(namedVariant))
        {
            return modName + " · " + namedVariant;
        }

        return optionCount > 1
            ? modName + " · " + ordinal
            : modName;
    }
}
