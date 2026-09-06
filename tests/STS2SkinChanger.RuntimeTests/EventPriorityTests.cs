using System.Collections;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using STS2SkinChanger;

internal static class EventPriorityTests
{
    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var policy = assembly.GetType("STS2SkinChanger.Core.EventSkinPriorityPolicy");
        Require(policy != null, "事件地区优先级需要独立配置和解析规则，不能污染怪物地区预设。");
        var entryType = assembly.GetType("STS2SkinChanger.Core.EventSkinPriorityEntry", true)!;
        var listType = typeof(List<>).MakeGenericType(entryType);
        var stored = (IList)Activator.CreateInstance(listType)!;
        object Entry(string id, bool enabled) => Activator.CreateInstance(entryType, id, enabled)!;
        stored.Add(Entry("missing", false));
        stored.Add(Entry("A", true));
        stored.Add(Entry("a", false));
        stored.Add(Entry("B", true));
        var entries = AccessTools.Method(policy, "Entries").Invoke(null, [stored, new[] { "A", "B", "C" }])!;
        var ids = ((IEnumerable)entries).Cast<object>().Select(e => (string)AccessTools.Property(entryType, "OptionId").GetValue(e)!).ToArray();
        Require(ids.SequenceEqual(new[] { "missing", "A", "B", "C" }), "已有次序和暂时卸载的皮肤要保留，新增皮肤默认启用，ID 不区分大小写去重。");
        string Resolve(string[] available) => (string)AccessTools.Method(policy, "Resolve").Invoke(null, [entries, available])!;
        Require(Resolve(["a", "B"]) == "a" && Resolve(["B", "C"]) == "B" && Resolve(["C"]) == "C" &&
                Resolve(["missing"]) == "__base__" && Resolve([]) == "__base__",
            "每个事件只选第一个启用且覆盖它的皮肤，无覆盖回原版，不叠加其它来源的文本或图片。");
        var configType = assembly.GetType("STS2SkinChanger.Core.SkinConfig", true)!;
        var config = AccessTools.Method(configType, "Deserialize").Invoke(null, ["{\"EventSkinPriorities\":null}"])!;
        var settingsProperty = AccessTools.Property(configType, "EventSkinPriorities");
        Require(settingsProperty != null && settingsProperty.GetValue(config) != null, "旧配置/空配置应补齐独立事件设置。");
        var settings = settingsProperty!.GetValue(config)!;
        var type = settings.GetType();
        var regions = (IDictionary)AccessTools.Property(type, "RegionGroups").GetValue(settings)!;
        regions.Add("act1", new List<string> { "event:a", "event:b" });
        ((IDictionary)AccessTools.Property(type, "Priorities").GetValue(settings)!).Add("act1", entries);
        ((IList)AccessTools.Property(type, "ManualGroups").GetValue(settings)!).Add("event:b");
        var json = JsonSerializer.Serialize(config, configType);
        var loaded = AccessTools.Method(configType, "Deserialize").Invoke(null, [json])!;
        var roundTrip = settingsProperty.GetValue(loaded)!;
        Require((bool)AccessTools.Method(type, "IsFollowing").Invoke(roundTrip, ["event:a"])! &&
                !(bool)AccessTools.Method(type, "IsFollowing").Invoke(roundTrip, ["event:b"])!,
            "跟随地区和手动单选必须在重启后保留。");
        var clone = AccessTools.Method(configType, "CloneForBundleTransaction").Invoke(config, null)!;
        var clonedSettings = settingsProperty.GetValue(clone)!;
        ((IList)AccessTools.Property(type, "ManualGroups").GetValue(clonedSettings)!).Clear();
        Require(!ReferenceEquals(settings, clonedSettings) && ((IList)AccessTools.Property(type, "ManualGroups").GetValue(settings)!).Count == 1,
            "事务回滚必须隔离事件配置，不能共享可变列表。");
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var runtime = assembly.GetType("STS2SkinChanger.Core.EventSkinRuntime", true)!;
        Require(Calls(AccessTools.Method(service, "ChangeEventPriorityConfiguration"), service, "MountOverlay") &&
                Calls(AccessTools.Method(service, "ChangeEventPriorityConfiguration"), runtime, "RefreshCurrent"),
            "批量优先级必须挂载资源并刷新局内事件，但不能重启事件逻辑。");
        Console.WriteLine("Event priorities passed: fallback, disabled providers, persistence, manual override and runtime wiring.");
    }

    internal static void CheckCatalog(object catalog)
    {
        var assembly = typeof(Entry).Assembly;
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var catalogProperty = AccessTools.Property(service, "Catalog");
        var configProperty = AccessTools.Property(service, "Config");
        var previousCatalog = catalogProperty.GetValue(null);
        var previousConfig = configProperty.GetValue(null);
        try
        {
            catalogProperty.SetValue(null, catalog);
            var json = """
                {"Selections":{"event:alpha":"skin","event:alpha_beta":"skin","ironclad":"keep"},
                 "EventSkinPriorities":{"RegionGroups":{"act1":["event:alpha"],"act2":["event:alpha_beta"]},
                  "Priorities":{"act1":[{"OptionId":"skin","Enabled":false}],"act2":[{"OptionId":"skin","Enabled":true}]}}}
                """;
            var config = AccessTools.Method(configProperty.PropertyType, "Deserialize").Invoke(null, [json])!;
            configProperty.SetValue(null, config);
            IReadOnlyDictionary<string, string> Updates() => (IReadOnlyDictionary<string, string>)
                AccessTools.Method(service, "BuildEventPrioritySelectionUpdates").Invoke(null, null)!;
            var updates = Updates();
            Require(updates.Count == 1 && updates["event:alpha"] == "__base__", "禁用本地区不能把另一地区或角色的选择一起改掉。");
            var settings = AccessTools.Property(config.GetType(), "EventSkinPriorities").GetValue(config)!;
            var manual = (IList)AccessTools.Property(settings.GetType(), "ManualGroups").GetValue(settings)!;
            manual.Add("event:alpha");
            Require(Updates().Count == 0, "单个事件手动选择必须优先于地区禁用。");
            manual.Clear();
            var selections = (Dictionary<string, string>)AccessTools.Property(config.GetType(), "Selections").GetValue(config)!;
            foreach (var update in Updates()) selections[update.Key] = update.Value;
            Require(Updates().Count == 0, "重复进入图鉴不应反复挂载没有变化的资源。");
            var priorities = (IDictionary)AccessTools.Property(settings.GetType(), "Priorities").GetValue(settings)!;
            var entries = (IList)priorities["act1"]!;
            entries[0] = Activator.CreateInstance(entries[0]!.GetType(), "skin", true)!;
            Require(Updates().Count == 1 && Updates()["event:alpha"] == "skin", "重新启用应恢复同一事件的独立皮肤。");
            Require(selections["ironclad"] == "keep" && selections["event:alpha_beta"] == "skin",
                "计算事务时不可提前修改其它选择。");
        }
        finally { catalogProperty.SetValue(null, previousCatalog); configProperty.SetValue(null, previousConfig); }
    }
    private static bool Calls(MethodBase method, Type type, string name) => PatchProcessor.GetOriginalInstructions(method)
        .Any(i => i.operand is MethodInfo m && m.DeclaringType == type && m.Name == name);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
