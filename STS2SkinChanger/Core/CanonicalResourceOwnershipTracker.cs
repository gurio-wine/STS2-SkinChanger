namespace STS2SkinChanger.Core;

/// <summary>
/// Tracks which runtime overlay most recently supplied each canonical Godot remap/import path.
/// Exported binary resources cannot rewrite their external dependency strings, so their private
/// aliases still need a short-lived canonical bridge. A cached overlay must be remounted when a
/// later skin or baseline pack has taken ownership of one of those bridge paths.
/// </summary>
internal sealed class CanonicalResourceOwnershipTracker
{
    private readonly Dictionary<string, string> _owners =
        new(StringComparer.OrdinalIgnoreCase);

    public bool RequiresActivation(string ownerId, IEnumerable<string> canonicalPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(canonicalPaths);

        return canonicalPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(path =>
                !_owners.TryGetValue(path, out var currentOwner) ||
                !currentOwner.Equals(ownerId, StringComparison.OrdinalIgnoreCase));
    }

    public void MarkActivated(string ownerId, IEnumerable<string> canonicalPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(canonicalPaths);

        foreach (var path in canonicalPaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _owners[path] = ownerId;
        }
    }

    public void Reset() => _owners.Clear();
}
