using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using STS2SkinChanger.Pck;

namespace STS2SkinChanger.Catalog;

internal sealed partial class SkinCatalog : IDisposable
{
    public const string BaseOptionId = "__base__";
    private static readonly JsonSerializerOptions CardReplacementJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly PckArchive _gameArchive;
    private readonly List<PckResourceIndex> _baselineIndexes;
    private readonly List<PckResourceIndex> _cosmeticIndexes;
    private readonly List<SkinGroup> _groups;
    private readonly IReadOnlyList<CardSkinGroup> _configuredCardGroups;
    private readonly IReadOnlyList<CardSkinOption> _pckCardOptions;
    private readonly List<CardSkinGroup> _cardGroups;

    private SkinCatalog(
        PckArchive gameArchive,
        List<PckResourceIndex> baselineIndexes,
        List<PckResourceIndex> cosmeticIndexes,
        IReadOnlyList<SkinGroup> groups,
        IReadOnlyList<CardSkinGroup> cardGroups,
        IReadOnlyList<CardSkinOption> pckCardOptions)
    {
        _gameArchive = gameArchive;
        _baselineIndexes = baselineIndexes;
        _cosmeticIndexes = cosmeticIndexes;
        _groups = groups.ToList();
        _configuredCardGroups = cardGroups;
        _pckCardOptions = pckCardOptions;
        _cardGroups = cardGroups.ToList();
    }

    public IReadOnlyList<SkinGroup> Groups => _groups;
    public IReadOnlyList<CardSkinGroup> CardGroups => _cardGroups;
    public IReadOnlyList<CardSkinOption> PckCardOptions => _pckCardOptions;
    public IReadOnlySet<string> CardProviderRoots => _cardGroups
        .SelectMany(group => group.Options)
        .Select(option => option.ProviderRootPath)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Cast<string>()
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static SkinCatalog Build(string gamePckPath, IEnumerable<SkinModDescriptor> mods)
    {
        var modList = mods.ToArray();
        var gameArchive = PckArchive.Open(gamePckPath);
        var baselineIndexes = new List<PckResourceIndex>();
        var cosmeticIndexes = new List<PckResourceIndex>();
        try
        {
            // importedToSource 有意跨索引共享：皮肤 Mod 的 PCK 常常只携带
            // .godot/imported/ 载荷而不带 .import/.remap，需要借助游戏 PCK
            // 先登记的 remap 映射把载荷归到正确源路径。相同哈希的导入文件内容
            // 必然一致，后注册覆盖先注册在视觉上没有差别。
            var importedToSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            baselineIndexes.Add(PckResourceIndex.Build(
                new SkinModDescriptor("game", "游戏原版", gamePckPath, true),
                gameArchive,
                importedToSource,
                IsAnimationRemap));

            foreach (var mod in modList)
            {
                if (mod.PckPath == null || !File.Exists(mod.PckPath))
                {
                    continue;
                }

                var archive = PckArchive.Open(mod.PckPath);
                try
                {
                    var index = PckResourceIndex.Build(
                        mod,
                        archive,
                        importedToSource,
                        remapFilter: null);
                    if (mod.AffectsGameplay)
                    {
                        baselineIndexes.Add(index);
                    }
                    else
                    {
                        cosmeticIndexes.Add(index);
                    }
                }
                catch
                {
                    // 索引构建失败时释放未登记到任何列表的档案句柄。
                    archive.Dispose();
                    throw;
                }
            }

            var groups = BuildGroups(cosmeticIndexes);
            var cardGroups = BuildCardGroups(cosmeticIndexes);
            var pckCardOptions = BuildPckCardOptions(cosmeticIndexes, baselineIndexes);
            var catalog = new SkinCatalog(
                gameArchive,
                baselineIndexes,
                cosmeticIndexes,
                groups,
                cardGroups,
                pckCardOptions);
            catalog.AddImageRuntimeProviderOptions(modList);
            catalog.SortGroupsAndOptions();
            return catalog;
        }
        catch
        {
            foreach (var index in baselineIndexes.Skip(1).Concat(cosmeticIndexes))
            {
                index.Dispose();
            }

            gameArchive.Dispose();
            throw;
        }
    }

    public static IReadOnlyList<SkinProviderProbe> ProbeSkinProviders(
        IEnumerable<SkinModDescriptor> mods)
    {
        var providers = new List<SkinProviderProbe>();
        foreach (var mod in mods.Where(mod => !mod.AffectsGameplay))
        {
            var visualGroups = 0;
            var cardAssets = 0;
            if (mod.PckPath != null && File.Exists(mod.PckPath))
            {
                PckArchive? archive = null;
                PckResourceIndex? index = null;
                try
                {
                    archive = PckArchive.Open(mod.PckPath);
                    index = PckResourceIndex.Build(
                        mod,
                        archive,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        remapFilter: null);
                    visualGroups = BuildGroups([index])
                        .Count(group => group.Options.Count > 0);
                    cardAssets = BuildCardGroups([index])
                        .Sum(group => group.Options.Sum(option =>
                            option.NormalPortraits.Count + option.AncientPortraits.Count));
                    cardAssets += BuildPckCardOptions([index]).Sum(option => option.Assets.Count);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"无法探测皮肤提供者 {mod.Id}: {exception.Message}");
                }
                finally
                {
                    if (index != null)
                    {
                        index.Dispose();
                    }
                    else
                    {
                        archive?.Dispose();
                    }
                }
            }

            var runtimeImages = 0;
            if (mod.HasDll && mod.RootPath != null)
            {
                var imageDirectory = System.IO.Path.Combine(mod.RootPath, "images");
                if (Directory.Exists(imageDirectory))
                {
                    runtimeImages = Directory.EnumerateFiles(
                            imageDirectory,
                            "*",
                            SearchOption.TopDirectoryOnly)
                        .Count(path =>
                            System.IO.Path.GetExtension(path).Equals(
                                ".png", StringComparison.OrdinalIgnoreCase) &&
                            KnownAncientIds.Contains(
                                System.IO.Path.GetFileNameWithoutExtension(path)));
                }

                if (visualGroups == 0 && cardAssets == 0 && LooksLikeDllSkinProvider(mod))
                {
                    // 只读取 PE 字符串，不把程序集载入运行时。纯 DLL 皮肤即使没有
                    // 可识别 PCK，只要明显补丁了视觉入口，也会被加载器隔离。
                    visualGroups = 1;
                }
            }

            if (visualGroups > 0 || cardAssets > 0 || runtimeImages > 0)
            {
                providers.Add(new SkinProviderProbe(
                    mod.Id,
                    mod.RootPath,
                    visualGroups,
                    cardAssets,
                    runtimeImages));
            }
        }

