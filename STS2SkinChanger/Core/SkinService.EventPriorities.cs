using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal static partial class SkinService
{
    public const string InheritEventSelectionId = "__event_category__";
    internal sealed record EventPriorityOptionState(string OptionId, string Name, bool Enabled, int Coverage, int TotalEvents);

    private static void InitializeEventSkinCategoriesAfterModels()
    {
        if (Catalog == null) return;
        try
        {
            var managed = Catalog.Groups.Where(g => EventSkinPolicy.IsEventGroup(g.Id) && g.Options.Count > 0)
                .Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var regions = EventRegionPolicy.Group(ModelDb.AllEvents.Select(e => e.Id.Entry),
                ModelDb.Acts.ToDictionary(a => a.Id.Entry, a => a.AllEvents.Select(e => e.Id.Entry)),
                ModelDb.AllSharedEvents.Select(e => e.Id.Entry));
            var registered = regions.ToDictionary(p => p.Key,
                    p => p.Value.Select(EventSkinPolicy.GroupId).Where(managed.Contains).ToList(), StringComparer.OrdinalIgnoreCase)
                .Where(p => p.Value.Count > 0).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (!ChangeEventPriorityConfiguration(() => Config.EventSkinPriorities.Register(registered, Config.Selections)))
                ModLog.Warn("初始化事件地区优先级失败：" + LastError);
        }
        catch (Exception exception)
        {
            // A modded event pool failing to enumerate must not abort card/monster startup.
            ModLog.Warn("无法登记事件地区优先级，保留当前事件皮肤选择：" + exception.GetBaseException().Message);
        }
    }

    private static SkinGroup[] GetEventRegionGroups(string region) =>
        Config.EventSkinPriorities.RegionGroups.TryGetValue(region, out var ids) && Catalog != null
            ? Catalog.Groups.Where(g => EventSkinPolicy.IsEventGroup(g.Id) && ids.Contains(g.Id, StringComparer.OrdinalIgnoreCase)).ToArray() : [];

    private static List<EventSkinPriorityEntry> GetEventPriorityEntries(string region) => EventSkinPriorityPolicy.Entries(
        Config.EventSkinPriorities.Priorities.GetValueOrDefault(region) ?? [],
        GetEventRegionGroups(region).SelectMany(g => g.Options).Select(o => o.Id));

    public static IReadOnlyList<EventPriorityOptionState> GetEventPriorityOptions(string region)
    {
        lock (Sync)
        {
            var groups = GetEventRegionGroups(region);
            var options = groups.SelectMany(g => g.Options).DistinctBy(o => o.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);
            return GetEventPriorityEntries(region).Where(e => options.ContainsKey(e.OptionId)).Select(e =>
                new EventPriorityOptionState(e.OptionId, options[e.OptionId].Name, e.Enabled,
                    groups.Count(g => g.Options.Any(o => o.Id.Equals(e.OptionId, StringComparison.OrdinalIgnoreCase))), groups.Length)).ToArray();
        }
    }

    public static bool SetEventPriorityOptionEnabled(string region, string optionId, bool enabled) =>
        EditEventPriority(region, optionId, entries => entries.Select(e =>
            e.OptionId.Equals(optionId, StringComparison.OrdinalIgnoreCase) ? e with { Enabled = enabled } : e).ToList());

    public static bool MoveEventPriority(string region, string optionId, int offset) => EditEventPriority(region, optionId, entries =>
    {
        // Uninstalled sources remain in storage, but are not counted as visible arrow steps.
        var visible = GetEventPriorityOptions(region).Select(o => o.OptionId).ToList();
        var index = visible.FindIndex(id => id.Equals(optionId, StringComparison.OrdinalIgnoreCase));
        var target = Math.Clamp(index + offset, 0, visible.Count - 1);
        if (target == index) return entries;
        var sourceIndex = entries.FindIndex(e => e.OptionId.Equals(optionId, StringComparison.OrdinalIgnoreCase));
        var targetIndex = entries.FindIndex(e => e.OptionId.Equals(visible[target], StringComparison.OrdinalIgnoreCase));
        var moved = entries[sourceIndex];
        entries.RemoveAt(sourceIndex);
        entries.Insert(targetIndex, moved);
        return entries;
    });

    private static bool EditEventPriority(string region, string optionId, Func<List<EventSkinPriorityEntry>, List<EventSkinPriorityEntry>> edit)
    {
        lock (Sync)
        {
            if (!GetEventPriorityOptions(region).Any(o => o.OptionId.Equals(optionId, StringComparison.OrdinalIgnoreCase)))
            { LastError = $"未知的事件皮肤选项：{region}/{optionId}"; return false; }
            return ChangeEventPriorityConfiguration(() => Config.EventSkinPriorities.Priorities[region] = edit(GetEventPriorityEntries(region)));
        }
    }

    public static bool HasEventSkinCategory(string groupId) => Config.EventSkinPriorities.RegionGroups.Values
        .Any(ids => ids.Contains(groupId, StringComparer.OrdinalIgnoreCase));

    public static string GetEventOverrideSelection(string groupId) => Config.EventSkinPriorities.IsFollowing(groupId)
        ? InheritEventSelectionId : Config.GetSelection(groupId);

    public static bool FollowEventCategoryPriority(string groupId)
    {
        lock (Sync)
        {
            if (!HasEventSkinCategory(groupId)) { LastError = $"事件 {groupId} 尚未登记地区。"; return false; }
            return ChangeEventPriorityConfiguration(() => Config.EventSkinPriorities.ManualGroups
                .RemoveAll(id => id.Equals(groupId, StringComparison.OrdinalIgnoreCase)));
        }
    }

    // One atomic resource transaction for all changed events. Do not invoke event actions,
    // BeginEvent, rewards or voting; only the existing presentation refresh is used afterwards.
    private static bool ChangeEventPriorityConfiguration(Action mutation)
    {
        if (Catalog == null) { LastError = "皮肤目录尚未初始化。"; return false; }
        var previousSettings = Config.EventSkinPriorities.Clone();
        var previousSelections = new Dictionary<string, string>(Config.Selections, StringComparer.OrdinalIgnoreCase);
        var previousProviders = Config.VisualProviderPriority.ToList();
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            mutation();
            foreach (var update in BuildEventPrioritySelectionUpdates())
            {
                Config.Selections[update.Key] = update.Value;
                affected.Add(update.Key);
            }
            foreach (var id in affected) { ClearRuntimeResourceCache(id); UpdateVisualProviderPriority(id, Config.GetSelection(id)); }
            if (affected.Count > 0) MountOverlay(affected);
            Config.Save(ConfigPath);
            foreach (var id in affected) EventSkinRuntime.RefreshCurrent(id);
            LastError = null;
            return true;
        }
        catch (Exception exception)
        {
            Config.EventSkinPriorities = previousSettings;
            Config.Selections = previousSelections;
            Config.VisualProviderPriority = previousProviders;
            foreach (var id in affected) ClearRuntimeResourceCache(id);
            if (affected.Count > 0) TryRestoreOverlay(affected, cardOverlay: false);
            LastError = exception.Message;
            ModLog.Error("调整事件地区优先级失败：" + exception);
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string> BuildEventPrioritySelectionUpdates()
    {
        var catalog = Catalog!;
        var pools = new Dictionary<string, (SkinGroup Group, List<EventSkinPriorityEntry> Entries)>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in Config.EventSkinPriorities.RegionGroups.Keys)
        {
            var entries = GetEventPriorityEntries(region);
            Config.EventSkinPriorities.Priorities[region] = entries;
            foreach (var group in GetEventRegionGroups(region).Where(g => Config.EventSkinPriorities.IsFollowing(g.Id)))
            {
                // A shared event still has one presentation. If a mod puts it in multiple
                // region pools, use the first registered pool consistently across reloads.
                pools.TryAdd(group.Id, (group, entries));
            }
        }
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> desired;
        while (true)
        {
            desired = pools.ToDictionary(p => p.Key, p => EventSkinPriorityPolicy.Resolve(p.Value.Entries,
                p.Value.Group.Options.Where(o => !excluded.Contains(catalog.ResolveVisualProviderId(o.Id))).Select(o => o.Id)),
                StringComparer.OrdinalIgnoreCase);
            // Match monster priorities: a non-isolatable runtime provider is eligible only
            // when every group it owns follows this same provider, never by overriding a
            // manual event or a character/merchant outside the event category system.
            var invalid = desired.Values.Select(catalog.ResolveVisualProviderId).Where(catalog.ProviderUsesFullRuntime)
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(provider => catalog.GetFullRuntimeProviderGroups(provider)
                    .Any(id => !desired.TryGetValue(id, out var option) ||
                        !catalog.ResolveVisualProviderId(option).Equals(provider, StringComparison.OrdinalIgnoreCase)))
                .Where(excluded.Add).ToArray();
            if (invalid.Length == 0) break;
        }
        var working = new Dictionary<string, string>(Config.Selections, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, selected) in desired)
        {
            if (working.GetValueOrDefault(id, SkinCatalog.BaseOptionId).Equals(selected, StringComparison.OrdinalIgnoreCase)) continue;
            var updates = catalog.BuildVisualSelectionTransaction(id, selected, working);
            if (updates.Keys.Any(key => !EventSkinPolicy.IsEventGroup(key)))
                throw new InvalidOperationException("该事件皮肤要求同时修改其它外观组，不能按事件地区批量应用。");
            foreach (var update in updates)
            {
                if (update.Key != id && (!desired.TryGetValue(update.Key, out var expected) || expected != update.Value))
                    throw new InvalidOperationException("该事件皮肤无法独立于其它事件切换。");
                working[update.Key] = update.Value;
            }
        }
        return working.Where(p => !Config.GetSelection(p.Key).Equals(p.Value, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }
}
