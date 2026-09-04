using System.Security.Cryptography;
using System.Text;

namespace STS2SkinChanger.Catalog;

internal sealed record ProviderInstanceCandidate(
    string ManifestId,
    string DisplayName,
    string? RootPath);

internal sealed record ProviderInstanceIdentity(
    string ManifestId,
    string InstanceId,
    string DisplayName);

internal static class ProviderInstanceIdentityPolicy
{
    private const string SourceMarker = "::source:";

    public static IReadOnlyList<ProviderInstanceIdentity> Resolve(
        IReadOnlyList<ProviderInstanceCandidate> candidates)
    {
        var result = new ProviderInstanceIdentity[candidates.Count];
        foreach (var indexedGroup in candidates
                     .Select((candidate, index) => (Candidate: candidate, Index: index))
                     .GroupBy(entry => entry.Candidate.ManifestId, StringComparer.OrdinalIgnoreCase))
        {
            var entries = indexedGroup.ToArray();
            if (entries.Length == 1)
            {
                var entry = entries[0];
                result[entry.Index] = new ProviderInstanceIdentity(
                    entry.Candidate.ManifestId,
                    entry.Candidate.ManifestId,
                    entry.Candidate.DisplayName);
                continue;
            }

            var canonicalManifestId = entries[0].Candidate.ManifestId;
            var sourceTokens = entries.ToDictionary(
                entry => entry.Index,
                entry => BuildSourceToken(entry.Candidate.RootPath, entry.Index),
                EqualityComparer<int>.Default);
            foreach (var collision in sourceTokens
                         .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
            {
                foreach (var pair in collision)
                {
                    sourceTokens[pair.Key] += "-" + ShortHash(
                        NormalizePath(candidates[pair.Key].RootPath).ToLowerInvariant());
                }
            }

            var displayRanks = entries
                .OrderBy(entry => sourceTokens[entry.Index], StringComparer.OrdinalIgnoreCase)
                .Select((entry, index) => (entry.Index, Rank: index + 1))
                .ToDictionary(entry => entry.Index, entry => entry.Rank);
            var duplicateNames = entries
                .GroupBy(entry => entry.Candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var displayName = duplicateNames.Contains(entry.Candidate.DisplayName)
                    ? $"{entry.Candidate.DisplayName} · {displayRanks[entry.Index]}"
                    : entry.Candidate.DisplayName;
                result[entry.Index] = new ProviderInstanceIdentity(
                    entry.Candidate.ManifestId,
                    canonicalManifestId + SourceMarker + sourceTokens[entry.Index],
                    displayName);
            }
        }

        return result;
    }

    public static string ScopeOptionId(
        string manifestId,
        string instanceId,
        string optionId)
    {
        if (manifestId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
        {
            return optionId;
        }

        if (optionId.Equals(manifestId, StringComparison.OrdinalIgnoreCase))
        {
            return instanceId;
        }

        if (optionId.StartsWith(manifestId + "::", StringComparison.OrdinalIgnoreCase))
        {
            return instanceId + optionId[manifestId.Length..];
        }

        return instanceId + "::option:" + optionId;
    }

    public static bool IsOptionSelectionAlias(
        string manifestId,
        string instanceId,
        string currentOptionId,
        string savedSelectionId)
    {
        if (currentOptionId.Equals(savedSelectionId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var currentLegacyId = ToLegacyOptionId(manifestId, instanceId, currentOptionId);
        var savedLegacyId = RemoveSourceScope(savedSelectionId);
        if (currentLegacyId.Equals(savedLegacyId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var optionMarker = manifestId + "::option:";
        return savedLegacyId.StartsWith(optionMarker, StringComparison.OrdinalIgnoreCase) &&
               currentLegacyId.Equals(
                   savedLegacyId[optionMarker.Length..],
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string ToLegacyOptionId(
        string manifestId,
        string instanceId,
        string optionId)
    {
        if (optionId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
        {
            return manifestId;
        }

        var arbitraryOptionMarker = instanceId + "::option:";
        if (optionId.StartsWith(arbitraryOptionMarker, StringComparison.OrdinalIgnoreCase))
        {
            return optionId[arbitraryOptionMarker.Length..];
        }

        return optionId.StartsWith(instanceId, StringComparison.OrdinalIgnoreCase)
            ? manifestId + optionId[instanceId.Length..]
            : optionId;
    }

    private static string RemoveSourceScope(string optionId)
    {
        var markerIndex = optionId.IndexOf(SourceMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return optionId;
        }

        var tokenStart = markerIndex + SourceMarker.Length;
        var suffixStart = optionId.IndexOf(':', tokenStart);
        return suffixStart < 0
            ? optionId[..markerIndex]
            : optionId[..markerIndex] + optionId[suffixStart..];
    }

    private static string BuildSourceToken(string? rootPath, int fallbackIndex)
    {
        var normalized = NormalizePath(rootPath);
        if (normalized.Length == 0)
        {
            return "unknown-" + (fallbackIndex + 1);
        }

        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (leaf.Length > 0 && leaf.All(char.IsAsciiDigit))
        {
            return leaf;
        }

        var readable = new string(leaf
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray())
            .Trim('-');
        if (readable.Length > 24)
        {
            readable = readable[..24].TrimEnd('-');
        }

        // Chinese names can all collapse to "local", and different drives can contain the
        // same leaf name. Include the source path from the start so adding/reordering sources
        // cannot change an existing ID; the display rank must never become persistence data.
        return (readable.Length == 0 ? "local" : readable) + "-" +
               ShortHash(normalized.ToLowerInvariant());
    }

    private static string NormalizePath(string? path) =>
        (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            [..8]
            .ToLowerInvariant();
}
