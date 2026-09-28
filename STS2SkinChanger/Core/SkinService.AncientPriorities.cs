using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal static partial class SkinService
{
    public const string AncientRegionKey = "all_ancients";
    public const string InheritAncientSelectionId = "__ancient_category__";

    public static void InitializeAncientSkinCategories()
    {
        if (Catalog == null) return;
        try
        {
            var ancientGroupIds = ModelDb.AllAncients
                .Select(a => a.Id.Entry.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(id => Catalog.Groups.Any(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (ancientGroupIds.Count == 0) return;

            var regions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [AncientRegionKey] = ancientGroupIds
            };

            ChangeAncientPriorityConfiguration(() =>
                Config.AncientSkinPriorities.Register(regions, Config.Selections));
        }
        catch (Exception ex)
        {
            ModLog.Warn("初始化先古优先级失败：" + ex.GetBaseException().Message);
        }
    }

    private static SkinGroup[] GetAncientGroups() =>
        Config.AncientSkinPriorities.RegionGroups.TryGetValue(AncientRegionKey, out var ids) && Catalog != null
            ? Catalog.Groups.Where(g => ids.Contains(g.Id, StringComparer.OrdinalIgnoreCase)).ToArray()
            : [];

    private static List<EventSkinPriorityEntry> GetAncientPriorityEntries() =>
        EventSkinPriorityPolicy.Entries(
            Config.AncientSkinPriorities.Priorities.GetValueOrDefault(AncientRegionKey) ?? [],
            GetAncientGroups().SelectMany(g => g.Options).Select(o => o.Id));

    public static IReadOnlyList<EventPriorityOptionState> GetAncientPriorityOptions()
    {
        lock (Sync)
        {
            var groups = GetAncientGroups();
            var options = groups.SelectMany(g => g.Options)
                .DistinctBy(o => o.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);

            return GetAncientPriorityEntries()
                .Where(e => options.ContainsKey(e.OptionId))
                .Select(e => new EventPriorityOptionState(
                    e.OptionId,
                    options[e.OptionId].Name,
                    e.Enabled,
                    groups.Count(g => g.Options.Any(o => o.Id.Equals(e.OptionId, StringComparison.OrdinalIgnoreCase))),
                    groups.Length))
                .ToArray();
        }
    }

    public static bool SetAncientPriorityOptionEnabled(string optionId, bool enabled) =>
        EditAncientPriority(optionId, entries => entries.Select(e =>
            e.OptionId.Equals(optionId, StringComparison.OrdinalIgnoreCase) ? e with { Enabled = enabled } : e).ToList());

    public static bool MoveAncientPriority(string optionId, int offset) =>
        EditAncientPriority(optionId, entries =>
        {
            var visible = GetAncientPriorityOptions().Select(o => o.OptionId).ToList();
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

    private static bool EditAncientPriority(string optionId, Func<List<EventSkinPriorityEntry>, List<EventSkinPriorityEntry>> edit)
    {
        lock (Sync)
        {
            if (!GetAncientPriorityOptions().Any(o => o.OptionId.Equals(optionId, StringComparison.OrdinalIgnoreCase)))
            {
                LastError = $"未知的先古皮肤选项：{optionId}";
                return false;
            }
            return ChangeAncientPriorityConfiguration(() =>
                Config.AncientSkinPriorities.Priorities[AncientRegionKey] = edit(GetAncientPriorityEntries()));
        }
    }

    public static bool HasAncientSkinCategory(string groupId) =>
        Config.AncientSkinPriorities.RegionGroups.Values
            .Any(ids => ids.Contains(groupId, StringComparer.OrdinalIgnoreCase));

    public static string GetAncientOverrideSelection(string groupId) =>
        Config.AncientSkinPriorities.IsFollowing(groupId)
            ? InheritAncientSelectionId
            : Config.GetSelection(groupId);

    public static bool FollowAncientCategoryPriority(string groupId)
    {
        lock (Sync)
        {
            if (!HasAncientSkinCategory(groupId))
            {
                LastError = $"先古 {groupId} 尚未登记。";
                return false;
            }
            return ChangeAncientPriorityConfiguration(() =>
                Config.AncientSkinPriorities.ManualGroups.RemoveAll(id => id.Equals(groupId, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public static bool SetAncientManualSelection(string groupId)
    {
        lock (Sync)
        {
            if (!Config.AncientSkinPriorities.ManualGroups.Contains(groupId, StringComparer.OrdinalIgnoreCase))
            {
                Config.AncientSkinPriorities.ManualGroups.Add(groupId);
            }
            return true;
        }
    }

    private static bool ChangeAncientPriorityConfiguration(Action mutation)
    {
        if (Catalog == null)
        {
            LastError = "皮肤目录尚未初始化。";
            return false;
        }

        var previousSettings = Config.AncientSkinPriorities.Clone();
        var previousSelections = new Dictionary<string, string>(Config.Selections, StringComparer.OrdinalIgnoreCase);
        var previousProviders = Config.VisualProviderPriority.ToList();
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            mutation();
            foreach (var update in BuildAncientPrioritySelectionUpdates())
            {
                Config.Selections[update.Key] = update.Value;
                affected.Add(update.Key);
            }

            foreach (var id in affected)
            {
                ClearRuntimeResourceCache(id);
                UpdateVisualProviderPriority(id, Config.GetSelection(id));
            }

            if (affected.Count > 0) MountOverlay(affected);
            Config.Save(ConfigPath);
            LastError = null;
            return true;
        }
        catch (Exception exception)
        {
            Config.AncientSkinPriorities = previousSettings;
            Config.Selections = previousSelections;
            Config.VisualProviderPriority = previousProviders;
            foreach (var id in affected) ClearRuntimeResourceCache(id);
            if (affected.Count > 0) TryRestoreOverlay(affected, cardOverlay: false);
            LastError = exception.Message;
            ModLog.Error("调整先古优先级失败：" + exception);
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string> BuildAncientPrioritySelectionUpdates()
    {
        var catalog = Catalog!;
        var entries = GetAncientPriorityEntries();
        Config.AncientSkinPriorities.Priorities[AncientRegionKey] = entries;

        var pools = GetAncientGroups()
            .Where(g => Config.AncientSkinPriorities.IsFollowing(g.Id))
            .ToDictionary(g => g.Id, g => (Group: g, Entries: entries), StringComparer.OrdinalIgnoreCase);

        var desired = pools.ToDictionary(
            p => p.Key,
            p => EventSkinPriorityPolicy.Resolve(p.Value.Entries, p.Value.Group.Options.Select(o => o.Id)),
            StringComparer.OrdinalIgnoreCase);

        var working = new Dictionary<string, string>(Config.Selections, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, selected) in desired)
        {
            if (working.GetValueOrDefault(id, SkinCatalog.BaseOptionId).Equals(selected, StringComparison.OrdinalIgnoreCase))
                continue;

            working[id] = selected;
        }

        return working
            .Where(p => !Config.GetSelection(p.Key).Equals(p.Value, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }
}