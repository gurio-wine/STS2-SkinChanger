using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Entities.Characters;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using STS2SkinChanger;

internal static class AnimatorPatchDiscoveryTests
{
    internal static void Run()
    {
        var patch = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.FrameworkEntryAnimationPatch", true)!;
        var enumerate = patch.GetMethod("TargetMethods", BindingFlags.Static | BindingFlags.NonPublic)!;
        // Discovery must scan the real loaded character types. A name-only lookup throws on
        // OverloadedAnimatorCharacter, exactly like the reported mod, before Harmony can install
        // any of SC's UI hooks. No character, Godot node or provider initializer is constructed.
        MethodBase[] targets;
        try
        {
            targets = ((IEnumerable<MethodBase>)enumerate.Invoke(null, null)!).ToArray();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "角色声明同名动画重载时，SC 的补丁目标扫描不能失败。", exception);
        }

        var fixtureEntry = typeof(AnimatorFixtureCharacter).GetMethod("GenerateAnimator", [typeof(MegaSprite)])!;
        var contextualEntry = typeof(AnimatorFixtureCharacter).GetMethod("GenerateAnimator", [typeof(MegaSprite), typeof(Creature)])!;
        var gameEntry = typeof(Ironclad).GetMethods().Single(method => method.Name == "GenerateAnimator");
        gameEntry = (MethodInfo)MethodBase.GetMethodFromHandle(gameEntry.MethodHandle, gameEntry.DeclaringType!.TypeHandle)!;
        Require(targets.Count(method => method.MethodHandle == fixtureEntry.MethodHandle) == 1,
            "多个角色继承同一动画入口时，只安装一次且不能漏掉该入口。");
        Require(targets.Count(method => method.MethodHandle == contextualEntry.MethodHandle) == 1,
            "带 Creature 上下文的动画入口也必须保留，不能只支持正式版的单参数签名。");
        Require(targets.Any(method => method.MethodHandle == gameEntry.MethodHandle),
            "不能因为扩展角色有重载而漏掉原版角色的动画入口。");
        Require(targets.All(method => method is MethodInfo info && !info.IsStatic && !info.IsAbstract &&
                                    !info.ContainsGenericParameters &&
                                    typeof(CreatureAnimator).IsAssignableFrom(info.ReturnType) &&
                                    (info.GetParameters().Select(parameter => parameter.ParameterType)
                                         .SequenceEqual([typeof(MegaSprite)]) ||
                                     info.GetParameters().Select(parameter => parameter.ParameterType)
                                         .SequenceEqual([typeof(MegaSprite), typeof(Creature)]))),
            "补丁只能作用于真正的游戏动画入口，不能绑定辅助、泛型或参数类型不符的方法。");
        Require(!targets.Any(method => method.DeclaringType == typeof(OverloadedAnimatorCharacter)),
            "同名辅助重载不能被视为游戏动画入口。");

        var harmony = new Harmony("Gurio.SkinChanger.Tests.AnimatorDiscovery");
        try
        {
            harmony.CreateClassProcessor(patch).Patch();
            foreach (var entry in new[] { fixtureEntry, contextualEntry, gameEntry })
                Require(Harmony.GetPatchInfo(entry)?.Postfixes.Any(info => info.owner == harmony.Id) == true,
                    "目标枚举通过后，还必须能实际安装 SC 的动画后置补丁。");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
        Console.WriteLine("Animator patch discovery passed: overloads, generic helpers, inherited entries and real Harmony installation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Real CharacterModel subclasses, with the required model properties stubbed because the
    // test only discovers/patches methods. In particular, no ModelDb registration is performed.
    private class AnimatorFixtureCharacter : CharacterModel
    {
        public override Color NameColor => default;
        public override CharacterGender Gender => default;
        protected override CharacterModel? UnlocksAfterRunAs => null;
        public override int StartingHp => 1;
        public override int StartingGold => 0;
        public override CardPoolModel CardPool => null!;
        public override RelicPoolModel RelicPool => null!;
        public override PotionPoolModel PotionPool => null!;
        public override IEnumerable<CardModel> StartingDeck => [];
        public override IReadOnlyList<RelicModel> StartingRelics => [];
        public override float AttackAnimDelay => 0;
        public override float CastAnimDelay => 0;
        public override List<string> GetArchitectAttackVfx() => [];

        // Keep both real game contracts in the fixture regardless of which game DLL this runner
        // references. Exactly one shadows the inherited version; the other is an additional overload.
#pragma warning disable CS0109
        [MethodImpl(MethodImplOptions.NoInlining)]
        public new virtual CreatureAnimator GenerateAnimator(MegaSprite controller) => null!;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public new virtual CreatureAnimator GenerateAnimator(MegaSprite controller, Creature creature) => null!;
#pragma warning restore CS0109
    }

    private sealed class OverloadedAnimatorCharacter : AnimatorFixtureCharacter
    {
        public CreatureAnimator GenerateAnimator(MegaSprite controller, int helperOption) => null!;
        public CreatureAnimator GenerateAnimator<T>(MegaSprite controller) => null!;
        public static CreatureAnimator GenerateAnimator(string helperOption) => null!;
    }

    private sealed class InheritedAnimatorCharacter : AnimatorFixtureCharacter;

    private sealed class WrongReturnAnimatorCharacter : AnimatorFixtureCharacter
    {
        public new object GenerateAnimator(MegaSprite controller) => new();
    }

    private sealed class OpenGenericAnimatorCharacter<T> : AnimatorFixtureCharacter
    {
        public override CreatureAnimator GenerateAnimator(MegaSprite controller) => null!;
    }
}
