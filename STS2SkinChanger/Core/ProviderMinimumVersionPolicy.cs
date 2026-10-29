using MegaCrit.Sts2.Core.Debug;

namespace STS2SkinChanger.Core;

internal enum ProviderMinimumVersionOutcome
{
    /// The declaration does not stand in the way; the game's own loader decides, as it always did.
    GameDecides,
    /// The game would refuse this Mod, but it is a cosmetic provider this Mod can run itself.
    VerifiedTakeOver,
    /// The declaration cannot be read; the constraint is never guessed away.
    UnreadableRule,
    /// The game would refuse it and its code cannot run on the running game version.
    Unsupported
}

/// <summary>
/// The game refuses every Mod whose declared minimum game version is newer than the running game
/// (ModManager.TryLoadMod). For a cosmetic provider that Skin Changer loads itself, that number is
/// the author's assumption about their build rather than a property of the artwork: the same
/// resources and the same code are bridged to the running snapshot instead. A package is taken over
/// only after its DLL was verified to run here, so an unverified package keeps the game's refusal.
/// </summary>
internal static class ProviderMinimumVersionPolicy
{
    public static ProviderMinimumVersionOutcome Decide(
        string? declaredMinimum,
        string? gameVersion,
        bool hasDll,
        Func<bool> assemblyRunsOnCurrentGame)
    {
        if (string.IsNullOrWhiteSpace(declaredMinimum)) return ProviderMinimumVersionOutcome.GameDecides;
        if (string.IsNullOrWhiteSpace(gameVersion) ||
            !SemanticVersion.TryFromString(gameVersion, out var current))
        {
            // Nothing to compare against: keep the previous behaviour and let the game decide.
            return ProviderMinimumVersionOutcome.GameDecides;
        }

        if (!SemanticVersion.TryFromString(declaredMinimum, out var minimum))
        {
            return ProviderMinimumVersionOutcome.UnreadableRule;
        }

        if (current!.CompareTo(minimum) >= 0) return ProviderMinimumVersionOutcome.GameDecides;
        if (!hasDll)
        {
            // Resource-only package: there is no code that could depend on the newer game.
            return ProviderMinimumVersionOutcome.VerifiedTakeOver;
        }

        return assemblyRunsOnCurrentGame()
            ? ProviderMinimumVersionOutcome.VerifiedTakeOver
            : ProviderMinimumVersionOutcome.Unsupported;
    }
}
