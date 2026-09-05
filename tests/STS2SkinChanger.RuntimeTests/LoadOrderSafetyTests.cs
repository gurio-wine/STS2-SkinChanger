using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using STS2SkinChanger;

internal static class LoadOrderSafetyTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Action? _save;

    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var loader = assembly.GetType("STS2SkinChanger.Core.ManagedSkinModLoader", true)!;
        var controller = assembly.GetType("STS2SkinChanger.Ui.LoadOrderWarning.LoadOrderWarningController", true)!;
        var move = controller.GetMethod("MoveSelfBeforeSkinProviders", Static)!;
        var modsField = AccessTools.Field(typeof(ModManager), "_mods");
        var providersField = AccessTools.Field(loader, "<SkinProvidersInLoadOrder>k__BackingField");
        var mockSaveField = AccessTools.Field(typeof(SaveManager), "_mockInstance");
        var originalMods = modsField.GetValue(null);
        var originalProviders = providersField.GetValue(null);
        var originalSave = mockSaveField.GetValue(null);
        var saveManager = (SaveManager)RuntimeHelpers.GetUninitializedObject(typeof(SaveManager));
        var settingsManager = (SettingsSaveManager)RuntimeHelpers.GetUninitializedObject(typeof(SettingsSaveManager));
        var settings = (SettingsSave)RuntimeHelpers.GetUninitializedObject(typeof(SettingsSave));
        settingsManager.Settings = settings;
        AccessTools.Field(typeof(SaveManager), "_settingsSaveManager").SetValue(saveManager, settingsManager);
        var harmony = new Harmony("Gurio.SkinChanger.Tests.LoadOrderSafety");
        try
        {
            mockSaveField.SetValue(null, saveManager);
            // Run the real controller and native settings objects. Only the disk-write boundary
            // is replaced, so these tests never read or overwrite the user's game settings.
            harmony.Patch(AccessTools.Method(typeof(SettingsSaveManager), "SaveSettings"),
                prefix: new HarmonyMethod(typeof(LoadOrderSafetyTests), nameof(SaveWithoutDisk)));
            var self = Mod("Gurio.SkinChanger");
            var skin = Mod("Tests.Skin");
            var library = Mod("STS2-RitsuLib");
            skin.manifest!.dependencies = [new ModDependency("STS2-RitsuLib")];
            modsField.SetValue(null, new List<Mod> { skin, library, self });
            providersField.SetValue(null, new[] { skin });

            var retained = new SettingsSaveMod(self) { IsEnabled = false };
            var unrelated = new SettingsSaveMod { Id = "Tests.Other", IsEnabled = false };
            var missingTargetOrder = new List<SettingsSaveMod> { unrelated, retained };
            settings.ModSettings = new ModSettings { ModList = missingTargetOrder };
            var savedSnapshot = "unchanged";
            _save = () => savedSnapshot = string.Join(",", settings.ModSettings.ModList.Select(entry => entry.Id));
            ExpectFailure(() => move.Invoke(null, null), "无法在已保存的 Mod 顺序中定位皮肤 Mod");
            Require(ReferenceEquals(settings.ModSettings.ModList, missingTargetOrder) &&
                    missingTargetOrder.SequenceEqual(new[] { unrelated, retained }) && savedSnapshot == "unchanged",
                "排序找不到插入目标时必须保留原列表，不能先把 Skin Changer 从设置里移除。");

            settings.ModSettings = null;
            ExpectFailure(() => move.Invoke(null, null), "无法在已保存的 Mod 顺序中定位皮肤 Mod");
            Require(settings.ModSettings == null, "没有 Mod 设置时的失败不能留下半初始化设置。");
            settings.ModSettings = new ModSettings();

            var originalOrder = new List<SettingsSaveMod>
                { unrelated, new(library), new(skin), retained };
            settings.ModSettings.ModList = originalOrder;
            _save = () => throw new IOException("simulated settings write failure");
            ExpectFailure(() => move.Invoke(null, null), "simulated settings write failure");
            Require(ReferenceEquals(settings.ModSettings.ModList, originalOrder) &&
                    originalOrder.Select(entry => entry.Id).SequenceEqual(
                        new[] { "Tests.Other", "STS2-RitsuLib", "Tests.Skin", "Gurio.SkinChanger" }),
                "保存失败时必须恢复原排序，不能把未保存的顺序误报为成功。");

            _save = () => savedSnapshot = string.Join(",", settings.ModSettings.ModList.Select(entry => entry.Id));
            move.Invoke(null, null);
            Require(savedSnapshot == "Tests.Other,STS2-RitsuLib,Gurio.SkinChanger,Tests.Skin" &&
                    ReferenceEquals(settings.ModSettings.ModList[0], unrelated) &&
                    !settings.ModSettings.ModList[2].IsEnabled,
                "仅移动自身到皮肤前，保留前置的位置、其它项目和玩家的启用状态。");
            move.Invoke(null, null);
            Require(savedSnapshot == "Tests.Other,STS2-RitsuLib,Gurio.SkinChanger,Tests.Skin",
                "重复自动排序不能不断移动前置或产生重复项。");

            self.modSource = ModSource.ModsDirectory;
            settings.ModSettings.ModList = new List<SettingsSaveMod>
            {
                new() { Id = "Gurio.SkinChanger", Source = ModSource.SteamWorkshop, IsEnabled = false },
                unrelated,
                new() { Id = "Tests.Skin", Source = ModSource.ModsDirectory },
                new(self)
            };
            move.Invoke(null, null);
            Require(settings.ModSettings.ModList.Select(entry => entry.Id).SequenceEqual(
                        new[] { "Tests.Other", "Gurio.SkinChanger", "Tests.Skin" }) &&
                    settings.ModSettings.ModList[1].Source == ModSource.ModsDirectory &&
                    settings.ModSettings.ModList[1].IsEnabled,
                "正式版本地快照应保留实际来源的启用状态；旧设置来源不同仍按 ID 找到皮肤目标。");
            self.modSource = ModSource.SteamWorkshop;

            var required = loader.GetMethod("IsRequiredByAnotherMod", Static)!;
            Require((bool)required.Invoke(null, [library, ModManager.Mods])! &&
                    !(bool)loader.GetMethod("CanBypassOriginalLoader", Static)!.Invoke(null, [library])!,
                "被皮肤依赖的真实前置必须留给游戏加载，不能因附带美化资源而隔离 DLL。");

            // Check our saved order against each branch's actual sorter, not a reimplementation.
            var nativeSort = AccessTools.Method(typeof(ModManager), "SortModList");
            harmony.Patch(nativeSort, transpiler: new HarmonyMethod(typeof(LoadOrderSafetyTests), nameof(OmitNativeLogs)));
            settings.ModSettings.ModList = new List<SettingsSaveMod> { new(skin), new(library), new(self) };
            move.Invoke(null, null);
            nativeSort.Invoke(null, [settings.ModSettings.ModList]);
            Require(ModManager.Mods.Select(mod => mod.manifest!.id).SequenceEqual(
                    new[] { "Gurio.SkinChanger", "STS2-RitsuLib", "Tests.Skin" }),
                "SC 在前置之前不等于破坏皮肤依赖；游戏排序后前置仍须先于依赖它的皮肤加载。");
            Console.WriteLine("Load order safety passed: dependency order, library preservation, failure rollback and stable insertion.");
        }
        finally
        {
            _save = null;
            harmony.UnpatchAll(harmony.Id);
            modsField.SetValue(null, originalMods);
            providersField.SetValue(null, originalProviders);
            mockSaveField.SetValue(null, originalSave);
        }
    }

    private static Mod Mod(string id) => new()
    {
        path = "unused-load-order-test",
        manifest = new ModManifest { id = id, name = id, affectsGameplay = false },
        modSource = ModSource.SteamWorkshop
    };

    private static bool SaveWithoutDisk()
    {
        _save?.Invoke();
        return false;
    }

    private static IEnumerable<CodeInstruction> OmitNativeLogs(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method &&
                method.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Logging.Log" && method.Name == "Info")
            {
                Require(method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string), typeof(int) }),
                    "游戏日志签名变化，不能跳过未知调用。");
                instruction.operand = AccessTools.Method(typeof(LoadOrderSafetyTests), nameof(IgnoreLog));
            }
            yield return instruction;
        }
    }

    private static void IgnoreLog(string text, int skipFrames) { }

    private static void ExpectFailure(Action action, string message)
    {
        try { action(); }
        catch (Exception exception)
        {
            Require(exception.GetBaseException().Message.Contains(message, StringComparison.Ordinal),
                "失败原因不符：" + exception.GetBaseException());
            return;
        }
        throw new InvalidOperationException("操作失败却被当成成功：" + message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
