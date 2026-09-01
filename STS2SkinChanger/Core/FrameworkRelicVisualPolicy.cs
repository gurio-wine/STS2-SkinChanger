using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal static class FrameworkRelicVisualPolicy
{
    public static FrameworkRelicVisualPlan? Resolve(
        IEnumerable<FrameworkModelSkinContract> relics,
        string targetModelName,
        bool largeIcon)
    {
        var relic = relics.LastOrDefault(candidate =>
            candidate.TargetModelName.Equals(targetModelName, StringComparison.Ordinal));
        if (relic == null)
        {
            return null;
        }

        var preferredKey = largeIcon ? "BigIconPath" : "PackedIconPath";
        var fallbackKey = largeIcon ? "PackedIconPath" : "BigIconPath";
        if (!relic.Resources.TryGetValue(preferredKey, out var iconPath) &&
            !relic.Resources.TryGetValue(fallbackKey, out iconPath))
        {
            return null;
        }

        var outlinePath = !largeIcon &&
                          relic.Resources.TryGetValue("PackedIconOutlinePath", out var outline)
            ? outline
            : null;
        return new FrameworkRelicVisualPlan(
            iconPath,
            outlinePath);
    }
}

internal sealed record FrameworkRelicVisualPlan(
    string IconPath,
    string? OutlinePath);
