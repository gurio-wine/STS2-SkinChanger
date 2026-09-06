namespace STS2SkinChanger.Core;

internal static class EventPreviewPolicy
{
    public static Godot.Rect2 TextBounds(Godot.Vector2 viewportSize, Godot.Rect2 nativeColumn,
        float contentHeight)
    {
        // The sidebar retracts and event skin controls live left of the text. Keep the
        // authored column position when it fits; use spare space above before scrolling.
        var width = Math.Max(800f, nativeColumn.Size.X) + 20f;
        var left = Math.Max(24f, Math.Min(nativeColumn.Position.X, viewportSize.X - 24f - width));
        var bottom = viewportSize.Y - 24f;
        var earliestTop = Math.Min(96f, Math.Max(0, bottom - 1));
        var nativeTop = Math.Clamp(nativeColumn.Position.Y, earliestTop, Math.Max(earliestTop, bottom - 1));
        var top = Math.Clamp(bottom - Math.Max(0, contentHeight), earliestTop, nativeTop);
        return new Godot.Rect2(left, top, width, Math.Max(1f, bottom - top));
    }

    public static string[] Pages(string eventId, IEnumerable<string> keys)
    {
        var prefix = eventId + ".pages.";
        return keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                key.EndsWith(".description", StringComparison.Ordinal))
            .Select(key => key[prefix.Length..^".description".Length])
            .Where(page => page.Length > 0 && !page.Contains('.'))
            .Distinct(StringComparer.Ordinal).OrderBy(page => page == "INITIAL" ? 0 : 1)
            .ThenBy(page => page, StringComparer.Ordinal).ToArray();
    }

    public static string[] Options(string eventId, string page, IEnumerable<string> keys)
    {
        var prefix = eventId + ".pages." + page + ".options.";
        return keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                (key.EndsWith(".title", StringComparison.Ordinal) || key.EndsWith(".description", StringComparison.Ordinal)))
            .Select(key => key[..key.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
