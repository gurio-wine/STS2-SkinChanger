using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;
using STS2SkinChanger.Pck;

if (args.Length == 1 && args[0].Equals("--self-test-game-pack-locator", StringComparison.OrdinalIgnoreCase))
{
    RunGamePackLocatorSelfTest();
    return;
}

if (args.Length == 2 && args[1].Equals("--self-test-card-export", StringComparison.OrdinalIgnoreCase))
{
    RunCardExportSelfTest(args[0]);
    return;
}

if (args.Length == 2 && args[1].Equals("--self-test-localization", StringComparison.OrdinalIgnoreCase))
{
    RunLocalizationOwnershipSelfTest(args[0]);
    return;
}

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "usage: CatalogInspect <game.pck> <mod-root> [<mod-root> ...] " +
        "[--runtime-scene <group> <selection> <scene> <output.pck> | --validate-runtime] " +
        "or CatalogInspect <game.pck> --self-test-card-export | --self-test-localization " +
        "or CatalogInspect --self-test-game-pack-locator");
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
var showAssets = Environment.GetEnvironmentVariable("CATALOG_INSPECT_ASSETS") == "1";
foreach (var providerId in catalog.Groups
             .SelectMany(group => group.Options)
             .Select(option => option.Id)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .Where(catalog.ProviderUsesFullRuntime)
             .OrderBy(providerId => providerId, StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(
        $"runtime-provider:{providerId}\t" +
        $"{catalog.GetFullRuntimeProviderGroups(providerId).Count} linked groups");
}

foreach (var providerId in catalog.Groups
             .SelectMany(group => group.Options)
             .Select(option => option.Id)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .Where(catalog.ProviderUsesScopedMonsterRuntime)
             .OrderBy(providerId => providerId, StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(
        $"scoped-monster-provider:{providerId}\t" +
        $"{catalog.GetScopedMonsterRuntimeProviderGroups(providerId).Count} independent groups");
}

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
        var modeResources = option.RuntimeMonsterVisualMode?.ResourcePaths.Count ?? 0;
        Console.WriteLine(
            $"  {option.Id}\t{option.Name}\t{option.Assets.Count} assets" +
            (option.RuntimeMonsterVisualMode == null
                ? string.Empty
                : $", mode={option.RuntimeMonsterVisualMode.ModeName}, {modeResources} mode resources"));
        if (showAssets)
        {
            foreach (var asset in option.Assets.OrderBy(
                         pair => pair.Key,
                         StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"    {asset.Key} <- {asset.Value.SourcePath}");
            }
        }
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
        if (showAssets)
        {
            foreach (var presentation in option.CardPresentations.OrderBy(
                         pair => pair.Key,
                         StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    $"    presentation:{presentation.Key} " +
                    $"ancient={presentation.Value.UseAncientLayout}, " +
                    $"full-frame={presentation.Value.UseFullFrameArt}");
            }
        }
    }
}

