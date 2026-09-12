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

    public static int UnrelatedHits { get; private set; }

    public static void CountMerchant() => MerchantHits++;

    public static void CountUnrelated() => UnrelatedHits++;

    public static void Reset()
    {
        MerchantHits = 0;
        UnrelatedHits = 0;
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
        var unrelatedTarget = AccessTools.Method(
            typeof(SyntheticSfxCommand),
            nameof(SyntheticSfxCommand.Play));
        harmony.Patch(
            merchantTarget,
            postfix: new HarmonyMethod(package.GetMethod("OnMerchantReady")!));
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
            Require(gated == 1,
                "只接该分组的画面入口：该包在商人场景上的补丁必须被接管，音效命令等无关入口不能被算进去。");

            PartTakeoverScope.IsOptionSelected = (_, _) => false;
            SyntheticMerchantRoom.Ready();
            SyntheticSfxCommand.Play();
            Require(PartTakeoverScopeProbe.MerchantHits == 0 && PartTakeoverScopeProbe.UnrelatedHits == 1,
                "没有选中该部件时，作者自己的商人房间与商店背景补丁必须停用，其它补丁必须继续按作者原意运行。");

            PartTakeoverScope.IsOptionSelected = (groupId, optionId) =>
                groupId == "merchant" && optionId == "StyleLikeOverhaul";
            SyntheticMerchantRoom.Ready();
            SyntheticSfxCommand.Play();
            Require(PartTakeoverScopeProbe.MerchantHits == 1 && PartTakeoverScopeProbe.UnrelatedHits == 2,
                "选中该部件时必须恢复作者原意，并且不能连带影响无关入口。");
        }
        finally
        {
            PartTakeoverScope.ResetSelectionProbe();
            harmony.UnpatchAll(harmony.Id);
        }

        Console.WriteLine(
            "Part takeover scope passed: the package's own group presentation follows the group " +
            "selection while unrelated patches keep running.");
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
        EmitCallback(type, "OnUnrelated", nameof(PartTakeoverScopeProbe.CountUnrelated));
        return type.CreateType()!.GetTypeInfo();
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal static class SyntheticMerchantRoom
{
    public static void Ready()
    {
    }
}

internal static class SyntheticSfxCommand
{
    public static void Play()
    {
    }
}
