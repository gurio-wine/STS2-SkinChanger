using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal static class RandomCharacterSkinPolicy
{
    public const string OptionId = "__random_character_skin__";
    public static bool IsRandom(string? id) => string.Equals(id, OptionId, StringComparison.OrdinalIgnoreCase);
    public static bool IsCandidate(string? id) => !string.IsNullOrWhiteSpace(id) &&
        !IsRandom(id) && WorkshopCatalogPolicy.IsSkinChoice(id);

    public static string Draw(IEnumerable<string> visibleOptions, Func<int, int> next)
    {
        var candidates = visibleOptions.Where(IsCandidate)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return candidates.Length == 0 ? SkinCatalog.BaseOptionId : candidates[next(candidates.Length)];
    }
}
