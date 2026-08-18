using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
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

foreach (var option in catalog.PckCardOptions)
{
    var namespaceFiles = catalog.BuildCardProviderNamespaceOverlay(
        [option.ProviderId ?? option.Id]);
    Console.WriteLine(
        $"card-provider:{option.Id}\t{option.Name}\t{option.Assets.Count} assets, " +
        $"{namespaceFiles.Count} namespace files");
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
    var probes = SkinCatalog.ProbeSkinProviders(descriptors);
    foreach (var probe in probes)
    {
        Console.WriteLine(
            $"provider {probe.Id}: visual={probe.VisualGroupCount}, " +
            $"cards={probe.CardAssetCount}, images={probe.RuntimeImageCount}");
    }

    foreach (var option in catalog.PckCardOptions)
    {
        var variants = option.Assets.Keys
            .Select(GetCardVariantKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (variants.Length > 1)
        {
            failures.Add(
                $"cards/{option.Id}: one option still mixes variants {string.Join(", ", variants)}");
        }
    }

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

            var ownPaths = group.Options
                .SelectMany(candidate => candidate.Assets.Keys)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var foreignAsset in catalog.Groups
                         .Where(otherGroup => !otherGroup.Id.Equals(group.Id, StringComparison.OrdinalIgnoreCase))
                         .SelectMany(otherGroup => otherGroup.Options
                             .Where(candidate => candidate.Id.Equals(option.Id, StringComparison.OrdinalIgnoreCase)))
                         .SelectMany(candidate => candidate.Assets)
                         .Where(asset => !ownPaths.Contains(asset.Key)))
            {
                if (ContainsAsset(selectedOverlay, foreignAsset.Key, foreignAsset.Value))
                {
                    failures.Add(
                        $"{group.Id}/{option.Id}: global overlay leaked another group's asset {foreignAsset.Key}");
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
                var provider = descriptors.FirstOrDefault(descriptor =>
                    descriptor.Id.Equals(option.Id, StringComparison.OrdinalIgnoreCase));
                if (provider?.PckPath != null && File.Exists(provider.PckPath))
                {
                    using var providerArchive = PckArchive.Open(provider.PckPath);
                    foreach (var file in overlay.Files.Where(pair => MayContainReferences(pair.Key)))
                    {
                        var textResource = Encoding.UTF8.GetString(file.Value);
                        foreach (Match reference in ResourceReferenceRegex().Matches(textResource))
                        {
                            var referencedPath = reference.Value;
                            if (providerArchive.Contains(referencedPath) &&
                                !ContainsRuntimeResource(overlay, referencedPath))
                            {
                                failures.Add(
                                    $"{group.Id}/{option.Id}: runtime overlay is missing referenced provider resource {referencedPath}");
                            }
                        }
                    }
                }

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

    static string GetCardVariantKey(string path)
    {
        var lower = path.ToLowerInvariant();
        var markerEnd = -1;
        foreach (var marker in new[]
                 {
                     "/card_portraits/", "/card_atlas.sprites/", "/cards/",
                     "/card/", "/card_art/", "/cardart/"
                 })
        {
            var markerIndex = lower.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                markerEnd = markerIndex + marker.Length;
                break;
            }
        }

        if (markerEnd < 0)
        {
            return string.Empty;
        }

        var categoryEnd = lower.IndexOf('/', markerEnd);
        var fileSeparator = lower.LastIndexOf('/');
        var variant = categoryEnd < 0 || fileSeparator <= categoryEnd
            ? string.Empty
            : lower[(categoryEnd + 1)..fileSeparator].Trim('/');
        return variant.Equals("beta", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : variant;
    }

    static bool ContainsRuntimeResource(RuntimeResourceOverlay overlay, string path) =>
        overlay.Files.ContainsKey(path) ||
        overlay.Files.ContainsKey(path + ".import") ||
        overlay.Files.ContainsKey(path + ".remap") ||
        (overlay.SourceAliases.TryGetValue(path, out var sourceAlias) &&
         (overlay.Files.ContainsKey(sourceAlias) ||
          overlay.Files.ContainsKey(sourceAlias + ".import") ||
          overlay.Files.ContainsKey(sourceAlias + ".remap"))) ||
        (overlay.PayloadAliases.TryGetValue(path, out var payloadAlias) &&
         overlay.Files.ContainsKey(payloadAlias));

    static bool MayContainReferences(string path) =>
        path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".scn", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".res", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gdc", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".spatlas", StringComparison.OrdinalIgnoreCase);

}

partial class Program
{
    [GeneratedRegex(
        "res://[^\\x00\\\"'\\r\\n\\t \\]\\[(){}<>]+?\\.(?:spatlas|spskel|ctex|tscn|tres|gdc|gd|gdshader|scn|res|png|webp|jpe?g|svg|skel|atlas|json|ogg|wav|mp3)(?=[\\x00\\\"'\\r\\n\\t \\]\\[(){}<>]|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ResourceReferenceRegex();
}
