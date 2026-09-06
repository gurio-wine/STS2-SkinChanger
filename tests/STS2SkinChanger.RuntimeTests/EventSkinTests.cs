using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using STS2SkinChanger;

internal static class EventSkinTests
{
    private static readonly Assembly Assembly = typeof(Entry).Assembly;
    private static readonly Type Catalog = Assembly.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;

    internal static void Run()
    {
        var previewPolicy = Assembly.GetType("STS2SkinChanger.Core.EventPreviewPolicy");
        Require(previewPolicy != null, "预览必须有不依赖事件执行逻辑的页面索引。");
        var pages = (string[])previewPolicy!.GetMethod("Pages")!.Invoke(null,
            ["ALPHA", new[] { "ALPHA.pages.NEXT.description", "ALPHA.pages.INITIAL.description",
                "ALPHA_BETA.pages.OTHER.description", "ALPHA.pages.NEXT.options.PAY.title" }])!;
        Require(pages.SequenceEqual(new[] { "INITIAL", "NEXT" }), "只列出本事件页面，初始页置顶。");
        var directory = Directory.CreateTempSubdirectory("sc-event-skins-");
        try
        {
            var baseline = new Dictionary<string, byte[]>();
            var provider = new Dictionary<string, byte[]>();
            baseline["res://scenes/events/background_scenes/modded_ancient.tscn"] =
                Encoding.UTF8.GetBytes("[gd_scene format=3]\n[node name=\"Background\" type=\"Control\"]\n");
            provider["res://scenes/events/background_scenes/modded_ancient.tscn"] =
                baseline["res://scenes/events/background_scenes/modded_ancient.tscn"];
            provider["res://scenes/vfx/events/alpha_vfx.tscn"] =
                Encoding.UTF8.GetBytes("[gd_scene format=3]\n[node name=\"Vfx\" type=\"Node2D\"]\n");
            foreach (var id in new[] { "alpha", "alpha_beta" })
            {
                AddTexture(baseline, $"res://images/events/{id}.png", 1);
                AddTexture(provider, $"res://images/events/{id}.png", 2);
                AddTexture(baseline, $"res://images/packed/vfx/event/{id}/glow.png", 3);
                AddTexture(provider, $"res://images/packed/vfx/event/{id}/glow.png", 4);
            }
            baseline["res://localization/eng/events.json"] = Json(new()
            {
                ["ALPHA.title"] = "Alpha", ["ALPHA.pages.INITIAL.description"] = "Base {Gold}",
                ["ALPHA.pages.INITIAL.options.PAY.title"] = "Pay", ["ALPHA.pages.NEXT.description"] = "Next",
                ["ALPHA_BETA.title"] = "Beta", ["ALPHA_BETA.pages.INITIAL.description"] = "Beta base",
                ["TEXT_ONLY.title"] = "Text only", ["TEXT_ONLY.pages.INITIAL.description"] = "Original",
                ["UNCHANGED.title"] = "Not a skin", ["UNCHANGED.pages.INITIAL.description"] = "Identical",
                ["FAKE_MERCHANT.title"] = "Merchant?",
                ["SHARED_EVENT_INFO"] = "Shared"
            });
            baseline["res://localization/zhs/events.json"] = Json(new() { ["ALPHA.title"] = "原版甲" });
            provider["res://skin/localization/eng/events.json"] = Json(new()
            {
                ["ALPHA.title"] = "Skin Alpha", ["ALPHA.pages.INITIAL.description"] = "Skin {Gold}",
                ["ALPHA.pages.INITIAL.options.PAY.title"] = "Offer", ["ALPHA.pages.NEXT.description"] = "Skin next",
                ["ALPHA_BETA.title"] = "Skin Beta", ["ALPHA_BETA.pages.INITIAL.description"] = "Skin beta body",
                ["TEXT_ONLY.pages.INITIAL.description"] = "New text", ["SHARED_EVENT_INFO"] = "Shared by provider"
                , ["FAKE_MERCHANT.title"] = "Disguised merchant"
            });
            provider["res://skin/localization/fra/events.json"] = Json(new()
            { ["UNCHANGED.title"] = "Not a skin", ["UNCHANGED.pages.INITIAL.description"] = "Identical" });
            provider["res://skin/localization/zhs/events.json"] = Json(new()
            { ["ALPHA.title"] = "事件外观甲" });
            var gamePath = Path.Combine(directory.FullName, "game.pck");
            var skinPath = Path.Combine(directory.FullName, "skin.pck");
            WritePack(gamePath, baseline);
            WritePack(skinPath, provider);
            using var catalog = Build(gamePath, skinPath);
            var groups = Items(Property(catalog, "Groups")).ToDictionary(g => (string)Property(g, "Id"));
            Require(groups.ContainsKey("event:alpha"), "普通事件图片必须创建独立皮肤分组。");
            Require(groups.ContainsKey("event:text_only"), "只有正文修改的事件也必须可选择。");
            Require(groups.ContainsKey("modded_ancient"), "新增事件功能不能破坏非原版先古的场景识别。");
            Require(groups.ContainsKey("fake_merchant_monster") && !groups.ContainsKey("event:fake_merchant"),
                "假商人的事件文本必须复用现有商人皮肤选择，不能新增一个互相冲突的分组。");
            Require(!groups.ContainsKey("event:unchanged"), "夹带的未修改语言条目不能被当成事件皮肤。");
            var alpha = Items(Property(groups["event:alpha"], "Options")).Single();
            var assets = Items(Property(alpha, "Assets")).Select(a => (string)Property(a, "Key")).ToArray();
            Require(assets.Contains("res://images/packed/vfx/event/alpha/glow.png"), "事件专属特效也应接管。");
            Require(!assets.Any(a => a.Contains("alpha_beta")), "较短事件 ID 不能抢走同前缀事件的资源。");
            Require((string?)Catalog.GetMethod("FindGroupIdForResourcePath")!.Invoke(catalog,
                ["res://scenes/vfx/events/alpha_vfx.tscn"]) == "event:alpha",
                "只替换特效贴图时，原生特效场景也必须走该事件的独立资源入口。");
            var selection = new Dictionary<string, string> { ["event:alpha"] = "skin" };
            var availability = Catalog.GetMethod("HasEventResource");
            Require(availability != null, "新增特效需要独立的存在性判定，不能依赖卸载不了的全局资源包。");
            Require((bool)availability!.Invoke(catalog, ["event:alpha", "skin", "res://scenes/vfx/events/alpha_vfx.tscn"])!,
                "选择提供特效的皮肤应启用特效。");
            Require(!(bool)availability.Invoke(catalog, ["event:alpha", "__base__", "res://scenes/vfx/events/alpha_vfx.tscn"])!,
                "切回原版后，新增特效不能残留。");
            var runtimeOverlay = Catalog.GetMethod("BuildRuntimeResourceOverlay")!;
            foreach (var choice in new[] { "skin", "__base__", "skin" })
            {
                var overlay = runtimeOverlay.Invoke(catalog, ["event:alpha", choice,
                    new[] { "res://images/events/alpha.png" }, "event-test/001", false, false])!;
                var files = (IReadOnlyDictionary<string, byte[]>)Property(overlay, "Files");
                var payloads = files.Where(pair => pair.Key.EndsWith(".ctex")).Select(pair => pair.Value).ToArray();
                Require(payloads.Length > 0 && payloads.All(payload => payload.SequenceEqual(
                        new byte[] { choice == "skin" ? (byte)2 : (byte)1 })),
                    "独立资源包必须携带当前皮肤自己的贴图，切回原版不能继续取已挂载的提供者贴图。");
            }
            Require(Resolve(catalog, "ALPHA.title", "eng", selection) == "Skin Alpha", "标题跟随所选皮肤。");
            Require(Resolve(catalog, "ALPHA.pages.INITIAL.description", "eng", selection) == "Skin {Gold}", "必须保留变量占位符。");
            Require(Resolve(catalog, "ALPHA.pages.INITIAL.options.PAY.title", "eng", selection) == "Offer", "选项标题也应切换。");
            Require(Resolve(catalog, "ALPHA.pages.NEXT.description", "eng", selection) == "Skin next", "后续页面也应切换。");
            Require(Resolve(catalog, "ALPHA_BETA.title", "eng", selection) == "Beta", "选择一个事件不能污染另一个事件。");
            Require(Resolve(catalog, "ALPHA.title", "zhs", selection) == "事件外观甲", "使用当前语言。");
            Require(Resolve(catalog, "ALPHA.pages.NEXT.description", "zhs", selection) == "Skin next", "缺项使用英文回退。");
            Require(Resolve(catalog, "SHARED_EVENT_INFO", "eng", selection) == null, "无事件归属的公共提示不得被皮肤选择修改。");
            Require(Resolve(catalog, "FAKE_MERCHANT.title", "eng", selection) == "Merchant?", "假商人原文也不能被强制替换。");
            selection["fake_merchant_monster"] = "skin";
            Require(Resolve(catalog, "FAKE_MERCHANT.title", "eng", selection) == "Disguised merchant", "假商人选择同时控制文本。");
            for (var i = 0; i < 4; i++)
            {
                selection["event:alpha"] = "__base__";
                Require(Resolve(catalog, "ALPHA.title", "eng", selection) == "Alpha", "切回原版必须恢复标题。");
                selection["event:alpha"] = "skin";
                Require(Resolve(catalog, "ALPHA.title", "eng", selection) == "Skin Alpha", "连续切换不应污染基线。");
            }
            CheckInstalledTextPatch(catalog);
            var gameplayPath = Path.Combine(directory.FullName, "gameplay.pck");
            WritePack(gameplayPath, new() { ["res://gameplay/localization/eng/events.json"] =
                Json(new() { ["ALPHA.title"] = "Gameplay Alpha" }) });
            using var withGameplay = Build(gamePath, skinPath, gameplayPath);
            Require(Resolve(withGameplay, "ALPHA.title", "zhs", new()) == "原版甲",
                "其它 Mod 的英文回退不能遮住原版当前语言文字。");
            Require(Resolve(withGameplay, "ALPHA.title", "eng", new()) == "Gameplay Alpha",
                "还原事件外观时仍应保留玩法 Mod 的合法文本。");
        }
        finally { directory.Delete(true); }
        Console.WriteLine("Event skin ownership passed: independent images, VFX, text-only, options, pages and fallback.");
    }

