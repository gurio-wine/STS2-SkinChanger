using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using STS2SkinChanger.Pck;

namespace STS2SkinChanger.Catalog;

internal sealed partial class SkinCatalog : IDisposable
{
    public const string BaseOptionId = "__base__";

    private readonly PckArchive _gameArchive;
    private readonly List<PckResourceIndex> _baselineIndexes;
    private readonly List<PckResourceIndex> _cosmeticIndexes;
    private readonly List<SkinGroup> _groups;

    private SkinCatalog(
        PckArchive gameArchive,
        List<PckResourceIndex> baselineIndexes,
        List<PckResourceIndex> cosmeticIndexes,
        IReadOnlyList<SkinGroup> groups)
    {
        _gameArchive = gameArchive;
        _baselineIndexes = baselineIndexes;
        _cosmeticIndexes = cosmeticIndexes;
        _groups = groups.ToList();
    }

    public IReadOnlyList<SkinGroup> Groups => _groups;

    public static SkinCatalog Build(string gamePckPath, IEnumerable<SkinModDescriptor> mods)
    {
        var modList = mods.ToArray();
        var gameArchive = PckArchive.Open(gamePckPath);
        var baselineIndexes = new List<PckResourceIndex>();
        var cosmeticIndexes = new List<PckResourceIndex>();
        try
        {
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

            var groups = BuildGroups(cosmeticIndexes);
            var catalog = new SkinCatalog(gameArchive, baselineIndexes, cosmeticIndexes, groups);
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

    public bool IsRuntimeProviderOption(string groupId, string optionId)
    {
        return Groups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(option => option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))?
            .IsRuntimeProvider == true;
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
            var selected = group.Options.FirstOrDefault(option => option.Id == selectedId);
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
                    files[file.Path] = file;
                    var takeoverPath = NormalizeTakeoverPath(file.Path);
                    if (!takeoverPath.Equals(file.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        files[takeoverPath] = file;
                    }
                }
            }
        }

        return files;
    }

    public IReadOnlySet<string> GetAffectedSourcePaths(string groupId)
    {
        var group = Groups.First(group => group.Id == groupId);
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
        string aliasToken)
    {
        var group = Groups.First(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        var selected = group.Options.FirstOrDefault(option => option.Id == selectionId);
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

        return new RuntimeResourceOverlay(aliasedResourcePaths, files);
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

    private static void AddPckRuntimeProviderOptions(
        IReadOnlyCollection<PckResourceIndex> indexes,
        IDictionary<string, SkinGroup> groups)
    {
        foreach (var index in indexes)
        {
            var enabledGroupIds = ReadEnabledRuntimeGroupIds(index.Mod);
            var identities = index.Assets.Keys
                .Select(TryGetRuntimeProviderGroup)
                .Where(identity => identity != null)
                .Cast<GroupIdentity>()
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

                if (group.Options.Any(option => option.Id.Equals(index.Mod.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                group.Options.Add(new SkinOption(
                    index.Mod.Id,
                    index.Mod.Name,
                    new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase),
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

            var imageIds = Directory.EnumerateFiles(imageDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => System.IO.Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                .Select(path => System.IO.Path.GetFileNameWithoutExtension(path)!)
                .Where(id => KnownAncientIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var imageId in imageIds)
            {
                AddRuntimeProviderOption(imageId, mod.Id, mod.Name);
            }
        }
    }

    private void AddRuntimeProviderOption(string groupId, string optionId, string optionName)
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
            IsRuntimeProvider: true));
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

        if (sourcePath.StartsWith("res://animations/backgrounds/merchant_room/", StringComparison.OrdinalIgnoreCase) ||
            sourcePath.StartsWith("res://animations/backgrounds/fake_merchant_room/", StringComparison.OrdinalIgnoreCase))
        {
            return new GroupIdentity("merchant", "商人");
        }

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
            NormalizeTakeoverPath(file.Path).Equals(sourcePath, StringComparison.OrdinalIgnoreCase));

    private static ResourceFile? FindRemapFile(ResourceAsset? asset, string sourcePath) =>
        asset?.Files.FirstOrDefault(file =>
            NormalizeTakeoverPath(file.Path).Equals(sourcePath + ".import", StringComparison.OrdinalIgnoreCase) ||
            NormalizeTakeoverPath(file.Path).Equals(sourcePath + ".remap", StringComparison.OrdinalIgnoreCase));

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
        foreach (var replacement in replacements.Concat(extraReplacements ?? new Dictionary<string, string>()))
        {
            text = text.Replace(replacement.Key, replacement.Value, StringComparison.OrdinalIgnoreCase);
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
        "neow" => 6,
        "merchant" => 7,
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

    [GeneratedRegex("^res://waifu_assets/[^/]+/(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WaifuAssetsPathRegex();

    [GeneratedRegex("\"(res://[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ResourcePathRegex();

    [GeneratedRegex("^uid=\"uid://[^\"]+\"\\r?\\n", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex UidLineRegex();

    [GeneratedRegex("\\s+uid=\"uid://[^\"]+\"", RegexOptions.IgnoreCase)]
    private static partial Regex UidAttributeRegex();

    private sealed record GroupIdentity(string Id, string DisplayName);
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
    bool IsRuntimeProvider = false);

internal sealed record RuntimeResourceOverlay(
    IReadOnlyDictionary<string, string> ResourcePaths,
    IReadOnlyDictionary<string, byte[]> Files);

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
}

internal static class DisplayTextExtensions
{
    public static string CapitalizeWords(this string value)
    {
        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
