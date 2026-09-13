using System.Text.Json;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

/// <summary>
/// Read-only check that a GitHub release folder is recognized through the same offline path
/// the Workshop submission scan uses: the folder is staged below a "github/&lt;id&gt;" path so the
/// catalog binds the synthetic source id, and the exported targets must match the package.
/// No provider initializer runs and no game process is started.
/// </summary>
internal static class GitHubRecognitionAudit
{
    internal static void Run(string gamePck, string root, string owner, string repo, string[] baselineDirectories)
    {
        var id = SkinSourceIdentity.GitHubId(owner, repo);
        Console.WriteLine($"source id={id} github={SkinSourceIdentity.IsGitHub(id)} root={root}");
        Console.WriteLine($"workshop source id={SkinCatalog.WorkshopSourceId(root)} (expected {id})");
        var cards = CardArtPackRecognitionAudit.CardEntries(gamePck);
        // An art pack that only replaces the art of another Mod is recognized against that Mod's
        // own archive, exactly like the live scan, which takes every installed provider as its
        // baseline. Without it such a package can never be recognized - offline or in game.
        var baselines = baselineDirectories.SelectMany(ReadBaselines).ToArray();
        Console.WriteLine($"baselines: {string.Join(", ", baselines.Select(mod => mod.Id + "@" + mod.RootPath))}");
        var result = WorkshopSubmissionScanner.Scan(new(id, repo, root), gamePck, cards, "0.107.1", new Dictionary<string, string>(), baselines);
        Console.WriteLine(result.Item == null
            ? $"recognition failed: {result.Error}"
            : "recognition targets=" + string.Join("|", result.Item.Targets.Select(target => target.Kind + ":" + target.Target)) +
              $" restart={result.Item.RestartRequired} provider={result.ProviderId}");
        Console.WriteLine("descriptors: " + string.Join(", ", WorkshopSubmissionScanner.ReadDescriptors(root)
            .Select(mod => mod.Id + "@" + mod.RootPath)));
    }

    // The live scan takes every loaded Mod as its baseline - art packs and gameplay Mods alike -
    // because a pack that only replaces another Mod's art can only be recognized against that
    // Mod's own archive. This mirrors the manifest reading the game feeds the catalog with, so a
    // baseline directory can be handed over exactly as it sits in the workshop folder.
    private static IEnumerable<SkinModDescriptor> ReadBaselines(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(path).TrimStart('\uFEFF')); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { continue; }
            using (doc)
            {
                var j = doc.RootElement;
                if (j.ValueKind != JsonValueKind.Object || !j.TryGetProperty("id", out var idField) ||
                    idField.ValueKind != JsonValueKind.String || !j.TryGetProperty("has_pck", out var hasPck) ||
                    hasPck.ValueKind != JsonValueKind.True) continue;
                var id = idField.GetString()!;
                var root = Path.GetDirectoryName(path)!;
                var pckName = j.TryGetProperty("pck_name", out var declared) && declared.ValueKind == JsonValueKind.String
                    ? declared.GetString()! : id;
                var pck = Path.Combine(root, pckName + ".pck");
                if (!File.Exists(pck)) pck = SkinPackagePaths.Resolve(root, id, ".pck");
                if (!File.Exists(pck)) continue;
                var gameplay = j.TryGetProperty("affects_gameplay", out var affects) && affects.ValueKind == JsonValueKind.True;
                var name = j.TryGetProperty("name", out var nameField) && nameField.ValueKind == JsonValueKind.String
                    ? nameField.GetString()! : id;
                yield return new SkinModDescriptor(id, string.IsNullOrWhiteSpace(name) ? id : name, pck, gameplay, root, false);
            }
        }
    }
}
