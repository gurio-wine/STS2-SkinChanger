using Godot;

namespace STS2SkinChanger.Ui;

internal static class WorkshopHoverPolicy
{
    // The introduction is display-only. Neither its rectangle nor a grace period
    // may keep an old item's description alive after the pointer leaves that item.
    public static bool CanDisplay(ulong hovered, ulong active, bool blocked) =>
        !blocked && hovered != 0 && hovered == active;

    public static Rect2 PlaceIntroduction(Rect2 anchor, Vector2 size, Rect2 viewport)
    {
        const float gap = 12;
        var bounds = viewport.Grow(-gap);
        size = size.Min(bounds.Size);
        float x;
        if (bounds.End.X - anchor.End.X >= size.X + gap) x = anchor.End.X + gap;
        else if (anchor.Position.X - bounds.Position.X >= size.X + gap) x = anchor.Position.X - size.X - gap;
        else x = bounds.Position.X;
        var y = Math.Clamp(anchor.Position.Y, bounds.Position.Y, Math.Max(bounds.Position.Y, bounds.End.Y - size.Y));
        // Narrow windows: prefer below/above before the final contained fallback.
        if (new Rect2(new Vector2(x, y), size).Intersects(anchor))
        {
            if (bounds.End.Y - anchor.End.Y >= size.Y + gap) y = anchor.End.Y + gap;
            else if (anchor.Position.Y - bounds.Position.Y >= size.Y + gap) y = anchor.Position.Y - size.Y - gap;
        }
        return new(new Vector2(Math.Clamp(x, bounds.Position.X, Math.Max(bounds.Position.X, bounds.End.X - size.X)), y), size);
    }
}
