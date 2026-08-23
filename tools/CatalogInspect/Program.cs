using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;

if (args.Length == 2 && args[1].Equals("--self-test-card-export", StringComparison.OrdinalIgnoreCase))
{
    RunCardExportSelfTest(args[0]);
    return;
}

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "usage: CatalogInspect <game.pck> <mod-root> [<mod-root> ...] " +
        "[--runtime-scene <group> <selection> <scene> <output.pck> | --validate-runtime] " +
        "or CatalogInspect <game.pck> --self-test-card-export");
    return;
}

var runtimeIndex = Array.IndexOf(args, "--runtime-scene");
var validateIndex = Array.IndexOf(args, "--validate-runtime");
var optionIndexes = new[] { runtimeIndex, validateIndex }.Where(index => index >= 0).ToArray();
var firstOptionIndex = optionIndexes.Length == 0 ? args.Length : optionIndexes.Min();
var modRoots = args.Skip(1).Take(firstOptionIndex - 1);
var manifests = new List<InspectedManifest>();
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
            var dependencies = new List<string>();
            if (json.TryGetProperty("dependencies", out var dependencyValues) &&
                dependencyValues.ValueKind == JsonValueKind.Array)
            {
                foreach (var dependencyValue in dependencyValues.EnumerateArray())
                {
                    var dependencyId = dependencyValue.ValueKind switch
                    {
                        JsonValueKind.String => dependencyValue.GetString(),
                        JsonValueKind.Object when dependencyValue.TryGetProperty("id", out var idValue) =>
                            idValue.GetString(),
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(dependencyId))
                    {
                        dependencies.Add(dependencyId);
                    }
                }
            }

            manifests.Add(new InspectedManifest(
                id,
                name,
                pckPath,
                affectsGameplay,
                rootPath,
                hasDll,
                dependencies));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"skip {manifestPath}: {exception.Message}");
        }
    }
}

var requiredIds = manifests
    .SelectMany(manifest => manifest.Dependencies)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var descriptors = manifests
    .Select(manifest => new SkinModDescriptor(
        manifest.Id,
        manifest.Name,
        manifest.PckPath,
        manifest.AffectsGameplay || requiredIds.Contains(manifest.Id),
        manifest.RootPath,
        manifest.HasDll))
    .ToList();

using var catalog = SkinCatalog.Build(args[0], descriptors);
var validationCards = validateIndex >= 0
    ? BuildValidationCardEntries(
        new[] { args[0] }.Concat(descriptors
            .Where(descriptor => descriptor.AffectsGameplay && descriptor.PckPath != null)
            .Select(descriptor => descriptor.PckPath!)))
    : [];
if (validationCards.Count > 0)
{
    catalog.FinalizeCardGroups(validationCards);
}
var validationCardGroups = validationCards
    .SelectMany(card => new[] { card.CatalogGroupId, card.FilterGroupId, card.PoolGroupId })
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            $"{option.Assets.Count} assets, " +
            $"{option.NormalPortraits.Count} normal, {option.AncientPortraits.Count} ancient, " +
            $"{option.CardPresentations.Count} presentations");
    }
}

