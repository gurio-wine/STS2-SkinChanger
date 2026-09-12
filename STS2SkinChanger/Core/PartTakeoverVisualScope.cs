using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

/// <summary>
/// Keeps the merchant's own figure and hand in line with the merchant selection while a style/UI
/// package draws them from its own images.
///
/// Such a package is not a skin: the game loads it itself, its own code keeps running and it keeps
/// drawing its own shop. Only the merchant's figure and hand belong to the group the player picks
/// (see <see cref="MerchantAppearanceScopePolicy"/>); the room and the shop interface are left
/// untouched, so a style overhaul always looks like itself.
///
/// While the package's part is in effect nothing changes — its art is what the player should see.
/// Choosing another merchant skin or the game original hands the merchant's figure and hand back to
/// the game's own value, and the package's art returns as soon as its part is selected again.
/// Nothing the package drew is ever removed or freed.
/// </summary>
internal static class PartTakeoverVisualScope
{
    private static readonly ConditionalWeakTable<Node, RoomVisualState> Rooms = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<PropertyInfo>> TextureSlots = new();
    private static readonly string[] TexturePropertyNames = ["Texture", "TextureNormal"];

    public static void Capture(Node root, string groupId)
    {
        try
        {
            var state = Rooms.GetOrCreateValue(root);
            state.GroupId = groupId;
            Walk(root, node => Remember(state, node));
        }
        catch (Exception exception)
        {
            ModLog.Warn("记录商人外观的原始画面失败：" + exception.GetBaseException().Message);
        }
    }

    public static void Reconcile(Node root, string groupId)
    {
        try
        {
            var parts = ResolveParts(groupId);
            if (parts.Count == 0)
            {
                return;
            }

            var state = Rooms.GetOrCreateValue(root);
            state.GroupId = groupId;
            var counters = new ReconcileCounters();
            Walk(root, node => ReconcileNode(state, node, parts, counters));
            if (counters.Any)
            {
                ModLog.Info(
                    $"商人外观已按当前 {groupId} 选择对齐：还原 {counters.Restored} 处原版画面、" +
                    $"隐藏 {counters.Hidden} 处部件自带画面、恢复 {counters.Reapplied} 处部件画面；" +
                    "商店房间与商店界面保持作者原样。");
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("按选择对齐商人外观失败，保留作者画面：" + exception.GetBaseException().Message);
        }
    }

    /// <summary>The selectable parts of this group and the resource namespaces their own art uses.</summary>
    private static IReadOnlyList<OwnedPart> ResolveParts(string groupId)
    {
        var catalog = SkinService.Catalog;
        if (catalog == null)
        {
            return [];
        }

        var parts = new List<OwnedPart>();
        foreach (var group in catalog.Groups)
        {
            if (!group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var option in group.Options.Where(option => option.IsBaselinePartOverride))
            {
                parts.Add(new OwnedPart(option.Id, ResolveNamespaces(option)));
            }
        }

        return parts;
    }

    /// <summary>
    /// The namespaces a package's own art appears under: the manifest ID it published with and the
    /// file names of the assemblies it ships. Both are read from disk, never assumed to be equal.
    /// </summary>
    private static IReadOnlySet<string> ResolveNamespaces(SkinOption option)
    {
        var namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { option.Id };
        if (option.ProviderRootPath == null)
        {
            return namespaces;
        }

        try
        {
            foreach (var assembly in Directory.EnumerateFiles(
                         option.ProviderRootPath,
                         "*.dll",
                         SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(assembly);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    namespaces.Add(name);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ModLog.Warn($"读取部件接管包 {option.Id} 的程序集名失败，仅使用清单 ID：" + exception.Message);
        }

        return namespaces;
    }

    private static void ReconcileNode(
        RoomVisualState state,
        Node node,
        IReadOnlyList<OwnedPart> parts,
        ReconcileCounters counters)
    {
        if (!IsInScope(node))
        {
            // The room and the shop interface belong to the package, not to this group's selection.
            return;
        }

        if (!state.Nodes.TryGetValue(node.GetInstanceId(), out var entry))
        {
            if (ResolveNodeOwner(node, parts) is { } addedBy)
            {
                // The package added this while drawing the merchant. It has no game original to
                // hand back, so it is shown with its own part and hidden with any other choice.
                ApplyAddedNode(state, node, addedBy, counters);
                return;
            }

            Remember(state, node);
            return;
        }

        if (entry.Node.TryGetTarget(out var tracked) && !GodotObject.IsInstanceValid(tracked))
        {
            state.Nodes.Remove(node.GetInstanceId());
            Remember(state, node);
            return;
        }

        foreach (var slot in ResolveTextureSlots(node))
        {
            var current = slot.GetValue(node) as Texture2D;
            var baseline = entry.Baselines.GetValueOrDefault(slot.Name);
            var appliedOwner = entry.AppliedOwners.GetValueOrDefault(slot.Name);
            var applied = entry.Applied.GetValueOrDefault(slot.Name);
            if (!ReferenceEquals(current, baseline))
            {
                if (baseline != null && ResolveOwner(current?.ResourcePath, parts) is { } writer)
                {
                    entry.AppliedOwners[slot.Name] = writer;
                    entry.Applied[slot.Name] = current;
                    if (!IsPartSelected(state.GroupId, writer))
                    {
                        slot.SetValue(node, baseline);
                        counters.Restored++;
                    }

                    continue;
                }

                if (baseline == null)
                {
                    // First sight of a node that appeared after the room was built; the value it
                    // carries now is the room's own.
                    entry.Baselines[slot.Name] = current;
                }

                continue;
            }

            if (appliedOwner != null && applied != null &&
                !ReferenceEquals(applied, baseline) &&
                IsPartSelected(state.GroupId, appliedOwner))
            {
                slot.SetValue(node, applied);
                counters.Reapplied++;
            }
        }
    }

    private static void ApplyAddedNode(
        RoomVisualState state,
        Node node,
        OwnedPart owner,
        ReconcileCounters counters)
    {
        var id = node.GetInstanceId();
        if (!state.PackageNodes.TryGetValue(id, out var entry) ||
            (entry.Node.TryGetTarget(out var tracked) && !GodotObject.IsInstanceValid(tracked)))
        {
            entry = new PackageNodeState(
                new WeakReference<Node>(node),
                owner,
                node is CanvasItem canvas ? canvas.Visible : true);
            state.PackageNodes[id] = entry;
        }

        if (node is not CanvasItem item)
        {
            return;
        }

        var selected = IsPartSelected(state.GroupId, entry.Owner);
        var visible = selected && entry.OriginalVisible;
        if (item.Visible == visible)
        {
            return;
        }

        item.Visible = visible;
        if (visible)
        {
            counters.Reapplied++;
        }
        else
        {
            counters.Hidden++;
        }
    }

    private static bool IsPartSelected(string groupId, OwnedPart part) =>
        SkinService.GetDisplayedSelection(groupId).Equals(part.OptionId, StringComparison.OrdinalIgnoreCase);

    private static void Remember(RoomVisualState state, Node node)
    {
        if (!IsInScope(node))
        {
            return;
        }

        var id = node.GetInstanceId();
        if (state.Nodes.ContainsKey(id))
        {
            return;
        }

        var entry = new NodeVisualState(new WeakReference<Node>(node));
        foreach (var slot in ResolveTextureSlots(node))
        {
            entry.Baselines[slot.Name] = slot.GetValue(node) as Texture2D;
        }

        state.Nodes[id] = entry;
    }

    /// <summary>Whether this node's own art belongs to the merchant's figure or its hand.</summary>
    private static bool IsInScope(Node node) =>
        MerchantAppearanceScopePolicy.IsMerchantAppearance(DescribeAncestry(node));

    private static IEnumerable<(string TypeName, string NodeName)> DescribeAncestry(Node node)
    {
        for (var current = node; current != null; current = current.GetParent())
        {
            yield return (current.GetType().Name, current.Name.ToString());
        }
    }

    /// <summary>
    /// The part owner a node's art belongs to: its own texture, its own instanced scene, or the
    /// nearest ancestor that was instanced from the package's own scene file.
    /// </summary>
    private static OwnedPart? ResolveNodeOwner(Node node, IReadOnlyList<OwnedPart> parts)
    {
        foreach (var slot in ResolveTextureSlots(node))
        {
            if (ResolveOwner((slot.GetValue(node) as Texture2D)?.ResourcePath, parts) is { } fromTexture)
            {
                return fromTexture;
            }
        }

        for (var current = node; current != null; current = current.GetParent())
        {
            if (ResolveOwner(current.SceneFilePath, parts) is { } fromScene)
            {
                return fromScene;
            }
        }

        return null;
    }

    private static OwnedPart? ResolveOwner(string? resourcePath, IReadOnlyList<OwnedPart> parts)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            return null;
        }

        const string Prefix = "res://";
        var path = resourcePath.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? resourcePath[Prefix.Length..]
            : resourcePath;
        var separator = path.IndexOf('/');
        var head = separator < 0 ? path : path[..separator];
        return head.Length == 0
            ? null
            : parts.FirstOrDefault(part => part.Namespaces.Contains(head));
    }

    private static IReadOnlyList<TextureSlot> ResolveTextureSlots(Node node)
    {
        var properties = TextureSlots.GetOrAdd(node.GetType(), static type =>
            TexturePropertyNames
                .Select(name => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance))
                .Where(property => property is { CanRead: true, CanWrite: true } &&
                                   typeof(Texture2D).IsAssignableFrom(property.PropertyType))
                .Cast<PropertyInfo>()
                .ToArray());
        return properties.Count == 0
            ? []
            : [.. properties.Select(property => new TextureSlot(property))];
    }

