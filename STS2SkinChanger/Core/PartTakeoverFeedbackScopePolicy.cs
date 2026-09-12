namespace STS2SkinChanger.Core;

/// <summary>
/// Which callbacks of a style/UI package belong to the merchant the player picks.
///
/// A package that overrides parts of a presentation the game already draws also reacts while the
/// player is in the shop: it answers an opened shop, a purchase, a click on the merchant and a
/// thrown potion with its own voice, and it keeps the game's native merchant line quiet while it
/// talks. Those reactions are the merchant the player selected, so they follow this group's
/// selection exactly like the merchant's figure and hand do.
///
/// Only reactions are matched, and only inside the package's own merchant work: anything that
/// loads, attaches, builds or refreshes what it drew — its room art, its shop interface, its
/// refreshed item slots, its fake-merchant presentation — stays installed and is never gated,
/// handed back or removed. Its unrelated modules (menus, combat, general interface sounds) are not
/// merchant work at all and are never touched. Type and method names only, so the rule can be
/// verified without a game.
/// </summary>
internal static class PartTakeoverFeedbackScopePolicy
{
    private static readonly string[] AudioTokens =
        ["Sfx", "Sound", "Audio", "Voice", "Music", "Bus"];

    private static readonly string[] ShopTokens =
        ["Merchant", "Shop"];

    /// <summary>Native shop verbs that only report that something happened.</summary>
    private static readonly string[] ReactionNames =
    [
        "Open", "Close", "Release", "Press", "Click", "Interact", "Interaction",
        "Dialogue", "Speak", "Talk"
    ];

    /// <summary>Event-style endings that only report that something happened.</summary>
    private static readonly string[] ReactionEndings =
    [
        "Thrown", "Opened", "Closed", "Pressed", "Released", "Clicked", "Purchased",
        "Selected", "Used", "Failed", "Completed", "Finished", "Started", "Ended"
    ];

    /// <summary>
    /// Whether a callback is a reaction of the merchant this selection owns. The callback has to
    /// belong to the package's merchant work, either because it patches a shop type or because the
    /// patch itself is one of the package's merchant patches — that is what keeps a general
    /// interface-sound replacement, or another module's callback, out of this scope.
    /// </summary>
    internal static bool IsMerchantSelectionFeedback(
        string? targetTypeName,
        string? targetMethodName,
        string? patchTypeName)
    {
        if (!ContainsAny(targetTypeName, ShopTokens) && !ContainsAny(patchTypeName, ShopTokens))
        {
            return false;
        }

        // Its own voice and its gate on the game's native merchant lines are one reaction: silencing
        // the game's line belongs to the same merchant the player selected.
        if (ContainsAny(targetTypeName, AudioTokens) || ContainsAny(targetMethodName, AudioTokens))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(targetMethodName))
        {
            return false;
        }

        return targetMethodName.StartsWith("On", StringComparison.Ordinal) ||
               ReactionNames.Contains(targetMethodName, StringComparer.Ordinal) ||
               ReactionEndings.Any(ending => targetMethodName.EndsWith(ending, StringComparison.Ordinal));
    }

    private static bool ContainsAny(string? value, string[] tokens) =>
        value != null && tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
}
