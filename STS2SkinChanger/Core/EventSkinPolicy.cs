namespace STS2SkinChanger.Core;

// Event identity comes from game/gameplay-Mod tables, never from a skin's display name.
internal static class EventSkinPolicy
{
    public const string Prefix = "event:";
    public static string GroupId(string eventId) => eventId.Equals("fake_merchant", StringComparison.OrdinalIgnoreCase)
        ? "fake_merchant_monster" : Prefix + eventId.ToLowerInvariant();
    public static bool IsEventGroup(string groupId) => groupId.StartsWith(Prefix, StringComparison.Ordinal);
    private static readonly string[] ResourceRoots =
    [
        "res://images/events/", "res://images/packed/vfx/event/",
        "res://scenes/vfx/events/", "res://scenes/events/background_scenes/",
        "res://scenes/events/custom/", "res://animations/events/", "res://audio/events/"
    ];
    public static bool CouldOwnResource(string path) => ResourceRoots.Any(root =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    public static string? EventIdFromKey(string key)
    {
        var separator = key.IndexOf('.');
        return separator > 0 ? key[..separator].ToLowerInvariant() : null;
    }

    public static string? FindResourceOwner(string path, IEnumerable<string> eventIds)
    {
        var normalized = path.ToLowerInvariant();
        var root = ResourceRoots.FirstOrDefault(normalized.StartsWith);
        if (root == null) return null;
        var tail = normalized[root.Length..];
        string? owner = null;
        foreach (var id in eventIds)
            if ((owner == null || id.Length > owner.Length) &&
                tail.StartsWith(id, StringComparison.OrdinalIgnoreCase) &&
                tail.Length > id.Length && tail[id.Length] is '.' or '/' or '_') owner = id;
        return owner;
    }
}
