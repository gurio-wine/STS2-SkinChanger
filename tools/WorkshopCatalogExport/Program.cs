using System.Text.Json;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;
using STS2SkinChanger.Pck;

// Developer-only local inventory export. The game never scans subscriptions to populate its browser.
if (args.Length == 5 && args[0] == "--refresh-restart-hints")
{
    // Only inspect curated, locally installed packages. Never run provider code.
    // Absence of startup code alone does not prove complete resource takeover.
    if (!File.Exists(args[1])) throw new FileNotFoundException("Game PCK required for resource coverage checks.", args[1]);
    var existing = WorkshopCatalogPolicy.Parse(File.ReadAllText(args[3]));
    CardCatalogEntry[]? baselineCards = null;
    var missing = 0;
    var audited = existing.Select(item =>
    {
        var directory = Path.Combine(args[2], item.Id.ToString());
        if (!Directory.Exists(directory))
        {
            missing++;
            Console.WriteLine($"{item.Id}: startup-only fallback (local package unavailable; NOT audited)");
            return item with { RestartRequired = true };
        }
        var before = WorkshopPackagePolicy.Snapshot(directory);
        // A catalog-wide promise cannot rely on another mod already being loaded.
        var assessment = WorkshopPackagePolicy.Assess(directory, args[4], new Dictionary<string, string>());
        var reason = assessment.Reason;
        if (assessment.CanInspectResources)
        {
            // Cheap negative proof, using the exact same format gate as hot registration.
            var unsupported = assessment.Mods.Any(mod =>
            {
                using var archive = PckArchive.Open(mod.PckPath!);
                return archive.Paths.Any(path => !SkinCatalog.SupportsWorkshopResourcePath(path));
            });
            if (unsupported) reason = WorkshopLoadReason.IncompleteResources;
            else
            {
                baselineCards ??= ReadCards([args[1]]);
                using var candidate = SkinCatalog.Build(args[1], assessment.Mods);
                candidate.FinalizeCardGroups(baselineCards);
                if (!assessment.Mods.All(mod => candidate.HasCompleteWorkshopResourceCoverage(mod.Id)))
                    reason = WorkshopLoadReason.IncompleteResources;
            }
        }
        if (!WorkshopPackagePolicy.Unchanged(directory, before))
            throw new IOException($"Package changed during audit: {item.Id}; catalog not written.");
        Console.WriteLine($"{item.Id}: {(reason == WorkshopLoadReason.None ? "hot" : "restart")} ({reason})");
        return item with { RestartRequired = reason != WorkshopLoadReason.None };
    }).ToArray();
    File.WriteAllText(args[3], JsonSerializer.Serialize(audited, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }).Replace("\r\n", "\n") + "\n");
    Console.WriteLine($"Checked {audited.Length - missing}/{audited.Length} local packages for {args[4]}: {audited.Count(item => !item.RestartRequired)} complete hot-load candidates; {missing} unavailable packages kept startup-only.");
    return;
}
if (args.FirstOrDefault() == "--refresh-restart-hints")
    throw new ArgumentException("--refresh-restart-hints <game.pck> <installed Workshop root> <catalog.json> <game version>");
if (args.Length != 3) throw new ArgumentException("WorkshopCatalogExport <game.pck> <installed Workshop root> <output.json>");
var mods = new List<SkinModDescriptor>();
foreach (var path in Directory.EnumerateFiles(args[1], "*.json", SearchOption.AllDirectories))
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path).TrimStart('\uFEFF'));
        var j = doc.RootElement;
        if (j.ValueKind != JsonValueKind.Object || !j.TryGetProperty("id", out var idValue) || !j.TryGetProperty("has_pck", out var hp)) continue;
        var id = idValue.GetString()!;
        var root = Path.GetDirectoryName(path)!;
        var pck = hp.GetBoolean() ? Path.Combine(root, (j.TryGetProperty("pck_name", out var pn) ? pn.GetString() : id) + ".pck") : null;
        if (pck != null && !File.Exists(pck)) continue;
        mods.Add(new(id, j.TryGetProperty("name", out var n) ? n.GetString() ?? id : id, pck,
            !j.TryGetProperty("affects_gameplay", out var ag) || ag.GetBoolean(), root,
            j.TryGetProperty("has_dll", out var hd) && hd.GetBoolean()));
    }
    catch (JsonException) { }
    catch (InvalidOperationException) { }
}
using var catalog = SkinCatalog.Build(args[0], mods);
catalog.FinalizeCardGroups(ReadCards(mods.Where(m => m.AffectsGameplay && m.PckPath != null).Select(m => m.PckPath!).Prepend(args[0]).Distinct()));
var entries = catalog.ExportWorkshopCatalog();
File.WriteAllText(args[2], JsonSerializer.Serialize(entries, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }).Replace("\r\n", "\n") + "\n");
Console.WriteLine($"Exported {entries.Length} recognized Workshop items from {mods.Count} installed manifests. Startup-only by default; run --refresh-restart-hints before publishing.");

static CardCatalogEntry[] ReadCards(IEnumerable<string> packs)
{
    var cards = new Dictionary<string, CardCatalogEntry>();
    foreach (var pck in packs)
    {
        using var archive = PckArchive.Open(pck);
        foreach (var path in archive.Paths)
        {
            var source = path.EndsWith(".import") ? path[..^7] : path.EndsWith(".remap") ? path[..^6] : path;
            var marker = new[] { "/card_portraits/", "/card_atlas.sprites/" }.FirstOrDefault(source.Contains);
            if (marker == null) continue;
            var tail = source[(source.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
            if (!tail.Contains('/')) continue;
            var group = tail.Split('/')[0];
            var stem = Path.GetFileNameWithoutExtension(source);
            cards.TryAdd(group + "/" + stem, new(stem, source, group, group, group));
        }
    }
    return cards.Values.ToArray();
}
