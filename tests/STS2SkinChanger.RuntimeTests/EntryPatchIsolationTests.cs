using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger;

internal static class EntryPatchIsolationTests
{
    // 目标方法不存在的坏补丁类：Patch() 阶段必然失败，模拟框架共存时目标解析失败的场景。
    [HarmonyPatch(typeof(ModelDb), "NoSuchMethodForPatchIsolationTest")]
    internal static class BrokenTargetPatch
    {
        private static void Prefix()
        {
        }
    }

    internal static void Run()
    {
        // 离线宿主里游戏 Log 的静态构造经 Logger.GetIsRunningFromGodotEditor 打到 Godot
        // 原生接口（OS.HasFeature/GetCmdlineArgs 连 OS 的静态构造都会崩）。在游戏托管层
        // 拦掉这个探测并显式跑完 Log 的静态构造后，失败路径的 ModLog.Error 才能安全触发；
        // 否则连 ModLog.Error 包装方法的 JIT 都会触发崩溃。
        var godotGuard = new Harmony("Gurio.SkinChanger.Tests.EntryPatchIsolation.GodotGuard");
        godotGuard.Patch(AccessTools.Method(typeof(Logger), "GetIsRunningFromGodotEditor"),
            prefix: new HarmonyMethod(typeof(EntryPatchIsolationTests), nameof(ReturnFalse)));
        // 探测桩让日志走 ConsoleLogPrinter，而它最终仍会调 Godot 原生输出；一并拦掉。
        godotGuard.Patch(AccessTools.Method(typeof(ConsoleLogPrinter), "Print"),
            prefix: new HarmonyMethod(typeof(EntryPatchIsolationTests), nameof(ReturnFalse)));
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Log).TypeHandle);

            // 好类 + 坏类混合安装：坏类必须被报告且不影响好类装上——一次 PatchAll 失败会
            // 放大成整个 Mod 停用，这正是与框架 Mod 共存时的启动健壮性要求。
            var harmony = new Harmony("Gurio.SkinChanger.Tests.EntryPatchIsolation");
            var failed = Entry.InstallPatchClasses(
                harmony,
                [typeof(EssentialInitializationPatch), typeof(BrokenTargetPatch), typeof(CosmeticLocalizationOwnershipPatch)]);
            Require(failed.Count == 1 && failed[0] == nameof(BrokenTargetPatch),
                $"坏目标补丁类应恰好失败一次并按类名报告，实际：[{string.Join("、", failed)}]。");
            Require(Harmony.GetPatchInfo(AccessTools.Method(typeof(OneTimeInitialization), nameof(OneTimeInitialization.ExecuteEssential)))!
                    .Owners.Contains(harmony.Id),
                "坏类失败后，好类的补丁必须仍然装上。");
            Require(Harmony.GetPatchInfo(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Modding.ModManager), nameof(MegaCrit.Sts2.Core.Modding.ModManager.GetModdedLocTables)))!
                    .Owners.Contains(harmony.Id),
                "排在坏类之后的好类也必须装上（安装不因单类失败中断）。");
            harmony.UnpatchAll(harmony.Id);
        }
        finally
        {
            godotGuard.UnpatchAll(godotGuard.Id);
        }

        Console.WriteLine("Entry 补丁逐类安装隔离测试 passed: 坏类跳过、好类保留、失败按类报告。");
    }

    private static bool ReturnFalse() => false;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
