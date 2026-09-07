using STS2SkinChanger.Core;

namespace STS2SkinChanger.Catalog;

internal sealed partial class SkinCatalog
{
    private readonly Dictionary<string, ulong> _workshopSourceIds;
    internal static bool SupportsWorkshopResourcePath(string path) =>
        System.IO.Path.GetExtension(path.EndsWith(".remap") ? path[..^6] : path).ToLowerInvariant()
            is not (".scn" or ".res" or ".cs" or ".gd" or ".gdc" or ".dll" or ".gdextension");
    internal bool HasCompleteWorkshopResourceCoverage(string providerId)
    {
        var indexes = _cosmeticIndexes.Where(index => index.Mod.Id == providerId).ToArray();
        if (indexes.Length != 1) return false;
        var index = indexes[0];
        var assets = Groups.SelectMany(group => group.Options).Where(option => option.EffectiveProviderId == providerId)
            .SelectMany(option => option.Assets.Values)
            .Concat(CardGroups.SelectMany(group => group.Options).Where(option => (option.ProviderId ?? option.Id) == providerId).SelectMany(option => option.Assets.Values))
            .ToArray();
        var ownedFiles = assets.SelectMany(asset => asset.Files).Where(file => ReferenceEquals(file.Archive, index.Archive))
            .Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Extra scene scripts, unclassified UI/localization/gameplay files, or missing dependencies
        // cannot be waved through merely because one character portrait was recognized.
        var resources = index.Archive.Paths.ToArray();
        bool Available(string dependency) => index.Archive.Contains(dependency) || index.Archive.Contains(dependency + ".import") ||
            index.Archive.Contains(dependency + ".remap") || _baselineIndexes.Any(b => b.Archive.Contains(dependency) ||
                b.Archive.Contains(dependency + ".import") || b.Archive.Contains(dependency + ".remap"));
        return ownedFiles.Count > 0 && resources.All(path => ownedFiles.Contains(path) ||
            path is "res://project.binary" or "res://.godot/uid_cache.bin" or "res://.godot/global_script_class_cache.cfg") &&
            resources.All(SupportsWorkshopResourcePath) &&
            resources.SelectMany(path => EnumerateDependencyPaths(new ResourceFile(index.Archive, path))).All(Available);
    }

    internal string WorkshopKind(string groupId) =>
        groupId is "osty" or "byrdpip" or "paels_legion" ? "companion" :
        IsCharacterAppearanceGroup(groupId) ? "character" :
        KnownAncientIds.Contains(groupId) ? "ancient" :
        EventSkinPolicy.IsEventGroup(groupId) ? "event" :
        groupId is "merchant" or "fake_merchant_monster" ? "merchant" :
        "monster";

    internal WorkshopCatalogItem[] ExportWorkshopCatalog()
    {
        var ids = _workshopSourceIds;
        var targets = new List<(ulong Id, WorkshopTarget Target)>();
        foreach (var group in Groups)
            foreach (var option in group.Options)
                if (ids.TryGetValue(option.EffectiveProviderId, out var id))
                    targets.Add((id, new(WorkshopKind(group.Id), group.Id)));
        foreach (var group in CardGroups)
            foreach (var option in group.Options)
                if (ids.TryGetValue(option.ProviderId ?? option.Id, out var id))
                    targets.Add((id, new("cards", group.Id)));
        return targets.GroupBy(t => t.Id).OrderBy(g => g.Key)
            .Select(g => new WorkshopCatalogItem(g.Key, g.Select(t => t.Target).Distinct().ToArray())).ToArray();
    }

    internal static ulong WorkshopSourceId(string? path)
    {
        var parts = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i++)
            if ((parts[i] == "2868840" || parts[i] == "_workshop_formal_cache") && ulong.TryParse(parts[i + 1], out var id)) return id;
        return 0;
    }
}
