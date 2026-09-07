namespace STS2SkinChanger.Core;

internal sealed record PresentationMetadataChange<T>(string Key, bool HadOriginal, T? Original, bool HasApplied, T? Applied);

internal static class PresentationMetadataChanges
{
    internal static PresentationMetadataChange<T>[] Capture<T>(IReadOnlyDictionary<string, T> before, IReadOnlyDictionary<string, T> after) =>
        before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).Select(key =>
            new PresentationMetadataChange<T>(key, before.TryGetValue(key, out var old), old,
                after.TryGetValue(key, out var current), current))
            .Where(change => change.HadOriginal != change.HasApplied || !EqualityComparer<T>.Default.Equals(change.Original, change.Applied)).ToArray();

    internal static bool CanRestore<T>(PresentationMetadataChange<T> change, bool hasCurrent, T? current) =>
        change.HasApplied == hasCurrent && (!hasCurrent || EqualityComparer<T>.Default.Equals(current, change.Applied));
}
