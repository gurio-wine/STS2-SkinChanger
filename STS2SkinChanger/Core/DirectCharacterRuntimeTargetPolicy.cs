namespace STS2SkinChanger.Core;

internal static class DirectCharacterRuntimeTargetPolicy
{
    public static IReadOnlySet<string> ResolveTargets(
        IEnumerable<(string FieldName, string Value)> declarations,
        IEnumerable<string> knownCharacterGroupIds)
    {
        var knownGroups = knownCharacterGroupIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value, value => value, StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in declarations)
        {
            if (!IsTargetCharacterField(declaration.FieldName) ||
                string.IsNullOrWhiteSpace(declaration.Value) ||
                !knownGroups.TryGetValue(declaration.Value.Trim(), out var knownGroup))
            {
                continue;
            }

            targets.Add(knownGroup);
        }

        return targets;
    }

    internal static bool IsTargetCharacterField(string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        var normalized = new string(fieldName
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized is "characterid" or "characterentry" ||
               normalized.StartsWith("targetcharacter", StringComparison.Ordinal) ||
               normalized.StartsWith("skincharacter", StringComparison.Ordinal);
    }
}
