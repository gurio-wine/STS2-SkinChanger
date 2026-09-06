using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal sealed record EventSkinPriorityEntry(string OptionId, bool Enabled);

internal sealed class EventSkinPrioritySettings
{
    public Dictionary<string, List<string>> RegionGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<EventSkinPriorityEntry>> Priorities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ManualGroups { get; set; } = [];
    public List<string> KnownGroups { get; set; } = [];

    public bool IsFollowing(string groupId) => RegionGroups.Values.Any(groups =>
        groups.Contains(groupId, StringComparer.OrdinalIgnoreCase)) &&
        !ManualGroups.Contains(groupId, StringComparer.OrdinalIgnoreCase);

    public EventSkinPrioritySettings Clone() => new()
    {
        RegionGroups = RegionGroups.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        Priorities = Priorities.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        ManualGroups = ManualGroups.ToList(), KnownGroups = KnownGroups.ToList()
    };

    public void Normalize()
    {
        RegionGroups = (RegionGroups ?? new()).Where(p => !string.IsNullOrWhiteSpace(p.Key)).ToDictionary(
            p => p.Key, p => (p.Value ?? []).Where(id => !string.IsNullOrWhiteSpace(id) && EventSkinPolicy.IsEventGroup(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        Priorities = (Priorities ?? new()).Where(p => !string.IsNullOrWhiteSpace(p.Key)).ToDictionary(
            p => p.Key, p => EventSkinPriorityPolicy.Entries(p.Value ?? [], []), StringComparer.OrdinalIgnoreCase);
        ManualGroups = (ManualGroups ?? []).Where(id => !string.IsNullOrWhiteSpace(id) && EventSkinPolicy.IsEventGroup(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        KnownGroups = (KnownGroups ?? []).Where(id => !string.IsNullOrWhiteSpace(id) && EventSkinPolicy.IsEventGroup(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Register(IReadOnlyDictionary<string, List<string>> regions, IReadOnlyDictionary<string, string> selections)
    {
        RegionGroups = regions.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var id in regions.Values.SelectMany(ids => ids).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (KnownGroups.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
            // Preserve old explicit skins during migration. Never replace an already chosen skin
            // merely because its region has just been discovered by the new priority UI.
            if (selections.TryGetValue(id, out var selected) && selected != SkinCatalog.BaseOptionId &&
                !ManualGroups.Contains(id, StringComparer.OrdinalIgnoreCase)) ManualGroups.Add(id);
            KnownGroups.Add(id);
        }
    }
}

internal static class EventSkinPriorityPolicy
{
    public static List<EventSkinPriorityEntry> Entries(IEnumerable<EventSkinPriorityEntry> stored, IEnumerable<string> available)
    {
        var result = stored.Where(e => e != null && !string.IsNullOrWhiteSpace(e.OptionId))
            .DistinctBy(e => e.OptionId, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var id in available)
            if (!result.Any(e => e.OptionId.Equals(id, StringComparison.OrdinalIgnoreCase))) result.Add(new(id, true));
        return result;
    }

    public static string Resolve(IEnumerable<EventSkinPriorityEntry> entries, IEnumerable<string> available)
    {
        var options = available.ToArray();
        foreach (var entry in entries.Where(e => e.Enabled))
        {
            var actual = options.FirstOrDefault(id => id.Equals(entry.OptionId, StringComparison.OrdinalIgnoreCase));
            if (actual != null) return actual;
        }
        return SkinCatalog.BaseOptionId;
    }
}
