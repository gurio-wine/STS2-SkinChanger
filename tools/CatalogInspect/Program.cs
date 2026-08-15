using System.Text.Json;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "usage: CatalogInspect <game.pck> <mod-root> [<mod-root> ...] " +
        "[--runtime-scene <group> <selection> <scene> <output.pck> | --validate-runtime]");
    return;
}

var runtimeIndex = Array.IndexOf(args, "--runtime-scene");
var validateIndex = Array.IndexOf(args, "--validate-runtime");
var optionIndexes = new[] { runtimeIndex, validateIndex }.Where(index => index >= 0).ToArray();
var firstOptionIndex = optionIndexes.Length == 0 ? args.Length : optionIndexes.Min();
var modRoots = args.Skip(1).Take(firstOptionIndex - 1);
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

if (validateIndex >= 0)
{
    var failures = new List<string>();
    var validated = 0;
    foreach (var group in catalog.Groups)
    {
        var groupSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Id };
        var baselineOverlay = catalog.BuildOverlay(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            groupSet);
        foreach (var sourcePath in group.Options.SelectMany(option => option.Assets.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var baseline = catalog.ResolveBaseline(sourcePath);
            if (baseline != null && !ContainsAsset(baselineOverlay, sourcePath, baseline))
            {
                failures.Add($"{group.Id}/base: global overlay is missing {sourcePath}");
            }
        }

        var characterId = group.Id.ToLowerInvariant();
        var characterSelectPath = $"res://scenes/screens/char_select/char_select_bg_{characterId}.tscn";
        var ancientPath = $"res://scenes/events/background_scenes/{characterId}.tscn";
        var creaturePath = $"res://scenes/creature_visuals/{characterId}.tscn";
        string[] resourcePaths;
        if (catalog.ResolveBaseline(characterSelectPath) != null)
        {
            resourcePaths =
            [
                characterSelectPath,
                creaturePath,
                $"res://scenes/rest_site/characters/{characterId}_rest_site.tscn",
                $"res://scenes/merchant/characters/{characterId}_merchant.tscn",
                $"res://images/packed/character_select/char_select_{characterId}.png",
                $"res://images/packed/character_select/char_select_{characterId}_locked.png"
            ];
        }
        else if (catalog.ResolveBaseline(ancientPath) != null)
        {
            resourcePaths = [ancientPath];
        }
        else if (catalog.ResolveBaseline(creaturePath) != null)
        {
            resourcePaths = [creaturePath];
        }
        else
        {
            continue;
        }

        foreach (var option in group.Options)
        {
            var selectedOverlay = catalog.BuildOverlay(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [group.Id] = option.Id
                },
                groupSet);
            foreach (var asset in option.Assets)
            {
                if (!ContainsAsset(selectedOverlay, asset.Key, asset.Value))
                {
                    failures.Add($"{group.Id}/{option.Id}: global overlay is missing {asset.Key}");
                }
            }

            if (option.RuntimeImagePath != null)
            {
                if (!File.Exists(option.RuntimeImagePath))
                {
                    failures.Add($"{group.Id}/{option.Id}: missing external image {option.RuntimeImagePath}");
                }
                else
                {
                    validated++;
                }

                continue;
            }

            try
            {
                var overlay = catalog.BuildRuntimeResourceOverlay(
                    group.Id,
                    option.Id,
                    resourcePaths,
                    $"validate/{validated:D4}",
                    includeProviderDependencies: true);
                validated++;
                Console.WriteLine($"validated {group.Id}/{option.Id}: {overlay.Files.Count} files");
            }
            catch (Exception exception)
            {
                failures.Add($"{group.Id}/{option.Id}: {exception.Message}");
            }
        }
    }

    Console.WriteLine($"runtime validation: {validated} passed, {failures.Count} failed");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine("FAILED " + failure);
    }

    if (failures.Count > 0)
    {
        Environment.ExitCode = 1;
    }

    static bool ContainsResource(
        IReadOnlyDictionary<string, ResourceFile> files,
        string resourcePath) =>
        files.ContainsKey(resourcePath) ||
        files.ContainsKey(resourcePath + ".import") ||
        files.ContainsKey(resourcePath + ".remap");

    static bool ContainsAsset(
        IReadOnlyDictionary<string, ResourceFile> files,
        string resourcePath,
        ResourceAsset asset) =>
        !resourcePath.Equals(asset.SourcePath, StringComparison.OrdinalIgnoreCase)
            ? ContainsResource(files, resourcePath)
            : asset.Files.Any(file =>
                files.ContainsKey(file.Path) ||
                files.ContainsKey(SkinCatalog.NormalizeTakeoverPath(file.Path)));
}
