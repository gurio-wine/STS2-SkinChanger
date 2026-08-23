using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using System.Reflection;

namespace STS2SkinChanger.Core;

/// <summary>
/// Some code-backed character skins use a conventional Spine animation set but depend on their
/// DLL to translate the game's logical triggers. Their routing DLL is intentionally isolated, so
/// Skin Changer supplies the same capability generically while that provider is selected.
/// </summary>
internal static class ManagedCharacterAnimationBridge
{
    private static readonly MethodInfo? SetAnimationMethod = ResolveAnimationMethod("SetAnimation", 3);
    private static readonly MethodInfo? AddAnimationMethod = ResolveAnimationMethod("AddAnimation", 4);

    /// <summary>
    /// Starts otherwise-idle Spine nodes in a rebuilt character-select scene without binding the
    /// published DLL to MegaAnimationState.SetAnimation's return type. That return type differs
    /// between the supported main and beta game branches, while the native Spine call is stable.
    /// </summary>
    public static void TryStartCharacterSelectLoops(Node sceneRoot, string providerId)
    {
        foreach (var node in DescendantsAndSelf(sceneRoot).Where(node =>
                     node.GetClass().ToString().Equals("SpineSprite", StringComparison.Ordinal)))
        {
            try
            {
                var sprite = new MegaSprite(node);
                sceneRoot.RunWhenSpineReady(sprite, animationState =>
                    TryStartCharacterSelectLoop(sprite, animationState, providerId));
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"准备 {providerId} 的选角 Spine 动画失败：" +
                    exception.GetBaseException().Message);
            }
        }
    }

    public static void TryDrive(NCreature creature, string trigger)
    {
        try
        {
            var characterId = creature.Entity?.Player?.Character?.Id.Entry;
            if (string.IsNullOrWhiteSpace(characterId) ||
                !SkinService.ShouldDriveManagedCharacterAnimations(characterId))
            {
                return;
            }

            var spine = creature.Visuals?.SpineBody;
            if (spine == null)
            {
                return;
            }

            var route = GetRoute(trigger);
            if (route.Candidates.Length == 0)
            {
                return;
            }

            var animation = route.Candidates.FirstOrDefault(spine.HasAnimation);
            if (animation == null)
            {
                return;
            }

            var animationState = spine.GetAnimationState();
            if (SetAnimationMethod == null)
            {
                return;
            }

            SetAnimationMethod.Invoke(animationState, [animation, IsLooping(animation), 0]);
            if (!route.QueueIdle)
            {
                return;
            }

            var idle = ResolveIdleAnimation(creature, spine);
            if (idle != null &&
                AddAnimationMethod != null &&
                !idle.Equals(animation, StringComparison.OrdinalIgnoreCase))
            {
                AddAnimationMethod.Invoke(animationState, [idle, 0f, true, 0]);
            }
        }
        catch
        {
            // Animation routing is cosmetic. Never let an unusual provider skeleton interrupt combat.
        }
    }

    private static AnimationRoute GetRoute(string trigger) => trigger switch
    {
        "Attack" => new(["attack1", "attack", "attack_1"], QueueIdle: true),
        "Shiv" => new(["attack2", "attack_2", "attack1", "attack"], QueueIdle: true),
        "Cast" or "PowerUp" => new(["cast", "power_up", "attack1", "attack"], QueueIdle: true),
        "Dead" => new(["die", "death", "dead"], QueueIdle: false),
        "Hit" => new(["hurt", "hit"], QueueIdle: true),
        "Idle" or "Revive" => new(["low_health_loop", "idle_loop", "idle"], QueueIdle: false),
        "Relaxed" => new(["relaxed_loop", "idle_loop", "idle"], QueueIdle: false),
        _ => new([], QueueIdle: false)
    };

    private static string? ResolveIdleAnimation(NCreature creature, MegaSprite spine)
    {
        if (creature.Entity is { MaxHp: > 0 } entity &&
            entity.CurrentHp * 4 <= entity.MaxHp &&
            spine.HasAnimation("low_health_loop"))
        {
            return "low_health_loop";
        }

        return new[] { "idle_loop", "idle" }.FirstOrDefault(spine.HasAnimation);
    }

    private static bool IsLooping(string animation) =>
        animation.EndsWith("_loop", StringComparison.OrdinalIgnoreCase) ||
        animation.Equals("idle", StringComparison.OrdinalIgnoreCase);

    private static void TryStartCharacterSelectLoop(
        MegaSprite sprite,
        MegaAnimationState animationState,
        string providerId)
    {
        try
        {
            var animationNames = sprite.GetSkeleton()?.GetData()?.GetAnimationNames();
            if (animationNames == null || animationNames.Count == 0)
            {
                return;
            }

            var currentName = animationState.GetCurrentAnimationName(0);
            if (!string.IsNullOrWhiteSpace(currentName) &&
                animationNames.Any(name =>
                    name.Equals(currentName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var loopAnimation = FindAnimation(animationNames, "idle_loop") ??
                                FindAnimation(animationNames, "idle") ??
                                FindAnimation(animationNames, "default") ??
                                FindAnimation(animationNames, "animation") ??
                                animationNames.FirstOrDefault(name =>
                                    !name.Equals("Dummy", StringComparison.OrdinalIgnoreCase));
            if (loopAnimation == null)
            {
                return;
            }

            using var result = animationState.BoundObject.Call(
                "set_animation", loopAnimation, true, 0);
        }
        catch (Exception exception)
        {
            ModLog.Warn(
                $"启动 {providerId} 的选角 Spine 动画失败：" +
                exception.GetBaseException().Message);
        }
    }

    private static string? FindAnimation(
        IReadOnlyList<string> animationNames,
        string expectedName) =>
        animationNames.FirstOrDefault(name =>
            name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<Node> DescendantsAndSelf(Node root)
    {
        yield return root;
        foreach (Node child in root.GetChildren())
        {
            foreach (var descendant in DescendantsAndSelf(child))
            {
                yield return descendant;
            }
        }
    }

    private static MethodInfo? ResolveAnimationMethod(string name, int parameterCount) =>
        typeof(MegaAnimationState)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method =>
            {
                if (!method.Name.Equals(name, StringComparison.Ordinal) ||
                    method.GetParameters() is not { } parameters ||
                    parameters.Length != parameterCount)
                {
                    return false;
                }

                var expected = name.Equals("SetAnimation", StringComparison.Ordinal)
                    ? new[] { typeof(string), typeof(bool), typeof(int) }
                    : new[] { typeof(string), typeof(float), typeof(bool), typeof(int) };
                return parameters.Select(parameter => parameter.ParameterType).SequenceEqual(expected);
            });

    private sealed record AnimationRoute(string[] Candidates, bool QueueIdle);
}

[HarmonyPatch(typeof(NCreature), "_Ready")]
internal static class ManagedCharacterAnimationReadyPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCreature __instance) =>
        ManagedCharacterAnimationBridge.TryDrive(
            __instance,
            __instance.Entity?.IsDead == true ? "Dead" : "Idle");
}

[HarmonyPatch(typeof(NCreature), nameof(NCreature.SetAnimationTrigger))]
internal static class ManagedCharacterAnimationTriggerPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCreature __instance, string trigger) =>
        ManagedCharacterAnimationBridge.TryDrive(__instance, trigger);
}
