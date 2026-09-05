using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2SkinChanger.Ui;

namespace STS2SkinChanger.Core;

/// <summary>Model-loading bridge inside the original manager's existing preview control.</summary>
internal static class FrameworkModelPreview
{
    public static void Refresh(Node selector, CharacterModel character)
    {
        var container = selector.GetNodeOrNull<Node2D>("VisualContainer");
        if (container == null) return;
        var groupId = ContextualSkinControls.FindGroup(character.Id.Entry, character.GetType().Name)?.Id;
        var scenePath = groupId != null
            ? ContextualSkinControls.CanonicalScenePath("creature_visuals/" + character.Id.Entry.ToLowerInvariant())
            : (string)AccessTools.PropertyGetter(typeof(CharacterModel), "VisualsPath").Invoke(character, null)!;
        Node2D? staged = null;
        NCreatureVisuals? visuals = null;
        try
        {
            // Keep the selected private dependencies mounted through creation AND Ready.
            using var scope = groupId == null ? null : SkinService.BeginRuntimeResourceScope(groupId, scenePath);
            visuals = CreateVisuals(character, groupId, scenePath);
            staged = new Node2D { Name = "SkinChangerPendingPreview", Scale = Vector2.One * 0.85f, Visible = false };
            // Keep the complete fresh model: extracting/duplicating only a node named Visuals
            // discards sibling sprites, model offsets and animation controllers, and leaks the root.
            staged.AddChild(visuals);
            container.AddChild(staged);
            foreach (var control in DescendantsAndSelf(staged).OfType<Control>())
                control.MouseFilter = Control.MouseFilterEnum.Ignore;
            var previous = container.GetNodeOrNull<Node>("PreviewSprite");
            if (previous != null)
            {
                container.RemoveChild(previous);
                previous.QueueFree();
            }
            staged.Name = "PreviewSprite";
            staged.Visible = true;
            StartAnimations(staged, groupId ?? character.Id.Entry);
            ModLog.Info($"已刷新原管理器小模型：{character.Id.Entry}/{(groupId == null ? "unmanaged" : SkinService.Config.GetSelection(groupId))}；" +
                        $"模型类型={visuals.GetType().Name}；完整模型子节点={visuals.GetChildCount()}。");
            staged = null;
            visuals = null;
        }
        catch (Exception exception)
        {
            ModLog.Warn($"原管理器小模型加载失败 {character.Id.Entry}/{scenePath}：{exception.GetBaseException().Message}");
        }
        finally
        {
            if (staged != null) staged.QueueFree();
            else if (visuals != null && visuals.GetParent() == null) visuals.Free();
        }
    }

    internal static NCreatureVisuals CreateVisuals(CharacterModel character, string? groupId, string scenePath)
    {
        if (groupId != null && SkinService.TryInstantiateSelectedCharacterCreatureVisuals(
                groupId, scenePath, visuals =>
                {
                    ContextualSkinControls.ApplySelectedProviderVisualPostfix(
                        character.Id.Entry, character.GetType().Name, character, ref visuals);
                    return visuals;
                }, out var result)) return result;
        // Pure runtime providers and characters outside SC keep their original creation logic.
        // This call already runs CharacterVisualResultPatch; do not apply its postfix twice.
        return character.CreateVisuals();
    }

    internal static (string? Entry, string? Idle) ResolveAnimations(IReadOnlyList<string> names)
    {
        string? Find(params string[] aliases) => aliases.Select(alias => names.FirstOrDefault(name =>
            name.Equals(alias, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(name => name != null);
        return (Find("entry"), Find("idle_loop", "idle", "stand", "standing", "default", "animation", "relaxed_loop"));
    }

    public static void StartAnimations(Node root, string groupId)
    {
        foreach (var node in DescendantsAndSelf(root).ToArray())
        {
            try
            {
                if (node.GetClass().ToString() == "SpineSprite")
                {
                    var sprite = new MegaSprite(node);
                    root.RunWhenSpineReady(sprite, state =>
                    {
                        if (!GodotObject.IsInstanceValid(root) || root.IsQueuedForDeletion() ||
                            !GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion()) return;
                        try
                        {
                            var names = sprite.GetSkeleton()?.GetData()?.GetAnimationNames();
                            if (names == null) return;
                            var plan = ResolveAnimations(names);
                            if (plan.Entry != null)
                            {
                                using var entry = state.BoundObject.Call("set_animation", plan.Entry, false, 0);
                                if (plan.Idle != null)
                                {
                                    using var idle = state.BoundObject.Call("add_animation", plan.Idle, 0f, true, 0);
                                }
                            }
                            else if (plan.Idle != null)
                            {
                                using var idle = state.BoundObject.Call("set_animation", plan.Idle, true, 0);
                            }
                        }
                        catch (Exception exception) { LogAnimationFailure(groupId, exception); }
                    });
                }
                else if (node is AnimatedSprite2D animated && !animated.IsPlaying() && animated.SpriteFrames != null)
                {
                    var plan = ResolveAnimations(animated.SpriteFrames.GetAnimationNames());
                    if (plan.Idle != null) animated.Play(plan.Idle);
                }
                else if (node is AnimationPlayer player && !player.IsPlaying())
                {
                    var plan = ResolveAnimations(player.GetAnimationList());
                    if (plan.Idle != null) player.Play(plan.Idle);
                }
                // Static sprites are already visible. They must never be wrapped as MegaSprite.
            }
            catch (Exception exception) { LogAnimationFailure(groupId, exception); }
        }
    }

    private static void LogAnimationFailure(string groupId, Exception exception) =>
        ModLog.Warn($"启动 {groupId} 的原管理器预览动画失败：{exception.GetBaseException().Message}");

    private static IEnumerable<Node> DescendantsAndSelf(Node root)
    {
        yield return root;
        foreach (var child in root.GetChildren())
            foreach (var node in DescendantsAndSelf(child)) yield return node;
    }
}
