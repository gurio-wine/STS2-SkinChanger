using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal static class SkinOptionStylePolicy
{
    public static bool IsAccented(string optionId) =>
        optionId is SkinService.InheritCardSelectionId or SkinService.InheritMonsterSelectionId or SkinService.InheritEventSelectionId ||
        optionId == SkinCatalog.BaseOptionId ||
        !WorkshopCatalogPolicy.IsSkinChoice(optionId) ||
        RandomCharacterSkinPolicy.IsRandom(optionId) ||
        CharacterSkinBundlePolicy.TryGetSelectionBundleName(optionId, out _);
}
