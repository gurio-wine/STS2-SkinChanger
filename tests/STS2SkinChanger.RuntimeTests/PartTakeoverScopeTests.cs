using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using STS2SkinChanger.Core;

/// <summary>
/// Public counters for the synthetic package below. They have to be public because the package's
/// callbacks are emitted into their own assembly, which has no access to test-internal types.
/// </summary>
public static class PartTakeoverScopeProbe
{
    public static int MerchantHits { get; private set; }

    public static int MerchantReadyHits { get; private set; }

    public static object? MerchantReadyScene { get; private set; }

    public static int UnrelatedHits { get; private set; }

    public static void CountMerchant() => MerchantHits++;

    public static void CountMerchantReady(SyntheticMerchantRoom scene)
    {
        MerchantReadyHits++;
        MerchantReadyScene = scene;
    }

    public static void CountUnrelated() => UnrelatedHits++;

    public static void Reset()
    {
        MerchantHits = 0;
        MerchantReadyHits = 0;
        MerchantReadyScene = null;
        UnrelatedHits = 0;
    }
}

public sealed class SyntheticMerchantRoom
{
    public static void Ready()
    {
    }

    public static void _Ready()
    {
    }
}

/// <summary>
/// A style/UI package that overrides a part of a group draws the rest of that group itself. Its own
/// patches for that group have to follow the group's selection, while everything else it patches
/// keeps running. The synthetic package below stands in for such a package: its callbacks are
/// emitted into their own assembly so the real game, the real Mod and the other suites are never
/// touched, and no provider initializer runs.
/// </summary>
internal static class PartTakeoverScopeTests
{
    internal static void Run()
    {
        PartTakeoverScopeProbe.Reset();
        var harmony = new Harmony("Gurio.SkinChanger.Tests.PartTakeoverScope");
        var package = BuildSyntheticPackage();
        var merchantTarget = AccessTools.Method(
            typeof(SyntheticMerchantRoom),
            nameof(SyntheticMerchantRoom.Ready));
        var merchantReadyTarget = AccessTools.Method(typeof(SyntheticMerchantRoom), "_Ready");
        var unrelatedTarget = AccessTools.Method(
            typeof(SyntheticSfxCommand),
            nameof(SyntheticSfxCommand.Play));
        harmony.Patch(
            merchantTarget,
            postfix: new HarmonyMethod(package.GetMethod("OnMerchantReady")!));
        harmony.Patch(
            merchantReadyTarget,
            postfix: new HarmonyMethod(package.GetMethod("OnMerchantReadyInstance")!));
        harmony.Patch(
            unrelatedTarget,
            postfix: new HarmonyMethod(package.GetMethod("OnUnrelated")!));
        try
        {
            var owner = new PartTakeoverOwner(
                "merchant",
                "StyleLikeOverhaul",
                null,
                PartTakeoverPolicy.ResolveResourceNamespaces("StyleLikeOverhaul", []));
            var gated = PartTakeoverScope.InstallGates(owner, [package.Assembly]);
            Require(gated == 2,
                "只接该分组的画面入口：该包在商人场景上的补丁必须被接管，音效命令等无关入口不能被算进去。");

            var scene = new SyntheticMerchantRoom();
            var presentation = PartTakeoverScope.ResolveScenePresentation(
                owner,
                typeof(SyntheticMerchantRoom));
            Require(
                presentation.Count == 1 &&
                presentation[0].Callback.Name == "OnMerchantReadyInstance" &&
                presentation[0].Target == merchantReadyTarget,
                "选择该部件后要能在已经建好的商店上重画作者的画面：只有该分组场景自己的 " +
                "_Ready 后置步骤算数，别的方法和别的入口都不能被当成画面重建。");

            PartTakeoverScope.IsOptionSelected = (_, _) => false;
            SyntheticMerchantRoom.Ready();
            SyntheticMerchantRoom._Ready();
            SyntheticSfxCommand.Play();
            Require(PartTakeoverScopeProbe.MerchantHits == 0 &&
                    PartTakeoverScopeProbe.MerchantReadyHits == 0 &&
                    PartTakeoverScopeProbe.UnrelatedHits == 1,
                "没有选中该部件时，作者自己的商人房间与商店背景补丁必须停用，其它补丁必须继续按作者原意运行。" +
                $"（商人={PartTakeoverScopeProbe.MerchantHits} 商店画面={PartTakeoverScopeProbe.MerchantReadyHits} " +
                $"无关={PartTakeoverScopeProbe.UnrelatedHits}）");

            PartTakeoverScope.IsOptionSelected = (groupId, optionId) =>
                groupId == "merchant" && optionId == "StyleLikeOverhaul";
            SyntheticMerchantRoom.Ready();
            SyntheticMerchantRoom._Ready();
            SyntheticSfxCommand.Play();
            Require(PartTakeoverScopeProbe.MerchantHits == 1 &&
                    PartTakeoverScopeProbe.MerchantReadyHits == 1 &&
                    PartTakeoverScopeProbe.UnrelatedHits == 2,
                "选中该部件时必须恢复作者原意，并且不能连带影响无关入口。");
            Require(ReplayPresentation(presentation, scene) == 1 &&
                    PartTakeoverScopeProbe.MerchantReadyHits == 2 &&
                    ReferenceEquals(PartTakeoverScopeProbe.MerchantReadyScene, scene),
                "选中该部件时，已经建好的商店要能用同一个场景实例重画作者的画面。");
        }
        finally
        {
            PartTakeoverScope.ResetSelectionProbe();
            harmony.UnpatchAll(harmony.Id);
        }

        Console.WriteLine(
            "Part takeover scope passed: the package's own group presentation follows the group " +
            "selection while unrelated patches keep running, and its drawing can be repeated on a " +
            "scene that is already standing.");
    }

