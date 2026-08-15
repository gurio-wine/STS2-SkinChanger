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
            var id = json.GetProperty("id").GetString()!;
            var name = json.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? id : id;
            var pckName = json.TryGetProperty("pck_name", out var pckNameValue) ? pckNameValue.GetString() ?? id : id;
            var rootPath = System.IO.Path.GetDirectoryName(manifestPath)!;
            var hasPck = json.TryGetProperty("has_pck", out var hasPckValue) && hasPckValue.GetBoolean();
            var hasDll = json.TryGetProperty("has_dll", out var hasDllValue) && hasDllValue.GetBoolean();
            var pckPath = hasPck ? System.IO.Path.Combine(rootPath, pckName + ".pck") : null;
            var affectsGameplay = !json.TryGetProperty("affects_gameplay", out var gameplayValue) || gameplayValue.GetBoolean();
            descriptors.Add(new SkinModDescriptor(id, name, pckPath, affectsGameplay, rootPath, hasDll));
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

foreach (var group in catalog.CardGroups)
{
    Console.WriteLine($"cards:{group.Id}\t{group.DisplayName}卡牌");
    foreach (var option in group.Options)
    {
        Console.WriteLine(
            $"  {option.Id}\t{option.Name}\t" +
            $"{option.NormalPortraits.Count} normal, {option.AncientPortraits.Count} ancient");
    }
}

if (runtimeIndex >= 0)
{
    if (args.Length < runtimeIndex + 5)
    {
        throw new ArgumentException("--runtime-scene requires: <group> <selection> <scene> <output.pck>");
    }

    var scenePaths = args[runtimeIndex + 3].Split(';', StringSplitOptions.RemoveEmptyEntries);
    var includeProviderDependencies = !args
        .Skip(runtimeIndex + 5)
        .Contains("--no-provider-dependencies", StringComparer.OrdinalIgnoreCase);
    var overlay = catalog.BuildRuntimeResourceOverlay(
        args[runtimeIndex + 1],
        args[runtimeIndex + 2],
        scenePaths,
        "inspect/001",
        includeProviderDependencies);
    PckArchive.Write(args[runtimeIndex + 4], overlay.Files);
    Console.WriteLine($"runtime resources: {string.Join(", ", overlay.ResourcePaths.Values)} ({overlay.Files.Count} files)");
}