    private static void Walk(Node root, Action<Node> visit)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            visit(node);
            foreach (var child in node.GetChildren())
            {
                if (child is Node childNode)
                {
                    stack.Push(childNode);
                }
            }
        }
    }

    /// <summary>One selectable part of the group and the namespaces its own art appears under.</summary>
    private sealed record OwnedPart(string OptionId, IReadOnlySet<string> Namespaces);

    private sealed class RoomVisualState
    {
        public string GroupId { get; set; } = string.Empty;
        public Dictionary<ulong, NodeVisualState> Nodes { get; } = [];
        public Dictionary<ulong, PackageNodeState> PackageNodes { get; } = [];
    }

    private sealed class NodeVisualState(WeakReference<Node> node)
    {
        public WeakReference<Node> Node { get; } = node;
        public Dictionary<string, Texture2D?> Baselines { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, OwnedPart> AppliedOwners { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Texture2D?> Applied { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PackageNodeState(
        WeakReference<Node> node,
        OwnedPart owner,
        bool originalVisible)
    {
        public WeakReference<Node> Node { get; } = node;
        public OwnedPart Owner { get; } = owner;
        public bool OriginalVisible { get; } = originalVisible;
    }

    private sealed class ReconcileCounters
    {
        public int Restored { get; set; }
        public int Hidden { get; set; }
        public int Reapplied { get; set; }
        public bool Any => Restored > 0 || Hidden > 0 || Reapplied > 0;
    }

    private readonly record struct TextureSlot(PropertyInfo Property)
    {
        public string Name => Property.Name;

        public object? GetValue(Node node) => Property.GetValue(node);

        public void SetValue(Node node, Texture2D? texture) => Property.SetValue(node, texture);
    }
}