    private static void CheckInstalledTextPatch(object catalog)
    {
        var service = Assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var catalogProperty = service.GetProperty("Catalog")!;
        var configProperty = service.GetProperty("Config")!;
        var oldCatalog = catalogProperty.GetValue(null);
        var oldConfig = configProperty.GetValue(null);
        var config = Activator.CreateInstance(configProperty.PropertyType)!;
        var selections = (Dictionary<string, string>)Property(config, "Selections");
        var harmony = new HarmonyLib.Harmony("Gurio.SkinChanger.Tests.EventSkins");
        try
        {
            catalogProperty.SetValue(null, catalog);
            configProperty.SetValue(null, config);
            foreach (var name in new[] { "Core.EventSkinTextPatch", "Core.EventSkinVfxPatch",
                         "Core.EventSkinPortraitTrackingPatch", "Core.EventSkinResourceAvailabilityPatch", "Ui.EventPreviewLifecyclePatch" })
                harmony.CreateClassProcessor(Assembly.GetType("STS2SkinChanger." + name, true)!).Patch();
            // Deliberately contaminated table: the live read must still resolve the selected
            // event source, not whatever the last mounted provider merged into LocManager.
            var table = new MegaCrit.Sts2.Core.Localization.LocTable("events",
                new Dictionary<string, string> { ["ALPHA.title"] = "Mounted other skin",
                    ["ALPHA_BETA.title"] = "Mounted other event" });
            Require(table.GetRawText("ALPHA.title") == "Alpha", "实际语言表挂钩必须还原原版文字。");
            selections["event:alpha"] = "skin";
            Require(table.GetRawText("ALPHA.title") == "Skin Alpha", "实际语言表挂钩必须切到选中来源。");
            Require(table.GetRawText("ALPHA_BETA.title") == "Beta", "挂钩不能污染未选中的另一事件。");
            var unrelated = new MegaCrit.Sts2.Core.Localization.LocTable("cards",
                new Dictionary<string, string> { ["ALPHA.title"] = "Card title" });
            Require(unrelated.GetRawText("ALPHA.title") == "Card title", "相同键名的非事件表不应被改变。");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            catalogProperty.SetValue(null, oldCatalog);
            configProperty.SetValue(null, oldConfig);
        }
    }

