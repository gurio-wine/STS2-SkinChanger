using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// UI-only metadata: never instantiate visual nodes, card views or skin providers.
internal sealed class WorkshopTargetNames
{
    private readonly Dictionary<(string Kind, string Id), string> _names = [];
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _regions = [];
    public Dictionary<string, string> RegionNames { get; } = [];
    internal static string Normalize(string id) => new(id.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    public string Name(string kind, string id)
    {
        if (_names.TryGetValue((kind, Normalize(id)), out var value)) return value;
        var entry = id.StartsWith(EventSkinPolicy.Prefix) ? id[EventSkinPolicy.Prefix.Length..] : id;
        var table = kind switch { "monster" or "companion" => "monsters", "character" or "cards" => "characters", _ => "events" };
        return Localize(table, entry.ToUpperInvariant() + (table == "monsters" ? ".name" : ".title"),
            SkinService.Catalog?.Groups.FirstOrDefault(g => g.Id == id)?.DisplayName ?? id.Replace('_', ' '));
    }
    public IReadOnlyDictionary<string, HashSet<string>> Regions(string kind) => _regions.GetValueOrDefault(kind) ?? [];
    private void Add(string kind, string id, string title) => _names[(kind, Normalize(id))] = Plain(title);
    private static string Plain(string text) => Regex.Replace(text, @"\[[^\]]*\]", "").Replace('\n', ' ');
    private static string Localize(string table, string key, string fallback)
    {
        try { return LocManager.Instance.GetTable(table).HasEntry(key) ? Plain(new LocString(table, key).GetFormattedText()) : fallback; }
        catch { return fallback; }
    }
    private static string SafeTitle(Func<string> read, string fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
    public static WorkshopTargetNames Build()
    {
        var result = new WorkshopTargetNames();
        foreach (var character in ModelDb.AllCharacters)
        {
            var title = SafeTitle(() => character.Title.GetFormattedText(), character.Id.Entry);
            result.Add("character", character.Id.Entry, title);
            result.Add("cards", character.CardPool.Title, title);
        }
        foreach (var (id, key) in new (string, WorkshopBrowserTextKey)[] {
            ("ancients", WorkshopBrowserTextKey.AncientCards), ("colorless", WorkshopBrowserTextKey.Colorless), ("misc", WorkshopBrowserTextKey.MiscCards),
            ("curse", WorkshopBrowserTextKey.Curse), ("event", WorkshopBrowserTextKey.Event), ("status", WorkshopBrowserTextKey.Status),
            ("token", WorkshopBrowserTextKey.Token), ("quest", WorkshopBrowserTextKey.Quest) }) result.Add("cards", id, WorkshopBrowserText.Get(key));
        foreach (var ancient in ModelDb.AllAncients) result.Add("ancient", ancient.Id.Entry, AncientCompendiumEntry.GetTitle(ancient));
        foreach (var creature in OtherCreatureCatalog.All) result.Add("companion", creature.Id,
            Localize(creature.LocalizationTable, creature.LocalizationKey, creature.FallbackTitle));
        result.Add("merchant", "merchant", Localize("map", "LEGEND_MERCHANT.title", WorkshopText.Kind("merchant")));
        result.Add("merchant", "fake_merchant_monster", Localize("events", "FAKE_MERCHANT.title", WorkshopText.Kind("merchant") + "???"));
        var acts = ModelDb.Acts.ToArray();
        var targets = SkinWorkshopService.Catalog.SelectMany(i => i.Targets).Where(WorkshopBrowserPolicy.VisibleTarget).Distinct().ToArray();
        var monsters = targets.Where(t => t.Kind == "monster").Select(t => t.Target).ToArray();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var regions = new Dictionary<string, HashSet<string>>();
        foreach (var act in acts)
        {
            result.RegionNames[act.Id.Entry] = SafeTitle(() => act.Title.GetFormattedText(), act.Id.Entry);
            var models = act.AllMonsters.ToArray();
            foreach (var monster in models) result.Add("monster", monster.Id.Entry, SafeTitle(() => monster.Title.GetFormattedText(), monster.Id.Entry));
            var tokens = models.Select(m => Normalize(m.Id.Entry)).ToHashSet();
            var members = monsters.Where(id => tokens.Contains(Normalize(id))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (members.Count > 0) regions[act.Id.Entry] = members;
            assigned.UnionWith(members);
        }
        var other = monsters.Where(id => !assigned.Contains(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var encounters = typeof(ModelDb).GetProperty("EventEncounters")?.GetValue(null) as IEnumerable<EncounterModel> ?? [];
        var eventMonsters = encounters.SelectMany(e => e.AllPossibleMonsters).DistinctBy(m => m.Id).ToArray();
        foreach (var monster in eventMonsters) result.Add("monster", monster.Id.Entry, SafeTitle(() => monster.Title.GetFormattedText(), monster.Id.Entry));
        var eventTokens = eventMonsters.Select(m => Normalize(m.Id.Entry)).ToHashSet();
        var eventMembers = monsters.Where(id => eventTokens.Contains(Normalize(id))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (eventMembers.Count > 0)
        {
            regions["events"] = eventMembers;
            result.RegionNames["events"] = Localize("bestiary", "EVENTS.title", WorkshopText.Kind("event"));
            other.ExceptWith(eventMembers);
        }
        if (other.Count > 0) regions[EventRegionPolicy.Other] = other;
        result._regions["monster"] = regions;
        var events = EventCompendiumPreview.Events();
        foreach (var model in events) result.Add("event", EventSkinPolicy.GroupId(model.Id.Entry), SafeTitle(() => EventCompendiumPreview.Title(model), model.Id.Entry));
        // Include curated events from absent content Mods under Other, rather than silently losing them.
        var eventIds = targets.Where(t => t.Kind == "event").Select(t => t.Target).ToArray();
        var grouped = EventRegionPolicy.Group(eventIds, acts.ToDictionary(a => a.Id.Entry, a => a.AllEvents.Select(e => EventSkinPolicy.GroupId(e.Id.Entry))),
            ModelDb.AllSharedEvents.Select(e => EventSkinPolicy.GroupId(e.Id.Entry)));
        result._regions["event"] = grouped.ToDictionary(p => p.Key, p => p.Value.ToHashSet(StringComparer.OrdinalIgnoreCase));
        result.RegionNames[EventRegionPolicy.Shared] = WorkshopBrowserText.Get(WorkshopBrowserTextKey.Shared);
        result.RegionNames[EventRegionPolicy.Other] = WorkshopBrowserText.Get(WorkshopBrowserTextKey.Other);
        return result;
    }
}