foreach (var option in catalog.PckCardOptions)
{
    var namespaceFiles = catalog.BuildCardProviderNamespaceOverlay(
        [option.ProviderId ?? option.Id]);
    Console.WriteLine(
        $"card-provider:{option.Id}\t{option.Name}\t{option.Assets.Count} assets, " +
        $"{namespaceFiles.Count} namespace files, " +
        $"{option.CardPresentations.Count} presentations");
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
            $"cards={probe.CardAssetCount}, presentations={probe.CardPresentationCount}, " +
            $"images={probe.RuntimeImageCount}, scripts={probe.ManagedScriptCount}");
    }

    foreach (var option in catalog.PckCardOptions.Where(option => option.Assets.Count > 0))
    {
        var variants = option.Assets.Keys
            .Select(path => GetCardVariantKey(path, validationCardGroups))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (variants.Length > 1)
        {
            failures.Add(
                $"cards/{option.Id}: one option still mixes variants {string.Join(", ", variants)}");
        }
    }

    var validatedCardResources = 0;
    foreach (var option in catalog.PckCardOptions.Where(option => option.Assets.Count > 0))
    {
        var routedGroup = catalog.CardGroups.FirstOrDefault(group =>
            group.Options.Any(candidate => candidate.Id.Equals(
                option.Id,
                StringComparison.OrdinalIgnoreCase)));
        var sample = option.Assets.FirstOrDefault();
        if (routedGroup == null || string.IsNullOrWhiteSpace(sample.Key))
        {
            failures.Add($"cards/{option.Id}: option was not routed to a card group");
            continue;
        }

        try
        {
            var overlay = catalog.BuildIsolatedCardResource(
                routedGroup.Id,
                option.Id,
                sample.Key,
                useSelectedProvider: true,
                $"validate/card/{validatedCardResources:D4}");
            if (!overlay.ResourcePaths.ContainsKey(sample.Key) || overlay.Files.Count == 0)
            {
                failures.Add($"cards/{option.Id}: sample card resource is empty");
                continue;
            }

            validatedCardResources++;
        }
        catch (Exception exception)
        {
            failures.Add($"cards/{option.Id}: cannot isolate sample card: {exception.Message}");
        }
    }
    Console.WriteLine($"card resource validation: {validatedCardResources} passed");

    var validatedPresentations = 0;
    var presentationFailures = 0;
    foreach (var option in catalog.PckCardOptions)
    {
        foreach (var presentation in option.CardPresentations)
        {
            if (string.IsNullOrWhiteSpace(option.ProviderRootPath))
            {
                failures.Add(
                    $"cards/{option.Id}/{presentation.Key}: presentation has no provider root");
                presentationFailures++;
                continue;
            }

            var presentationFailed = false;
            foreach (var resourcePath in presentation.Value.ResourcePaths)
            {
                try
                {
                    RuntimeResourceOverlay overlay;
                    try
                    {
                        overlay = catalog.BuildIsolatedCardResource(
                            string.Empty,
                            option.Id,
                            resourcePath,
                            useSelectedProvider: true,
                            $"validate/presentation/{validatedPresentations:D4}/provider");
                    }
                    catch
                    {
                        overlay = catalog.BuildIsolatedCardResource(
                            string.Empty,
                            option.Id,
                            resourcePath,
                            useSelectedProvider: false,
                            $"validate/presentation/{validatedPresentations:D4}/base");
                    }
                    if (!overlay.ResourcePaths.ContainsKey(resourcePath) || overlay.Files.Count == 0)
                    {
                        failures.Add(
                            $"cards/{option.Id}/{presentation.Key}: empty presentation resource {resourcePath}");
                        presentationFailures++;
                        presentationFailed = true;
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(
                        $"cards/{option.Id}/{presentation.Key}: cannot isolate {resourcePath}: " +
                        exception.Message);
                    presentationFailures++;
                    presentationFailed = true;
                }
            }

            if (!presentationFailed)
            {
                validatedPresentations++;
            }
        }
    }
    Console.WriteLine(
        $"presentation validation: {validatedPresentations} passed, " +
        $"{presentationFailures} failed");

    var presentationKeys = catalog.PckCardOptions
        .SelectMany(option => option.CardPresentations.Keys)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (presentationKeys.Length > 0)
    {
        var groupByCardType = presentationKeys.ToDictionary(
            cardType => cardType,
            cardType => "presentation-validation-" + cardType.ToLowerInvariant(),
            StringComparer.OrdinalIgnoreCase);
        catalog.FinalizeCardGroups(presentationKeys.Select(cardType => new CardCatalogEntry(
            cardType,
            $"res://validation/card_atlas.sprites/{cardType.ToLowerInvariant()}.tres",
            groupByCardType[cardType],
            groupByCardType[cardType],
            groupByCardType[cardType])));

        foreach (var sourceOption in catalog.PckCardOptions)
        {
            foreach (var presentation in sourceOption.CardPresentations)
            {
                var routedOption = catalog.CardGroups
                    .FirstOrDefault(group => group.Id.Equals(
                        groupByCardType[presentation.Key],
                        StringComparison.OrdinalIgnoreCase))?
                    .Options.FirstOrDefault(option => option.Id.Equals(
                        sourceOption.Id,
                        StringComparison.OrdinalIgnoreCase));
                if (routedOption == null ||
                    !routedOption.CardPresentations.ContainsKey(presentation.Key) ||
                    routedOption.CardPresentations.Keys.Any(cardType =>
                        !cardType.Equals(presentation.Key, StringComparison.OrdinalIgnoreCase)) ||
                    !string.Equals(
                        routedOption.ProviderRootPath,
                        sourceOption.ProviderRootPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(
                        $"cards/{sourceOption.Id}/{presentation.Key}: presentation routing leaked or lost provider ownership");
                }
            }
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
            if (IsAncientBackgroundScene(sourcePath))
            {
                if (ContainsResource(baselineOverlay, sourcePath))
                {
                    failures.Add(
                        $"{group.Id}/base: global overlay must preserve the game's Ancient scene {sourcePath}");
                }

                continue;
            }

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
        else if (group.Options.Any(option => option.ManagedMonsterScene != null))
        {
            // DLL-only monster skins can replace a protected VisualsPath with a private scene whose
            // file name does not match the model ID. BuildRuntimeResourceOverlay intentionally maps
            // that private scene onto the requested creature scene at runtime.
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
                if (IsAncientBackgroundScene(asset.Key))
                {
                    if (ContainsResource(selectedOverlay, asset.Key))
                    {
                        failures.Add(
                            $"{group.Id}/{option.Id}: global overlay replaced the game's Ancient scene {asset.Key}");
                    }

                    continue;
                }

                if (!ContainsAsset(selectedOverlay, asset.Key, asset.Value))
                {
                    failures.Add($"{group.Id}/{option.Id}: global overlay is missing {asset.Key}");
                }

                if (option.IsRuntimeProvider)
                {
                    foreach (var privateFile in asset.Value.Files.Where(file =>
                                 IsProviderNamespaceFile(file.Path, option.Id)))
                    {
                        if (!selectedOverlay.ContainsKey(privateFile.Path))
                        {
                            failures.Add(
                                $"{group.Id}/{option.Id}: selected private dependency is missing {privateFile.Path}");
                        }
                    }
                }
            }

            if (option.IsRuntimeProvider)
            {
                var privateAtlasFiles = selectedOverlay.Values
                    .Where(file =>
                        (file.Path.EndsWith(".atlas.import", StringComparison.OrdinalIgnoreCase) ||
                         file.Path.EndsWith(".atlas.remap", StringComparison.OrdinalIgnoreCase)) &&
                        IsProviderNamespaceFile(
                            file.Path.EndsWith(".import", StringComparison.OrdinalIgnoreCase)
                                ? file.Path[..^7]
                                : file.Path[..^6],
                            option.Id))
                    .DistinctBy(file => file.Archive.Path + "\n" + file.Path);
                foreach (var atlasFile in privateAtlasFiles)
                {
                    foreach (var textureFilePath in GetSiblingAtlasTextureFiles(atlasFile))
                    {
                        if (!selectedOverlay.ContainsKey(textureFilePath))
                        {
                            failures.Add(
                                $"{group.Id}/{option.Id}: atlas texture page is missing {textureFilePath}");
                        }
                    }
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

            var layeredImages = catalog.GetAncientLayeredImagePaths(group.Id, option.Id);
            if (layeredImages != null)
            {
                try
                {
                    var layerPaths = new[]
                        {
                            layeredImages.Character,
                            layeredImages.BackgroundCover,
                            layeredImages.Mask,
                            layeredImages.SleepingCharacter
                        }
                        .Where(path => path != null)
                        .Cast<string>()
                        .ToArray();
                    var layerOverlay = catalog.BuildRuntimeResourceOverlay(
                        group.Id,
                        option.Id,
                        layerPaths,
                        $"validate/layers/{validated:D4}");
                    foreach (var layerPath in layerPaths)
                    {
                        if (!layerOverlay.ResourcePaths.ContainsKey(layerPath))
                        {
                            failures.Add(
                                $"{group.Id}/{option.Id}: cannot isolate Ancient image layer {layerPath}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(
                        $"{group.Id}/{option.Id}: Ancient image layer validation failed: {exception.Message}");
                }
            }

            try
            {
                var overlay = catalog.BuildRuntimeResourceOverlay(
                    group.Id,
                    option.Id,
                    resourcePaths,
                    $"validate/{validated:D4}",
                    includeProviderDependencies: true);
                if (option.ManagedMonsterScene != null &&
                    !overlay.ResourcePaths.ContainsKey(creaturePath))
                {
                    failures.Add(
                        $"{group.Id}/{option.Id}: private monster scene was not mapped to {creaturePath}");
                }
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
                            if (ContainsProviderResource(providerArchive, referencedPath) &&
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

    static bool ContainsProviderResource(PckArchive archive, string resourcePath) =>
        archive.Contains(resourcePath) ||
        archive.Contains(resourcePath + ".import") ||
        archive.Contains(resourcePath + ".remap");

    static bool IsAncientBackgroundScene(string resourcePath)
    {
        var path = SkinCatalog.NormalizeTakeoverPath(resourcePath);
        return path.StartsWith(
                   "res://scenes/events/background_scenes/",
                   StringComparison.OrdinalIgnoreCase) &&
               path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase);
    }

    static bool ContainsAsset(
        IReadOnlyDictionary<string, ResourceFile> files,
        string resourcePath,
        ResourceAsset asset) =>
        !resourcePath.Equals(asset.SourcePath, StringComparison.OrdinalIgnoreCase)
            ? ContainsResource(files, resourcePath)
            : asset.Files.Any(file =>
                files.ContainsKey(file.Path) ||
                files.ContainsKey(SkinCatalog.NormalizeTakeoverPath(file.Path)));

    static string GetCardVariantKey(string path, IReadOnlySet<string> knownCardGroups)
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

        var fileSeparator = lower.LastIndexOf('/');
        if (fileSeparator < markerEnd)
        {
            return string.Empty;
        }

        var directories = lower[markerEnd..fileSeparator]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (directories.Length == 0)
        {
            return string.Empty;
        }

        var groupIndex = Array.FindIndex(directories, knownCardGroups.Contains);
        if (groupIndex < 0)
        {
            groupIndex = 0;
        }

        var variant = string.Join('/', directories.Where((_, index) => index != groupIndex));
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

static IReadOnlyList<CardCatalogEntry> BuildValidationCardEntries(IEnumerable<string> pckPaths)
{
    var cards = new Dictionary<string, CardCatalogEntry>(StringComparer.OrdinalIgnoreCase);
    foreach (var pckPath in pckPaths.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        using var archive = PckArchive.Open(pckPath);
        foreach (var archivePath in archive.Paths)
        {
            var sourcePath = archivePath.EndsWith(".import", StringComparison.OrdinalIgnoreCase)
                ? archivePath[..^7]
                : archivePath.EndsWith(".remap", StringComparison.OrdinalIgnoreCase)
                    ? archivePath[..^6]
                    : archivePath;
            var lower = sourcePath.ToLowerInvariant();
            var markerEnd = -1;
            foreach (var marker in new[] { "/card_portraits/", "/card_atlas.sprites/" })
            {
                var markerIndex = lower.IndexOf(marker, StringComparison.Ordinal);
                if (markerIndex >= 0)
                {
                    markerEnd = markerIndex + marker.Length;
                    break;
                }
            }

            var fileSeparator = lower.LastIndexOf('/');
            if (markerEnd < 0 || fileSeparator < markerEnd)
            {
                continue;
            }

            var groupEnd = lower.IndexOf('/', markerEnd);
            if (groupEnd < 0 || groupEnd > fileSeparator)
            {
                groupEnd = fileSeparator;
            }

            var group = lower[markerEnd..groupEnd];
            var fileName = sourcePath[(fileSeparator + 1)..];
            var extension = fileName.LastIndexOf('.');
            var stem = extension < 0 ? fileName : fileName[..extension];
            if (group.Length == 0 || stem.Length == 0)
            {
                continue;
            }

            cards.TryAdd(
                group + "\n" + stem,
                new CardCatalogEntry(stem, sourcePath, group, group, group));
        }
    }

    return cards.Values.ToArray();
}

static void RunCardExportSelfTest(string gamePckPath)
{
    var testRoot = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "skin-changer-card-export-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testRoot);
    try
    {
        var providerPck = System.IO.Path.Combine(testRoot, "ExportedCardSkin.pck");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["res://generated/card_replacements.json"] = Encoding.UTF8.GetBytes(
                """
                {"entries":[
                  {"cardId":"Tests.ExportCard","kind":"image","image":"res://generated/export.png"}
                ]}
                """),
            ["res://generated/framed_card_project.json"] = Encoding.UTF8.GetBytes(
                """
                {"entries":[
                  {"cardId":"Tests.FramedCard","portrait":"res://generated/framed.png",
                   "frame":"res://generated/frame.tres","frameVisible":true}
                ]}
                """),
            ["res://generated/animations/card_animations.json"] = Encoding.UTF8.GetBytes(
                """
                {"entries":[
                  {"cardId":"Tests.AnimatedCard","fallbackImage":"res://generated/fallback.png"}
                ]}
                """),
            ["res://generated/export.png"] = [1, 2, 3],
            ["res://generated/framed.png"] = [4, 5, 6],
            ["res://generated/fallback.png"] = [7, 8, 9],
            ["res://generated/frame.tres"] = Encoding.UTF8.GetBytes(
                "[gd_resource type=\"StyleBoxFlat\" format=3]\n")
        };
        PckArchive.Write(providerPck, files);

        using var catalog = SkinCatalog.Build(
            gamePckPath,
            [new SkinModDescriptor(
                "Tests.ExportedCardSkin",
                "Exported Card Skin",
                providerPck,
                false,
                testRoot,
                false)]);
        var sourceOption = catalog.PckCardOptions.Single(option =>
            option.Id.Equals("Tests.ExportedCardSkin", StringComparison.OrdinalIgnoreCase));
        var expectedPortraits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ExportCard"] = "res://generated/export.png",
            ["FramedCard"] = "res://generated/framed.png",
            ["AnimatedCard"] = "res://generated/fallback.png"
        };
        foreach (var expected in expectedPortraits)
        {
            if (!sourceOption.NormalPortraits.TryGetValue(expected.Key, out var actual) ||
                !actual.Equals(expected.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"export portrait mapping failed for {expected.Key}: {actual ?? "<missing>"}");
            }
        }
        if (!sourceOption.CardPresentations.TryGetValue("FramedCard", out var framed) ||
            !string.Equals(framed.Frame, "res://generated/frame.tres", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("framed export presentation mapping failed");
        }

        var cards = expectedPortraits.Keys.Select(cardType => new CardCatalogEntry(
            cardType,
            $"res://validation/{cardType.ToLowerInvariant()}.png",
            "tests",
            "tests",
            "tests")).ToArray();
        catalog.FinalizeCardGroups(cards);
        var routed = catalog.CardGroups.Single(group => group.Id == "tests")
            .Options.Single(option => option.Id.Equals(
                sourceOption.Id,
                StringComparison.OrdinalIgnoreCase));
        if (routed.NormalPortraits.Count != expectedPortraits.Count ||
            routed.CardPresentations.Keys.Any(key =>
                !key.Equals("FramedCard", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("exported card routing leaked or lost mappings");
        }

        foreach (var expected in expectedPortraits)
        {
            var overlay = catalog.BuildIsolatedCardResource(
                "tests",
                routed.Id,
                expected.Value,
                useSelectedProvider: true,
                "self-test/" + expected.Key.ToLowerInvariant());
            if (!overlay.ResourcePaths.ContainsKey(expected.Value) || overlay.Files.Count == 0)
            {
                throw new InvalidOperationException(
                    $"exported portrait isolation failed for {expected.Key}");
            }
        }

        Console.WriteLine(
            "card export self-test passed: static, framed and animation fallback manifests");
    }
    finally
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

static bool IsProviderNamespaceFile(string path, string providerId)
{
    if (!path.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var relative = path[6..];
    var separator = relative.IndexOf('/');
    var topLevel = separator < 0 ? relative : relative[..separator];
    var topLevelToken = new string(topLevel
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant)
        .ToArray());
    var providerToken = new string(providerId
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant)
        .ToArray());
    return providerToken.Length > 0 &&
           (topLevelToken.Equals(providerToken, StringComparison.OrdinalIgnoreCase) ||
            topLevelToken.StartsWith(providerToken, StringComparison.OrdinalIgnoreCase));
}

static IEnumerable<string> GetSiblingAtlasTextureFiles(ResourceFile atlasFile)
{
    var atlasSourcePath = atlasFile.Path.EndsWith(".import", StringComparison.OrdinalIgnoreCase)
        ? atlasFile.Path[..^7]
        : atlasFile.Path[..^6];
    var separator = atlasSourcePath.LastIndexOf('/');
    if (separator < 0)
    {
        yield break;
    }

    var directory = atlasSourcePath[..(separator + 1)];
    foreach (var path in atlasFile.Archive.Paths)
    {
        var sourcePath = path.EndsWith(".import", StringComparison.OrdinalIgnoreCase)
            ? path[..^7]
            : path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase)
                ? path[..^6]
                : path;
        if ((!sourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
             !sourcePath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) &&
             !sourcePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
             !sourcePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) ||
            !sourcePath.StartsWith(directory, StringComparison.OrdinalIgnoreCase) ||
            sourcePath[directory.Length..].Contains('/'))
        {
            continue;
        }

        yield return path;
    }
}

partial class Program
{
    [GeneratedRegex(
        "res://[^\\x00\\\"'\\r\\n\\t \\]\\[(){}<>]+?\\.(?:spatlas|spskel|ctex|tscn|tres|gdc|gd|gdshader|scn|res|png|webp|jpe?g|svg|skel|atlas|json|ogg|wav|mp3)(?=[\\x00\\\"'\\r\\n\\t \\]\\[(){}<>]|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ResourceReferenceRegex();
}

internal sealed record InspectedManifest(
    string Id,
    string Name,
    string? PckPath,
    bool AffectsGameplay,
    string RootPath,
    bool HasDll,
    IReadOnlyList<string> Dependencies);
