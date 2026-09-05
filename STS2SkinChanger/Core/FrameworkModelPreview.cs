using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2SkinChanger.Ui;
using System.Reflection;

namespace STS2SkinChanger.Core;

/// <summary>Model-loading bridge inside the original manager's existing preview control.</summary>
internal static class FrameworkModelPreview
{
    public static void Refresh(Node selector, CharacterModel character)
    {
        var container = selector.GetNodeOrNull<Node2D>("VisualContainer");
        if (container == null || selector is not Control control) return;
        var groupId = ContextualSkinControls.FindGroup(character.Id.Entry, character.GetType().Name)?.Id;
        var scenePath = groupId != null
            ? ContextualSkinControls.CanonicalScenePath("creature_visuals/" + character.Id.Entry.ToLowerInvariant())
            : (string)AccessTools.PropertyGetter(typeof(CharacterModel), "VisualsPath").Invoke(character, null)!;
        FrameworkPreviewSurface? staged = null;
        NCreatureVisuals? visuals = null;
        try
        {
            // Keep the selected private dependencies mounted through creation AND Ready.
            using var scope = groupId == null ? null : SkinService.BeginRuntimeResourceScope(groupId, scenePath);
            var player = CreatePreviewPlayer(character, UnlockState.all);
            visuals = CreateVisuals(character, groupId, scenePath, player.Creature);
            staged = new FrameworkPreviewSurface { Name = "SkinChangerPendingPreview" };
            // Keep the complete fresh model: extracting/duplicating only a node named Visuals
            // discards sibling sprites, model offsets and animation controllers, and leaks the root.
            var owner = new FrameworkPreviewCreature();
            owner.Initialize(player, visuals);
            staged.Initialize(control, container, owner, groupId ?? character.Id.Entry);
            container.AddChild(staged);
            ApplyRuntimeSpine(visuals, character, groupId);
            // Same selected, node-local cosmetic finishing as a live hot swap. Never replay
            // NCombatRoom callbacks or the original NCreature lifecycle in a menu preview.
            CharacterAppearanceRuntime.ReplaySelectedCreatureNodeReady(owner);
            foreach (var childControl in DescendantsAndSelf(staged).OfType<Control>())
                childControl.MouseFilter = Control.MouseFilterEnum.Ignore;
            var previous = container.GetNodeOrNull<Node>("PreviewSprite");
            if (previous != null)
            {
                container.RemoveChild(previous);
                previous.QueueFree();
            }
            staged.Name = "PreviewSprite";
            staged.BeginCapture();
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

    internal static Player CreatePreviewPlayer(CharacterModel character, UnlockState unlockState)
    {
        // Both supported game versions expose the same constructor. CreateForNewRun also
        // populates the deck/relics and runs their hooks; a cosmetic preview must not do that.
        var constructor = typeof(Player).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(ctor => ctor.GetParameters() is { Length: 15 } parameters &&
                parameters[0].ParameterType == typeof(CharacterModel) && parameters[1].ParameterType == typeof(ulong));
        return (Player)constructor.Invoke([character, 0UL, character.StartingHp, character.StartingHp,
            character.MaxEnergy, 0, 0, character.BaseOrbSlotCount, new RelicGrabBag(), unlockState,
            null, null, null, null, null]);
    }

    private static void ApplyRuntimeSpine(NCreatureVisuals visuals, CharacterModel character, string? groupId)
    {
        if (groupId == null) return;
        var path = ResolveCombatSpinePath(
            ManagedSkinModLoader.GetSelectedCharacterSkinPathMethods(groupId), character.GetType().Name);
        if (path == null) return;
        var body = visuals.SpineBody ?? throw new InvalidOperationException("所选骨骼皮肤的预览缺少 Spine 主模型。");
        var resource = SkinService.GetOrLoadRuntimeResource(groupId, path, includeProviderDependencies: true);
        if (resource.GetClass().ToString() != "SpineSkeletonDataResource")
            throw new InvalidOperationException($"预览路径不是完整 Spine 骨骼资源：{path}");
        body.SetSkeletonDataRes(new MegaSkeletonDataResource(resource));
        body.GetSkeleton()?.SetSlotsToSetupPose();
        ModLog.Info($"原管理器小模型已应用运行时骨骼：{groupId}/{path}。");
    }

    internal static string? ResolveCombatSpinePath(IEnumerable<MethodInfo> methods, string characterName)
    {
        var routes = methods.Where(method => method.IsStatic && method.ReturnType == typeof(string) &&
                method.GetParameters() is [{ ParameterType: var type }] && type == typeof(string) &&
                method.Name.Contains("CombatSkinPath", StringComparison.OrdinalIgnoreCase)).ToArray();
        // Ambiguous routes must not randomly borrow one another's models.
        if (routes.Length != 1) return null;
        var path = routes[0].Invoke(null, [characterName]) as string;
        return path?.StartsWith("res://", StringComparison.OrdinalIgnoreCase) == true &&
               (path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".res", StringComparison.OrdinalIgnoreCase)) ? path : null;
    }

    internal static (float Scale, Vector2 Position)? FitBounds(Rect2 bounds, Rect2 area)
    {
        if (!bounds.Position.IsFinite() || !bounds.Size.IsFinite() || !area.Position.IsFinite() ||
            !area.Size.IsFinite() || bounds.Size.X <= 0 || bounds.Size.Y <= 0 ||
            area.Size.X <= 0 || area.Size.Y <= 0) return null;
        var scale = Math.Min(area.Size.X / bounds.Size.X, area.Size.Y / bounds.Size.Y);
        return (scale, new Vector2(area.GetCenter().X - bounds.GetCenter().X * scale,
            area.End.Y - bounds.End.Y * scale));
    }

    internal static Rect2 PreviewArea(Rect2 panel, Rect2? footer)
    {
        var area = panel.Grow(-12);
        if (footer is { } f && f.Position.Y > area.Position.Y && f.Position.Y < panel.End.Y)
            area.Size = new Vector2(area.Size.X, Math.Min(area.End.Y, f.Position.Y - 12) - area.Position.Y);
        return area;
    }

    internal static Rect2 MeasureCaptureSeed(NCreatureVisuals visuals)
    {
        Rect2? result = null;
        // Only a broad first camera position, before any pixels have been read. Include the
        // complete model (not just a child named Visuals); the renderer decides what is visible.
        // GetGlobalTransform is in this isolated viewport's canvas coordinates. Do not remove
        // the owner's transform: providers may move/scale the owner as well as its model.
        foreach (var node in DescendantsAndSelf(visuals).OfType<Node2D>())
        {
            if (!node.IsVisibleInTree() || node.Modulate.A <= 0 || node.SelfModulate.A <= 0) continue;
            Rect2? rect = node switch
            {
                Sprite2D sprite when sprite.Texture != null => sprite.GetRect(),
                AnimatedSprite2D animated when animated.SpriteFrames?.GetFrameTexture(animated.Animation, animated.Frame) is { } texture =>
                    new Rect2(animated.Offset - (animated.Centered ? texture.GetSize() / 2 : Vector2.Zero), texture.GetSize()),
                _ => null
            };
            if (node.GetClass().ToString() == "SpineSprite" && new MegaSprite(node).GetSkeleton() is { } skeleton)
                rect = skeleton.GetBounds();
            if (rect is not { } r || !r.Position.IsFinite() || !r.Size.IsFinite() || r.Size.X <= 0 || r.Size.Y <= 0) continue;
            var transformed = node.GlobalTransform * r;
            result = result?.Merge(transformed) ?? transformed;
        }
        if (result != null) return result.Value;
        if (visuals.Bounds is { } bounds && GodotObject.IsInstanceValid(bounds))
            return bounds.GetGlobalTransform() * new Rect2(Vector2.Zero, bounds.Size);
        return new Rect2(-200, -400, 400, 400);
    }

    internal static NCreatureVisuals CreateVisuals(CharacterModel character, string? groupId, string scenePath,
        Creature? creature = null)
    {
        if (groupId != null && SkinService.TryInstantiateSelectedCharacterCreatureVisuals(
                groupId, scenePath, visuals =>
                {
                    ContextualSkinControls.ApplySelectedProviderVisualPostfix(
                        character.Id.Entry, character.GetType().Name, character, ref visuals);
                    NCreatureVisuals? completed = visuals;
                    if (creature != null) ContextualSkinControls.ApplySelectedCreatureVisualPostfix(creature, ref completed);
                    return completed ?? throw new InvalidOperationException("所选皮肤未返回预览模型。");
                }, out var result)) return result;
        // Pure runtime providers and characters outside SC keep their original creation logic.
        // This call already runs CharacterVisualResultPatch; do not apply its postfix twice.
        return (creature != null ? creature.CreateVisuals() : character.CreateVisuals())
            ?? throw new InvalidOperationException("角色未返回预览模型。");
    }

    internal static (string? Entry, string? Idle) ResolveAnimations(IReadOnlyList<string> names)
    {
        string? Find(params string[] aliases) => aliases.Select(alias => names.FirstOrDefault(name =>
            name.Equals(alias, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(name => name != null);
        return (Find("entry"), Find("idle_loop", "idle", "stand", "standing", "default", "animation", "relaxed_loop"));
    }

    public static void StartAnimations(Node root, string groupId, bool playEntry = true)
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
                            if (playEntry && plan.Entry != null)
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
