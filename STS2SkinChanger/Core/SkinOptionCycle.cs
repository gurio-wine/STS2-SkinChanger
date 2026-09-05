namespace STS2SkinChanger.Core;

internal static class SkinOptionCycle
{
    // The caller supplies the actual visible list. Never rebuild it from provider registries:
    // that loses compositions, bundle entries, user ordering and hidden-ingredient filtering.
    public static string? NextOption(IReadOnlyList<string> options, string? current, int direction)
    {
        if (options.Count == 0) return null;
        for (var index = 0; index < options.Count; index++)
        {
            if (options[index].Equals(current, StringComparison.OrdinalIgnoreCase))
                return options[(index + options.Count + Math.Sign(direction)) % options.Count];
        }
        return direction < 0 ? options[^1] : options[0];
    }
}
