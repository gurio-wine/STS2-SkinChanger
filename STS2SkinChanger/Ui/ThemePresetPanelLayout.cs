using Godot;

namespace STS2SkinChanger.Ui;

internal static class ThemePresetPanelLayout
{
    // The pair stays right-docked even when the theme window was dragged against
    // the screen edge. A narrow viewport scrolls the table, not the whole editor.
    internal static (Vector2 ThemePosition, Rect2 Preset) Place(Vector2 viewport, Rect2 theme, Vector2 desiredSize)
    {
        const float margin = 12, gap = 12;
        var width = Math.Max(1, Math.Min(desiredSize.X, viewport.X - 2 * margin - gap - theme.Size.X));
        var height = Math.Max(1, Math.Min(desiredSize.Y, viewport.Y - 2 * margin));
        var x = Math.Clamp(theme.Position.X, margin, Math.Max(margin, viewport.X - margin - theme.Size.X - gap - width));
        var minY = margin + Math.Max(0, height - theme.Size.Y);
        var y = Math.Clamp(theme.Position.Y, minY, Math.Max(minY, viewport.Y - margin - theme.Size.Y));
        return (new Vector2(x, y), new Rect2(x + theme.Size.X + gap, y + theme.Size.Y - height, width, height));
    }
}
