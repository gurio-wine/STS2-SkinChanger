using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using STS2SkinChanger.Core;
using STS2SkinChanger.Pck;

namespace STS2SkinChanger.Catalog;

internal sealed partial class SkinCatalog
{
    private static readonly ConditionalWeakTable<PckArchive, EventTables> EventTableCache = new();
    private sealed class EventTables
    {
        public Dictionary<string, Dictionary<string, string>> Languages { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static EventTables ReadEventTables(PckArchive archive) => EventTableCache.GetValue(archive, pack =>
    {
        var result = new EventTables();
        foreach (var path in pack.Paths.Where(path =>
                     path.Contains("/localization/", StringComparison.OrdinalIgnoreCase) &&
                     path.EndsWith("/events.json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            var language = path.Split('/')[^2];
            try
            {
                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    Encoding.UTF8.GetString(pack.ReadFile(path)).TrimStart('\uFEFF'));
                if (entries == null) continue;
                if (!result.Languages.TryGetValue(language, out var table))
                    result.Languages[language] = table = new(StringComparer.Ordinal);
                foreach (var entry in entries) table[entry.Key] = entry.Value;
            }
            catch (JsonException exception)
            {
                ModLog.Error($"事件语言表读取失败 {pack.Path}/{path}: {exception.Message}");
            }
        }
        return result;
    });

    private static HashSet<string> KnownEventIds(IEnumerable<PckResourceIndex> baselines)
    {
        var keys = baselines.SelectMany(index => ReadEventTables(index.Archive).Languages.Values)
            .SelectMany(table => table.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return keys.Where(key => key.EndsWith(".title", StringComparison.Ordinal) && key.Count(c => c == '.') == 1)
            .Select(EventSkinPolicy.EventIdFromKey).OfType<string>()
            .Where(id => !KnownAncientIds.Contains(id) && !keys.Contains(id + ".epithet"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddEventSkinGroups(
        IReadOnlyList<PckResourceIndex> indexes,
        IReadOnlyList<PckResourceIndex> baselines,
        IDictionary<string, SkinGroup> groups)
    {
        var knownIds = KnownEventIds(baselines);
        // The historical Ancient matcher accepts background_scenes/<id> for modded Ancients.
        // Keep that support; only move positively identified ordinary-event resources out of
        // the old bare-ID group. Never remove a monster/character with the same ID.
        foreach (var id in knownIds)
        {
            if (!groups.TryGetValue(id, out var existing)) continue;
            for (var i = existing.Options.Count - 1; i >= 0; i--)
            {
                var option = existing.Options[i];
                var retained = option.Assets.Where(pair => EventSkinPolicy.FindResourceOwner(pair.Key, knownIds) != id)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                if (retained.Count == 0 && !option.IsRuntimeProvider) existing.Options.RemoveAt(i);
                else existing.Options[i] = option with { Assets = retained };
            }
            if (existing.Options.Count == 0) groups.Remove(id);
        }
        foreach (var index in indexes)
        {
            // Raw images/audio are legal too; the general model index deliberately skips
            // most loose images. Limit lazy indexing to known event presentation roots.
            foreach (var path in index.Archive.Paths.Where(EventSkinPolicy.CouldOwnResource))
            {
                var source = path.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ? path[..^7] :
                    path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase) ? path[..^6] : path;
                if (EventSkinPolicy.FindResourceOwner(source, knownIds) != null &&
                    Path.GetExtension(source).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".tscn" or ".tres" or ".wav" or ".ogg" or ".mp3")
                    index.TryBuildAsset(source);
            }
            var assetsById = index.Assets.Values
                .Select(asset => (Asset: asset, Id: EventSkinPolicy.FindResourceOwner(asset.SourcePath, knownIds)))
                .Where(pair => pair.Id != null)
                .GroupBy(pair => pair.Id!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => (IReadOnlyDictionary<string, ResourceAsset>)group.ToDictionary(
                        pair => pair.Asset.SourcePath, pair => pair.Asset, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
            var textIds = ReadEventTables(index.Archive).Languages.SelectMany(table => table.Value
                    .Where(entry => !TryReadBaselineEventText(baselines, entry.Key, table.Key, out var original) ||
                                    entry.Value != original)
                    .Select(entry => entry.Key))
                .Select(EventSkinPolicy.EventIdFromKey).OfType<string>().Where(knownIds.Contains);
            foreach (var id in assetsById.Keys.Concat(textIds).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var groupId = EventSkinPolicy.GroupId(id);
                if (!groups.TryGetValue(groupId, out var group))
                    groups[groupId] = group = new SkinGroup(groupId, DisplayName(id));
                var assets = assetsById.GetValueOrDefault(id) ?? new Dictionary<string, ResourceAsset>();
                var existingIndex = group.Options.FindIndex(option => option.Id == index.Mod.Id);
                if (existingIndex < 0)
                    group.Options.Add(new SkinOption(index.Mod.Id, index.Mod.Name, assets));
                else
                {
                    var existing = group.Options[existingIndex];
                    var merged = new Dictionary<string, ResourceAsset>(existing.Assets, StringComparer.OrdinalIgnoreCase);
                    foreach (var asset in assets) merged[asset.Key] = asset.Value;
                    group.Options[existingIndex] = existing with { Assets = merged };
                }
            }
        }
    }

    public bool TryResolveEventText(string key, string language,
        IReadOnlyDictionary<string, string> selections, out string text)
    {
        text = string.Empty;
        var id = EventSkinPolicy.EventIdFromKey(key);
        if (id == null) return false;
        var groupId = EventSkinPolicy.GroupId(id);
        var group = Groups.FirstOrDefault(group => group.Id == groupId);
        if (group == null) return false;
        // Only provider-owned keys are intercepted. Preserve unrelated localization supplied
        // by gameplay Mods and shared UI strings instead of resetting an entire table.
        var providers = _cosmeticIndexes.Where(index => group.Options.Any(option => option.Id == index.Mod.Id)).ToArray();
        if (!providers.Any(index => ReadEventTables(index.Archive).Languages.Values.Any(table => table.ContainsKey(key))))
            return false;
        var selection = selections.GetValueOrDefault(groupId, BaseOptionId);
        var selected = providers.FirstOrDefault(index => index.Mod.Id == selection);
        if (selected != null && TryReadEventText(ReadEventTables(selected.Archive), key, language, out text))
            return true;
        if (TryReadBaselineEventText(_baselineIndexes, key, language, out text)) return true;
        return false;
    }

    private static bool TryReadBaselineEventText(IReadOnlyList<PckResourceIndex> baselines,
        string key, string language, out string text)
    {
        foreach (var locale in new[] { language, "eng" }.Distinct(StringComparer.OrdinalIgnoreCase))
            for (var i = baselines.Count - 1; i >= 0; i--)
                if (ReadEventTables(baselines[i].Archive).Languages.TryGetValue(locale, out var table) &&
                    table.TryGetValue(key, out text!)) return true;
        text = string.Empty;
        return false;
    }

    public bool HasEventResource(string groupId, string selection, string path) =>
        Groups.FirstOrDefault(group => group.Id == groupId)?.Options
            .FirstOrDefault(option => option.Id == selection)?.Assets.ContainsKey(path) == true ||
        _baselineIndexes.Any(index => index.Archive.Contains(path) ||
            index.Archive.Contains(path + ".import") || index.Archive.Contains(path + ".remap"));

    private static bool TryReadEventText(EventTables tables, string key, string language, out string text)
    {
        text = string.Empty;
        return (tables.Languages.TryGetValue(language, out var local) && local.TryGetValue(key, out text!)) ||
               (tables.Languages.TryGetValue("eng", out var fallback) && fallback.TryGetValue(key, out text!));
    }
}
