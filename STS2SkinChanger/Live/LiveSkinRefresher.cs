using Godot;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Live;

internal static class LiveSkinRefresher
{
    public static void Refresh(
        Node root,
        IReadOnlyDictionary<string, Resource> freshResources,
        IReadOnlySet<string> affectedPaths)
    {
        AnimationRestoreHost.EnsureInstalled(root.GetTree());
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
                AnimationRestoreHost.Queue(node, animationName);
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

internal sealed partial class AnimationRestoreHost : Node
{
    private sealed record PendingAnimation(Node Node, string AnimationName, int Retries);

    private static AnimationRestoreHost? _instance;
    private readonly List<PendingAnimation> _pending = [];

    public static void EnsureInstalled(SceneTree tree)
    {
        if (_instance != null && IsInstanceValid(_instance))
        {
            return;
        }

        _instance = new AnimationRestoreHost
        {
            Name = "STS2SkinAnimationRestoreHost",
            ProcessMode = ProcessModeEnum.Always
        };
        tree.Root.AddChild(_instance);
        _instance.SetProcess(true);
    }

    public static void Queue(Node node, string animationName)
    {
        EnsureInstalled(node.GetTree());
        _instance!._pending.Add(new PendingAnimation(node, animationName, 30));
    }

    public override void _Process(double delta)
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var pending = _pending[i];
            if (!IsInstanceValid(pending.Node) || pending.Retries <= 0)
            {
                _pending.RemoveAt(i);
                continue;
            }

            try
            {
                var mega = new MegaSprite(Variant.From(pending.Node));
                var state = mega.TryGetAnimationState();
                if (state != null && mega.HasAnimation(pending.AnimationName))
                {
                    state.SetAnimation(pending.AnimationName);
                    _pending.RemoveAt(i);
                    continue;
                }
            }
            catch
            {
                // Spine 在换资源后的几帧内可能尚未重建，继续重试。
            }

            _pending[i] = pending with { Retries = pending.Retries - 1 };
        }
    }
}