    private static TypeInfo BuildSyntheticPackage()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("PartTakeoverSyntheticPackage"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("main");
        var type = module.DefineType(
            "Synthetic.Package.MerchantCallbacks",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        EmitCallback(type, "OnMerchantReady", nameof(PartTakeoverScopeProbe.CountMerchant));
        EmitInstanceCallback(type, "OnMerchantReadyInstance", nameof(PartTakeoverScopeProbe.CountMerchantReady));
        EmitCallback(type, "OnUnrelated", nameof(PartTakeoverScopeProbe.CountUnrelated));
        return type.CreateType()!.GetTypeInfo();
    }

    /// <summary>
    /// Mirrors the production replay: the recorded callback is repeated on the live scene with the
    /// arguments the policy builds for it.
    /// </summary>
    private static int ReplayPresentation(
        IReadOnlyList<PartTakeoverPatch> presentation,
        SyntheticMerchantRoom scene)
    {
        var replayed = 0;
        foreach (var patch in presentation)
        {
            if (!PartTakeoverPolicy.TryBuildPresentationArguments(
                    patch.Callback,
                    patch.Target,
                    scene,
                    out var arguments))
            {
                continue;
            }

            patch.Callback.Invoke(null, arguments);
            replayed++;
        }

        return replayed;
    }

    private static void EmitCallback(TypeBuilder type, string name, string counter)
    {
        var method = type.DefineMethod(
            name,
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            Type.EmptyTypes);
        var target = typeof(PartTakeoverScopeProbe).GetMethod(
            counter,
            BindingFlags.Public | BindingFlags.Static)!;
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitInstanceCallback(TypeBuilder type, string name, string counter)
    {
        var method = type.DefineMethod(
            name,
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [typeof(SyntheticMerchantRoom)]);
        method.DefineParameter(1, ParameterAttributes.None, "__instance");
        var target = typeof(PartTakeoverScopeProbe).GetMethod(
            counter,
            BindingFlags.Public | BindingFlags.Static)!;
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal static class SyntheticSfxCommand
{
    public static void Play()
    {
    }
}
