using System.Reflection;
using STS2SkinChanger;

internal static class WorkshopTests
{
    public static void Run()
    {
        WorkshopMetadataTests.Run();
        var policy = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopCatalogPolicy");
        Require(policy != null, "缺少工坊清单验证和按目标过滤的生产实现。");
        object Call(string method, params object[] args) => policy!.GetMethod(method)!.Invoke(null, args)!;
        const string json = """
            [{"id":123,"targets":[{"kind":"character","target":"silent"},{"kind":"cards","target":"regent"}]},
             {"id":124,"targets":[{"kind":"cards","target":"silent"}]},
             {"id":123,"targets":[{"kind":"monster","target":"jaw_worm"}]},
             {"id":0,"targets":[{"kind":"cards","target":"silent"}]}]
            """;
        Require(((ulong[])Call("FilterIds", json, "cards", "silent")).SequenceEqual(new ulong[] { 124 }),
            "不能把角色皮肤标签错当同角色卡牌皮肤；过滤必须匹配同一个类型/目标组合。");
        Require(((ulong[])Call("FilterIds", json, "", "")).SequenceEqual(new ulong[] {123, 124}),
            "ID 去重合并标签、拒绝零 ID，保持清单顺序。");
        Require(((ulong[])Call("FilterIds", json, "monster", "jaw_worm")).SequenceEqual(new ulong[] {123}),
            "重复 ID 的其它标签不能丢失。");
        Require(!(bool)Call("IsSkinChoice", "__workshop__") && (bool)Call("IsSkinChoice", "skin:a"),
            "工坊命令不能进入真实皮肤的循环/随机池。");
        var random = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.RandomCharacterSkinPolicy")!;
        var drawn = random.GetMethod("Draw")!.Invoke(null, [new[] { "skin:a", "__workshop__" }, (Func<int,int>)(n => n - 1)]);
        Require((string)drawn! == "skin:a", "随机抽取必须跳过工坊命令，不能保存伪皮肤 ID。");
        Require(!(bool)Call("CanUseInstalledFiles", true, true, false, false) &&
                !(bool)Call("CanUseInstalledFiles", true, false, true, false) &&
                !(bool)Call("CanUseInstalledFiles", true, false, false, true) &&
                (bool)Call("CanUseInstalledFiles", true, false, false, false),
            "正在更新/下载/排队的旧文件不能标记为可用。");
        var marked = (Array)Call("Parse", json.Replace("\"id\":123", "\"id\":123,\"restartRequired\":true"));
        Require((bool)marked.GetValue(0)!.GetType().GetProperty("RestartRequired")!.GetValue(marked.GetValue(0))!, "合并重复 ID 不能丢失已审计的重启标记。");
        var unspecified = (Array)Call("Parse", json);
        Require(unspecified.Cast<object>().All(item => (bool)item.GetType().GetProperty("RestartRequired")!.GetValue(item)!),
            "导出或漏填重启要求不能自动承诺免重启；只有完整检查通过才能明确填写 false。");
        var png = new byte[24];
        new byte[] {137,80,78,71,13,10,26,10}.CopyTo(png, 0);
        new byte[] {73,72,68,82}.CopyTo(png, 12);
        png[19] = 144; png[23] = 100;
        Require(policy!.GetMethod("IsSafeCover") != null, "封面解码之前需要校验尺寸，不能只在分配后拒绝超大图。");
        Require((bool)Call("IsSafeCover", png), "允许尺寸正常的 PNG 头。");
        png[16] = 127;
        Require(!(bool)Call("IsSafeCover", png) && !(bool)Call("IsSafeCover", new byte[3]), "拒绝超大或截断的封面头。");
        var texts = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopText")!;
        var key = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopTextKey")!;
        foreach (var language in new[] { "eng", "zhs", "zht", "deu", "esp", "spa", "fra", "ita", "jpn", "kor", "pol", "ptb", "rus", "tha", "tur" })
        foreach (var value in Enum.GetValues(key))
            Require(!string.IsNullOrWhiteSpace((string)texts.GetMethod("ForLanguage")!.Invoke(null, [language, value])!), $"{language} 缺少工坊文本 {value}");
        CheckResourceCoverage();
        CheckNativeNoticeScope();
        CheckPanelLifecycle();
        CheckPackageCapabilities();
        CheckReasonTexts();
        CheckBrowserNavigation();
        WorkshopBrowserInteractionTests.Run();
        using var catalogResource = typeof(Entry).Assembly.GetManifestResourceStream("STS2SkinChanger.Data.workshop-catalog.json");
        Require(catalogResource != null, "发布 DLL 缺少内置清单，不能依赖开发机器的文件。");
        using var reader = new StreamReader(catalogResource!);
        var bundled = (ulong[])Call("FilterIds", reader.ReadToEnd(), "", "");
        Require(bundled.Length > 0 && bundled.Distinct().Count() == bundled.Length && !bundled.Contains(3787302680UL), "工坊浏览器不能收录自身或重复物品。");
        Console.WriteLine("Workshop policy tests passed.");
    }
    private static void CheckPackageCapabilities()
    {
        var policy = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopPackagePolicy");
        Require(policy != null, "需要按实际版本、已加载前置和代码能力检查工坊包，而不是一律要求重启。");
        using var package = new WorkshopPackageFixture();
        string Assess(string json, string version, Dictionary<string, string> loaded)
        {
            File.WriteAllText(Path.Combine(package.Path, "skin.json"), json);
            var result = policy!.GetMethod("Assess")!.Invoke(null, [package.Path, version, loaded])!;
            return result.GetType().GetProperty("Reason")!.GetValue(result)!.ToString()!;
        }
        const string manifest = """{"id":"skin","has_pck":true,"has_dll":false,"affects_gameplay":false,"min_game_version":"0.107.0","dependencies":[{"id":"BaseLib","min_version":"1.0.0"}]}""";
        Require(Assess(manifest, "0.111.0", new() { ["BaseLib"] = "1.1.0" }) == "None", "已经加载且版本符合的前置不应阻止纯资源热加载。");
        Require(Assess(manifest, "0.111.0", new()) == "Dependency", "未加载的前置不能假装已满足。");
        Require(Assess(manifest, "0.106.0", new() { ["BaseLib"] = "1.1.0" }) == "Version", "游戏版本不匹配不能只提示重启。");
        Require(Assess(manifest, "0.111.0", new() { ["BaseLib"] = "0.9.0" }) == "Dependency", "不能忽略前置的版本下限。");
        Require(Assess(manifest.Replace("min_game_version", "unknown_game_version"), "0.111.0", new()) == "VersionRule", "不能略过不认识的版本约束。");
        Require(Assess(manifest.Replace("0.107.0", "0.112.0"), "0.111.0", new()) == "Version", "比当前测试版更高的版本要求也必须拒绝。");
        Require(Assess(manifest.Replace("skin\"", "../skin\""), "0.111.0", new()) == "InvalidPackage", "包内 ID 不能穿越安装目录。");
        var bootstrap = System.IO.Path.Combine(AppContext.BaseDirectory, "STS2SkinChanger.WorkshopBootstrapFixture.dll");
        File.Copy(bootstrap, System.IO.Path.Combine(package.Path, "skin.dll"));
        Require(Assess(manifest.Replace("\"has_dll\":false", "\"has_dll\":true"), "0.111.0", new() { ["BaseLib"] = "1.1.0" }) == "None", "仅有可省略启动代码的 DLL 不应挡住后续完整资源验证。");
        Require(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "STS2SkinChanger.WorkshopBootstrapFixture"), "检查 DLL 不得执行或加载它。");
        File.Copy(typeof(WorkshopTests).Assembly.Location, System.IO.Path.Combine(package.Path, "skin.dll"), true);
        Require(Assess(manifest, "0.111.0", new() { ["BaseLib"] = "1.1.0" }) == "StartupCode", "含未知逻辑的 DLL 即使未声明也不得绕过初始化检查。");
        var snapshot = HarmonyLib.AccessTools.Method(policy, "Snapshot").Invoke(null, [package.Path])!;
        Require((bool)HarmonyLib.AccessTools.Method(policy, "Unchanged").Invoke(null, [package.Path, snapshot])!, "未改动的下载包应该通过源文件检查。");
        File.WriteAllText(System.IO.Path.Combine(package.Path, "new.json"), "{}");
        Require(!(bool)HarmonyLib.AccessTools.Method(policy, "Unchanged").Invoke(null, [package.Path, snapshot])!, "检查期间新增文件也必须阻止发布，不能只验证旧文件。");
    }

    private static void CheckBrowserNavigation()
    {
        var assembly = typeof(Entry).Assembly;
        var type = assembly.GetType("STS2SkinChanger.Core.WorkshopBrowserPolicy");
        Require(type != null, "工坊需要按地区过滤和每页最多十个对象的统一策略。");
        object Call(string method, params object[] args) => type!.GetMethod(method)!.Invoke(null, args)!;
        Require((bool)Call("HasRegions", "monster") && (bool)Call("HasRegions", "event") && !(bool)Call("HasRegions", "cards"), "仅怪物和事件使用地区层级。");
        var ids = Enumerable.Range(1, 23).Select(i => "m" + i).ToArray();
        Require(((string[])Call("Page", ids, 0)).SequenceEqual(new[] {"m1","m2","m3","m4","m5","m6","m7","m8","m9","m10"}), "首屏不能塞满整个怪物列表。");
        Require(((string[])Call("Page", ids, 2)).SequenceEqual(new[] {"m21","m22","m23"}), "翻页不能漏掉尾部对象。");
        const string json = """[{"id":1,"targets":[{"kind":"monster","target":"jaw_worm"},{"kind":"cards","target":"silent"}]},{"id":2,"targets":[{"kind":"monster","target":"slime"}]},{"id":3,"targets":[{"kind":"companion","target":"osty"}]}]""";
        Require(((ulong[])Call("FilterIds", json, "monster", "", new[] {"jaw_worm"})).SequenceEqual(new ulong[] {1}), "地区筛选必须限制同类型的对象，而不是让任何标签匹配即可。");
        Require(((ulong[])Call("FilterIds", json, "", "slime", new[] {"slime"})).SequenceEqual(new ulong[] {1,2}), "选择全部类型后须清除隐藏的对象/地区筛选，且不得包含仅奥斯提标签的条目。");
        var controls = assembly.GetType("STS2SkinChanger.Ui.ContextualSkinControls", true)!;
        Require((bool)HarmonyLib.AccessTools.Method(controls, "IsAccentedCharacterOption").Invoke(null, ["__base__"])!, "游戏默认选项也应使用强调色。");
        Require((float)Call("MarqueeOffset", 72f, .3d) == 0 && (float)Call("MarqueeOffset", 72f, 1.8d) == 36f &&
            (float)Call("MarqueeOffset", 0f, 10d) == 0, "长标题应停留后匀速平移，短标题不能无故移动。");
        var browserTexts = assembly.GetType("STS2SkinChanger.Core.WorkshopBrowserText", true)!;
        var keyType = assembly.GetType("STS2SkinChanger.Core.WorkshopBrowserTextKey", true)!;
        foreach (var language in new[] { "eng", "zhs", "zht", "deu", "esp", "spa", "fra", "ita", "jpn", "kor", "pol", "ptb", "rus", "tha", "tur" })
        foreach (var key in Enum.GetValues(keyType))
            Require(!string.IsNullOrWhiteSpace((string)browserTexts.GetMethod("ForLanguage")!.Invoke(null, [language, key])!), $"{language} 缺少工坊筛选文本 {key}");
        var service = assembly.GetType("STS2SkinChanger.Core.SkinWorkshopService", true)!;
        var unsubscribe = HarmonyLib.AccessTools.Method(service, "Unsubscribe");
        Require(unsubscribe != null, "工坊需要实际的取消订阅操作，不是修改本地 UI 状态。");
        var downloads = (System.Collections.IDictionary)HarmonyLib.AccessTools.Field(service, "Downloads").GetValue(null)!;
        var count = downloads.Count;
        ((Task)unsubscribe!.Invoke(null, [0UL])!).GetAwaiter().GetResult();
        Require(downloads.Count == count, "非清单物品不能触发退订或创建任务。");
        var moveNext = unsubscribe.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()!.StateMachineType.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var calls = HarmonyLib.PatchProcessor.GetOriginalInstructions(moveNext).Select(i => i.operand).OfType<MethodBase>().ToArray();
        Require(calls.Any(m => m.DeclaringType == typeof(Steamworks.SteamUGC) && m.Name == "UnsubscribeItem") &&
            !calls.Any(m => m.Name == "Delete" && (m.DeclaringType == typeof(File) || m.DeclaringType == typeof(Directory))),
            "取消订阅必须交给 Steam，不能直接删除运行中资源。");
    }

    private static void CheckReasonTexts()
    {
        var noticeText = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopNoticeText");
        Require(noticeText != null, "需要面向玩家的重启原因和版本不匹配提示。");
        var keyType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopNoticeKey", true)!;
        foreach (var language in new[] { "eng", "zhs", "zht", "deu", "esp", "spa", "fra", "ita", "jpn", "kor", "pol", "ptb", "rus", "tha", "tur" })
        foreach (var key in Enum.GetValues(keyType))
            Require(!string.IsNullOrWhiteSpace((string)noticeText!.GetMethod("ForLanguage")!.Invoke(null, [language, key])!), $"{language} 缺少订阅提醒 {key}");
    }

    private sealed class WorkshopPackageFixture : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("sc-workshop-capability-").FullName;
        public WorkshopPackageFixture()
        {
            var pck = typeof(Entry).Assembly.GetType("STS2SkinChanger.Pck.PckArchive", true)!;
            HarmonyLib.AccessTools.Method(pck, "Write").Invoke(null, [System.IO.Path.Combine(Path, "skin.pck"),
                new Dictionary<string, byte[]> { ["res://animations/characters/necrobinder/model.tres"] = System.Text.Encoding.UTF8.GetBytes("[gd_resource type=\"Resource\" format=3]\n[resource]\n") }]);
        }
        public void Dispose() => Directory.Delete(Path, true);
    }
    private static void CheckPanelLifecycle()
    {
        var panel = typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.SkinWorkshopPanel", true)!;
        static bool Calls(MethodBase method, string name) => HarmonyLib.PatchProcessor.GetOriginalInstructions(method)
            .Any(i => i.operand is MethodInfo called && called.Name == name);
        // This assembly has no Godot source generator: a plain Control's _Ready override is
        // not dispatched by the engine. Assert the factory/native-signal boundary instead.
        var show = panel.GetMethod("Show", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
        Require(Calls(show, "Initialize"),
            "工坊入口必须显式初始化面板；当前 DLL 不会调用新建 Control 的 _Ready，点击后只会生成空控件。");
        var initialize = HarmonyLib.AccessTools.Method(panel, "Initialize");
        Require(Calls(initialize, "add_Timeout") && Calls(initialize, "add_TreeExiting") &&
                Calls(initialize, "add_WindowInput"),
            "工坊的进度刷新、关闭输入和退树清理也必须绑定原生信号，不能依赖未接通的虚方法。");
        Require(Calls(HarmonyLib.AccessTools.Method(panel, "Cleanup"), "remove_WindowInput"),
            "关闭工坊后必须解除主窗口输入监听，不能残留已释放面板的回调。");
        var controls = typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.ContextualSkinControls", true)!;
        var accent = HarmonyLib.AccessTools.Method(controls, "IsAccentedCharacterOption");
        Require((bool)accent.Invoke(null, ["__workshop__"])! && !(bool)accent.Invoke(null, ["skin:a"])!,
            "工坊入口需要强调色，但不能给所有普通皮肤染色。");
    }
    private static void CheckNativeNoticeScope()
    {
        var assembly = typeof(Entry).Assembly;
        var service = assembly.GetType("STS2SkinChanger.Core.SkinWorkshopService", true)!;
        var method = HarmonyLib.AccessTools.Method(service, "DeferNativeNotice");
        var downloads = (System.Collections.IDictionary)HarmonyLib.AccessTools.Field(service, "Downloads").GetValue(null)!;
        var stateType = assembly.GetType("STS2SkinChanger.Core.WorkshopDownload", true)!;
        var state = Activator.CreateInstance(stateType)!;
        var origin = stateType.GetField("BrowserSubscription");
        Require(origin != null, "需要记录订阅来源：本面板发起的订阅不弹窗，不能全局屏蔽游戏提示。");
        stateType.GetField("Busy")!.SetValue(state, true);
        downloads.Add(123UL, state);
        try
        {
            var ours = new MegaCrit.Sts2.Core.Modding.Mod { path = "D:/Steam/steamapps/workshop/content/2868840/123" };
            var other = new MegaCrit.Sts2.Core.Modding.Mod { path = "D:/Steam/steamapps/workshop/content/2868840/456" };
            Require(!(bool)method.Invoke(null, [ours])!, "仅有退订或其它来源状态不能视作本面板的订阅。");
            origin!.SetValue(state, true);
            Require((bool)method.Invoke(null, [ours])! && !(bool)method.Invoke(null, [other])!, "只能接管本面板发起的订阅提示。");
            stateType.GetField("Busy")!.SetValue(state, false);
            var keyType = assembly.GetType("STS2SkinChanger.Core.WorkshopTextKey", true)!;
            foreach (var status in new[] { "Ready", "Restart", "Failed" })
            {
                stateType.GetField("State")!.SetValue(state, Enum.Parse(keyType, status));
                Require((bool)method.Invoke(null, [ours])! && !(bool)method.Invoke(null, [other])!, "本面板结果只用标签、状态和按钮；晚到的原生通知也不能再次弹窗。");
            }
            var patch = assembly.GetType("STS2SkinChanger.Core.WorkshopRuntimeNoticePatch", true)!;
            Require(HarmonyLib.AccessTools.Method(patch, "TargetMethod").Invoke(null, null) is MethodBase, "当前游戏版本不存在预期的官方重启提示入口。");
        }
        finally { downloads.Remove(123UL); }
    }
    private static void CheckResourceCoverage()
    {
        var mod = typeof(Entry).Assembly;
        var catalogType = mod.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
        var resourceGate = HarmonyLib.AccessTools.Method(catalogType, "SupportsWorkshopResourcePath");
        Require(resourceGate != null, "本地清单审计和实际热注册必须共用资源格式检查。");
        foreach (var path in new[] { "res://model.res", "res://model.scn.remap", "res://logic.gd", "res://logic.DLL" })
            Require(!(bool)resourceGate!.Invoke(null, [path])!, "启动型资源不能仅因没有外置 DLL 就标为免重启。");
        Require((bool)resourceGate!.Invoke(null, ["res://model.tres"])!, "文本资源仍可继续完整资源检查。");
        var write = HarmonyLib.AccessTools.Method(mod.GetType("STS2SkinChanger.Pck.PckArchive", true)!, "Write");
        var descriptor = mod.GetType("STS2SkinChanger.Catalog.SkinModDescriptor", true)!;
        var directory = Directory.CreateTempSubdirectory("sc-workshop-test-");
        try
        {
            var game = Path.Combine(directory.FullName, "game.pck");
            var pack = Path.Combine(directory.FullName, "skin.pck");
            const string resource = "res://animations/characters/necrobinder/model.tres";
            var files = new Dictionary<string, byte[]> { [resource] = System.Text.Encoding.UTF8.GetBytes("[gd_resource type=\"Resource\" format=3]\n[resource]\n") };
            write.Invoke(null, [game, files]);
            var descriptors = Array.CreateInstance(descriptor, 1);
            descriptors.SetValue(Activator.CreateInstance(descriptor, "skin", "Skin", pack, false, directory.FullName, false, null), 0);
            bool Covered()
            {
                write.Invoke(null, [pack, files]);
                using var catalog = (IDisposable)HarmonyLib.AccessTools.Method(catalogType, "Build").Invoke(null, [game, descriptors])!;
                return (bool)HarmonyLib.AccessTools.Method(catalogType, "HasCompleteWorkshopResourceCoverage").Invoke(catalog, ["skin"])!;
            }
            Require(Covered(), "完整的无脚本原生资源替换应允许热注册。");
            files["res://unmanaged/menu.txt"] = [1];
            Require(!Covered(), "有未接管的 UI/文本文件时不能宣称完整支持。");
            files.Remove("res://unmanaged/menu.txt");
            files[resource] = System.Text.Encoding.UTF8.GetBytes("[gd_resource type=\"Resource\" format=3]\n[ext_resource type=\"Texture2D\" path=\"res://missing/texture.png\" id=\"1\"]\n[resource]\n");
            Require(!Covered(), "缺失的场景资源依赖必须拒绝，不能等玩家选择后出现空白。");
        }
        finally { directory.Delete(true); }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
