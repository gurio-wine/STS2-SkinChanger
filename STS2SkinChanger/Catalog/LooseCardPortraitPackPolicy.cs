namespace STS2SkinChanger.Catalog;

/// <summary>
/// The rules that decide whether a provider folder's pictures are card art. Kept free of engine,
/// file-system and catalog dependencies so they stay testable offline.
/// </summary>
internal static class LooseCardPortraitPackPolicy
{
    /// <summary>
    /// Card art is a set rather than a single picture, so a provider folder needs at least this
    /// many card portraits before it is read as a pack.
    /// </summary>
    public const int MinimumCards = 20;

    /// <summary>
    /// Share of a folder's images that must name a real card. Cards are what such a folder is for;
    /// a folder that mostly holds something else stays the author's own material.
    /// </summary>
    public const int MinimumMatchPercent = 80;

    /// <summary>
    /// Whether a folder's images are a card pack rather than a provider's incidental pictures.
    /// </summary>
    public static bool IsPack(int imageCount, int matchedCards) =>
        imageCount > 0 &&
        matchedCards >= MinimumCards &&
        matchedCards * 100 >= imageCount * MinimumMatchPercent;

    /// <summary>
    /// A Mod ID can hold spaces, dots and other characters that a resource path should not, so the
    /// path token keeps only word characters. The pack ID itself is never rewritten: it has to stay
    /// equal to the Mod ID for the provider's own row to read as taken over.
    /// </summary>
    public static string SanitizePathToken(string id)
    {
        var token = new string(id
            .Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_')
            .ToArray());
        return token.Length == 0 || char.IsDigit(token[0]) ? "_" + token : token;
    }
}
