namespace STS2SkinChanger.Core;

/// <summary>
/// A framework-registered pack declares its character through a content registry and keeps every
/// asset in its own namespace. Skin Changer serves those assets itself, which is why the pack's
/// registry call is intercepted - but that call is one line of the pack's initializer, and the rest
/// of it is the author's real setup: the mod's own settings page and persistent data, its combat
/// audio preload, its lifecycle subscriptions and its script types. Dropping the whole initializer
/// leaves those features dead: a setting that is off by default (an opt-in portrait, for example)
/// can never be switched on, because the page that would switch it does not exist.
/// </summary>
internal static class FrameworkProviderInitializerPolicy
{
    /// <summary>
    /// True when the pack's own initializer still runs after Skin Changer took the pack over. Only a
    /// pack whose appearance the legacy native manager serves keeps its initializer skipped: that
    /// manager owns the pack's lifecycle and calls the initializer itself, so a second call would
    /// duplicate its subscriptions and patches.
    /// </summary>
    public static bool RunsAuthorInitializer(
        bool hasDeclarativeCharacterAssetReplacement,
        bool isNativeFrameworkProvider) =>
        !hasDeclarativeCharacterAssetReplacement || !isNativeFrameworkProvider;

    /// <summary>
    /// True when a failing initializer must not end the visual session. For a framework-registered
    /// pack the resources and the scene creation are already owned by Skin Changer, so the failure
    /// only costs whatever else the initializer would have added; the skin itself must keep working
    /// exactly as it does when the initializer cannot be run at all.
    /// </summary>
    public static bool KeepsSessionWhenAuthorInitializerFails(
        bool hasDeclarativeCharacterAssetReplacement) =>
        hasDeclarativeCharacterAssetReplacement;
}
