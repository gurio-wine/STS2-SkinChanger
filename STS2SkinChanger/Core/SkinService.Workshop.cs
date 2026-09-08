using Godot;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal static partial class SkinService
{
    private static string? _workshopGamePack;
    private static SkinModDescriptor[] _workshopDescriptors = [];
    private static Dictionary<string, (long Length, DateTime Modified)> _workshopSourceStamps = [];
    private static readonly SemaphoreSlim WorkshopRegistrationGate = new(1);

    internal static async Task<WorkshopLoadReason> TryRegisterWorkshopResources(string directory)
    {
        await WorkshopRegistrationGate.WaitAsync();
        SkinCatalog? staged = null;
        try
        {
            SkinCatalog? baseline;
            SkinModDescriptor[] descriptors;
            string? gamePack;
            CardCatalogEntry[] cards;
            lock (Sync)
            {
                baseline = Catalog;
                descriptors = _workshopDescriptors;
                gamePack = _workshopGamePack;
                if (baseline == null || gamePack == null) return WorkshopLoadReason.ChangedFiles;
                if (!WorkshopSourcesUnchanged()) return WorkshopLoadReason.ChangedFiles;
                cards = ModelDb.AllCards.Select(card => new CardCatalogEntry(card.GetType().Name, FrameworkCardVisualGuard.GetBaselinePortraitPath(card),
                    GetCardPoolGroupId(card), GetCardCatalogGroupId(card), GetCardFilterGroupId(card))
                { IsCharacterPool = IsCharacterCardPool(card) }).ToArray();
            }
            var gameVersion = (HarmonyLib.AccessTools.Field(typeof(MegaCrit.Sts2.Core.Modding.ModManager), "_gameVersion")?.GetValue(null))?.ToString();
            var loaded = MegaCrit.Sts2.Core.Modding.ModManager.Mods
                .Where(mod => mod.state == MegaCrit.Sts2.Core.Modding.ModLoadState.Loaded && mod.manifest?.id != null)
                .GroupBy(mod => mod.manifest!.id!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().manifest!.version ?? "", StringComparer.OrdinalIgnoreCase);
            var before = await Task.Run(() => WorkshopPackagePolicy.Snapshot(directory));
            var package = await Task.Run(() => WorkshopPackagePolicy.Assess(directory, gameVersion, loaded));
            if (!package.CanInspectResources) return package.Reason;
            var additions = package.Mods;
            if (additions.Any(mod => descriptors.Any(old => old.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase)))) return WorkshopLoadReason.DuplicateId;
            var combined = descriptors.Concat(additions).ToArray();
            staged = await Task.Run<SkinCatalog?>(() =>
            {
                var candidate = SkinCatalog.Build(gamePack, combined);
                try
                {
                    candidate.FinalizeCardGroups(cards);
                    if (additions.All(mod => candidate.HasCompleteWorkshopResourceCoverage(mod.Id))) return candidate;
                    candidate.Dispose();
                    return null;
                }
                catch { candidate.Dispose(); throw; }
            });
            if (staged == null) return WorkshopLoadReason.IncompleteResources;
            if (!await Task.Run(() => WorkshopPackagePolicy.Unchanged(directory, before))) return WorkshopLoadReason.ChangedFiles;
            lock (Sync)
            {
                if (!ReferenceEquals(Catalog, baseline) || !WorkshopSourcesUnchanged()) return WorkshopLoadReason.ChangedFiles;
                staged.SynchronizeCharacterSkinCompositions(Config.CharacterSkinCompositions);
                var previousConfig = Config;
                var nextConfig = Config.CloneForBundleTransaction();
                var nextStamps = WorkshopSourceStamps(combined);
                Config = nextConfig;
                try
                {
                    // Do not auto-enable a freshly downloaded pack through a region/card priority.
                    // Preserve the previous entries and append only the new provider disabled.
                    var providerIds = additions.Select(mod => mod.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var group in staged.CardGroups)
                    {
                        var added = group.Options.Where(o => providerIds.Contains(o.ProviderId ?? o.Id)).Select(o => o.Id).Distinct().ToArray();
                        if (added.Length == 0) continue;
                        var entries = nextConfig.CardSkinPriorities.GetValueOrDefault(group.Id) ??
                            (baseline.CardGroups.FirstOrDefault(g => g.Id == group.Id) is { } oldGroup ? GetCardPriorityEntriesInternal(oldGroup).ToList() : []);
                        nextConfig.CardSkinPriorities[group.Id] = entries.Concat(added.Where(id => entries.All(e => e.OptionId != id)).Select(id => new CardSkinPriorityEntry(id, false))).ToList();
                    }
                    foreach (var act in ModelDb.Acts)
                    {
                        var category = "act:" + act.Id.Entry.ToLowerInvariant();
                        var ids = ResolveMonsterSkinGroupIds(staged, act.AllMonsters);
                        if (ids.Count == 0) continue;
                        var entries = GetMonsterPriorityEntriesInternal(category).ToList();
                        var added = staged.Groups.Where(g => ids.Contains(g.Id)).SelectMany(g => g.Options)
                            .Where(o => providerIds.Contains(o.EffectiveProviderId)).Select(o => o.Id).Distinct();
                        nextConfig.MonsterSkinPriorities[category] = entries.Concat(added.Where(id => entries.All(e => e.OptionId != id)).Select(id => new MonsterSkinPriorityEntry(id, false))).ToList();
                        nextConfig.MonsterSkinCategoryGroups[category] = ids.ToList();
                    }
                    var eventRegions = EventRegionPolicy.Group(ModelDb.AllEvents.Select(e => e.Id.Entry),
                        ModelDb.Acts.ToDictionary(a => a.Id.Entry, a => a.AllEvents.Select(e => e.Id.Entry)),
                        ModelDb.AllSharedEvents.Select(e => e.Id.Entry));
                    var managedEvents = staged.Groups.Where(g => EventSkinPolicy.IsEventGroup(g.Id) && g.Options.Count > 0).Select(g => g.Id).ToHashSet();
                    var registered = eventRegions.ToDictionary(p => p.Key, p => p.Value.Select(EventSkinPolicy.GroupId).Where(managedEvents.Contains).ToList());
                    foreach (var (region, ids) in registered)
                    {
                        var entries = GetEventPriorityEntries(region);
                        var added = staged.Groups.Where(g => ids.Contains(g.Id)).SelectMany(g => g.Options)
                            .Where(o => providerIds.Contains(o.EffectiveProviderId)).Select(o => o.Id).Distinct();
                        nextConfig.EventSkinPriorities.Priorities[region] = entries.Concat(added.Where(id => entries.All(e => e.OptionId != id)).Select(id => new EventSkinPriorityEntry(id, false))).ToList();
                    }
                    nextConfig.EventSkinPriorities.Register(registered, nextConfig.Selections);
                    nextConfig.Save(ConfigPath);
                }
                catch { Config = previousConfig; throw; }
                // No sanitize/default application or mounting here: registering a choice must
                // not alter current selections, active providers or existing overlays.
                // Don't Dispose the old catalog: ResourceFile holders in existing overlays keep
                // their PckArchive/FileStream alive. Once those references disappear, normal
                // FileStream finalization releases the handles. Do not retain entire catalogs.
                Catalog = staged;
                Config = nextConfig;
                staged = null;
                _workshopDescriptors = combined;
                _workshopSourceStamps = nextStamps;
                _cardLookupCache = new System.Runtime.CompilerServices.ConditionalWeakTable<CardModel, CardLookup>();
                CardCoverageCache.Clear();
            }
            return WorkshopLoadReason.None;
        }
        finally { staged?.Dispose(); WorkshopRegistrationGate.Release(); }
    }

    private static Dictionary<string, (long Length, DateTime Modified)> WorkshopSourceStamps(IEnumerable<SkinModDescriptor> mods) =>
        mods.Where(mod => mod.PckPath != null && File.Exists(mod.PckPath)).Select(mod => mod.PckPath!).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, path => { var file = new FileInfo(path); return (file.Length, file.LastWriteTimeUtc); }, StringComparer.OrdinalIgnoreCase);
    private static bool WorkshopSourcesUnchanged() => _workshopSourceStamps.All(pair =>
        File.Exists(pair.Key) && new FileInfo(pair.Key) is { } file && (file.Length, file.LastWriteTimeUtc) == pair.Value);

}
