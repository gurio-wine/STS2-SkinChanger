using System.Text.Json;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;
using STS2SkinChanger.Pck;

// Developer-only local inventory export. The game never scans subscriptions to populate its browser.
if (args.Length == 3 && args[0] == "--refresh-restart-hints")
{
    // Only inspect IDs already curated in this file. Hints describe the audited
    // local snapshot; subscription always rechecks the actual downloaded package.
    var existing = WorkshopCatalogPolicy.Parse(File.ReadAllText(args[2]));
    var audited = existing.Select(item =>
    {
        var directory = Path.Combine(args[1], item.Id.ToString());
        return Directory.Exists(directory) ? item with { RestartRequired = WorkshopPackagePolicy.RequiresCodeAtStartup(directory) } : item;
    }).ToArray();
    File.WriteAllText(args[2], JsonSerializer.Serialize(audited, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }).Replace("\r\n", "\n") + "\n");
    Console.WriteLine($"Audited {audited.Length} curated IDs: {audited.Count(item => item.RestartRequired)} require startup code; others remain download-to-check.");
    return;
}
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
var cards = new Dictionary<string, CardCatalogEntry>();
foreach (var pck in mods.Where(m => m.AffectsGameplay && m.PckPath != null).Select(m => m.PckPath!).Prepend(args[0]).Distinct())
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
catalog.FinalizeCardGroups(cards.Values);
var entries = catalog.ExportWorkshopCatalog();
File.WriteAllText(args[2], JsonSerializer.Serialize(entries, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }).Replace("\r\n", "\n") + "\n");
Console.WriteLine($"Exported {entries.Length} recognized Workshop items from {mods.Count} installed manifests.");