        return providers;
    }

    private static bool LooksLikeDllSkinProvider(SkinModDescriptor mod)
    {
        if (mod.RootPath == null)
        {
            return false;
        }

        var assemblyPath = System.IO.Path.Combine(mod.RootPath, mod.Id + ".dll");
        try
        {
            var info = new FileInfo(assemblyPath);
            if (!info.Exists || info.Length <= 0 || info.Length > 32 * 1024 * 1024)
            {
                return false;
            }

            var metadata = Encoding.Latin1.GetString(File.ReadAllBytes(assemblyPath));
            var usesPatchMechanism = metadata.Contains("HarmonyLib", StringComparison.Ordinal) ||
                                     metadata.Contains("HarmonyPatch", StringComparison.Ordinal) ||
                                     metadata.Contains("PatchAll", StringComparison.Ordinal);
            if (!usesPatchMechanism)
            {
                return false;
            }

            var hasSkinResourcePath = new[]
            {
                "scenes/creature_visuals",
                "screens/char_select",
                "packed/character_select",
                "events/background_scenes",
                "card_portraits",
                "card_atlas.sprites",
                "map_marker_",
                "ui/run_history"
            }.Any(path => metadata.Contains(path, StringComparison.OrdinalIgnoreCase));
            if (!hasSkinResourcePath)
            {
                return false;
            }

            return HasTarget("CharacterModel", "CreateVisuals", "CharacterSelectIcon", "IconTexture") ||
                   HasTarget("MonsterModel", "CreateVisuals") ||
                   HasTarget("EventModel", "CreateBackgroundScene", "MapIcon", "RunHistoryIcon") ||
                   HasTarget("CardModel", "Portrait", "PortraitPath") ||
                   HasTarget("AssetCache", "GetScene", "GetTexture2D", "GetAsset") ||
                   HasTarget("AtlasManager", "GetSprite", "LoadAtlas");

            bool HasTarget(string typeName, params string[] methodNames) =>
                metadata.Contains(typeName, StringComparison.Ordinal) &&
                methodNames.Any(methodName => metadata.Contains(methodName, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    public bool IsRuntimeProviderOption(string groupId, string optionId)
    {
        return Groups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(option => option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))?
            .IsRuntimeProvider == true;
    }

    public bool IsResourceBackedOption(string groupId, string optionId)
    {
        return Groups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(option => option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))?
            .Assets.Count > 0;
    }

    public string? GetRuntimeImagePath(string groupId, string optionId)
    {
        return Groups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(option => option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))?
            .RuntimeImagePath;
    }

    public string? FindGroupIdForResourcePath(string resourcePath)
    {
        var identity = TryGetPrimaryGroup(resourcePath) ??
                       TryGetCharacterSelectIconGroup(resourcePath) ??
                       TryGetCharacterUiTextureGroup(resourcePath) ??
                       TryGetCharacterMapMarkerGroup(resourcePath) ??
                       TryGetCharacterIconSceneGroup(resourcePath) ??
                       TryGetAncientIconGroup(resourcePath);
        return identity != null && Groups.Any(group =>
            group.Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase))
            ? identity.Id
            : null;
    }

    public ResourceAsset? ResolveBaseline(string sourcePath)
    {
        for (var i = _baselineIndexes.Count - 1; i >= 0; i--)
        {
            var index = _baselineIndexes[i];
            if (index.Assets.TryGetValue(sourcePath, out var known))
            {
                return known;
            }

            var lazy = index.TryBuildAsset(sourcePath);
            if (lazy != null)
            {
                return lazy;
            }
        }

        return null;
    }

    public Dictionary<string, ResourceFile> BuildOverlay(
        IReadOnlyDictionary<string, string> selections,
        IReadOnlySet<string>? onlyGroups = null)
    {
        var files = new Dictionary<string, ResourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups)
        {
            if (onlyGroups != null && !onlyGroups.Contains(group.Id))
            {
                continue;
            }

            selections.TryGetValue(group.Id, out var selectedId);
            var selected = group.Options.FirstOrDefault(option =>
                option.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
            var sourcePaths = group.Options
                .SelectMany(option => option.Assets.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var sourcePath in sourcePaths)
            {
                var asset = selected != null && selected.Assets.TryGetValue(sourcePath, out var selectedAsset)
                    ? selectedAsset
                    : ResolveBaseline(sourcePath);
                if (asset == null)
                {
                    continue;
                }

                foreach (var file in asset.Files)
                {
                    var targetPath = MapAssetFilePath(sourcePath, asset.SourcePath, file.Path);
                    files[targetPath] = file;
                    var takeoverPath = NormalizeTakeoverPath(targetPath);
                    if (!takeoverPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        files[takeoverPath] = file;
                    }
                }
            }

        }

        return files;
    }

    public Dictionary<string, ResourceFile> BuildCardOverlay(
        IReadOnlyDictionary<string, string> selections,
        IReadOnlySet<string>? onlyGroups = null)
    {
        var files = new Dictionary<string, ResourceFile>(StringComparer.OrdinalIgnoreCase);
        var selectedProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in CardGroups)
        {
            if (onlyGroups != null && !onlyGroups.Contains(group.Id))
            {
                continue;
            }

            selections.TryGetValue("cards:" + group.Id, out var selectedId);
            var selected = group.Options.FirstOrDefault(option =>
                option.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
            if (selected != null)
            {
                selectedProviderIds.Add(selected.ProviderId ?? selected.Id);
            }

            var sourcePaths = group.Options
                .SelectMany(option => option.Assets.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var sourcePath in sourcePaths)
            {
                var asset = selected != null && selected.Assets.TryGetValue(sourcePath, out var selectedAsset)
                    ? selectedAsset
                    : ResolveBaseline(sourcePath);
                if (asset == null)
                {
                    continue;
                }

                foreach (var file in asset.Files)
                {
                    // 与 BuildOverlay 一致：把文件映射到请求的源路径名下，保证基线回退时
                    // 文件仍出现在游戏实际加载的路径上。
                    var targetPath = MapAssetFilePath(sourcePath, asset.SourcePath, file.Path);
                    files[targetPath] = file;
                    var takeoverPath = NormalizeTakeoverPath(targetPath);
                    if (!takeoverPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        files[takeoverPath] = file;
                    }
                }
            }
        }

        foreach (var selection in selections
                     .Where(pair => pair.Key.StartsWith("cards:item:", StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Value)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var option = CardGroups.SelectMany(group => group.Options)
                .FirstOrDefault(candidate => candidate.Id.Equals(selection, StringComparison.OrdinalIgnoreCase));
            if (option != null)
            {
                selectedProviderIds.Add(option.ProviderId ?? option.Id);
            }
        }

        foreach (var file in BuildCardProviderNamespaceOverlay(selectedProviderIds))
        {
            files[file.Key] = file.Value;
        }

        return files;
    }

    public Dictionary<string, ResourceFile> BuildCardProviderNamespaceOverlay(
        IEnumerable<string> providerIds)
    {
        var files = new Dictionary<string, ResourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var providerId in providerIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var index in _cosmeticIndexes.Where(index =>
                         index.Mod.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var file in CollectProviderNamespaceFiles(index, providerId))
                {
                    files[file.Path] = file;
                }
            }
        }

        return files;
    }

    private static IReadOnlyCollection<ResourceFile> CollectProviderNamespaceFiles(
        PckResourceIndex index,
        string providerId)
    {
        var idToken = NormalizeResourceToken(providerId);
        var paths = index.Archive.Paths
            .Where(path => IsProviderNamespacePath(path, idToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(paths);
        while (queue.TryDequeue(out var path))
        {
            if (!MayContainResourceReferences(path))
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(index.Archive.ReadFile(path));
            foreach (Match reference in ResourcePathRegex().Matches(text))
            {
                var referencedPath = reference.Groups[1].Value;
                if (index.Archive.Contains(referencedPath) && paths.Add(referencedPath))
                {
                    queue.Enqueue(referencedPath);
                }
            }
        }

        return paths.Select(path => new ResourceFile(index.Archive, path)).ToArray();
    }

    private static bool IsProviderNamespacePath(string path, string idToken)
    {
        if (idToken.Length == 0 || !path.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = path[6..];
        var separator = relative.IndexOf('/');
        var topLevel = separator < 0 ? relative : relative[..separator];
        var topLevelToken = NormalizeResourceToken(topLevel);
        return topLevelToken.Equals(idToken, StringComparison.OrdinalIgnoreCase) ||
               topLevelToken.StartsWith(idToken, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeResourceToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public void FinalizeCardGroups(IEnumerable<CardCatalogEntry> cards)
    {
        var cardEntries = cards
            .Where(card => !string.IsNullOrWhiteSpace(card.PortraitPath) &&
                           !string.IsNullOrWhiteSpace(card.CatalogGroupId) &&
                           !string.IsNullOrWhiteSpace(card.FilterGroupId))
            .ToArray();
        var cardsByType = cardEntries
            .GroupBy(card => card.TypeName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                entries => entries.Key,
                entries => entries.First(),
                StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, CardSkinGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var configuredGroup in _configuredCardGroups)
        {
            foreach (var option in configuredGroup.Options)
            {
                AddCardOption(groups, configuredGroup.Id, option);

                var specialGroupIds = option.NormalPortraits.Keys
                    .Concat(option.AncientPortraits.Keys)
                    .Select(cardType => cardsByType.GetValueOrDefault(cardType))
                    .Where(card => card != null &&
                                   !card.FilterGroupId.Equals(
                                       configuredGroup.Id,
                                       StringComparison.OrdinalIgnoreCase))
                    .Select(card => card!.FilterGroupId)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var specialGroupId in specialGroupIds)
                {
                    var normal = option.NormalPortraits
                        .Where(pair => cardsByType.TryGetValue(pair.Key, out var card) &&
                                       card.FilterGroupId.Equals(
                                           specialGroupId,
                                           StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    var ancient = option.AncientPortraits
                        .Where(pair => cardsByType.TryGetValue(pair.Key, out var card) &&
                                       card.FilterGroupId.Equals(
                                           specialGroupId,
                                           StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    AddCardOption(groups, specialGroupId, option with
                    {
                        NormalPortraits = normal,
                        AncientPortraits = ancient,
                        Assets = new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase)
                    });
                }
            }
        }

        foreach (var option in _pckCardOptions)
        {
            var assetsByGroup = new Dictionary<string, Dictionary<string, ResourceAsset>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var card in cardEntries)
            {
                var assets = option.Assets
                    .Where(pair => CardArtMatches(pair.Key, card))
                    .ToArray();
                if (assets.Length == 0)
                {
                    continue;
                }

                var groupId = card.FilterGroupId.Equals(
                    card.CatalogGroupId,
                    StringComparison.OrdinalIgnoreCase)
                    ? card.CatalogGroupId
                    : card.FilterGroupId;
                if (!assetsByGroup.TryGetValue(groupId, out var groupAssets))
                {
                    groupAssets = new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase);
                    assetsByGroup.Add(groupId, groupAssets);
                }

                foreach (var asset in assets)
                {
                    groupAssets[asset.Key] = asset.Value;
                }
            }

            foreach (var pair in assetsByGroup)
            {
                AddCardOption(groups, pair.Key, option with { Assets = pair.Value });
            }
        }

        _cardGroups.Clear();
        _cardGroups.AddRange(groups.Values
            .Where(group => group.Options.Count > 0)
            .OrderBy(group => GroupSortOrder(group.Id))
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase));
        foreach (var group in _cardGroups)
        {
            group.Options.Sort((left, right) =>
                string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
        }
    }

    private static void AddCardOption(
        IDictionary<string, CardSkinGroup> groups,
        string groupId,
        CardSkinOption option)
    {
        if (!groups.TryGetValue(groupId, out var group))
        {
            group = new CardSkinGroup(groupId, DisplayName(groupId));
            groups.Add(groupId, group);
        }

        var existingIndex = group.Options.FindIndex(existing =>
            existing.Id.Equals(option.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            group.Options[existingIndex] = group.Options[existingIndex].Merge(option);
        }
        else
        {
            group.Options.Add(option);
        }
    }

    public IReadOnlySet<string> GetAffectedSourcePaths(string groupId)
    {
        var group = Groups.First(group =>
            group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        var affected = group.Options
            .SelectMany(option => option.Assets.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var index in _baselineIndexes)
        {
            foreach (var sourcePath in index.Assets.Keys)
            {
                var identity = TryGetPrimaryGroup(sourcePath);
                if (identity?.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase) == true &&
                    sourcePath.EndsWith(".tres", StringComparison.OrdinalIgnoreCase))
                {
                    affected.Add(sourcePath);
                }
            }
        }

        return affected;
    }

    public RuntimeResourceOverlay BuildRuntimeResourceOverlay(
        string groupId,
        string selectionId,
        IReadOnlyCollection<string> resourcePaths,
        string aliasToken,
        bool includeProviderDependencies = false)
    {
        var group = Groups.First(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        var selected = group.Options.FirstOrDefault(option =>
            option.Id.Equals(selectionId, StringComparison.OrdinalIgnoreCase));
        var sourcePaths = GetAffectedSourcePaths(groupId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        sourcePaths.UnionWith(resourcePaths);
        IncludeAtlasTexturePages(selected, sourcePaths);

        var resources = new List<RuntimeResource>();
        foreach (var sourcePath in sourcePaths)
        {
            var baseline = ResolveBaseline(sourcePath);
            var primary = selected != null && selected.Assets.TryGetValue(sourcePath, out var selectedAsset)
                ? selectedAsset
                : baseline;
            if (primary == null)
            {
                continue;
            }

            var directFile = FindDirectFile(primary, sourcePath);
            var remapFile = FindRemapFile(primary, sourcePath);
            var payloadFiles = GetImportedPayloadFiles(primary, sourcePath);
            if (directFile == null && remapFile == null && baseline != null && !ReferenceEquals(primary, baseline))
            {
                directFile = FindDirectFile(baseline, sourcePath);
                remapFile = FindRemapFile(baseline, sourcePath);
                if (payloadFiles.Length == 0)
                {
                    payloadFiles = GetImportedPayloadFiles(baseline, sourcePath);
                }
            }

            resources.Add(new RuntimeResource(sourcePath, directFile, remapFile, payloadFiles));
        }

        var overlay = BuildAliasedResourceOverlay(resources, resourcePaths, aliasToken);
        if (selected == null || !includeProviderDependencies)
        {
            return overlay;
        }

        var dependencyFiles = CollectSelectedProviderDependencies(
            selected,
            resources,
            overlay.SourceAliases,
            overlay.PayloadAliases);
        if (dependencyFiles.Count == 0)
        {
            return overlay;
        }

        var files = overlay.Files.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencyFiles)
        {
            files[dependency.Key] = dependency.Value;
        }

        return new RuntimeResourceOverlay(
            overlay.ResourcePaths,
            files,
            overlay.SourceAliases,
            overlay.PayloadAliases);
    }

    private Dictionary<string, byte[]> CollectSelectedProviderDependencies(
        SkinOption selected,
        IReadOnlyCollection<RuntimeResource> resources,
        IReadOnlyDictionary<string, string> sourceAliases,
        IReadOnlyDictionary<string, string> payloadAliases)
    {
        var indexes = _cosmeticIndexes
            .Where(index => index.Mod.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (indexes.Length == 0)
        {
            return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(PckResourceIndex? Index, ResourceFile File)>();
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in resources
                     .SelectMany(resource => new[] { resource.DirectFile, resource.RemapFile }
                         .Where(file => file != null)
                         .Cast<ResourceFile>()
                         .Concat(resource.PayloadFiles)))
        {
            var index = indexes.FirstOrDefault(candidate => ReferenceEquals(candidate.Archive, file.Archive));
            // 基线场景本身也必须参与扫描：它可能引用提供者中没有角色 ID 的
            // 辅助贴图或脚本，而这些资源无法靠文件名归入皮肤分组。
            Enqueue(index, file);
        }

        while (queue.TryDequeue(out var pending))
        {
            if (!MayContainResourceReferences(pending.File.Path))
            {
                continue;
            }

            // 扫描始终用原始文本（提供者索引按原始路径登记），写入时才做别名重写。
            var bytes = pending.File.Archive.ReadFile(pending.File.Path);
            var text = Encoding.UTF8.GetString(bytes);
            foreach (Match match in EmbeddedResourcePathRegex().Matches(text))
            {
                var sourcePath = match.Value;
                PckResourceIndex? dependencyIndex = null;
                ResourceAsset? dependency = null;
                var candidates = pending.Index == null
                    ? indexes
                    : [pending.Index, .. indexes.Where(index => !ReferenceEquals(index, pending.Index))];
                foreach (var candidate in candidates)
                {
                    dependency = candidate.Assets.GetValueOrDefault(sourcePath) ??
                                 candidate.TryBuildAsset(sourcePath);
                    if (dependency != null)
                    {
                        dependencyIndex = candidate;
                        break;
                    }
                }

                if (dependency == null || dependencyIndex == null)
                {
                    continue;
                }

                foreach (var file in dependency.Files)
                {
                    // 挂在原始路径上的依赖副本同样重写文本引用：二进制资源
                    // (.scn/.res) 内部无法重写，其回退引用会落到这些原始路径副本，
                    // 重写后整条链都指向别名空间的新鲜副本，避免命中游戏缓存的
                    // 原版贴图导致预览图混用资源。
                    var dependencyBytes = file.Archive.ReadFile(file.Path);
                    if (IsRewritableTextResource(file.Path))
                    {
                        dependencyBytes = RewriteTextResource(dependencyBytes, sourceAliases, payloadAliases);
                    }

                    result[file.Path] = dependencyBytes;
                    var takeoverPath = NormalizeTakeoverPath(file.Path);
                    if (!takeoverPath.Equals(file.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        result[takeoverPath] = dependencyBytes;
                    }

                    Enqueue(dependencyIndex, file);
                }
            }
        }

        return result;

        void Enqueue(PckResourceIndex? index, ResourceFile file)
        {
            var key = (index?.Mod.Id ?? file.Archive.Path) + "\n" + file.Path;
            if (queued.Add(key))
            {
                queue.Enqueue((index, file));
            }
        }
    }

    private static bool IsRewritableTextResource(string path) =>
        path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".spatlas", StringComparison.OrdinalIgnoreCase);

    private static bool MayContainResourceReferences(string path) =>
        path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".scn", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".res", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gdc", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".spatlas", StringComparison.OrdinalIgnoreCase);

    public RuntimeResourceOverlay BuildIsolatedCardResource(
        string groupId,
        string selectionId,
        string resourcePath,
        bool useSelectedProvider,
        string aliasToken)
    {
        ResourceAsset? asset;
        if (useSelectedProvider)
        {
            var option = CardGroups
                .FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
                .Options.FirstOrDefault(option =>
                    option.Id.Equals(selectionId, StringComparison.OrdinalIgnoreCase));
            asset = option == null ? null : ResolveCardProviderAsset(option, resourcePath);
        }
        else
        {
            asset = ResolveBaseline(resourcePath);
        }

        if (asset == null)
        {
            throw new InvalidOperationException($"找不到独立卡图资源：{resourcePath}");
        }
        var resource = new RuntimeResource(
            resourcePath,
            FindDirectFile(asset, resourcePath),
            FindRemapFile(asset, resourcePath),
            GetImportedPayloadFiles(asset, resourcePath));
        return BuildAliasedResourceOverlay([resource], [resourcePath], aliasToken);
    }

    private ResourceAsset? ResolveCardProviderAsset(CardSkinOption option, string resourcePath)
    {
        if (option.Assets.TryGetValue(resourcePath, out var configured))
        {
            return configured;
        }

        foreach (var index in _cosmeticIndexes.Where(index =>
                     string.Equals(
                         index.Mod.RootPath,
                         option.ProviderRootPath,
                         StringComparison.OrdinalIgnoreCase)))
        {
            if (index.Assets.TryGetValue(resourcePath, out var known))
            {
                return known;
            }

            var lazy = index.TryBuildAsset(resourcePath);
            if (lazy != null)
            {
                return lazy;
            }
        }

        return null;
    }

    private static RuntimeResourceOverlay BuildAliasedResourceOverlay(
        IReadOnlyCollection<RuntimeResource> resources,
        IReadOnlyCollection<string> resourcePaths,
        string aliasToken)
    {
        var sourceAliases = resources.ToDictionary(
            resource => resource.SourcePath,
            resource => $"res://sts2_skin_runtime/{aliasToken}/{resource.SourcePath[6..]}",
            StringComparer.OrdinalIgnoreCase);
        var payloadAliases = resources
            .SelectMany(resource => resource.PayloadFiles)
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                file => file.Path,
                file => $"res://sts2_skin_runtime/{aliasToken}/_payload/{file.Path[6..]}",
                StringComparer.OrdinalIgnoreCase);

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resources)
        {
            if (resource.DirectFile != null)
            {
                var bytes = resource.DirectFile.Archive.ReadFile(resource.DirectFile.Path);
                files[sourceAliases[resource.SourcePath]] = RewriteTextResource(bytes, sourceAliases, payloadAliases);
            }

            foreach (var payloadFile in resource.PayloadFiles)
            {
                var bytes = payloadFile.Archive.ReadFile(payloadFile.Path);
                files[payloadAliases[payloadFile.Path]] = payloadFile.Path.EndsWith(".spatlas", StringComparison.OrdinalIgnoreCase)
                    ? RewriteTextResource(bytes, sourceAliases, payloadAliases, stripUids: false)
                    : bytes;
            }

            if (resource.RemapFile == null)
            {
                continue;
            }

            var remapText = Encoding.UTF8.GetString(resource.RemapFile.Archive.ReadFile(resource.RemapFile.Path));
            var replacements = new Dictionary<string, string>(sourceAliases, StringComparer.OrdinalIgnoreCase);
            foreach (Match match in ResourcePathRegex().Matches(remapText))
            {
                var originalPath = match.Groups[1].Value;
                if (replacements.ContainsKey(originalPath))
                {
                    continue;
                }

                var payloadFile = MatchImportedFile(originalPath, resource.PayloadFiles);
                if (payloadFile != null)
                {
                    replacements[originalPath] = payloadAliases[payloadFile.Path];
                }
            }

            var remapSuffix = resource.RemapFile.Path.EndsWith(".import", StringComparison.OrdinalIgnoreCase)
                ? ".import"
                : ".remap";
            files[sourceAliases[resource.SourcePath] + remapSuffix] =
                RewriteTextResource(Encoding.UTF8.GetBytes(remapText), replacements, null);
        }

        var aliasedResourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourcePath in resourcePaths)
        {
            if (!sourceAliases.TryGetValue(resourcePath, out var aliasedResourcePath) ||
                (!files.ContainsKey(aliasedResourcePath) &&
                 !files.ContainsKey(aliasedResourcePath + ".import") &&
                 !files.ContainsKey(aliasedResourcePath + ".remap")))
            {
                throw new InvalidOperationException($"无法为 {resourcePath} 创建独立皮肤资源。");
            }

            aliasedResourcePaths[resourcePath] = aliasedResourcePath;
        }

        return new RuntimeResourceOverlay(aliasedResourcePaths, files, sourceAliases, payloadAliases);
    }

    private void IncludeAtlasTexturePages(SkinOption? selected, HashSet<string> sourcePaths)
    {
        var atlasDirectories = sourcePaths
            .Where(path => path.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase))
            .Select(path => path[..(path.LastIndexOf('/') + 1)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (atlasDirectories.Length == 0)
        {
            return;
        }

        var candidates = _baselineIndexes
            .SelectMany(index => index.Assets.Keys)
            .Concat(selected?.Assets.Keys ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!candidate.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                !candidate.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var directory in atlasDirectories)
            {
                if (!candidate.StartsWith(directory, StringComparison.OrdinalIgnoreCase) ||
                    candidate[directory.Length..].Contains('/'))
                {
                    continue;
                }

                sourcePaths.Add(candidate);
                break;
            }
        }
    }

    public void Dispose()
    {
        foreach (var index in _baselineIndexes.Skip(1).Concat(_cosmeticIndexes))
        {
            index.Dispose();
        }

        _gameArchive.Dispose();
    }

    private static IReadOnlyList<SkinGroup> BuildGroups(IEnumerable<PckResourceIndex> cosmeticIndexes)
    {
        var indexes = cosmeticIndexes.ToArray();
        var groups = new Dictionary<string, SkinGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in indexes)
        {
            var primaryGroups = index.Assets.Keys
                .Select(TryGetPrimaryGroup)
                .Where(group => group != null)
                .Cast<GroupIdentity>()
                .DistinctBy(group => group.Id)
                .ToArray();

            if (primaryGroups.Length == 0)
            {
                continue;
            }

            var assigned = new Dictionary<string, Dictionary<string, ResourceAsset>>(StringComparer.OrdinalIgnoreCase);
            foreach (var identity in primaryGroups)
            {
                assigned[identity.Id] = new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var asset in index.Assets.Values)
            {
                if (IsCardArtSourcePath(asset.SourcePath))
                {
                    continue;
                }

                var identity = TryGetPrimaryGroup(asset.SourcePath);
                if (identity != null && assigned.TryGetValue(identity.Id, out var primaryAssets))
                {
                    primaryAssets[asset.SourcePath] = asset;
                    continue;
                }

                var tokenMatches = primaryGroups
                    .Where(group => ContainsGroupToken(asset.SourcePath, group.Id))
                    .ToArray();
                if (tokenMatches.Length == 1)
                {
                    assigned[tokenMatches[0].Id][asset.SourcePath] = asset;
                }
            }

            foreach (var identity in primaryGroups)
            {
                var assets = assigned[identity.Id];
                if (assets.Count == 0)
                {
                    continue;
                }

                if (!groups.TryGetValue(identity.Id, out var group))
                {
                    group = new SkinGroup(identity.Id, identity.DisplayName);
                    groups.Add(identity.Id, group);
                }

                group.Options.Add(new SkinOption(index.Mod.Id, index.Mod.Name, assets));
            }
        }

        MergeCharacterSelectIconPacks(indexes, groups);
        AddPckRuntimeProviderOptions(indexes, groups);

        foreach (var group in groups.Values)
        {
            group.Options.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
        }

        return groups.Values
            .OrderBy(group => GroupSortOrder(group.Id))
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<CardSkinGroup> BuildCardGroups(IEnumerable<PckResourceIndex> cosmeticIndexes)
    {
        var groups = new Dictionary<string, CardSkinGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in cosmeticIndexes)
        {
            foreach (var configPath in index.Archive.Paths.Where(path =>
                         path.EndsWith("/card_replacements.json", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var config = JsonSerializer.Deserialize<CardReplacementConfig>(
                        index.Archive.ReadFile(configPath),
                        CardReplacementJsonOptions);
                    if (config == null)
                    {
                        continue;
                    }

                    var groupIds = config.NormalReplacements
                        .Select(entry => TryGetCardPortraitGroup(entry.PortraitPath))
                        .Concat(config.AncientReplacements.SelectMany(entry => new[]
                        {
                            TryGetCardPortraitGroup(entry.NormalPortrait),
                            TryGetCardPortraitGroup(entry.AncientPortrait)
                        }))
                        .Where(id => id != null)
                        .Cast<string>()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    foreach (var groupId in groupIds)
                    {
                        var normal = config.NormalReplacements
                            .Where(entry => TryGetCardPortraitGroup(entry.PortraitPath)?.Equals(
                                groupId, StringComparison.OrdinalIgnoreCase) == true)
                            .Where(entry => !string.IsNullOrWhiteSpace(entry.CardType) &&
                                            !string.IsNullOrWhiteSpace(entry.PortraitPath))
                            .GroupBy(entry => entry.CardType, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                entries => entries.Key,
                                entries => entries.Last().PortraitPath,
                                StringComparer.OrdinalIgnoreCase);
                        var ancient = config.AncientReplacements
                            .Where(entry => TryGetCardPortraitGroup(entry.PathForGrouping)?.Equals(
                                groupId, StringComparison.OrdinalIgnoreCase) == true)
                            .Where(entry => !string.IsNullOrWhiteSpace(entry.CardType))
                            .GroupBy(entry => entry.CardType, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                entries => entries.Key,
                                entries => new AncientCardPortrait(
                                    entries.Last().NormalPortrait,
                                    entries.Last().AncientPortrait),
                                StringComparer.OrdinalIgnoreCase);
                        if (normal.Count == 0 && ancient.Count == 0)
                        {
                            continue;
                        }

                        if (!groups.TryGetValue(groupId, out var group))
                        {
                            group = new CardSkinGroup(groupId, DisplayName(groupId));
                            groups.Add(groupId, group);
                        }

                        var existingIndex = group.Options.FindIndex(option =>
                            option.Id.Equals(index.Mod.Id, StringComparison.OrdinalIgnoreCase));
                        var option = new CardSkinOption(
                            index.Mod.Id,
                            index.Mod.Name,
                            normal,
                            ancient,
                            ProviderRootPath: index.Mod.RootPath,
                            ProviderId: index.Mod.Id);
                        if (existingIndex >= 0)
                        {
                            group.Options[existingIndex] = group.Options[existingIndex].Merge(option);
                        }
                        else
                        {
                            group.Options.Add(option);
                        }
                    }
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine($"无法读取卡牌皮肤配置 {configPath}: {exception.Message}");
                }
            }
        }

        foreach (var group in groups.Values)
        {
            group.Options.Sort((left, right) =>
                string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
        }

        return groups.Values
            .OrderBy(group => GroupSortOrder(group.Id))
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<CardSkinOption> BuildPckCardOptions(
        IEnumerable<PckResourceIndex> cosmeticIndexes,
        IReadOnlyList<PckResourceIndex>? baselineIndexes = null)
    {
        var options = new List<CardSkinOption>();
        foreach (var index in cosmeticIndexes)
        {
            var standardAssets = index.Assets.Values
                .Where(asset => IsCardArtSourcePath(asset.SourcePath))
                .ToArray();
            var looseCandidates = index.Assets.Values
                .Where(asset => IsLooseProviderCardArtPath(index.Mod.Id, asset.SourcePath))
                .ToArray();
            var looseAssets = IsBulkLooseCardPack(index.Mod.Id, looseCandidates)
                ? looseCandidates
                : [];
            var allAssets = standardAssets
                .Concat(looseAssets)
                .DistinctBy(asset => asset.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var changedAssets = baselineIndexes == null
                ? allAssets
                : allAssets.Where(asset => AssetDiffersFromBaseline(asset, baselineIndexes)).ToArray();
            if (changedAssets.Length == 0)
            {
                continue;
            }

            var originalVariantKeys = allAssets
                .Select(asset => GetCardVariantKey(asset.SourcePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var variants = changedAssets
                .GroupBy(asset => GetCardVariantKey(asset.SourcePath), StringComparer.OrdinalIgnoreCase)
                .Select(group => new CardArtVariant(
                    group.Key,
                    group.ToArray(),
                    group.Select(asset => TryGetCardArtIdentity(asset.SourcePath)?.Stem)
                        .Where(stem => !string.IsNullOrEmpty(stem))
                        .Cast<string>()
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)))
                .OrderBy(group => group.Key.Length == 0 ? 0 : 1)
                .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var splitVariants = variants.Length > 1 && VariantsOverlap(variants);
            var optionVariants = splitVariants
                ? variants
                :
                [
                    new CardArtVariant(
                        variants.Length == 1 ? variants[0].Key : string.Empty,
                        changedAssets,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                ];
            var exposeVariants = splitVariants ||
                                 (optionVariants[0].Key.Length > 0 && originalVariantKeys.Length > 1);
            foreach (var variant in optionVariants)
            {
                var variantId = optionVariants.Length == 1 || variant.Key.Length == 0
                    ? index.Mod.Id
                    : index.Mod.Id + "::variant:" + variant.Key.ToLowerInvariant();
                var variantName = exposeVariants
                    ? index.Mod.Name + " · " + DisplayCardVariant(variant.Key)
                    : index.Mod.Name;
                options.Add(new CardSkinOption(
                    variantId,
                    variantName,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, AncientCardPortrait>(StringComparer.OrdinalIgnoreCase),
                    variant.Assets.ToDictionary(
                        asset => asset.SourcePath,
                        asset => asset,
                        StringComparer.OrdinalIgnoreCase),
                    index.Mod.RootPath,
                    index.Mod.Id));
            }
        }

        return options;
    }

    private static bool IsBulkLooseCardPack(
        string providerId,
        IReadOnlyCollection<ResourceAsset> candidates)
    {
        if (candidates.Count < 20)
        {
            return false;
        }

        var directChildren = candidates.Count(asset =>
            IsDirectProviderChild(providerId, asset.SourcePath));
        return directChildren >= 20 && directChildren * 100 >= candidates.Count * 80;
    }

    private static bool IsDirectProviderChild(string providerId, string path)
    {
        if (!IsProviderNamespacePath(path, NormalizeResourceToken(providerId)))
        {
            return false;
        }

        var relative = path[6..];
        var firstSeparator = relative.IndexOf('/');
        return firstSeparator >= 0 && !relative[(firstSeparator + 1)..].Contains('/');
    }

    private static bool VariantsOverlap(IReadOnlyList<CardArtVariant> variants)
    {
        for (var left = 0; left < variants.Count; left++)
        {
            for (var right = left + 1; right < variants.Count; right++)
            {
                if (variants[left].Stems.Overlaps(variants[right].Stems))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool AssetDiffersFromBaseline(
        ResourceAsset asset,
        IReadOnlyList<PckResourceIndex> baselineIndexes)
    {
        ResourceAsset? baseline = null;
        for (var index = baselineIndexes.Count - 1; index >= 0 && baseline == null; index--)
        {
            baseline = baselineIndexes[index].Assets.GetValueOrDefault(asset.SourcePath) ??
                       baselineIndexes[index].TryBuildAsset(asset.SourcePath);
        }

        if (baseline == null)
        {
            return true;
        }

        foreach (var file in asset.Files)
        {
            var path = NormalizeTakeoverPath(file.Path);
            var baselineFile = baseline.Files.FirstOrDefault(candidate =>
                NormalizeTakeoverPath(candidate.Path).Equals(path, StringComparison.OrdinalIgnoreCase));
            if (baselineFile == null ||
                file.Archive.GetFileSize(file.Path) != baselineFile.Archive.GetFileSize(baselineFile.Path) ||
                !file.Archive.GetFileMd5(file.Path)
                    .SequenceEqual(baselineFile.Archive.GetFileMd5(baselineFile.Path)))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetCardVariantKey(string path)
    {
        var match = CardArtIdentityRegex().Match(path);
        if (!match.Success)
        {
            return string.Empty;
        }

        var fileSeparator = path.LastIndexOf('/');
        var variantStart = match.Index + match.Length;
        var variant = fileSeparator <= variantStart
            ? string.Empty
            : path[variantStart..fileSeparator].Trim('/');
        return variant.Equals("beta", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : variant;
    }

    private static string DisplayCardVariant(string variant)
    {
        if (variant.Length == 0)
        {
            return "默认";
        }

        return variant
            .Replace('/', ' ')
            .Replace('_', ' ')
            .Replace('-', ' ')
            .CapitalizeWords();
    }

    private static string? TryGetCardPortraitGroup(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var match = CardPortraitGroupRegex().Match(path);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static bool IsCardArtSourcePath(string path) =>
        CardArtPathRegex().IsMatch(path) && IsCardArtResourceExtension(path);

    private static bool IsCardArtResourceExtension(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tres", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".res", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLooseProviderCardArtPath(string providerId, string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".tres", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsProviderNamespacePath(path, NormalizeResourceToken(providerId));
    }

    private static bool CardArtMatches(string assetPath, CardCatalogEntry card)
    {
        var asset = TryGetCardArtIdentity(assetPath);
        var portrait = TryGetCardArtIdentity(card.PortraitPath);
        if (asset == null || portrait == null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(asset.Category) &&
            !asset.Category.Equals(card.PoolGroupId, StringComparison.OrdinalIgnoreCase) &&
            !asset.Category.Equals(portrait.Category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var typeStem = NormalizeCardToken(card.TypeName);
        return CardStemsMatch(asset.Stem, portrait.Stem) ||
               CardStemsMatch(asset.Stem, typeStem);
    }

    private static CardArtIdentity? TryGetCardArtIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var match = CardArtIdentityRegex().Match(path);
        var category = match.Success ? match.Groups[1].Value.ToLowerInvariant() : string.Empty;
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        var extensionIndex = fileName.LastIndexOf('.');
        var rawStem = extensionIndex >= 0 ? fileName[..extensionIndex] : fileName;
        var typeSeparator = rawStem.LastIndexOf('.');
        if (typeSeparator >= 0)
        {
            rawStem = rawStem[(typeSeparator + 1)..];
        }

        foreach (var suffix in new[]
                 {
                     "_card_art", "-card-art", " card art", "card_art", "cardart"
                 })
        {
            if (rawStem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                rawStem = rawStem[..^suffix.Length];
                break;
            }
        }

        var stem = NormalizeCardToken(rawStem);
        return new CardArtIdentity(category, stem);
    }

    private static bool CardStemsMatch(string candidate, string expected) =>
        candidate.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
        candidate.Equals(expected + "ancient", StringComparison.OrdinalIgnoreCase) ||
        candidate.Equals(expected + "normal", StringComparison.OrdinalIgnoreCase) ||
        candidate.Equals(expected + "portrait", StringComparison.OrdinalIgnoreCase) ||
        candidate.Equals(expected + "art", StringComparison.OrdinalIgnoreCase) ||
        IsNumberedCardVariant(candidate, expected);

    private static bool IsNumberedCardVariant(string candidate, string expected) =>
        candidate.StartsWith(expected, StringComparison.OrdinalIgnoreCase) &&
        candidate.Length > expected.Length &&
        candidate[expected.Length..].All(char.IsDigit);

    private static string NormalizeCardToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static void AddPckRuntimeProviderOptions(
        IReadOnlyCollection<PckResourceIndex> indexes,
        IDictionary<string, SkinGroup> groups)
    {
        foreach (var index in indexes)
        {
            var enabledGroupIds = ReadEnabledRuntimeGroupIds(index.Mod);
            var runtimeAssets = index.Assets.Values
                .Select(asset => (Asset: asset, Mapping: TryGetRuntimeProviderAsset(asset.SourcePath)))
                .Where(pair => pair.Mapping != null)
                .Select(pair => (pair.Asset, Mapping: pair.Mapping!))
                .ToArray();
            var identities = runtimeAssets
                .Select(pair => pair.Mapping.Identity)
                .Where(identity => enabledGroupIds == null || enabledGroupIds.Contains(identity.Id))
                .DistinctBy(identity => identity.Id)
                .ToArray();
            foreach (var identity in identities)
            {
                if (!groups.TryGetValue(identity.Id, out var group))
                {
                    group = new SkinGroup(identity.Id, identity.DisplayName);
                    groups.Add(identity.Id, group);
                }

                var mappedAssets = runtimeAssets
                    .Where(pair => pair.Mapping.Identity.Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(pair => pair.Mapping.CanonicalPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        pairs => pairs.Key,
                        pairs => pairs.Last().Asset,
                        StringComparer.OrdinalIgnoreCase);
                var existingIndex = group.Options.FindIndex(option =>
                    option.Id.Equals(index.Mod.Id, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    var existing = group.Options[existingIndex];
                    var mergedAssets = new Dictionary<string, ResourceAsset>(
                        existing.Assets, StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in mappedAssets)
                    {
                        mergedAssets[pair.Key] = pair.Value;
                    }

                    group.Options[existingIndex] = existing with
                    {
                        Assets = mergedAssets,
                        IsRuntimeProvider = true
                    };
                    continue;
                }

                group.Options.Add(new SkinOption(
                    index.Mod.Id,
                    index.Mod.Name,
                    mappedAssets,
                    IsRuntimeProvider: true));
            }
        }
    }

    private static HashSet<string>? ReadEnabledRuntimeGroupIds(SkinModDescriptor mod)
    {
        if (mod.RootPath == null || !Directory.Exists(mod.RootPath))
        {
            return null;
        }

        foreach (var configPath in Directory.EnumerateFiles(mod.RootPath, "*_config.cfg", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                var replacements = document.RootElement.EnumerateObject()
                    .FirstOrDefault(property =>
                        property.Name.Equals("template_replacements", StringComparison.OrdinalIgnoreCase));
                if (replacements.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                return replacements.Value.EnumerateObject()
                    .Where(property => property.Value.ValueKind == JsonValueKind.True)
                    .Select(property => property.Name.ToLowerInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"无法读取运行时皮肤配置 {configPath}: {exception.Message}");
            }
        }

        return null;
    }

    private void AddImageRuntimeProviderOptions(IEnumerable<SkinModDescriptor> mods)
    {
        foreach (var mod in mods.Where(mod => mod.HasDll && !mod.AffectsGameplay && mod.RootPath != null))
        {
            var imageDirectory = System.IO.Path.Combine(mod.RootPath!, "images");
            if (!Directory.Exists(imageDirectory))
            {
                continue;
            }

            var images = Directory.EnumerateFiles(imageDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => System.IO.Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                .Select(path => (Path: path, Id: System.IO.Path.GetFileNameWithoutExtension(path)!))
                .Where(image => KnownAncientIds.Contains(image.Id))
                .DistinctBy(image => image.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var image in images)
            {
                AddRuntimeProviderOption(image.Id, mod.Id, mod.Name, image.Path);
            }
        }
    }

    private void AddRuntimeProviderOption(
        string groupId,
        string optionId,
        string optionName,
        string runtimeImagePath)
    {
        var group = _groups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            group = new SkinGroup(groupId, DisplayName(groupId));
            _groups.Add(group);
        }

        if (group.Options.Any(option => option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        group.Options.Add(new SkinOption(
            optionId,
            optionName,
            new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase),
            IsRuntimeProvider: true,
            RuntimeImagePath: runtimeImagePath));
    }

    private static RuntimeProviderAsset? TryGetRuntimeProviderAsset(string sourcePath)
    {
        var canonicalPath = sourcePath.StartsWith("res://custom/", StringComparison.OrdinalIgnoreCase)
            ? "res://" + sourcePath[13..]
            : sourcePath;
        var identity = TryGetRuntimeProviderGroup(sourcePath) ??
                       TryGetCharacterSelectIconGroup(canonicalPath) ??
                       TryGetCharacterUiTextureGroup(canonicalPath) ??
                       TryGetCharacterMapMarkerGroup(canonicalPath) ??
                       TryGetCharacterIconSceneGroup(canonicalPath) ??
                       TryGetCharacterSupplementGroup(canonicalPath);
        return identity == null ? null : new RuntimeProviderAsset(identity, canonicalPath);
    }

    private static GroupIdentity? TryGetCharacterSupplementGroup(string sourcePath)
    {
        foreach (var regex in new[] { CharacterIconTemplateRegex(), MultiplayerHandRegex() })
        {
            var match = regex.Match(sourcePath);
            if (match.Success)
            {
                var id = match.Groups[1].Value.ToLowerInvariant();
                return new GroupIdentity(id, DisplayName(id));
            }
        }

        return null;
    }

    private static string MapAssetFilePath(string targetSourcePath, string assetSourcePath, string filePath)
    {
        if (targetSourcePath.Equals(assetSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return filePath;
        }

        if (filePath.Equals(assetSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return targetSourcePath;
        }

        foreach (var suffix in new[] { ".import", ".remap" })
        {
            if (filePath.Equals(assetSourcePath + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return targetSourcePath + suffix;
            }
        }

        return filePath;
    }

    private void SortGroupsAndOptions()
    {
        foreach (var group in _groups)
        {
            group.Options.Sort((left, right) =>
                string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
        }

        _groups.Sort((left, right) =>
        {
            var order = GroupSortOrder(left.Id).CompareTo(GroupSortOrder(right.Id));
            return order != 0
                ? order
                : string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        });
    }

    private static void MergeCharacterSelectIconPacks(
        IReadOnlyCollection<PckResourceIndex> indexes,
        IReadOnlyDictionary<string, SkinGroup> groups)
    {
        foreach (var index in indexes)
        {
            var hasPrimaryAppearance = index.Assets.Keys.Any(path => TryGetPrimaryGroup(path) != null);
            if (hasPrimaryAppearance)
            {
                continue;
            }

            var iconGroups = index.Assets.Values
                .Select(asset => (Asset: asset, Group: TryGetCharacterSelectIconGroup(asset.SourcePath)))
                .Where(pair => pair.Group != null)
                .GroupBy(pair => pair.Group!.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var iconGroup in iconGroups)
            {
                if (!groups.TryGetValue(iconGroup.Key, out var group))
                {
                    continue;
                }

                var iconAssets = iconGroup.ToDictionary(
                    pair => pair.Asset.SourcePath,
                    pair => pair.Asset,
                    StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < group.Options.Count; i++)
                {
                    var option = group.Options[i];
                    var mergedAssets = new Dictionary<string, ResourceAsset>(option.Assets, StringComparer.OrdinalIgnoreCase);
                    foreach (var iconAsset in iconAssets)
                    {
                        mergedAssets.TryAdd(iconAsset.Key, iconAsset.Value);
                    }

                    group.Options[i] = option with { Assets = mergedAssets };
                }
            }
        }
    }

    private static GroupIdentity? TryGetPrimaryGroup(string sourcePath)
    {
        var character = CharacterPathRegex().Match(sourcePath);
        if (character.Success)
        {
            var id = character.Groups[1].Value.ToLowerInvariant();
            return new GroupIdentity(id, DisplayName(id));
        }

        var monster = MonsterPathRegex().Match(sourcePath);
        if (monster.Success)
        {
            var id = monster.Groups[1].Value.ToLowerInvariant();
            return new GroupIdentity(id, DisplayName(id));
        }

        foreach (var sceneRegex in new[]
                 {
                     CreatureVisualSceneRegex(),
                     CharacterSelectSceneRegex(),
                     MerchantCharacterSceneRegex(),
                     RestSiteCharacterSceneRegex()
                 })
        {
            var scene = sceneRegex.Match(sourcePath);
            if (!scene.Success)
            {
                continue;
            }

            var id = scene.Groups[1].Value.ToLowerInvariant();
            return new GroupIdentity(id, DisplayName(id));
        }

        var ancientScene = AncientBackgroundSceneRegex().Match(sourcePath);
        if (ancientScene.Success)
        {
            var id = ancientScene.Groups[1].Value.ToLowerInvariant();
            return new GroupIdentity(id, DisplayName(id));
        }

        var ancientAnimation = AncientBackgroundAnimationRegex().Match(sourcePath);
        if (ancientAnimation.Success)
        {
            var id = ancientAnimation.Groups[1].Value.ToLowerInvariant();
            if (id.EndsWith("_room", StringComparison.OrdinalIgnoreCase))
            {
                id = id[..^5];
            }

            if (KnownAncientIds.Contains(id))
            {
                return new GroupIdentity(id, DisplayName(id));
            }
        }

        // 商人 NPC 不纳入本 Mod 的管理范围（无切换界面，其呈现依赖提供者
        // 自身的代码补丁）。不识别 merchant 分组后，纯商人 Mod 不会被当作
        // 皮肤提供者，走游戏原加载器，表现与未安装本 Mod 时一致。
        return null;
    }

    private static GroupIdentity? TryGetCharacterSelectIconGroup(string sourcePath)
    {
        var icon = CharacterSelectIconRegex().Match(sourcePath);
        if (!icon.Success)
        {
            return null;
        }

        var id = icon.Groups[1].Value.ToLowerInvariant();
        return new GroupIdentity(id, DisplayName(id));
    }

    private static GroupIdentity? TryGetCharacterUiTextureGroup(string sourcePath)
    {
        var match = CharacterUiTextureRegex().Match(sourcePath);
        if (!match.Success)
        {
            return null;
        }

        var id = match.Groups[1].Value.ToLowerInvariant();
        return new GroupIdentity(id, DisplayName(id));
    }

    private static GroupIdentity? TryGetCharacterIconSceneGroup(string sourcePath)
    {
        var match = CharacterIconSceneRegex().Match(sourcePath);
        if (!match.Success)
        {
            return null;
        }

        var id = match.Groups[1].Value.ToLowerInvariant();
        return new GroupIdentity(id, DisplayName(id));
    }

    private static GroupIdentity? TryGetCharacterMapMarkerGroup(string sourcePath)
    {
        var match = CharacterMapMarkerRegex().Match(sourcePath);
        if (!match.Success)
        {
            return null;
        }

        var id = match.Groups[1].Value.ToLowerInvariant();
        return new GroupIdentity(id, DisplayName(id));
    }

    private static GroupIdentity? TryGetAncientIconGroup(string sourcePath)
    {
        foreach (var regex in new[] { AncientMapIconRegex(), AncientRunHistoryIconRegex() })
        {
            var match = regex.Match(sourcePath);
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups[1].Value.ToLowerInvariant();
            if (KnownAncientIds.Contains(id))
            {
                return new GroupIdentity(id, DisplayName(id));
            }
        }

        return null;
    }

    private static GroupIdentity? TryGetRuntimeProviderGroup(string sourcePath)
    {
        foreach (var regex in new[]
                 {
                     RuntimeCharacterSelectSceneRegex(),
                     RuntimeCharacterSelectIconRegex(),
                     RuntimeCreatureTemplateRegex(),
                     RuntimeMerchantTemplateRegex(),
                     RuntimeRestSiteTemplateRegex(),
                     RuntimeCharacterIconTemplateRegex()
                 })
        {
            var match = regex.Match(sourcePath);
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups[1].Value.ToLowerInvariant();
            return new GroupIdentity(id, DisplayName(id));
        }

        return null;
    }

    private static bool ContainsGroupToken(string path, string groupId)
    {
        return Regex.IsMatch(path, $"(?:^|[/_.-]){Regex.Escape(groupId)}(?:[/_.-]|$)", RegexOptions.IgnoreCase);
    }

    private static bool IsAnimationRemap(string path)
    {
        return path.StartsWith("res://animations/", StringComparison.OrdinalIgnoreCase) &&
               (path.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase));
    }

    private static ResourceFile? FindDirectFile(ResourceAsset? asset, string sourcePath) =>
        asset?.Files.FirstOrDefault(file =>
            NormalizeTakeoverPath(file.Path).Equals(sourcePath, StringComparison.OrdinalIgnoreCase) ||
            NormalizeTakeoverPath(file.Path).Equals(asset.SourcePath, StringComparison.OrdinalIgnoreCase));

    private static ResourceFile? FindRemapFile(ResourceAsset? asset, string sourcePath) =>
        asset?.Files.FirstOrDefault(file =>
            NormalizeTakeoverPath(file.Path).Equals(sourcePath + ".import", StringComparison.OrdinalIgnoreCase) ||
            NormalizeTakeoverPath(file.Path).Equals(sourcePath + ".remap", StringComparison.OrdinalIgnoreCase) ||
            NormalizeTakeoverPath(file.Path).Equals(asset.SourcePath + ".import", StringComparison.OrdinalIgnoreCase) ||
            NormalizeTakeoverPath(file.Path).Equals(asset.SourcePath + ".remap", StringComparison.OrdinalIgnoreCase));

    private static ResourceFile[] GetImportedPayloadFiles(ResourceAsset asset, string sourcePath) =>
        asset.Files.Where(file =>
                !file.Path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase) &&
                !file.Path.Equals(sourcePath + ".import", StringComparison.OrdinalIgnoreCase) &&
                !file.Path.Equals(sourcePath + ".remap", StringComparison.OrdinalIgnoreCase) &&
                IsImportedPayloadPath(file.Path))
            .ToArray();

    private static bool IsImportedPayloadPath(string path) =>
        path.EndsWith(".spatlas", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".spskel", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ctex", StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeTakeoverPath(string path)
    {
        var match = WaifuAssetsPathRegex().Match(path);
        return match.Success ? "res://" + match.Groups[1].Value : path;
    }

    private static ResourceFile? MatchImportedFile(string targetPath, IReadOnlyList<ResourceFile> files)
    {
        var exact = files.FirstOrDefault(file => file.Path.Equals(targetPath, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        var suffix = targetPath.EndsWith(".spatlas", StringComparison.OrdinalIgnoreCase) ? ".spatlas"
            : targetPath.EndsWith(".spskel", StringComparison.OrdinalIgnoreCase) ? ".spskel"
            : targetPath.EndsWith(".ctex", StringComparison.OrdinalIgnoreCase) ? ".ctex"
            : System.IO.Path.GetExtension(targetPath);
        var matches = files.Where(file => file.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static byte[] RewriteTextResource(
        byte[] bytes,
        IReadOnlyDictionary<string, string> replacements,
        IReadOnlyDictionary<string, string>? extraReplacements,
        bool stripUids = true)
    {
        var text = Encoding.UTF8.GetString(bytes);
        // 按 key 长度降序替换，避免短 key 是长 key 前缀时破坏后者；
        // 同时要求 key 后紧跟路径终止符，防止 ".gd" 误伤 ".gdc" 这类前缀引用。
        foreach (var replacement in replacements
                     .Concat(extraReplacements ?? new Dictionary<string, string>())
                     .OrderByDescending(pair => pair.Key.Length))
        {
            text = Regex.Replace(
                text,
                Regex.Escape(replacement.Key) + "(?=[\\x00\\\"'\\s,\\]\\[]|$)",
                replacement.Value.Replace("$", "$$"),
                RegexOptions.IgnoreCase);
        }

        if (stripUids)
        {
            text = UidLineRegex().Replace(text, string.Empty);
            text = UidAttributeRegex().Replace(text, string.Empty);
        }

        return Encoding.UTF8.GetBytes(text);
    }

    private static int GroupSortOrder(string id) => id switch
    {
        "ironclad" => 0,
        "silent" => 1,
        "regent" => 2,
        "necrobinder" => 3,
        "defect" => 4,
        "watcher" => 5,
        "colorless" => 6,
        "ancients" => 7,
        "misc" => 8,
        "neow" => 9,
        "merchant" => 10,
        _ => 100
    };

    private static string DisplayName(string id) => id switch
    {
        "ironclad" => "铁甲战士",
        "silent" => "静默猎手",
        "regent" => "储君",
        "necrobinder" => "亡灵契约师",
        "defect" => "故障机器人",
        "watcher" => "观者",
        "colorless" => "无色",
        "ancients" => "远古",
        "misc" => "其他",
        "neow" => "涅奥",
        "merchant" => "商人",
        _ => id.Replace('_', ' ').Trim().CapitalizeWords()
    };

    internal static readonly HashSet<string> KnownAncientIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "darv",
        "neow",
        "nonupeipe",
        "orobas",
        "pael",
        "tanx",
        "tezcatara",
        "vakuu"
    };

    [GeneratedRegex("^res://animations/(?:characters|character_select|merchant|rest_site)/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterPathRegex();

    [GeneratedRegex("(?:^|/)card_portraits/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex CardPortraitGroupRegex();

    [GeneratedRegex("/(?:card_portraits|card_atlas\\.sprites|cards?|card_art|cardart)/", RegexOptions.IgnoreCase)]
    private static partial Regex CardArtPathRegex();

    [GeneratedRegex("/(?:card_portraits|card_atlas\\.sprites|cards?|card_art|cardart)/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex CardArtIdentityRegex();

    [GeneratedRegex("^res://animations/monsters/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex MonsterPathRegex();

    [GeneratedRegex("^res://scenes/creature_visuals/([^/.]+)\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex CreatureVisualSceneRegex();

    [GeneratedRegex("^res://scenes/screens/char_select/char_select_bg_([^/.]+)\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterSelectSceneRegex();

    [GeneratedRegex("^res://scenes/merchant/characters/([^/.]+)_merchant\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex MerchantCharacterSceneRegex();

    [GeneratedRegex("^res://scenes/rest_site/characters/([^/.]+)_rest_site\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex RestSiteCharacterSceneRegex();

    [GeneratedRegex("^res://scenes/events/background_scenes/([^/.]+)\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex AncientBackgroundSceneRegex();

    [GeneratedRegex("^res://animations/backgrounds/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex AncientBackgroundAnimationRegex();

    [GeneratedRegex("^res://images/packed/character_select/char_select_([^/.]+?)(?:_locked)?\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterSelectIconRegex();

    [GeneratedRegex("^res://images/ui/top_panel/character_icon_([^/.]+?)(?:_outline)?\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterUiTextureRegex();

    [GeneratedRegex("^res://scenes/ui/character_icons/([^/.]+?)_icon\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterIconSceneRegex();

    [GeneratedRegex("^res://images/packed/map/icons/map_marker_([^/.]+)\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterMapMarkerRegex();

    [GeneratedRegex("^res://images/packed/map/ancients/ancient_node_([^/.]+?)(?:_outline)?\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex AncientMapIconRegex();

    [GeneratedRegex("^res://images/ui/run_history/([^/.]+?)(?:_outline)?\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex AncientRunHistoryIconRegex();

    [GeneratedRegex("^res://custom/scenes/screens/char_select/char_select_bg_([^/.]+)\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeCharacterSelectSceneRegex();

    [GeneratedRegex("^res://custom/images/packed/character_select/char_select_([^/.]+?)(?:_locked)?\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeCharacterSelectIconRegex();

    [GeneratedRegex("^res://scenes/creature_visuals/templates/([^/.]+?)_template\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeCreatureTemplateRegex();

    [GeneratedRegex("^res://scenes/merchant/characters/templates/([^/.]+?)_merchant_template\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeMerchantTemplateRegex();

    [GeneratedRegex("^res://scenes/rest_site/characters/templates/([^/.]+?)_rest_site_template\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeRestSiteTemplateRegex();

    [GeneratedRegex("^res://custom/scenes/ui/character_icons/([^/.]+?)_icon\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeCharacterIconTemplateRegex();

    [GeneratedRegex("^res://scenes/ui/character_icons/templates/([^/.]+?)_icon\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterIconTemplateRegex();

    [GeneratedRegex("^res://images/ui/hands/multiplayer_hand_([^/.]+?)_(?:paper|point|rock|scissors)\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex MultiplayerHandRegex();

    [GeneratedRegex("^res://waifu_assets/[^/]+/(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WaifuAssetsPathRegex();

    [GeneratedRegex("\"(res://[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ResourcePathRegex();

    [GeneratedRegex(
        "res://[^\\x00\\\"'\\r\\n\\t \\]\\[(){}<>]+?\\.(?:spatlas|spskel|ctex|tscn|tres|gdc|gd|gdshader|scn|res|png|webp|jpe?g|svg|skel|atlas|json|ogg|wav|mp3)(?=[\\x00\\\"'\\r\\n\\t \\]\\[(){}<>]|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedResourcePathRegex();

    [GeneratedRegex("^uid=\"uid://[^\"]+\"\\r?\\n", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex UidLineRegex();

    [GeneratedRegex("\\s+uid=\"uid://[^\"]+\"", RegexOptions.IgnoreCase)]
    private static partial Regex UidAttributeRegex();

    private sealed record GroupIdentity(string Id, string DisplayName);
    private sealed record RuntimeProviderAsset(GroupIdentity Identity, string CanonicalPath);
    private sealed record CardArtIdentity(string Category, string Stem);
    private sealed record CardArtVariant(string Key, ResourceAsset[] Assets, HashSet<string> Stems);
    private sealed record RuntimeResource(
        string SourcePath,
        ResourceFile? DirectFile,
        ResourceFile? RemapFile,
        IReadOnlyList<ResourceFile> PayloadFiles);
}

internal sealed record SkinModDescriptor(
    string Id,
    string Name,
    string? PckPath,
    bool AffectsGameplay,
    string? RootPath = null,
    bool HasDll = false);

internal sealed record SkinProviderProbe(
    string Id,
    string? RootPath,
    int VisualGroupCount,
    int CardAssetCount,
    int RuntimeImageCount);

internal sealed class SkinGroup(string id, string displayName)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public List<SkinOption> Options { get; } = [];
}

internal sealed record SkinOption(
    string Id,
    string Name,
    IReadOnlyDictionary<string, ResourceAsset> Assets,
    bool IsRuntimeProvider = false,
    string? RuntimeImagePath = null);

internal sealed class CardSkinGroup(string id, string displayName)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public List<CardSkinOption> Options { get; } = [];
}

internal sealed record CardSkinOption(
    string Id,
    string Name,
    IReadOnlyDictionary<string, string> NormalPortraits,
    IReadOnlyDictionary<string, AncientCardPortrait> AncientPortraits,
    IReadOnlyDictionary<string, ResourceAsset>? PckAssets = null,
    string? ProviderRootPath = null,
    string? ProviderId = null)
{
    public IReadOnlyDictionary<string, ResourceAsset> Assets { get; init; } =
        PckAssets ?? new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase);

    public CardSkinOption Merge(CardSkinOption other)
    {
        var normal = new Dictionary<string, string>(NormalPortraits, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in other.NormalPortraits)
        {
            normal[pair.Key] = pair.Value;
        }

        var ancient = new Dictionary<string, AncientCardPortrait>(AncientPortraits, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in other.AncientPortraits)
        {
            ancient[pair.Key] = pair.Value;
        }

        var assets = new Dictionary<string, ResourceAsset>(Assets, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in other.Assets)
        {
            assets[pair.Key] = pair.Value;
        }

        return this with
        {
            NormalPortraits = normal,
            AncientPortraits = ancient,
            Assets = assets,
            ProviderRootPath = ProviderRootPath ?? other.ProviderRootPath,
            ProviderId = ProviderId ?? other.ProviderId
        };
    }

    public string? GetPortraitPath(string cardType, bool useAncientStyle)
    {
        if (AncientPortraits.TryGetValue(cardType, out var ancient))
        {
            var path = useAncientStyle ? ancient.AncientPortrait : ancient.NormalPortrait;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return NormalPortraits.GetValueOrDefault(cardType);
    }
}

internal sealed record CardCatalogEntry(
    string TypeName,
    string PortraitPath,
    string PoolGroupId,
    string CatalogGroupId,
    string FilterGroupId);

internal sealed record AncientCardPortrait(string? NormalPortrait, string? AncientPortrait);

internal sealed class CardReplacementConfig
{
    public List<NormalCardReplacement> NormalReplacements { get; set; } = [];
    public List<AncientCardReplacement> AncientReplacements { get; set; } = [];
}

internal sealed class NormalCardReplacement
{
    public string CardType { get; set; } = string.Empty;
    public string PortraitPath { get; set; } = string.Empty;
}

internal sealed class AncientCardReplacement
{
    public string CardType { get; set; } = string.Empty;
    public string? NormalPortrait { get; set; }
    public string? AncientPortrait { get; set; }
    public string? ConfigKey { get; set; }
    public string? PathForGrouping =>
        !string.IsNullOrWhiteSpace(AncientPortrait) ? AncientPortrait : NormalPortrait;
}

internal sealed record RuntimeResourceOverlay(
    IReadOnlyDictionary<string, string> ResourcePaths,
    IReadOnlyDictionary<string, byte[]> Files,
    IReadOnlyDictionary<string, string> SourceAliases,
    IReadOnlyDictionary<string, string> PayloadAliases);

internal sealed class ResourceAsset(string sourcePath)
{
    public string SourcePath { get; } = sourcePath;
    public List<ResourceFile> Files { get; } = [];

    public void AddFile(PckArchive archive, string path)
    {
        if (Files.All(file => !file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            Files.Add(new ResourceFile(archive, path));
        }
    }
}

internal sealed record ResourceFile(PckArchive Archive, string Path);

internal sealed partial class PckResourceIndex : IDisposable
{
    private readonly Dictionary<string, string> _importedToSource;

    private PckResourceIndex(SkinModDescriptor mod, PckArchive archive, Dictionary<string, string> importedToSource)
    {
        Mod = mod;
        Archive = archive;
        _importedToSource = importedToSource;
    }

    public SkinModDescriptor Mod { get; }
    public PckArchive Archive { get; }
    public Dictionary<string, ResourceAsset> Assets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static PckResourceIndex Build(
        SkinModDescriptor mod,
        PckArchive archive,
        Dictionary<string, string> importedToSource,
        Func<string, bool>? remapFilter)
    {
        var index = new PckResourceIndex(mod, archive, importedToSource);
        foreach (var remapPath in archive.Paths.Where(IsRemapPath))
        {
            if (remapFilter != null && !remapFilter(remapPath))
            {
                continue;
            }

            index.AddRemap(remapPath);
        }

        foreach (var path in archive.Paths.Where(IsDirectAnimationResource))
        {
            var sourcePath = SkinCatalog.NormalizeTakeoverPath(path);
            index.GetAsset(sourcePath).AddFile(archive, path);
        }

        foreach (var importedPath in archive.Paths.Where(IsImportedPath))
        {
            if (importedToSource.TryGetValue(importedPath, out var sourcePath))
            {
                index.GetAsset(sourcePath).AddFile(archive, importedPath);
            }
        }

        return index;
    }

    public ResourceAsset? TryBuildAsset(string sourcePath)
    {
        if (Assets.TryGetValue(sourcePath, out var existing))
        {
            return existing;
        }

        foreach (var suffix in new[] { ".import", ".remap" })
        {
            var remapPath = sourcePath + suffix;
            if (Archive.Contains(remapPath))
            {
                AddRemap(remapPath);
                return Assets.GetValueOrDefault(sourcePath);
            }
        }

        if (Archive.Contains(sourcePath))
        {
            var asset = GetAsset(sourcePath);
            asset.AddFile(Archive, sourcePath);
            return asset;
        }

        return null;
    }

    public void Dispose() => Archive.Dispose();

    private void AddRemap(string remapPath)
    {
        var sourcePath = remapPath.EndsWith(".import", StringComparison.OrdinalIgnoreCase)
            ? remapPath[..^7]
            : remapPath[..^6];
        sourcePath = SkinCatalog.NormalizeTakeoverPath(sourcePath);
        var asset = GetAsset(sourcePath);
        asset.AddFile(Archive, remapPath);

        var text = Encoding.UTF8.GetString(Archive.ReadFile(remapPath));
        foreach (Match match in RemapTargetRegex().Matches(text))
        {
            var targetPath = match.Groups[1].Value;
            _importedToSource[targetPath] = sourcePath;
            if (Archive.Contains(targetPath))
            {
                asset.AddFile(Archive, targetPath);
            }
        }

        // Godot 4 .import 的 [deps] dest_files 数组可能包含多个导入产物（音频/字体/3D 等），
        // 逐一登记，避免这些 payload 在覆盖包生成时丢失。
        var destFiles = DestFilesArrayRegex().Match(text);
        if (destFiles.Success)
        {
            foreach (Match pathMatch in QuotedPathRegex().Matches(destFiles.Value))
            {
                var targetPath = pathMatch.Groups[1].Value;
                _importedToSource[targetPath] = sourcePath;
                if (Archive.Contains(targetPath))
                {
                    asset.AddFile(Archive, targetPath);
                }
            }
        }
    }

    private ResourceAsset GetAsset(string sourcePath)
    {
        if (!Assets.TryGetValue(sourcePath, out var asset))
        {
            asset = new ResourceAsset(sourcePath);
            Assets.Add(sourcePath, asset);
        }

        return asset;
    }

    private static bool IsRemapPath(string path) =>
        path.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase);

    private static bool IsImportedPath(string path) =>
        path.StartsWith("res://.godot/imported/", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectAnimationResource(string path) =>
        (SkinCatalog.NormalizeTakeoverPath(path).StartsWith("res://animations/", StringComparison.OrdinalIgnoreCase) ||
         SkinCatalog.NormalizeTakeoverPath(path).StartsWith("res://backgrounds/", StringComparison.OrdinalIgnoreCase) ||
         SkinCatalog.NormalizeTakeoverPath(path).StartsWith("res://scenes/", StringComparison.OrdinalIgnoreCase)) &&
        (path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("path(?:\\.[a-z0-9_]+)?\\s*=\\s*\"(res://[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex RemapTargetRegex();

    [GeneratedRegex("dest_files\\s*=\\s*\\[[^\\]]*\\]", RegexOptions.IgnoreCase)]
    private static partial Regex DestFilesArrayRegex();

    [GeneratedRegex("\"(res://[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedPathRegex();
}

internal static class DisplayTextExtensions
{
    public static string CapitalizeWords(this string value)
    {
        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
