using Godot;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using STS2SkinChanger.Core;
using STS2SkinChanger.Ui;

namespace STS2SkinChanger.Live;

internal static class LiveSkinRefresher
{
    public static void Refresh(
        Node root,
        IReadOnlyDictionary<string, Resource> freshResources,
        IReadOnlySet<string> affectedPaths)
    {
        var refreshedSpines = 0;
        var refreshedTextures = 0;
        Walk(root, node =>
        {
            if (node.GetClass().ToString() == "SpineSprite")
            {
                refreshedSpines += RefreshSpine(node, freshResources);
            }

            refreshedTextures += RefreshTexture(node, freshResources, affectedPaths);
        });
        ModLog.Info($"运行时刷新完成：{refreshedSpines} 个 Spine，{refreshedTextures} 个纹理节点。");
    }

    private static int RefreshSpine(Node node, IReadOnlyDictionary<string, Resource> freshResources)
    {
        try
        {
            var currentResource = node.Get("skeleton_data_res").AsGodotObject() as Resource;
            if (currentResource == null || string.IsNullOrEmpty(currentResource.ResourcePath) ||
                !freshResources.TryGetValue(currentResource.ResourcePath, out var replacement))
            {
                return 0;
            }

            var mega = new MegaSprite(Variant.From(node));
            var animationName = mega.TryGetAnimationState()?.GetCurrentAnimationName();
            node.Call("set_skeleton_data_res", replacement);
            if (!string.IsNullOrEmpty(animationName))
            {
                SkinPanel.QueueAnimationRestore(node, animationName);
            }

            return 1;
        }
        catch (Exception exception)
        {
            ModLog.Warn($"刷新 Spine 节点 {node.Name} 失败：{exception.Message}");
            return 0;
        }
    }

    private static int RefreshTexture(
        Node node,
        IReadOnlyDictionary<string, Resource> freshResources,
        IReadOnlySet<string> affectedPaths)
    {
        Texture2D? current;
        Action<Texture2D?> setter;
        switch (node)
        {
            case Sprite2D sprite:
                current = sprite.Texture;
                setter = texture => sprite.Texture = texture;
                break;
            case TextureRect rect:
                current = rect.Texture;
                setter = texture => rect.Texture = texture;
                break;
            case TextureButton button:
                current = button.TextureNormal;
                setter = texture => button.TextureNormal = texture;
                break;
            default:
                return 0;
        }

        if (current == null || string.IsNullOrEmpty(current.ResourcePath) ||
            !affectedPaths.Contains(current.ResourcePath) ||
            !freshResources.TryGetValue(current.ResourcePath, out var resource) ||
            resource is not Texture2D replacement)
        {
            return 0;
        }

        setter(replacement);
        return 1;
    }

    private static void Walk(Node node, Action<Node> visitor)
    {
        visitor(node);
        foreach (var child in node.GetChildren())
        {
            Walk(child, visitor);
        }
    }
}
