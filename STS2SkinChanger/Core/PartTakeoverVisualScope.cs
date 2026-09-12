using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;

namespace STS2SkinChanger.Core;

/// <summary>
/// A style/UI package that overrides parts of a group draws the rest of that group's presentation
/// from its own images, on nodes the game created. Its group patches are gated on the selection
/// (see <see cref="PartTakeoverScope"/>), which covers every shop the player enters afterwards.
///
/// A shop the player is standing in when the selection changes is already styled, so the art that
/// package wrote has to be handed back: the game's own value is captured when the room is built,
/// before any package writes, and the room is reconciled with the selection afterwards. Whatever
/// the package drew is remembered, so selecting its part again restores its look instead of
/// requiring another shop visit.
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
            ModLog.Warn("记录商店房间的原始画面失败：" + exception.GetBaseException().Message);
        }
    }

    public static void Reconcile(Node root, string groupId)
    {
        try
        {
            var owners = PartTakeoverScope.ResolveCatalogOwners()
                .Where(owner => owner.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (owners.Length == 0)
            {
                return;
            }

            var state = Rooms.GetOrCreateValue(root);
            state.GroupId = groupId;
            var counters = new ReconcileCounters();
            Walk(root, node => ReconcileNode(state, node, owners, counters));
            if (counters.Any)
            {
                ModLog.Info(
                    $"商店房间已按当前 {groupId} 选择对齐：还原 {counters.Restored} 处原版画面、" +
                    $"隐藏 {counters.Hidden} 处部件自带画面、恢复 {counters.Reapplied} 处部件画面。");
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("按选择对齐商店房间画面失败，保留作者画面：" + exception.GetBaseException().Message);
        }
    }

    private static void ReconcileNode(
        RoomVisualState state,
        Node node,
        IReadOnlyList<PartTakeoverOwner> owners,
        ReconcileCounters counters)
    {
        if (!state.Nodes.TryGetValue(node.GetInstanceId(), out var entry))
        {
            if (ResolveNodeOwner(node, owners) is { } addedBy)
            {
                // The package added this while its part was in effect. Its own art has no original
                // to hand back, so it is shown with its own part and hidden with any other choice,
                // exactly like the part resources themselves.
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
                if (baseline != null && PartTakeoverPolicy.ResolveOwner(current?.ResourcePath, owners) is { } writer)
                {
                    entry.AppliedOwners[slot.Name] = writer;
                    entry.Applied[slot.Name] = current;
                    if (!PartTakeoverScope.IsOptionSelected(writer.GroupId, writer.OptionId))
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
                PartTakeoverScope.IsOptionSelected(appliedOwner.GroupId, appliedOwner.OptionId))
            {
                slot.SetValue(node, applied);
                counters.Reapplied++;
            }
        }
    }

    private static void ApplyAddedNode(
        RoomVisualState state,
        Node node,
        PartTakeoverOwner owner,
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

        var selected = PartTakeoverScope.IsOptionSelected(entry.Owner.GroupId, entry.Owner.OptionId);
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

    private static void Remember(RoomVisualState state, Node node)
    {
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

    /// <summary>
    /// The part owner a node's art belongs to: its own texture, its own instanced scene, or the
    /// nearest ancestor that was instanced from the package's own scene file.
    /// </summary>
    internal static PartTakeoverOwner? ResolveNodeOwner(Node node, IReadOnlyList<PartTakeoverOwner> owners)
    {
        foreach (var slot in ResolveTextureSlots(node))
        {
            if (PartTakeoverPolicy.ResolveOwner((slot.GetValue(node) as Texture2D)?.ResourcePath, owners)
                is { } fromTexture)
            {
                return fromTexture;
            }
        }

        for (var current = node; current != null; current = current.GetParent())
        {
            if (PartTakeoverPolicy.ResolveOwner(current.SceneFilePath, owners) is { } fromScene)
            {
                return fromScene;
            }
        }

        return null;
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

    internal sealed class RoomVisualState
    {
        public string GroupId { get; set; } = string.Empty;
        public Dictionary<ulong, NodeVisualState> Nodes { get; } = [];
        public Dictionary<ulong, PackageNodeState> PackageNodes { get; } = [];
    }

    internal sealed class NodeVisualState(WeakReference<Node> node)
    {
        public WeakReference<Node> Node { get; } = node;
        public Dictionary<string, Texture2D?> Baselines { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PartTakeoverOwner> AppliedOwners { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Texture2D?> Applied { get; } = new(StringComparer.Ordinal);
    }

    internal sealed class PackageNodeState(
        WeakReference<Node> node,
        PartTakeoverOwner owner,
        bool originalVisible)
    {
        public WeakReference<Node> Node { get; } = node;
        public PartTakeoverOwner Owner { get; } = owner;
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
