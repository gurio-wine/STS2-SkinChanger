namespace STS2SkinChanger.Core;

internal static class EventPreviewPolicy
{
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
