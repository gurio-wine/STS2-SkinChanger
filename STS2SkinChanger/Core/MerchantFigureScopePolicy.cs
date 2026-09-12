namespace STS2SkinChanger.Core;

/// <summary>
/// The merchant's own figure is the part of the shop this group's selection owns.
///
/// A style/UI package draws that figure from its own image and switches the game's figure off while
/// it does. That is the author's look and stays exactly as shipped. The moment any other merchant
/// skin — or the game original — is selected, the game's figure has to come back: the package's own
/// figure is hidden together with the selection, so without it the shop shows no merchant at all.
/// The layout the game gave the merchant's button is restored the same way, because another skin
/// authors its own and the package overwrote it with its own rectangle.
///
/// Type and value only, so the rule can be verified without a game.
/// </summary>
internal static class MerchantFigureScopePolicy
{
    internal enum FigureAction
    {
        /// <summary>Nothing on screen changes.</summary>
        Leave,

        /// <summary>The game's own merchant figure takes the package's place.</summary>
        ShowGameFigure,

        /// <summary>The package draws the merchant itself, so the game's figure steps aside.</summary>
        HideGameFigure
    }

    /// <summary>
    /// Which figure the shop should be showing. A package only makes the game's figure step aside
    /// for a figure that is really on screen, so a package that could not draw its merchant never
    /// leaves the player with no merchant at all.
    /// </summary>
    internal static FigureAction ResolveFigure(
        bool partSelected,
        bool packageFigureVisible,
        bool gameFigureVisibleAtRest,
        bool gameFigureVisible)
    {
        if (partSelected)
        {
            return packageFigureVisible && gameFigureVisible
                ? FigureAction.HideGameFigure
                : FigureAction.Leave;
        }

        return gameFigureVisibleAtRest && !gameFigureVisible
            ? FigureAction.ShowGameFigure
            : FigureAction.Leave;
    }

    /// <summary>
    /// Whether the merchant's button returns to the layout the game built for it. Only another
    /// selection does that; while the package's part is selected its own layout is the one to keep.
    /// </summary>
    internal static bool ShouldRestoreLayout(bool partSelected, bool layoutDiffersFromRest) =>
        !partSelected && layoutDiffersFromRest;
}
