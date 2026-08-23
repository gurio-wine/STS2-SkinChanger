using Godot;

namespace STS2SkinChanger.Core;

/// <summary>
/// Keeps replacement scenes compatible with game code that looks up required nodes through
/// Godot's unique-name syntax (for example, <c>%Visuals</c>). Some sprite skin scenes intentionally
/// omit invisible control nodes from the original template; those nodes still have to exist in
/// combat even though they are not part of the visible skin.
/// </summary>
internal static class ManagedSceneCompatibility
{
    public static int CopyMissingUniqueNodes(Node? baselineRoot, Node? replacementRoot)
    {
        if (!GodotObject.IsInstanceValid(baselineRoot) ||
            !GodotObject.IsInstanceValid(replacementRoot) ||
            ReferenceEquals(baselineRoot, replacementRoot))
        {
            return 0;
        }

        var uniqueNodes = new List<Node>();
        CollectUniqueNodes(baselineRoot!, baselineRoot!, uniqueNodes);
        // GetPath() emits an engine error for nodes that have not entered SceneTree yet. Scene
        // replacements are deliberately hardened before AddChild(), so determine depth from the
        // parent chain instead. This is also O(n) rather than repeatedly formatting NodePaths.
        uniqueNodes.Sort((left, right) =>
            GetRelativeDepth(left, baselineRoot!).CompareTo(
                GetRelativeDepth(right, baselineRoot!)));

        var copied = 0;
        foreach (var source in uniqueNodes)
        {
            if (ReferenceEquals(source, baselineRoot))
            {
                continue;
            }

            var uniqueName = source.Name.ToString();
            if (string.IsNullOrWhiteSpace(uniqueName) ||
                replacementRoot!.GetNodeOrNull<Node>('%' + uniqueName) != null)
            {
                continue;
            }

            var clone = source.Duplicate();
            if (clone == null)
            {
                continue;
            }

            var destinationParent = FindDestinationParent(
                baselineRoot!, replacementRoot!, source.GetParent());
            clone.Name = source.Name;
            destinationParent.AddChild(clone);
            clone.UniqueNameInOwner = true;
            SetOwnerRecursive(clone, replacementRoot!);
            copied++;
        }

        return copied;
    }

    private static void CollectUniqueNodes(Node node, Node owner, ICollection<Node> output)
    {
        if (node.UniqueNameInOwner &&
            (ReferenceEquals(node, owner) || ReferenceEquals(node.Owner, owner)))
        {
            output.Add(node);
        }

        foreach (var child in node.GetChildren())
        {
            CollectUniqueNodes(child, owner, output);
        }
    }

    private static int GetRelativeDepth(Node node, Node root)
    {
        var depth = 0;
        for (var current = node; current != null && !ReferenceEquals(current, root); current = current.GetParent())
        {
            depth++;
        }

        return depth;
    }

    private static Node FindDestinationParent(
        Node baselineRoot,
        Node replacementRoot,
        Node? baselineParent)
    {
        if (baselineParent == null || ReferenceEquals(baselineParent, baselineRoot))
        {
            return replacementRoot;
        }

        var segments = new Stack<string>();
        for (var current = baselineParent;
             current != null && !ReferenceEquals(current, baselineRoot);
             current = current.GetParent())
        {
            segments.Push(current.Name.ToString());
        }

        return segments.Count == 0
            ? replacementRoot
            : replacementRoot.GetNodeOrNull<Node>(string.Join('/', segments)) ?? replacementRoot;
    }

    private static void SetOwnerRecursive(Node node, Node owner)
    {
        if (!ReferenceEquals(node, owner))
        {
            node.Owner = owner;
        }

        foreach (var child in node.GetChildren())
        {
            SetOwnerRecursive(child, owner);
        }
    }
}
