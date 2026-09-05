namespace STS2SkinChanger.Core;

internal static class PresentationNodeOwnership
{
    // SpineSprite creates these children itself and stores raw pointers to them in its
    // mesh_instances vector. Changing skeleton data regenerates them, but that does not
    // make them provider-added nodes. Only their owning SpineSprite may remove them.
    internal static bool IsRendererOwned(string nativeClass) => nativeClass == "SpineMesh2D";

    internal static IEnumerable<T> Walk<T>(
        T root, Func<T, string> nativeClass, Func<T, IEnumerable<T>> children)
    {
        if (IsRendererOwned(nativeClass(root)))
        {
            yield break;
        }

        yield return root;
        foreach (var child in children(root))
        {
            foreach (var descendant in Walk(child, nativeClass, children))
            {
                yield return descendant;
            }
        }
    }
}
