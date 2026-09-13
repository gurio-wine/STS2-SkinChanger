namespace STS2SkinChanger.Catalog;

/// <summary>
/// Decides whether a package's Harmony patch metadata is evidence of a skin — a character
/// appearance the player can pick — or of an additive visual Mod.
/// <para>
/// A creature lifecycle hook (NCreature._Ready / SetAnimationTrigger) is not skin evidence on its
/// own, and neither are the room entry points (NRestSiteCharacter._Ready,
/// NMerchantRoom.AfterRoomIsLoaded): Mods that add their own actors next to the player (cosmetic
/// companions, extra effects) hook exactly those entry points to place them. The entry point that
/// separates the two is the character-select presentation: a package that also patches it is
/// presenting a character the player can pick. A package that replaces the character's own visual
/// creation is a skin whatever else it hooks.
/// </para>
/// </summary>
internal static class SkinProviderPatchEvidencePolicy
{
    internal static bool IsSkinEvidence(
        bool replacesCharacterVisualCreation,
        bool hooksCreatureLifecycle,
        bool patchesCharacterSelectPresentation) =>
        replacesCharacterVisualCreation ||
        (hooksCreatureLifecycle && patchesCharacterSelectPresentation);

    /// <summary>
    /// Whether a package the DLL heuristic might call a skin actually brings an appearance of its
    /// own. A package with no pack resource, no runtime image, no appearance file and no
    /// replacement of the character's own visuals can only add actors built from the game's or
    /// another Mod's resources: isolating it holds back its code and its files without serving
    /// anything, which is exactly how an additive visual Mod stops working.
    /// </summary>
    internal static bool BringsOwnAppearance(
        int packResourceCount,
        int runtimeImageCount,
        bool replacesCharacterVisualCreation,
        bool hasAppearanceFile) =>
        packResourceCount > 0 ||
        runtimeImageCount > 0 ||
        replacesCharacterVisualCreation ||
        hasAppearanceFile;
}
