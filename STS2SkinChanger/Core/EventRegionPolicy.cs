namespace STS2SkinChanger.Core;

internal static class EventRegionPolicy
{
    public const string Shared = "__shared__";
    public const string Other = "__other__";

    public static IReadOnlyDictionary<string, string[]> Group(IEnumerable<string> available,
        IReadOnlyDictionary<string, IEnumerable<string>> acts, IEnumerable<string> sharedEvents)
    {
        var all = available.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var valid = all.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shared = sharedEvents.Where(valid.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(shared, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, events) in acts)
        {
            var members = events.Where(e => valid.Contains(e) && !shared.Contains(e))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (members.Length == 0) continue;
            result[id] = members;
            assigned.UnionWith(members);
        }
        if (shared.Count > 0) result[Shared] = all.Where(shared.Contains).ToArray();
        var remaining = all.Where(e => !assigned.Contains(e)).ToArray();
        if (remaining.Length > 0) result[Other] = remaining;
        return result;
    }
}
