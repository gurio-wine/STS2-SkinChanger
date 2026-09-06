namespace STS2SkinChanger.Core;

internal static class EventPreviewPolicy
{
    public static Godot.Rect2 TextBounds(Godot.Vector2 viewportSize, Godot.Rect2 nativeColumn,
        float sidebarWidth, float controlsTop)
    {
        // Keep native option width and allow room for the vertical scrollbar. Only the text
        // column moves to make room for compendium controls; the scene keeps its native scale.
        var width = Math.Max(800f, nativeColumn.Size.X) + 20f;
        var right = viewportSize.X - sidebarWidth - 24f;
        var left = Math.Max(24f, Math.Min(nativeColumn.Position.X, right - width));
        var bottom = Math.Min(viewportSize.Y, controlsTop) - 24f;
        return new Godot.Rect2(left, nativeColumn.Position.Y, width,
            Math.Max(1f, bottom - nativeColumn.Position.Y));
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