foreach (var option in catalog.PckCardOptions)
{
    var namespaceFiles = catalog.BuildCardProviderNamespaceOverlay(
        [option.ProviderId ?? option.Id]);
    Console.WriteLine(
        $"card-provider:{option.Id}\t{option.Name}\t{option.Assets.Count} assets, " +
        $"{option.NormalPortraits.Count} normal, {option.AncientPortraits.Count} ancient, " +
        $"{option.CardPresentations.Count(pair => pair.Value.UseFullFrameArt)} full-frame, " +
        $"{namespaceFiles.Count} namespace files, " +
        $"{option.CardPresentations.Count} presentations");
    if (showAssets)
    {
        foreach (var presentation in option.CardPresentations.OrderBy(
                     pair => pair.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"    presentation:{presentation.Key} " +
                $"ancient={presentation.Value.UseAncientLayout}, " +
                $"full-frame={presentation.Value.UseFullFrameArt}");
        }
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
    ValidateLocalizationOwnership(catalog, descriptors, failures);
    var probes = SkinCatalog.ProbeSkinProviders(descriptors);
    foreach (var probe in probes)
    {
        Console.WriteLine(
            $"provider {probe.Id}: visual={probe.VisualGroupCount}, " +
            $"cards={probe.CardAssetCount}, presentations={probe.CardPresentationCount}, " +
            $"images={probe.RuntimeImageCount}, scripts={probe.ManagedScriptCount}, " +
            $"interactive={probe.HasInteractiveScenes}");
    }

    foreach (var provider in catalog.Groups
                 .SelectMany(group => group.Options.Select(option =>
                     (Group: group, Option: option)))
                 .GroupBy(pair => pair.Option.Id, StringComparer.OrdinalIgnoreCase)
                 .Where(group => group.All(pair =>
                     SkinCatalog.KnownAncientIds.Contains(pair.Group.Id) &&
                     (pair.Option.RuntimeImagePath != null ||
                      catalog.GetAncientLayeredImagePaths(
                          pair.Group.Id,
                          pair.Option.Id) != null))))
    {
        if (catalog.ProviderUsesFullRuntime(provider.Key))
        {
            failures.Add(
                $"provider {provider.Key}: independently managed Ancient visuals were linked as one runtime bundle");
            continue;
        }

        var selections = provider.ToDictionary(
            pair => pair.Group.Id,
            pair => pair.Option.Id,
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in provider)
        {
            var selectTransaction = catalog.BuildVisualSelectionTransaction(
                pair.Group.Id,
                pair.Option.Id,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            if (selectTransaction.Count != 1 ||
                !selectTransaction.TryGetValue(pair.Group.Id, out var selectedId) ||
                !selectedId.Equals(pair.Option.Id, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    $"{pair.Group.Id}/{pair.Option.Id}: managed Ancient selection changed another group");
            }

            var resetTransaction = catalog.BuildVisualSelectionTransaction(
                pair.Group.Id,
                SkinCatalog.BaseOptionId,
                selections);
            if (resetTransaction.Count != 1 ||
                !resetTransaction.TryGetValue(pair.Group.Id, out var resetId) ||
                !resetId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    $"{pair.Group.Id}/{pair.Option.Id}: managed Ancient reset changed another group");
            }
        }
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

            var textureDependencies = sample.Value.Files
                .SelectMany(file => ResourceReferenceRegex()
                    .Matches(Encoding.UTF8.GetString(file.Archive.ReadFile(file.Path)))
                    .Select(match => match.Value))
                .Where(path =>
                    path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var missingTexture = textureDependencies.FirstOrDefault(path =>
                !overlay.SourceAliases.ContainsKey(path));
            if (missingTexture != null)
            {
                failures.Add(
                    $"cards/{option.Id}: sample card omitted texture dependency {missingTexture}");
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
        else if (group.Id.Equals("merchant", StringComparison.OrdinalIgnoreCase))
        {
            resourcePaths =
            [
                "res://scenes/rooms/merchant_button.tscn",
                "res://scenes/merchant/merchant_inventory.tscn"
            ];
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

        var baselineResourcePaths = resourcePaths
            .Where(path => catalog.ResolveBaseline(path) != null)
            .ToArray();
        if (baselineResourcePaths.Length > 0)
        {
            try
            {
                var baselineRuntimeOverlay = catalog.BuildRuntimeResourceOverlay(
                    group.Id,
                    SkinCatalog.BaseOptionId,
                    baselineResourcePaths,
                    $"validate/base/{validated:D4}");
                ValidatePrivateBaselineReferences(
                    catalog,
                    baselineRuntimeOverlay,
                    $"{group.Id}/base",
                    failures);
                validated++;
                Console.WriteLine(
                    $"validated {group.Id}/base: {baselineRuntimeOverlay.Files.Count} files");
            }
            catch (Exception exception)
            {
                failures.Add($"{group.Id}/base: {exception.Message}");
            }
        }

        foreach (var option in group.Options)
        {
            if (option.IsRuntimeProvider)
            {
                ValidateCharacterTemplateMapping(
                    option,
                    group.Id,
                    $"res://scenes/creature_visuals/templates/{characterId}_template.tscn",
                    creaturePath,
                    failures);
                ValidateCharacterTemplateMapping(
                    option,
                    group.Id,
                    $"res://scenes/merchant/characters/templates/{characterId}_merchant_template.tscn",
                    $"res://scenes/merchant/characters/{characterId}_merchant.tscn",
                    failures);
                ValidateCharacterTemplateMapping(
                    option,
                    group.Id,
                    $"res://scenes/rest_site/characters/templates/{characterId}_rest_site_template.tscn",
                    $"res://scenes/rest_site/characters/{characterId}_rest_site.tscn",
                    failures);
            }

            var selectedSelections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [group.Id] = option.Id
            };
            var selectedGroupSet = groupSet;
            var isFullRuntimeProvider = catalog.ProviderUsesFullRuntime(option.Id);
            if (isFullRuntimeProvider)
            {
                var ownedGroups = catalog.GetFullRuntimeProviderGroups(option.Id);
                var transaction = catalog.BuildVisualSelectionTransaction(
                    group.Id,
                    option.Id,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                foreach (var ownedGroupId in ownedGroups)
                {
                    selectedSelections[ownedGroupId] = option.Id;
                    if (!transaction.TryGetValue(ownedGroupId, out var transactionSelection) ||
                        !transactionSelection.Equals(option.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add(
                            $"{group.Id}/{option.Id}: linked selection omitted {ownedGroupId}");
                    }
                }

                selectedGroupSet = ownedGroups.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!catalog.IsFullRuntimeProviderFullySelected(option.Id, selectedSelections))
                {
                    failures.Add($"{group.Id}/{option.Id}: linked provider was not fully selected");
                }

                var exitTransaction = catalog.BuildVisualSelectionTransaction(
                    group.Id,
                    SkinCatalog.BaseOptionId,
                    selectedSelections);
                foreach (var ownedGroupId in ownedGroups)
                {
                    if (!exitTransaction.TryGetValue(ownedGroupId, out var exitSelection) ||
                        !exitSelection.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add(
                            $"{group.Id}/{option.Id}: linked deselection left {ownedGroupId} active");
                    }
                }

                if (ownedGroups.Count > 1)
                {
                    var partialOverlay = catalog.BuildOverlay(
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [group.Id] = option.Id
                        },
                        groupSet);
                    var ownProviderFiles = option.Assets.Values
                        .SelectMany(asset => asset.Files)
                        .Select(file => file.Path)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var leakedProviderFile = partialOverlay.Values.FirstOrDefault(file =>
                        file.Archive.Path.Equals(
                            descriptors.FirstOrDefault(descriptor => descriptor.Id.Equals(
                                option.Id,
                                StringComparison.OrdinalIgnoreCase))?.PckPath,
                            StringComparison.OrdinalIgnoreCase) &&
                        !ownProviderFiles.Contains(file.Path));
                    if (leakedProviderFile != null)
                    {
                        failures.Add(
                            $"{group.Id}/{option.Id}: partial selection mounted bundled file " +
                            leakedProviderFile.Path);
                    }
                }

                var provider = descriptors.FirstOrDefault(descriptor =>
                    descriptor.Id.Equals(option.EffectiveProviderId, StringComparison.OrdinalIgnoreCase));
                if (provider?.PckPath != null && File.Exists(provider.PckPath))
                {
                    var deselectedOverlay = catalog.BuildOverlay(
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        selectedGroupSet);
                    using var providerArchive = PckArchive.Open(provider.PckPath);
                    var baselineCollisions = providerArchive.Paths
                        .Where(path => !IsProviderProjectControlFile(path))
                        .Select(SkinCatalog.NormalizeTakeoverPath)
                        .Where(path => !IsProviderNamespaceFile(path, option.EffectiveProviderId))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(path => catalog.ResolveBaseline(path) != null)
                        .ToArray();
                    var unrestoredCollision = baselineCollisions.FirstOrDefault(path =>
                        !ContainsResource(deselectedOverlay, path));
                    if (unrestoredCollision != null)
                    {
                        failures.Add(
                            $"{group.Id}/{option.Id}: deselection did not restore {unrestoredCollision}");
                    }
                }
            }

            var selectedOverlay = catalog.BuildOverlay(selectedSelections, selectedGroupSet);
            var isolatedRelicProviderPaths = catalog.GetIsolatedRelicProviderPaths(option);
            var providerRelicSpritePaths = catalog.GetProviderRelicSpritePaths(option);
            var providerOnlyRelicPath = providerRelicSpritePaths.FirstOrDefault(path =>
                !option.Assets.ContainsKey(path));
            if (providerOnlyRelicPath != null)
            {
                var resolvedRelicGroupId = catalog.FindSelectedRelicIconGroup(
                    providerOnlyRelicPath,
                    selectedSelections,
                    [option.EffectiveProviderId]);
                if (resolvedRelicGroupId != null)
                {
                    var resolvedSelectionId = selectedSelections[resolvedRelicGroupId];
                    try
                    {
                        var providerRelic = catalog.BuildIsolatedRelicResourceOverlay(
                            resolvedRelicGroupId,
                            resolvedSelectionId,
                            providerRelicSpritePaths,
                            $"validate/provider-relic/{validated:D4}");
                        var hasProviderPayload = catalog.TryResolveProviderAsset(
                            option,
                            providerOnlyRelicPath,
                            out var providerRelicAsset) &&
                            providerRelicAsset.Files
                                .Select(file => file.Archive.ReadFile(file.Path))
                                .Any(providerBytes => providerRelic.Files.Values.Any(bytes =>
                                    bytes.AsSpan().SequenceEqual(providerBytes)));
                        var missingAliases = providerRelicSpritePaths.Count(path =>
                            !providerRelic.ResourcePaths.ContainsKey(path));
                        var atlasAliases = providerRelic.ResourcePaths.Keys.Count(
                            SkinCatalog.IsRelicAtlasTexturePath);
                        if (!providerRelic.ResourcePaths.TryGetValue(
                                providerOnlyRelicPath,
                                out var alias) ||
                            !alias.StartsWith(
                                "res://sts2_skin_runtime/",
                                StringComparison.OrdinalIgnoreCase) ||
                            !hasProviderPayload ||
                            missingAliases > 0 ||
                            atlasAliases is < 1 or > 2 ||
                            providerRelic.CanonicalDependencyPaths.Count > 0 ||
                            providerRelic.Files.Keys.Any(path =>
                                path.StartsWith(
                                    "res://images/atlases/relic_atlas",
                                    StringComparison.OrdinalIgnoreCase) ||
                                path.StartsWith(
                                    "res://images/atlases/relic_outline_atlas",
                                    StringComparison.OrdinalIgnoreCase)))
                        {
                            failures.Add(
                                $"{group.Id}/{option.Id}: provider-wide relic did not use provider asset " +
                                providerOnlyRelicPath);
                        }
                        else
                        {
                            validated++;
                            Console.WriteLine(
                                $"validated provider relic takeover {group.Id}/{option.Id}: " +
                                $"{providerRelicSpritePaths.Count} slices");
                        }
                    }
                    catch (Exception exception)
                    {
                        failures.Add(
                            $"{group.Id}/{option.Id}: provider-wide relic failed " +
                            $"{providerOnlyRelicPath}: {exception.Message}");
                    }
                }
            }
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

                if (SkinCatalog.IsRelicAtlasSpritePath(asset.Key))
                {
                    if (ContainsAsset(selectedOverlay, asset.Key, asset.Value))
                    {
                        failures.Add(
                            $"{group.Id}/{option.Id}: global overlay leaked relic atlas slice {asset.Key}");
                    }

                    var baselineRelic = catalog.ResolveBaseline(asset.Key);
                    if (baselineRelic != null &&
                        !ContainsAsset(selectedOverlay, asset.Key, baselineRelic))
                    {
                        failures.Add(
                            $"{group.Id}/{option.Id}: global overlay did not restore baseline relic slice {asset.Key}");
                    }

                    try
                    {
                        var isolatedRelic = catalog.BuildRuntimeResourceOverlay(
                            group.Id,
                            option.Id,
                            [asset.Key],
                            $"validate/relic/{validated:D4}");
                        if (!isolatedRelic.ResourcePaths.TryGetValue(asset.Key, out var alias) ||
                            !alias.StartsWith(
                                "res://sts2_skin_runtime/",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            failures.Add(
                                $"{group.Id}/{option.Id}: relic slice was not assigned a private alias {asset.Key}");
                        }
                        else
                        {
                            validated++;
                        }
                    }
                    catch (Exception exception)
                    {
                        failures.Add(
                            $"{group.Id}/{option.Id}: isolated relic slice failed {asset.Key}: " +
                            exception.Message);
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
                                 IsProviderNamespaceFile(file.Path, option.EffectiveProviderId)))
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
                if (isFullRuntimeProvider)
                {
                    var provider = descriptors.FirstOrDefault(descriptor =>
                        descriptor.Id.Equals(option.EffectiveProviderId, StringComparison.OrdinalIgnoreCase));
                    if (provider?.PckPath != null && File.Exists(provider.PckPath))
                    {
                        using var providerArchive = PckArchive.Open(provider.PckPath);
                        var independentlySelectableCardFiles = catalog.PckCardOptions
                            .Where(candidate => (candidate.ProviderId ?? candidate.Id).Equals(
                                option.EffectiveProviderId,
                                StringComparison.OrdinalIgnoreCase))
                            .SelectMany(candidate => candidate.Assets.Values)
                            .SelectMany(asset => asset.Files)
                            .Select(file => SkinCatalog.NormalizeTakeoverPath(file.Path))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var expectedPackagePaths = providerArchive.Paths
                            .Where(path => !IsProviderProjectControlFile(path))
                            .Where(path => !independentlySelectableCardFiles.Contains(
                                SkinCatalog.NormalizeTakeoverPath(path)))
                            .Where(path => !isolatedRelicProviderPaths.Contains(
                                SkinCatalog.NormalizeTakeoverPath(path)))
                            .ToArray();
                        var missingPackagePaths = expectedPackagePaths
                            .Where(path => !selectedOverlay.ContainsKey(path))
                            .Take(20)
                            .ToArray();
                        if (missingPackagePaths.Length > 0)
                        {
                            failures.Add(
                                $"{group.Id}/{option.Id}: selected full provider package is missing " +
                                string.Join(", ", missingPackagePaths));
                        }
                        else
                        {
                            Console.WriteLine(
                                $"validated full provider package {group.Id}/{option.Id}: " +
                                $"{expectedPackagePaths.Length} files");
                        }
                    }
                }

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

            if (!isFullRuntimeProvider)
            {
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
                var selectedResourcePaths = resourcePaths
                    .Concat(option.RuntimeMonsterVisualMode?.ResourcePaths ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var overlay = catalog.BuildRuntimeResourceOverlay(
                    group.Id,
                    option.Id,
                    selectedResourcePaths,
                    $"validate/{validated:D4}",
                    includeProviderDependencies: true);
                ValidatePrivateBaselineReferences(
                    catalog,
                    overlay,
                    $"{group.Id}/{option.Id}",
                    failures);
                if (option.ManagedMonsterScene != null &&
                    !overlay.ResourcePaths.ContainsKey(creaturePath))
                {
                    failures.Add(
                        $"{group.Id}/{option.Id}: private monster scene was not mapped to {creaturePath}");
                }
                var provider = descriptors.FirstOrDefault(descriptor =>
                    descriptor.Id.Equals(option.EffectiveProviderId, StringComparison.OrdinalIgnoreCase));
                if (provider?.PckPath != null && File.Exists(provider.PckPath))
                {
                    using var providerArchive = PckArchive.Open(provider.PckPath);
                    var providerCanonicalPaths = providerArchive.Paths
                        .Select(SkinCatalog.NormalizeTakeoverPath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var canonicalDependencyPath in overlay.CanonicalDependencyPaths)
                    {
                        var isPrivateRuntimeRedirect =
                            overlay.Files.TryGetValue(canonicalDependencyPath, out var redirectBytes) &&
                            Encoding.UTF8.GetString(redirectBytes).Contains(
                                "res://sts2_skin_runtime/",
                                StringComparison.OrdinalIgnoreCase);
                        if (!providerArchive.Contains(canonicalDependencyPath) &&
                            !providerCanonicalPaths.Contains(
                                SkinCatalog.NormalizeTakeoverPath(canonicalDependencyPath)) &&
                            !isPrivateRuntimeRedirect)
                        {
                            failures.Add(
                                $"{group.Id}/{option.Id}: runtime overlay mounted a non-provider " +
                                $"dependency at canonical path {canonicalDependencyPath}");
                        }
                    }

                    var baselineDependencyOverlay = catalog.BuildBaselineDependencyOverlay(
                        overlay.CanonicalDependencyPaths);
                    foreach (var canonicalDependencyPath in overlay.CanonicalDependencyPaths)
                    {
                        var sourcePath = StripRedirectSuffix(
                            SkinCatalog.NormalizeTakeoverPath(canonicalDependencyPath));
                        var baseline = catalog.ResolveBaseline(sourcePath);
                        if (baseline == null)
                        {
                            continue;
                        }

                        var missingBaselineFile = baseline.Files.FirstOrDefault(file =>
                            baselineDependencyOverlay.Values.All(restored =>
                                !ReferenceEquals(restored.Archive, file.Archive) ||
                                !restored.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)));
                        if (missingBaselineFile != null)
                        {
                            failures.Add(
                                $"{group.Id}/{option.Id}: runtime dependency restoration omitted " +
                                missingBaselineFile.Path);
                        }
                    }

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

    var sharedRelicPriorityProbe = catalog.Groups
        .SelectMany(group => group.Options.SelectMany(option =>
            catalog.GetProviderRelicSpritePaths(option).Select(path =>
                (Path: path, Group: group, Option: option))))
        .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
        .Select(entries => entries
            .DistinctBy(
                entry => entry.Group.Id + "\n" + entry.Option.EffectiveProviderId,
                StringComparer.OrdinalIgnoreCase)
            .Where(entry => catalog.FindSelectedRelicIconGroup(
                entry.Path,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [entry.Group.Id] = entry.Option.Id
                },
                [entry.Option.EffectiveProviderId]) != null)
            .ToArray())
        .FirstOrDefault(entries => entries
            .Select(entry => entry.Group.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() >= 2 &&
            entries.Select(entry => entry.Option.EffectiveProviderId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() >= 2);
    if (sharedRelicPriorityProbe != null)
    {
        var first = sharedRelicPriorityProbe[0];
        var second = sharedRelicPriorityProbe.First(entry =>
            !entry.Group.Id.Equals(first.Group.Id, StringComparison.OrdinalIgnoreCase) &&
            !entry.Option.EffectiveProviderId.Equals(
                first.Option.EffectiveProviderId,
                StringComparison.OrdinalIgnoreCase));
        var selections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [first.Group.Id] = first.Option.Id,
            [second.Group.Id] = second.Option.Id
        };
        foreach (var expected in new[] { first, second })
        {
            var other = expected == first ? second : first;
            var resolvedGroupId = catalog.FindSelectedRelicIconGroup(
                first.Path,
                selections,
                [other.Option.EffectiveProviderId, expected.Option.EffectiveProviderId]);
            var resolvedProviderId = catalog.Groups
                .FirstOrDefault(group => group.Id.Equals(
                    resolvedGroupId,
                    StringComparison.OrdinalIgnoreCase))?
                .Options.FirstOrDefault(option => option.Id.Equals(
                    selections.GetValueOrDefault(resolvedGroupId ?? string.Empty),
                    StringComparison.OrdinalIgnoreCase))?
                .EffectiveProviderId;
            if (!expected.Option.EffectiveProviderId.Equals(
                    resolvedProviderId,
                    StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    $"provider relic priority selected {resolvedProviderId ?? "base"} instead of " +
                    expected.Option.EffectiveProviderId);
            }
            else
            {
                validated++;
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

    static void ValidateCharacterTemplateMapping(
        SkinOption option,
        string groupId,
        string templatePath,
        string canonicalPath,
        ICollection<string> failures)
    {
        var templateAsset = option.Assets.Values.FirstOrDefault(asset =>
            asset.SourcePath.Equals(templatePath, StringComparison.OrdinalIgnoreCase));
        if (templateAsset == null)
        {
            return;
        }

        if (!option.Assets.TryGetValue(canonicalPath, out var mappedAsset) ||
            !ReferenceEquals(mappedAsset, templateAsset))
        {
            failures.Add(
                $"{groupId}/{option.Id}: character template {templatePath} was not mapped to {canonicalPath}");
        }
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

    static string StripRedirectSuffix(string path)
    {
        if (path.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
        {
            return path[..^7];
        }

        return path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase)
            ? path[..^6]
            : path;
    }

    static void ValidatePrivateBaselineReferences(
        SkinCatalog catalog,
        RuntimeResourceOverlay overlay,
        string context,
        ICollection<string> failures)
    {
        foreach (var file in overlay.Files.Where(pair =>
                     pair.Key.StartsWith(
                         "res://sts2_skin_runtime/",
                         StringComparison.OrdinalIgnoreCase) &&
                     MayContainReferences(pair.Key)))
        {
            var textResource = Encoding.UTF8.GetString(file.Value);
            foreach (Match reference in ResourceReferenceRegex().Matches(textResource))
            {
                var referencedPath = reference.Value;
                if (referencedPath.StartsWith(
                        "res://sts2_skin_runtime/",
                        StringComparison.OrdinalIgnoreCase) ||
                    referencedPath.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) ||
                    referencedPath.EndsWith(".gdc", StringComparison.OrdinalIgnoreCase) ||
                    referencedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sourcePath = StripRedirectSuffix(
                    SkinCatalog.NormalizeTakeoverPath(referencedPath));
                var redirectedToPrivatePayload = new[] { ".remap", ".import" }
                    .Select(suffix => sourcePath + suffix)
                    .Any(redirectPath =>
                        overlay.Files.TryGetValue(redirectPath, out var redirectBytes) &&
                        Encoding.UTF8.GetString(redirectBytes).Contains(
                            "res://sts2_skin_runtime/",
                            StringComparison.OrdinalIgnoreCase));
                if (redirectedToPrivatePayload)
                {
                    continue;
                }

                if (catalog.ResolveBaseline(sourcePath) != null)
                {
                    failures.Add(
                        $"{context}: private resource {file.Key} still references public baseline {referencedPath}");
                }
            }
        }
    }

}

static void RunGamePackLocatorSelfTest()
{
    var testRoot = Path.Combine(
        Path.GetTempPath(),
        "Gurio.SkinChanger.GamePackLocator." + Guid.NewGuid().ToString("N"));
    try
    {
        var windowsDirectory = Path.Combine(testRoot, "windows");
        Directory.CreateDirectory(windowsDirectory);
        var windowsPack = Path.Combine(windowsDirectory, "SlayTheSpire2.pck");
        File.WriteAllBytes(windowsPack, []);
        var windowsResult = GamePackLocator.Resolve(
            Path.Combine(windowsDirectory, "SlayTheSpire2.exe"));
        if (!windowsResult.Equals(windowsPack, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Windows pack resolution failed: {windowsResult}");
        }

        var macContents = Path.Combine(testRoot, "Slay the Spire 2.app", "Contents");
        var macExecutableDirectory = Path.Combine(macContents, "MacOS");
        var macResourcesDirectory = Path.Combine(macContents, "Resources");
        Directory.CreateDirectory(macExecutableDirectory);
        Directory.CreateDirectory(macResourcesDirectory);
        var macPack = Path.Combine(macResourcesDirectory, "SlayTheSpire2.pck");
        File.WriteAllBytes(macPack, []);
        var macResult = GamePackLocator.Resolve(
            Path.Combine(macExecutableDirectory, "SlayTheSpire2"));
        if (!macResult.Equals(macPack, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"macOS bundle pack resolution failed: {macResult}");
        }

        File.Delete(macPack);
        var alternateMacPack = Path.Combine(macResourcesDirectory, "Slay the Spire 2.pck");
        File.WriteAllBytes(alternateMacPack, []);
        var alternateMacResult = GamePackLocator.Resolve(
            Path.Combine(macExecutableDirectory, "Slay the Spire 2"));
        if (!alternateMacResult.Equals(alternateMacPack, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"macOS executable-name pack resolution failed: {alternateMacResult}");
        }

        Console.WriteLine("game pack locator self-test passed: Windows and macOS app bundle layouts");
    }
    finally
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
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

static void ValidateLocalizationOwnership(
    SkinCatalog catalog,
    IReadOnlyList<SkinModDescriptor> descriptors,
    List<string> failures)
{
    var paths = new List<string>();
    foreach (var descriptor in descriptors.Where(descriptor =>
                 descriptor.PckPath != null && File.Exists(descriptor.PckPath)))
    {
        using var archive = PckArchive.Open(descriptor.PckPath!);
        paths.AddRange(archive.Paths.Where(path => path.StartsWith(
                $"res://{descriptor.Id}/localization/",
                StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)));
    }

    var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var basePaths = catalog.FilterModdedLocalizationTables(paths, empty);
    var checkedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var group in catalog.Groups)
    {
        foreach (var option in group.Options)
        {
            var selections = catalog.BuildVisualSelectionTransaction(group.Id, option.Id, empty);
            if (!catalog.GetSelectedLocalizationProviderIds(selections).Contains(option.EffectiveProviderId) ||
                !checkedProviders.Add(option.EffectiveProviderId))
            {
                continue;
            }

            var providerPaths = paths.Where(path => path.StartsWith(
                    $"res://{option.EffectiveProviderId}/localization/",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var ownedPaths = providerPaths
                .Where(catalog.IsManagedCosmeticLocalizationPath)
                .ToArray();
            var passthroughPaths = providerPaths
                .Where(path => !catalog.IsManagedCosmeticLocalizationPath(path))
                .ToArray();
            var activePaths = catalog.FilterModdedLocalizationTables(paths, selections);
            var mountedPaths = catalog.BuildOverlay(selections).Keys
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Keep every old path present: just like the game, mounted PCKs cannot be unloaded.
            var afterLeaving = new Dictionary<string, string>(selections, StringComparer.OrdinalIgnoreCase);
            foreach (var update in catalog.BuildVisualSelectionTransaction(
                         group.Id, SkinCatalog.BaseOptionId, afterLeaving))
            {
                afterLeaving[update.Key] = update.Value;
            }
            var restoredPaths = catalog.FilterModdedLocalizationTables(paths, afterLeaving);
            if (ownedPaths.Any(path => basePaths.Contains(path) ||
                                       !activePaths.Contains(path) ||
                                       !mountedPaths.Contains(path) ||
                                       restoredPaths.Contains(path)) ||
                passthroughPaths.Any(path => !basePaths.Contains(path) ||
                                             !activePaths.Contains(path) ||
                                             !mountedPaths.Contains(path) ||
                                             !restoredPaths.Contains(path)))
            {
                failures.Add(
                    $"{option.EffectiveProviderId}: localization was not mounted with, or outlived, its visual selection");
            }
            else if (ownedPaths.Length > 0)
            {
                Console.WriteLine($"validated localization ownership {option.EffectiveProviderId}: {ownedPaths.Length} tables");
            }
        }
    }
}

static void RunLocalizationOwnershipSelfTest(string gamePckPath)
{
    var testRoot = Directory.CreateTempSubdirectory("skin-changer-localization-").FullName;
    try
    {
        var descriptors = new List<SkinModDescriptor>();
        var allTables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        AddProvider("Tests.IronSkin", "ironclad", "Skin Ironclad");
        AddProvider("Tests.SilentSkin", "silent", "Skin Silent");
        AddProvider("Tests.OtherIronSkin", "ironclad", "Other Ironclad");
        AddProvider("Tests.Gameplay", "watcher", "New Character", affectsGameplay: true);
        AddProvider("Tests.IronSkinExtras", null, "Independent Translation");
        AddEventVisualProvider();
        AddFakeMerchantEventReplacement();
        using var catalog = SkinCatalog.Build(gamePckPath, descriptors);
        if (catalog.Groups.Any(group => group.Options.Any(option =>
                option.Id.Equals("Tests.FakeMerchantEvent", StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException(
                "a fake-merchant event replacement was classified as a shop merchant skin");
        }
        var selections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Check("unselected/startup", "Base Ironclad", "Base Silent");
        selections["cards:ironclad"] = "Tests.IronSkin";
        selections["cards:item:card.strike_ironclad"] = "Tests.IronSkin";
        Check("card-only selection", "Base Ironclad", "Base Silent");
        selections["ironclad"] = "Tests.IronSkin";
        Check("enter first skin", "Skin Ironclad", "Base Silent");
        selections["silent"] = "Tests.SilentSkin";
        Check("independent characters", "Skin Ironclad", "Skin Silent");
        selections["ironclad"] = SkinCatalog.BaseOptionId;
        Check("leave first skin", "Base Ironclad", "Skin Silent");
        selections["ironclad"] = "tests.otherironskin";
        Check("another skin, case insensitive", "Other Ironclad", "Skin Silent");
        selections["silent"] = SkinCatalog.BaseOptionId;
        Check("leave second skin", "Other Ironclad", "Base Silent");
        selections["ironclad"] = SkinCatalog.BaseOptionId;
        Check("both restored", "Base Ironclad", "Base Silent");
        selections["ironclad"] = "Tests.IronSkin";
        Check("reselect", "Skin Ironclad", "Base Silent");

        Console.WriteLine("localization ownership self-test passed: 9 transitions in both eng/zhs, stale paths, card-only selection, event text, fake-merchant event classification and unrelated translations");

        void AddProvider(string id, string? character, string title, bool affectsGameplay = false)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (character != null)
            {
                files[$"res://animations/characters/{character}/skin.tres"] =
                    Encoding.UTF8.GetBytes("[gd_resource type=\"Resource\" format=3]\n");
            }
            foreach (var language in new[] { "eng", "zhs" })
            {
                var path = $"res://{id}/localization/{language}/characters.json";
                var entries = new Dictionary<string, string>
                {
                    [character == null ? "TRANSLATION.title" : character.ToUpperInvariant() + ".title"] = title
                };
                allTables[path] = entries;
                files[path] = JsonSerializer.SerializeToUtf8Bytes(entries);
            }
            var pckPath = System.IO.Path.Combine(testRoot, id + ".pck");
            PckArchive.Write(pckPath, files);
            descriptors.Add(new SkinModDescriptor(id, id, pckPath, affectsGameplay, testRoot));
        }

        void AddEventVisualProvider()
        {
            const string id = "Tests.EventVisual";
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["res://scenes/events/background_scenes/neow.tscn"] =
                    Encoding.UTF8.GetBytes("[gd_scene format=3]\n")
            };
            foreach (var language in new[] { "eng", "zhs" })
            {
                var path = $"res://{id}/localization/{language}/events.json";
                var entries = new Dictionary<string, string>
                {
                    ["TEST_EVENT.body"] = "Event Replacement"
                };
                allTables[path] = entries;
                files[path] = JsonSerializer.SerializeToUtf8Bytes(entries);
            }

            var pckPath = System.IO.Path.Combine(testRoot, id + ".pck");
            PckArchive.Write(pckPath, files);
            descriptors.Add(new SkinModDescriptor(id, id, pckPath, false, testRoot));
        }

        void AddFakeMerchantEventReplacement()
        {
            const string id = "Tests.FakeMerchantEvent";
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["res://animations/backgrounds/fake_merchant_room/bottom/shop_fake_merchant_bottom.png"] =
                    [1, 2, 3],
                ["res://images/events/fake_merchant.png"] = [4, 5, 6]
            };
            var pckPath = System.IO.Path.Combine(testRoot, id + ".pck");
            PckArchive.Write(pckPath, files);
            descriptors.Add(new SkinModDescriptor(id, id, pckPath, false, testRoot));
        }

        void Check(string scenario, string expectedIronclad, string expectedSilent)
        {
            var selectedLocalizationProviders = catalog.GetSelectedLocalizationProviderIds(selections);
            var mountedPaths = catalog.BuildOverlay(selections).Keys
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var providerId in new[]
                     {
                         "Tests.IronSkin",
                         "Tests.SilentSkin",
                         "Tests.OtherIronSkin"
                     })
            {
                foreach (var path in allTables.Keys.Where(path => path.StartsWith(
                             $"res://{providerId}/localization/",
                             StringComparison.OrdinalIgnoreCase)))
                {
                    var shouldMount = !catalog.IsManagedCosmeticLocalizationPath(path) ||
                                      selectedLocalizationProviders.Contains(providerId);
                    if (mountedPaths.Contains(path) != shouldMount)
                    {
                        throw new InvalidOperationException(
                            $"localization mounting failed: {scenario}/{providerId}");
                    }
                }
            }

            foreach (var eventPath in allTables.Keys.Where(path => path.StartsWith(
                         "res://Tests.EventVisual/localization/",
                         StringComparison.OrdinalIgnoreCase)))
            {
                if (!mountedPaths.Contains(eventPath) ||
                    catalog.IsManagedCosmeticLocalizationPath(eventPath) ||
                    selectedLocalizationProviders.Contains("Tests.EventVisual"))
                {
                    throw new InvalidOperationException(
                        $"event localization was tied to a cosmetic selection: {scenario}/{eventPath}");
                }
            }

            foreach (var language in new[] { "eng", "zhs" })
            {
                var translations = new Dictionary<string, string>
                {
                    ["IRONCLAD.title"] = "Base Ironclad",
                    ["SILENT.title"] = "Base Silent"
                };
                foreach (var path in catalog.FilterModdedLocalizationTables(
                             allTables.Keys.Where(path => path.Contains($"/{language}/")), selections))
                {
                    foreach (var entry in allTables[path])
                    {
                        translations[entry.Key] = entry.Value;
                    }
                }
                if (translations["IRONCLAD.title"] != expectedIronclad ||
                    translations["SILENT.title"] != expectedSilent ||
                    translations.GetValueOrDefault("WATCHER.title") != "New Character" ||
                    translations.GetValueOrDefault("TRANSLATION.title") != "Independent Translation" ||
                    translations.GetValueOrDefault("TEST_EVENT.body") != "Event Replacement")
                {
                    throw new InvalidOperationException($"localization ownership failed: {scenario}/{language}");
                }
            }
        }
    }
    finally
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

static void RunCardExportSelfTest(string gamePckPath)
{
    // Imported textures, binary scenes and other compiled resources can appear as direct
    // dependency nodes. They must be copied byte-for-byte; decoding them as UTF-8 inserts
    // replacement characters into headers and can crash Godot's native texture loader.
    byte[] binaryResource = [0x47, 0x53, 0x54, 0x32, 0xff, 0x00, 0x80, 0x7f];
    var untouchedBinary = SkinCatalog.RewriteAliasedResourceBytes(
        "res://Provider/_imported/example.ctex",
        binaryResource,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    if (!ReferenceEquals(binaryResource, untouchedBinary) ||
        !binaryResource.AsSpan().SequenceEqual(untouchedBinary))
    {
        throw new InvalidOperationException("binary runtime alias rewriting corrupted the payload");
    }

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
            ["res://generated/framed_card_project.json"] = Encoding.UTF8.Preamble.ToArray().Concat(
                Encoding.UTF8.GetBytes(
                    """
                    {"entries":[
                      {"cardId":"Tests.FramedCard","portrait":"res://generated/framed.png",
                       "frame":"res://generated/frame.tres","frameVisible":true}
                    ]}
                    """)).ToArray(),
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
                "[gd_resource type=\"StyleBoxFlat\" format=3]\n"),
            ["res://Tests.ExportedCardSkin/images/atlases/lance_cards.sprites/silent/shiv.tres.remap"] =
                Encoding.UTF8.GetBytes(
                    "[remap]\npath=\"res://.godot/exported/test-shiv.res\"\n"),
            ["res://Tests.ExportedCardSkin/images/atlases/lance_cards.sprites/silent/strike.tres.remap"] =
                Encoding.UTF8.GetBytes(
                    "[remap]\npath=\"res://.godot/exported/test-strike.res\"\n"),
            ["res://Tests.ExportedCardSkin/images/atlases/lance_cards.sprites/silent/acrobatics.tres.remap"] =
                Encoding.UTF8.GetBytes(
                    "[remap]\npath=\"res://.godot/exported/test-acrobatics.res\"\n"),
            ["res://.godot/exported/test-shiv.res"] = [10, 11, 12],
            ["res://.godot/exported/test-strike.res"] = [13, 14, 15],
            ["res://.godot/exported/test-acrobatics.res"] = [16, 17, 18]
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
                "tests"))
            .Concat([
                new CardCatalogEntry(
                    "Shiv",
                    "res://images/atlases/card_atlas.sprites/token/shiv.tres",
                    "token",
                    "misc",
                    "misc"),
                new CardCatalogEntry(
                    "StrikeIronclad",
                    "res://images/atlases/card_atlas.sprites/ironclad/strike.tres",
                    "ironclad",
                    "ironclad",
                    "ironclad"),
                new CardCatalogEntry(
                    "StrikeSilent",
                    "res://images/atlases/card_atlas.sprites/silent/strike.tres",
                    "silent",
                    "silent",
                    "silent"),
                new CardCatalogEntry(
                    "Acrobatics",
                    "res://images/atlases/card_atlas.sprites/silent/acrobatics.tres",
                    "silent",
                    "silent",
                    "silent")
            ])
            .ToArray();
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

        const string sharedPoolShiv =
            "res://Tests.ExportedCardSkin/images/atlases/lance_cards.sprites/silent/shiv.tres";
        var miscOption = catalog.CardGroups.Single(group => group.Id == "misc")
            .Options.Single(option => option.Id.Equals(
                sourceOption.Id,
                StringComparison.OrdinalIgnoreCase));
        if (!miscOption.NormalPortraits.TryGetValue("Shiv", out var routedShiv) ||
            !routedShiv.Equals(sharedPoolShiv, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"unique shared-pool card routing failed: {routedShiv ?? "<missing>"}");
        }
        if (catalog.CardGroups.FirstOrDefault(group => group.Id == "ironclad")?
                .Options.Any(option => option.Id.Equals(
                    sourceOption.Id,
                    StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new InvalidOperationException(
                "ambiguous cross-category card stem leaked into the Ironclad group");
        }
        var silentOption = catalog.CardGroups.Single(group => group.Id == "silent")
            .Options.Single(option => option.Id.Equals(
                sourceOption.Id,
                StringComparison.OrdinalIgnoreCase));
        if (!silentOption.Assets.Keys.Any(path => path.EndsWith(
                "/silent/acrobatics.tres",
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("normal same-category card routing regressed");
        }

        var sharedPoolOverlay = catalog.BuildIsolatedCardResource(
            "misc",
            miscOption.Id,
            sharedPoolShiv,
            useSelectedProvider: true,
            "self-test/shared-pool-shiv");
        if (!sharedPoolOverlay.ResourcePaths.ContainsKey(sharedPoolShiv) ||
            sharedPoolOverlay.Files.Count == 0)
        {
            throw new InvalidOperationException(
                "shared-pool portrait isolation failed");
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

        var batchedOverlay = catalog.BuildIsolatedCardResources(
            "tests",
            routed.Id,
            expectedPortraits.Values,
            useSelectedProvider: true,
            "self-test/batched");
        if (expectedPortraits.Values.Any(path =>
                !batchedOverlay.ResourcePaths.ContainsKey(path)) ||
            batchedOverlay.Files.Count < expectedPortraits.Count)
        {
            throw new InvalidOperationException("batched exported portrait isolation failed");
        }

        Console.WriteLine(
            "card export self-test passed: static, BOM-framed, animation fallback, " +
            "unique shared-pool routing and batched isolation");
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

static bool IsProviderProjectControlFile(string path)
{
    if (path.Equals("res://project.binary", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("res://project.godot", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("res://export_presets.cfg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gdextension", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return path.StartsWith("res://.godot/", StringComparison.OrdinalIgnoreCase) &&
           !path.StartsWith("res://.godot/imported/", StringComparison.OrdinalIgnoreCase) &&
           !path.StartsWith("res://.godot/exported/", StringComparison.OrdinalIgnoreCase);
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
