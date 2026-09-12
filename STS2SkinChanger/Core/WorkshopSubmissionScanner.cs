using System.Text.Json;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal sealed record WorkshopLocalSubmission(ulong Id, string Name, string Directory);
internal sealed record WorkshopScanResult(WorkshopCatalogItem? Item, string Name, string Error);
internal static class WorkshopSubmissionScanner
{
    internal static SkinModDescriptor[] ReadDescriptors(string directory)
    {
        var results = new List<SkinModDescriptor>();
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Take(20001).ToArray();
        if (files.Length > 20000) throw new InvalidDataException("Too many files in the selected Mod.");
        foreach (var path in files.Where(p => Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase)))
        {
            if (new FileInfo(path).Length > 1024 * 1024) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(path).TrimStart('\uFEFF')); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var j = doc.RootElement;
                if (j.ValueKind != JsonValueKind.Object || !j.TryGetProperty("id", out var idValue) ||
                    idValue.ValueKind != JsonValueKind.String || !j.TryGetProperty("has_pck", out var hp) ||
                    !j.TryGetProperty("affects_gameplay", out var gameplay) || gameplay.ValueKind != JsonValueKind.False) continue;
                var id = idValue.GetString()!;
                if (string.IsNullOrWhiteSpace(id) || Entry.IsSelfModId(id)) continue;
                var root = Path.GetDirectoryName(path)!;
                var hasDll = j.TryGetProperty("has_dll", out var hd) && hd.ValueKind == JsonValueKind.True;
                var hasPck = hp.ValueKind == JsonValueKind.True;
                var pck = hasPck ? SkinPackagePaths.Resolve(root, id, ".pck") : null;
                if (hasPck && j.TryGetProperty("pck_name", out var declared))
                {
                    var name = declared.GetString();
                    if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 ||
                        name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("Invalid declared PCK name.");
                    pck = Path.Combine(root, name + ".pck");
                }
                if (hasPck && !File.Exists(pck) || hasDll && !File.Exists(SkinPackagePaths.Resolve(root, id, ".dll")))
                    throw new InvalidDataException("Declared skin files are missing.");
                if (hasPck || hasDll) results.Add(new(id, j.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : id,
                    pck, false, root, hasDll));
            }
        }
        return results.DistinctBy(m => (m.Id, m.RootPath)).ToArray();
    }

    /// <summary>
    /// Names a folder can be offered under. A pure asset card-art pack ships no cosmetic
    /// manifest, so a folder that only carries a bundle must still be nameable and submittable.
    /// </summary>
    internal static string[] SubmittableNames(string directory)
    {
        var names = ReadDescriptors(directory).Select(mod => mod.Name).ToList();
        names.AddRange(CardArtPackScanner.Describe(directory).Select(pack => pack.Name));
        return names.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static WorkshopScanResult Scan(WorkshopLocalSubmission local, string gamePack, CardCatalogEntry[] cards,
        string? gameVersion, IReadOnlyDictionary<string, string> dependencies, SkinModDescriptor[]? baselines = null)
    {
        try
        {
            var before = WorkshopPackagePolicy.Snapshot(local.Directory);
            var mods = ReadDescriptors(local.Directory);
            // A pack is taken over straight from its bundle, exactly as at startup, so a folder
            // without any cosmetic manifest must be scannable through its artwork alone.
            var packs = CardArtPackScanner.Scan([local.Directory]).Packs;
            if (mods.Length == 0 && packs.Count == 0) return new(null, local.Name, "No cosmetic manifest.");
            using var catalog = SkinCatalog.Build(gamePack, (baselines ?? []).Where(b => b.AffectsGameplay &&
                !mods.Any(m => string.Equals(m.RootPath, b.RootPath, StringComparison.OrdinalIgnoreCase))).Concat(mods));
            if (packs.Count > 0) catalog.AttachCardArtPacks(packs, cards);
            catalog.FinalizeCardGroups(cards);
            var exported = catalog.ExportWorkshopCatalog();
            // Compare the exact installed source, not manifest ID: same-ID submissions coexist.
            var own = exported.FirstOrDefault(i => i.Id == local.Id);
            if (own == null) return new(null, local.Name, "No recognized skin resources.");
            var assessment = WorkshopPackagePolicy.Assess(local.Directory, gameVersion, dependencies);
            var hot = assessment.CanInspectResources && assessment.Mods.All(m => catalog.HasCompleteWorkshopResourceCoverage(m.Id));
            if (!WorkshopPackagePolicy.Unchanged(local.Directory, before)) return new(null, local.Name, "Files changed during scanning.");
            return new(own with { RestartRequired = !hot }, local.Name, "");
        }
        catch (Exception ex) { return new(null, local.Name, ex.GetBaseException().Message); }
    }
}