    private static string? Resolve(object catalog, string key, string language, Dictionary<string, string> selections)
    {
        var method = Catalog.GetMethod("TryResolveEventText");
        Require(method != null, "事件文本缺少按键解析入口。");
        object?[] args = [key, language, selections, null];
        return (bool)method!.Invoke(catalog, args)! ? (string?)args[3] : null;
    }

    internal static void Audit(string game, string manifestPath)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var id = manifest.RootElement.GetProperty("id").GetString()!;
        var root = Path.GetDirectoryName(manifestPath)!;
        var pack = Path.Combine(root, id + ".pck");
        var type = Assembly.GetType("STS2SkinChanger.Catalog.SkinModDescriptor", true)!;
        var descriptors = Array.CreateInstance(type, 1);
        descriptors.SetValue(Activator.CreateInstance(type, id, id, pack, false, root, false, null), 0);
        using var catalog = (IDisposable)Catalog.GetMethod("Build")!.Invoke(null, [game, descriptors])!;
        var groups = Items(Property(catalog, "Groups"))
            .Where(group => ((string)Property(group, "Id")).StartsWith("event:")).ToArray();
        Require(groups.Length > 0, "实包没有识别出事件。");
        var selections = new Dictionary<string, string>();
        var changedTexts = 0;
        var resourceCount = 0;
        foreach (var group in groups)
        {
            var groupId = (string)Property(group, "Id");
            var key = groupId[6..].ToUpperInvariant() + ".pages.INITIAL.description";
            var original = Resolve(catalog, key, "zhs", selections);
            selections[groupId] = id;
            var selected = Resolve(catalog, key, "zhs", selections);
            selections.Remove(groupId);
            Require(Resolve(catalog, key, "zhs", selections) == original, "实包事件文本不能恢复。");
            if (original != selected) changedTexts++;
            var option = Items(Property(group, "Options")).Single();
            var paths = Items(Property(option, "Assets")).Select(pair => (string)Property(pair, "Key")).ToArray();
            resourceCount += paths.Length;
            Console.WriteLine($"{groupId}: resources={paths.Length}, initialTextChanged={original != selected}");
        }
        var probes = Items(Catalog.GetMethod("ProbeSkinProviders")!.Invoke(null, [descriptors, game])!).ToArray();
        Require(probes.Length == 1 && (int)Property(probes[0], "VisualGroupCount") > 0,
            "加载器探测必须识别事件来源，不能只有图鉴能看到。");
        Console.WriteLine($"Event provider audit: {groups.Length} events, {resourceCount} assets, {changedTexts} changed initial descriptions; probe recognized.");
    }

    private static IDisposable Build(string game, string skin, string? gameplay = null)
    {
        var type = Assembly.GetType("STS2SkinChanger.Catalog.SkinModDescriptor", true)!;
        var descriptors = Array.CreateInstance(type, gameplay == null ? 1 : 2);
        descriptors.SetValue(Activator.CreateInstance(type, "skin", "skin", skin, false,
            Path.GetDirectoryName(skin), false, null), 0);
        if (gameplay != null)
            descriptors.SetValue(Activator.CreateInstance(type, "gameplay", "gameplay", gameplay, true,
                Path.GetDirectoryName(gameplay), false, null), 1);
        return (IDisposable)Catalog.GetMethod("Build")!.Invoke(null, [game, descriptors])!;
    }

    private static void AddTexture(Dictionary<string, byte[]> files, string path, byte payload)
    {
        var imported = "res://.godot/imported/" + path[6..].Replace('/', '-') + ".ctex";
        files[path + ".import"] = Encoding.UTF8.GetBytes($"[remap]\npath=\"{imported}\"\n");
        files[imported] = [payload];
    }
    private static byte[] Json(Dictionary<string, string> values) => JsonSerializer.SerializeToUtf8Bytes(values);
    private static void WritePack(string path, Dictionary<string, byte[]> files) =>
        Assembly.GetType("STS2SkinChanger.Pck.PckArchive", true)!.GetMethod("Write")!.Invoke(null, [path, files]);
    private static object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
    private static IEnumerable<object> Items(object value) => ((IEnumerable)value).Cast<object>();
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
