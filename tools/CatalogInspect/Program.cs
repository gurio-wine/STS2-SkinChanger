using System.Text.Json;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: CatalogInspect <game.pck> <mod-root> [<mod-root> ...]");
    return;
}

var runtimeIndex = Array.IndexOf(args, "--runtime-scene");
var modRoots = runtimeIndex < 0 ? args.Skip(1) : args.Skip(1).Take(runtimeIndex - 1);
var descriptors = new List<SkinModDescriptor>();
foreach (var root in modRoots)
{
    foreach (var manifestPath in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath).TrimStart('\uFEFF'));
            var json = document.RootElement;
            if (!json.TryGetProperty("has_pck", out var hasPck) || !hasPck.GetBoolean())
            {
                continue;
            }

            var id = json.GetProperty("id").GetString()!;
            var name = json.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? id : id;
            var pckName = json.TryGetProperty("pck_name", out var pckNameValue) ? pckNameValue.GetString() ?? id : id;
            var pckPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(manifestPath)!, pckName + ".pck");
            var affectsGameplay = !json.TryGetProperty("affects_gameplay", out var gameplayValue) || gameplayValue.GetBoolean();
            descriptors.Add(new SkinModDescriptor(id, name, pckPath, affectsGameplay));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"skip {manifestPath}: {exception.Message}");
        }
    }
}

using var catalog = SkinCatalog.Build(args[0], descriptors);
foreach (var group in catalog.Groups)
{
    Console.WriteLine($"{group.Id}\t{group.DisplayName}");
    foreach (var option in group.Options)
    {
        Console.WriteLine($"  {option.Id}\t{option.Name}\t{option.Assets.Count} assets");
    }
}

if (runtimeIndex >= 0)
{
    if (args.Length < runtimeIndex + 5)
    {
        throw new ArgumentException("--runtime-scene requires: <group> <selection> <scene> <output.pck>");
    }

    var overlay = catalog.BuildRuntimeSceneOverlay(
        args[runtimeIndex + 1],
        args[runtimeIndex + 2],
        args[runtimeIndex + 3],
        "inspect/001");
    PckArchive.Write(args[runtimeIndex + 4], overlay.Files);
    Console.WriteLine($"runtime scene: {overlay.ScenePath} ({overlay.Files.Count} files)");
}
