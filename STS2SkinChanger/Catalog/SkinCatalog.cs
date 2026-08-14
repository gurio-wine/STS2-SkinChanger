using System.Text;
using System.Text.RegularExpressions;
using STS2SkinChanger.Pck;

namespace STS2SkinChanger.Catalog;

internal sealed partial class SkinCatalog : IDisposable
{
    public const string BaseOptionId = "__base__";

    private readonly PckArchive _gameArchive;
    private readonly List<PckResourceIndex> _baselineIndexes;
    private readonly List<PckResourceIndex> _cosmeticIndexes;

    private SkinCatalog(
        PckArchive gameArchive,
        List<PckResourceIndex> baselineIndexes,
        List<PckResourceIndex> cosmeticIndexes,
        IReadOnlyList<SkinGroup> groups)
    {
        _gameArchive = gameArchive;
        _baselineIndexes = baselineIndexes;
        _cosmeticIndexes = cosmeticIndexes;
        Groups = groups;
    }

    public IReadOnlyList<SkinGroup> Groups { get; }

    public static SkinCatalog Build(string gamePckPath, IEnumerable<SkinModDescriptor> mods)
    {
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

            foreach (var mod in mods)
            {
                if (!File.Exists(mod.PckPath))
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
            return new SkinCatalog(gameArchive, baselineIndexes, cosmeticIndexes, groups);
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

    public RuntimeSceneOverlay BuildRuntimeSceneOverlay(
        string groupId,
        string selectionId,
        IReadOnlyCollection<string> scenePaths,
        string aliasToken)
    {
        var group = Groups.First(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        var selected = group.Options.FirstOrDefault(option => option.Id == selectionId);
        var sourcePaths = GetAffectedSourcePaths(groupId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        sourcePaths.UnionWith(scenePaths);

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

            var directFile = FindDirectFile(primary, sourcePath) ?? FindDirectFile(baseline, sourcePath);
            var remapFile = FindRemapFile(primary, sourcePath) ?? FindRemapFile(baseline, sourcePath);
            var payloadFiles = GetImportedPayloadFiles(primary, sourcePath);
            if (payloadFiles.Length == 0 && baseline != null)
            {
                payloadFiles = GetImportedPayloadFiles(baseline, sourcePath);
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

        var aliasedScenePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scenePath in scenePaths)
        {
            if (!sourceAliases.TryGetValue(scenePath, out var aliasedScenePath) || !files.ContainsKey(aliasedScenePath))
            {
                throw new InvalidOperationException($"无法为 {scenePath} 创建独立皮肤场景。");
            }

            aliasedScenePaths[scenePath] = aliasedScenePath;
        }

        return new RuntimeSceneOverlay(aliasedScenePaths, files);
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
        var groups = new Dictionary<string, SkinGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in cosmeticIndexes)
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

        foreach (var group in groups.Values)
        {
            group.Options.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
        }

        return groups.Values
            .OrderBy(group => GroupSortOrder(group.Id))
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
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

        if (sourcePath.StartsWith("res://animations/backgrounds/neow_room/", StringComparison.OrdinalIgnoreCase))
        {
            return new GroupIdentity("neow", "涅奥");
        }

        if (sourcePath.StartsWith("res://animations/backgrounds/merchant_room/", StringComparison.OrdinalIgnoreCase) ||
            sourcePath.StartsWith("res://animations/backgrounds/fake_merchant_room/", StringComparison.OrdinalIgnoreCase))
        {
            return new GroupIdentity("merchant", "商人");
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
        asset?.Files.FirstOrDefault(file => file.Path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));

    private static ResourceFile? FindRemapFile(ResourceAsset? asset, string sourcePath) =>
        asset?.Files.FirstOrDefault(file =>
            file.Path.Equals(sourcePath + ".import", StringComparison.OrdinalIgnoreCase) ||
            file.Path.Equals(sourcePath + ".remap", StringComparison.OrdinalIgnoreCase));

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

    [GeneratedRegex("^res://animations/(?:characters|character_select|merchant|rest_site)/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterPathRegex();

    [GeneratedRegex("^res://animations/monsters/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex MonsterPathRegex();

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

internal sealed record SkinModDescriptor(string Id, string Name, string PckPath, bool AffectsGameplay);

internal sealed class SkinGroup(string id, string displayName)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public List<SkinOption> Options { get; } = [];
}

internal sealed record SkinOption(string Id, string Name, IReadOnlyDictionary<string, ResourceAsset> Assets);

internal sealed record RuntimeSceneOverlay(
    IReadOnlyDictionary<string, string> ScenePaths,
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
            index.GetAsset(path).AddFile(archive, path);
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
        (path.StartsWith("res://animations/", StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith("res://scenes/", StringComparison.OrdinalIgnoreCase)) &&
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
