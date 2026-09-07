namespace STS2SkinChanger.Core;

internal static class WorkshopBrowserPolicy
{
    public const int TargetPageSize = 10;
    public static bool HasRegions(string kind) => kind is "monster" or "event";
    public static bool VisibleTarget(WorkshopTarget target) => !(target.Kind == "companion" && target.Target.Equals("osty", StringComparison.OrdinalIgnoreCase));
    public static string[] Page(string[] ids, int page) => ids.Skip(Math.Clamp(page, 0, Math.Max(0, (ids.Length - 1) / TargetPageSize)) * TargetPageSize).Take(TargetPageSize).ToArray();
    public static IEnumerable<WorkshopCatalogItem> Filter(IEnumerable<WorkshopCatalogItem> items, string kind, string target, IReadOnlySet<string>? regionMembers)
        => items.Where(item => item.Targets.Any(t => VisibleTarget(t) && (kind.Length == 0 ||
            t.Kind == kind && (target.Length == 0 || t.Target.Equals(target, StringComparison.OrdinalIgnoreCase)) &&
            (regionMembers == null || regionMembers.Contains(t.Target)))));
    public static ulong[] FilterIds(string json, string kind, string target, string[] members) =>
        Filter(WorkshopCatalogPolicy.Parse(json), kind, target, members.ToHashSet(StringComparer.OrdinalIgnoreCase)).Select(i => i.Id).ToArray();
    public static float MarqueeOffset(float overflow, double elapsed)
    {
        if (overflow <= 0 || !float.IsFinite(overflow) || !double.IsFinite(elapsed)) return 0;
        var travel = overflow / 36d;
        var phase = Math.Max(0, elapsed) % (2 * travel + 1.6);
        if (phase <= .8) return 0;
        if (phase <= travel + .8) return (float)((phase - .8) * 36);
        if (phase <= travel + 1.6) return overflow;
        return (float)Math.Max(0, overflow - (phase - travel - 1.6) * 36);
    }
}
