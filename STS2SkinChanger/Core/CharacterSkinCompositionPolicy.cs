namespace STS2SkinChanger.Core;

internal sealed class CharacterSkinComposition
{
    public string Id { get; set; } = string.Empty;

    public string GroupId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<string> SourceOptionIds { get; set; } = [];

    public bool HideSources { get; set; }
}

internal sealed record CharacterSkinCompositionSource<T>(
    string OptionId,
    bool HasDynamicBehavior,
    IReadOnlyDictionary<string, T> Assets);

internal sealed record ResolvedCharacterSkinComposition<T>(
    IReadOnlyList<string> SourceOptionIds,
    IReadOnlyDictionary<string, T> Assets,
    string? DynamicSourceId);

internal static class CharacterSkinCompositionPolicy
{
    public const string IdPrefix = "composition:";
    public const int MaxNameLength = 40;

    public static string CreateId() => IdPrefix + Guid.NewGuid().ToString("N");

    public static List<CharacterSkinComposition> Normalize(
        IEnumerable<CharacterSkinComposition>? compositions)
    {
        var normalized = new List<CharacterSkinComposition>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in compositions ?? [])
        {
            if (source == null || string.IsNullOrWhiteSpace(source.GroupId))
            {
                continue;
            }

            var id = source.Id?.Trim() ?? string.Empty;
            if (!id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase) ||
                !ids.Add(id))
            {
                do
                {
                    id = CreateId();
                }
                while (!ids.Add(id));
            }

            normalized.Add(new CharacterSkinComposition
            {
                Id = id,
                GroupId = source.GroupId.Trim(),
                Name = TrimName(source.Name),
                SourceOptionIds = NormalizeOptionIds(source.SourceOptionIds),
                HideSources = source.HideSources
            });
        }

        return normalized;
    }

    public static string UniqueName(
        string? requestedName,
        IEnumerable<string> existingNames,
        string defaultBaseName)
    {
        var existing = existingNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(TrimName)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var requested = TrimName(requestedName);
        var defaultName = TrimName(defaultBaseName);
        if (string.IsNullOrWhiteSpace(defaultName))
        {
            defaultName = "Combined Skin";
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            return FirstAvailableNumberedName(defaultName, 1, existing);
        }

        if (!existing.Contains(requested))
        {
            return requested;
        }

        return FirstAvailableNumberedName(requested, 2, existing);
    }

    public static IReadOnlyList<string> VisibleRawOptionIds(
        string groupId,
        IEnumerable<string> rawOptionIds,
        IEnumerable<CharacterSkinComposition>? compositions)
    {
        var hidden = (compositions ?? [])
            .Where(composition => composition != null &&
                                  composition.HideSources &&
                                  composition.GroupId.Equals(
                                      groupId,
                                      StringComparison.OrdinalIgnoreCase))
            .SelectMany(composition => composition.SourceOptionIds ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NormalizeOptionIds(rawOptionIds)
            .Where(optionId => !hidden.Contains(optionId))
            .ToArray();
    }

    public static IReadOnlyList<string> ResolveAvailableSourceIds(
        IEnumerable<string>? sourceOptionIds,
        IEnumerable<string> availableOptionIds)
    {
        var available = availableOptionIds
            .Where(optionId => !string.IsNullOrWhiteSpace(optionId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NormalizeOptionIds(sourceOptionIds)
            .Where(available.Contains)
            .ToArray();
    }

    public static ResolvedCharacterSkinComposition<T> ResolveAssets<T>(
        IEnumerable<string>? sourceOptionIds,
        IReadOnlyDictionary<string, CharacterSkinCompositionSource<T>> availableSources,
        Func<string, string> canonicalizeTarget)
    {
        ArgumentNullException.ThrowIfNull(availableSources);
        ArgumentNullException.ThrowIfNull(canonicalizeTarget);

        var resolvedIds = ResolveAvailableSourceIds(sourceOptionIds, availableSources.Keys);
        var assets = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        string? dynamicSourceId = null;
        foreach (var optionId in resolvedIds)
        {
            var source = availableSources[optionId];
            if (dynamicSourceId == null && source.HasDynamicBehavior)
            {
                dynamicSourceId = source.OptionId;
            }

            foreach (var asset in source.Assets)
            {
                var target = canonicalizeTarget(asset.Key);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    assets.TryAdd(target, asset.Value);
                }
            }
        }

        return new ResolvedCharacterSkinComposition<T>(
            resolvedIds,
            assets,
            dynamicSourceId);
    }

    private static List<string> NormalizeOptionIds(IEnumerable<string>? optionIds) =>
        (optionIds ?? [])
        .Where(optionId => !string.IsNullOrWhiteSpace(optionId))
        .Select(optionId => optionId.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string TrimName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length <= MaxNameLength
            ? trimmed
            : trimmed[..MaxNameLength].TrimEnd();
    }

    private static string FirstAvailableNumberedName(
        string baseName,
        int firstNumber,
        IReadOnlySet<string> existing)
    {
        for (var number = firstNumber; number < int.MaxValue; number++)
        {
            var suffix = " " + number;
            var prefixLength = Math.Max(0, MaxNameLength - suffix.Length);
            var prefix = baseName.Length <= prefixLength
                ? baseName
                : baseName[..prefixLength].TrimEnd();
            var candidate = prefix + suffix;
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("无法为合并皮肤生成唯一名称。");
    }
}
