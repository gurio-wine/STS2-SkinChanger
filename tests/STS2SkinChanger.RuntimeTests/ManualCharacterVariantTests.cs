using System.Collections;
using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using STS2SkinChanger;

internal static class ManualCharacterVariantTests
{
    public static void Run()
    {
        var descriptors = Scan(typeof(ManualCharacterVariantTests).Assembly.Location);
        Require(descriptors.Any(d => Property(d, "FieldName") == "withoutHat"), "实例部件开关必须成为可保存的差分。");
        Require(descriptors.Any(d => Property(d, "FieldName") == "AlternatePalette"), "静态配置/调色板开关必须成为差分。");
        Require(descriptors.All(d => Property(d, "FieldName") != "DebugEnabled"), "不能把调试或非外观开关变成皮肤。");
        var slot = descriptors.Single(d => Property(d, "FieldName") == "withoutHat");
        Require(((IEnumerable)slot.GetType().GetProperty("SourceSlots")!.GetValue(slot)!).Cast<string>().SequenceEqual(["hat"]),
            "离线扫描必须保留准确部件列表，不扫描时执行作者构造函数。");
        VerifyMetadataRestore();
        var controls = typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.ContextualSkinControls", true)!;
        var replay = AccessTools.Method(controls, "ReplaySelectedCharacterPresentation");
        Require(PatchProcessor.GetOriginalInstructions(replay).Any(i => i.operand is MethodInfo method &&
            method.Name == "GetSelectedCreatureRuntimeProvider"), "选角回调不能只对全局联动包开放。");
        Console.WriteLine("Manual character variants passed: slot alpha and persisted shader palette, no debug switches.");
    }

    public static void AuditCatalog(string gamePack, string root)
    {
        var assembly = typeof(Entry).Assembly;
        var catalogType = assembly.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
        var descriptorType = assembly.GetType("STS2SkinChanger.Catalog.SkinModDescriptor", true)!;
        var manifestPath = Directory.GetFiles(root, "*.json").Single();
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var id = manifest.RootElement.GetProperty("id").GetString()!;
        var descriptors = Array.CreateInstance(descriptorType, 1);
        descriptors.SetValue(Activator.CreateInstance(descriptorType, id, id, Path.Combine(root, id + ".pck"), false, root, true, null), 0);
        using var catalog = (IDisposable)catalogType.GetMethod("Build")!.Invoke(null, [gamePack, descriptors])!;
        var groups = ((IEnumerable)catalogType.GetProperty("Groups")!.GetValue(catalog)!).Cast<object>().ToArray();
        var variants = groups.SelectMany(group => ((IEnumerable)group.GetType().GetProperty("Options")!.GetValue(group)!).Cast<object>()
            .Where(option => option.GetType().GetProperty("ManualCharacterVariant")!.GetValue(option) != null)
            .Select(option => (Group: Property(group, "Id")!, Option: option))).ToArray();
        Require(variants.Length == 2 && variants.Select(v => v.Group).Distinct().Count() == 1, "只能给已证实所属角色添加两个差分。");
        Require(variants.Any(v => Property(v.Option, "Id") == id), "原选项 ID 必须保留，已有选择不能失效。");
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var configType = assembly.GetType("STS2SkinChanger.Core.SkinConfig", true)!;
        var oldCatalog = AccessTools.Property(service, "Catalog").GetValue(null);
        var oldConfig = AccessTools.Property(service, "Config").GetValue(null);
        var loaded = AccessTools.Field(service, "_configLoaded");
        var oldLoaded = loaded.GetValue(null);
        var config = Activator.CreateInstance(configType)!;
        var selections = (IDictionary)configType.GetProperty("Selections")!.GetValue(config)!;
        try
        {
            AccessTools.Property(service, "Catalog").SetValue(null, catalog);
            AccessTools.Property(service, "Config").SetValue(null, config);
            loaded.SetValue(null, true);
            var sampleMode = variants[0].Option.GetType().GetProperty("ManualCharacterVariant")!.GetValue(variants[0].Option)!;
            var sampleState = sampleMode.GetType().GetProperty("State")!.GetValue(sampleMode)!;
            if (!(bool)sampleState.GetType().GetProperty("IsStatic")!.GetValue(sampleState)!)
            {
                var legacy = Activator.CreateInstance(assembly.GetType("STS2SkinChanger.Core.SlotVisibilitySelection", true)!,
                    variants[0].Group, id, Property(sampleState, "Id")!, true, sampleState.GetType().GetProperty("SourceSlots")!.GetValue(sampleState)!);
                ((IList)configType.GetProperty("SlotVisibilitySelections")!.GetValue(config)!).Add(legacy);
                selections[variants[0].Group] = id;
                var migrate = AccessTools.Method(service, "MigrateLegacyManualSlots");
                Require((int)migrate.Invoke(null, [config, catalog])! == 1 && (string)selections[variants[0].Group]! != id,
                    "升级时旧隐藏头骨的选择必须迁移到差分 2。");
                selections[variants[0].Group] = id;
                Require((int)migrate.Invoke(null, [config, catalog])! == 0 && (string)selections[variants[0].Group]! == id,
                    "旧开关记录不能在下次启动时覆盖后来选择的差分 1。");
            }
            foreach (var (group, option) in variants)
            {
                var optionId = Property(option, "Id")!;
                selections[group] = optionId;
                Require((string?)AccessTools.Method(service, "GetSelectedCreatureRuntimeProvider").Invoke(null, [group]) == id,
                    "差分 ID 必须解析为原提供者，不能丢失骨骼/模型回调。");
                var mode = AccessTools.Method(service, "GetSelectedManualCharacterVariant").Invoke(null, [group])!;
                var state = mode.GetType().GetProperty("State")!.GetValue(mode)!;
                var expected = optionId != id;
                Require((bool)mode.GetType().GetProperty("Value")!.GetValue(mode)! == expected, "列表选择必须决定实际状态。");
                if (!(bool)state.GetType().GetProperty("IsStatic")!.GetValue(state)!)
                {
                    var slotStates = ((IEnumerable)AccessTools.Method(service, "GetSlotVisibilitySelections").Invoke(null, [group, id])!).Cast<object>().ToArray();
                    Require(slotStates.Length == 1 && (bool)slotStates[0].GetType().GetProperty("Hidden")!.GetValue(slotStates[0])! == expected,
                        "显隐差分必须覆盖战斗/小预览的部件状态。");
                }
                var other = variants.First(v => Property(v.Option, "Id") != optionId);
                Require((bool)AccessTools.Method(service, "IsSameManualVariantSource").Invoke(null, [group, Property(other.Option, "Id")])!,
                    "同一套资源的差分不能重复重挂完整 PCK。");
                var resourceKey = AccessTools.Method(service, "RuntimeResourceKey", [typeof(string), typeof(string), typeof(string)]);
                Require(Equals(resourceKey.Invoke(null, [group, optionId, "res://fixture.tscn"]),
                    resourceKey.Invoke(null, [group, Property(other.Option, "Id"), "res://fixture.tscn"])),
                    "手动配色/部件差分必须共用资源缓存，但不能合并不同提供者。");
                Require(AccessTools.Method(service, "GetSelectedManualCharacterVariant").Invoke(null, ["ironclad"]) == null,
                    "差分不能进入其它角色的状态。");
            }
            Console.WriteLine($"Actual variant catalog passed: {id}; two options, one character, provider/state/cache isolation.");
        }
        finally
        {
            AccessTools.Property(service, "Catalog").SetValue(null, oldCatalog);
            AccessTools.Property(service, "Config").SetValue(null, oldConfig);
            loaded.SetValue(null, oldLoaded);
        }
    }

