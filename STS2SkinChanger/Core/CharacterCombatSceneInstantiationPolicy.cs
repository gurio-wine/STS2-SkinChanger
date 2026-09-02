namespace STS2SkinChanger.Core;

internal static class CharacterCombatSceneInstantiationPolicy
{
    public static bool ShouldUseManagedFactory(
        bool isBaseSelection,
        bool hasManagedCombatScene) =>
        !isBaseSelection && hasManagedCombatScene;
}