    private static void VerifyMetadataRestore()
    {
        var policy = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.PresentationMetadataChanges", true)!;
        var capture = AccessTools.Method(policy, "Capture").MakeGenericMethod(typeof(bool));
        var changes = ((IEnumerable)capture.Invoke(null, [new Dictionary<string, bool> { ["existing"] = false },
            new Dictionary<string, bool> { ["existing"] = true, ["injected"] = true }])!).Cast<object>().ToArray();
        Require(changes.Length == 2, "作者按钮的注入标记必须和新增 UI 一起跟踪。");
        var check = AccessTools.Method(policy, "CanRestore").MakeGenericMethod(typeof(bool));
        foreach (var change in changes)
        {
            Require((bool)check.Invoke(null, [change, true, true])!, "自己的注入标记必须可恢复，以便切回时重新生成按钮。");
            Require(!(bool)check.Invoke(null, [change, true, false])!, "不能覆盖其它代码后来修改的标记。");
        }
    }

    public static void Audit(string path)
    {
        var descriptors = Scan(path);
        Require(descriptors.Length == 1, "实包应精确识别一个手动外观状态。");
        foreach (var descriptor in descriptors)
            Console.WriteLine($"Verified manual variant: {Property(descriptor, "TypeName")}:{Property(descriptor, "FieldName")}");
    }

    private static object[] Scan(string path)
    {
        var type = typeof(Entry).Assembly.GetType("STS2SkinChanger.Catalog.ManualCharacterVariantScanner")
            ?? throw new InvalidOperationException("手动外观状态尚未接入皮肤差分目录。");
        return ((IEnumerable)type.GetMethod("ScanAssembly", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [path])!)
            .Cast<object>().ToArray();
    }
    private static string? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)?.ToString();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static class PaletteFixture
    {
        public static bool AlternatePalette;
        public static bool DebugEnabled;
        public static void Bind(Button button) => button.Pressed += Toggle;
        private static void Toggle() { AlternatePalette = !AlternatePalette; Save(); }
        private static void Save()
        {
            var config = new ConfigFile();
            config.SetValue("skin", "palette", AlternatePalette);
            config.Save("user://fixture.cfg");
        }
        public static void Paint(ShaderMaterial material) => material.SetShaderParameter("palette_lut", AlternatePalette ? 1 : 0);
        public static void DebugToggle() => DebugEnabled = !DebugEnabled;
    }
}
