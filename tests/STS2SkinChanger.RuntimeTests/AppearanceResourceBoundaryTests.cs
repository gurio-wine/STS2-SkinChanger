using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using STS2SkinChanger;

internal static class AppearanceResourceBoundaryTests
{
    private static readonly Assembly Mod = typeof(Entry).Assembly;
    public static void Run()
    {
        CheckButtonIcon();
        CheckCardReentry();
        CheckRelicTextureOwnership();
        Console.WriteLine("Appearance boundaries passed: cold button icons, model-scoped card tree reentry and standalone relic ownership.");
    }

    private static void CheckRelicTextureOwnership()
    {
        var type = Mod.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
        var owns = AccessTools.Method(type, "IsSelectedCharacterStandaloneTexture");
        Require(owns != null, "选择中的角色作者返回独立遗物图时，最后的遗物补丁仍会将它覆盖为原版图集切片。");
        var directory = Directory.CreateTempSubdirectory("sc-relic-boundary-");
        try
        {
            var game = Path.Combine(directory.FullName, "game.pck");
            var pack = Path.Combine(directory.FullName, "skin.pck");
            var files = new Dictionary<string, byte[]>
            {
                ["res://animations/characters/necrobinder/model.tres"] = System.Text.Encoding.UTF8.GetBytes("[gd_resource type=\"Resource\" format=3]\n[resource]\n")
            };
            var write = AccessTools.Method(Mod.GetType("STS2SkinChanger.Pck.PckArchive", true)!, "Write");
            write.Invoke(null, [game,files]);
            const string texture = "res://custom/relic.png";
            files[texture + ".import"] = System.Text.Encoding.UTF8.GetBytes("[remap]\ntype=\"CompressedTexture2D\"\npath=\"res://.godot/imported/relic.ctex\"\n");
            files["res://.godot/imported/relic.ctex"] = [0];
            files["res://images/atlases/relic_atlas.png"] = [0];
            write.Invoke(null,[pack,files]);
            var dt = Mod.GetType("STS2SkinChanger.Catalog.SkinModDescriptor",true)!;
            var ds = Array.CreateInstance(dt,1);
            ds.SetValue(Activator.CreateInstance(dt,"skin","skin",pack,false,directory.FullName,false,null),0);
            using var catalog = (IDisposable)AccessTools.Method(type,"Build").Invoke(null,[game,ds])!;
            bool Owns(string path,string group,string selection) => (bool)owns!.Invoke(catalog,
                [path,new Dictionary<string,string>{{group,selection}}])!;
            Require(Owns(texture,"necrobinder","skin"), "已选角色包中的独立图片不能被遗物原皮恢复覆盖。");
            Require(!Owns(texture,"necrobinder","__base__") && !Owns(texture,"silent","skin"), "未选中或属于其它角色的包不能保留遗物覆盖。");
            Require(!Owns("res://custom/missing.png","necrobinder","skin") &&
                    !Owns("res://images/atlases/relic_atlas.png","necrobinder","skin"), "必须核验实际文件，公共遗物图集仍走隔离切片逻辑。");
        }
        finally { directory.Delete(true); }
    }

    private static void CheckButtonIcon()
    {
        var scanner = Mod.GetType("STS2SkinChanger.Catalog.ManagedCharacterAssetReplacementScanner", true)!;
        var scan = AccessTools.Method(scanner, "ScanButtonIcon");
        Require(scan != null, "首次选角按钮缺少静态图片映射，仍然依赖点击角色后才执行作者回调。");
        var result = scan!.Invoke(null, [typeof(AppearanceResourceBoundaryTests).Assembly.Location])!;
        Require((string)result.GetType().GetProperty("TargetGroupId")!.GetValue(result)! == "necrobinder", "按钮映射必须归属回调明确比较的角色。");
        var paths = (IReadOnlyDictionary<string, string>)result.GetType().GetProperty("CanonicalPathsByProviderPath")!.GetValue(result)!;
        Require(paths.Count == 1 && paths["res://fixture/avatar.png"] == "res://images/packed/character_select/char_select_necrobinder.png",
            "仅提取按钮实际引用的纹理，不将其它静态缓存图片当头像，也不覆盖锁定头像。");
        Require(!BoundaryIconFixture.Initialized, "扫描按钮图标不能触发提供者静态初始化或加载完整模型。");
    }

    private static void CheckCardReentry()
    {
        // NCard's static StringNames need the native Godot runtime. Keep only this engine
        // boundary out of the identity test; the real weak-table/model ownership code runs.
        var nativeBoundary = new Harmony("SkinChanger.Tests.CardStaticBoundary");
        nativeBoundary.Patch(typeof(NCard).TypeInitializer!,
            prefix: new HarmonyMethod(typeof(AppearanceResourceBoundaryTests), nameof(SkipNativeInitialization)));
        try { CheckCardReentryIdentity(); }
        finally { nativeBoundary.UnpatchAll(nativeBoundary.Id); }
    }

    private static bool SkipNativeInitialization() => false;

    private static void CheckCardReentryIdentity()
    {
        var guard = Mod.GetType("STS2SkinChanger.Core.VisualPatchGuard", true)!;
        var isVisual = AccessTools.Method(guard, "IsVisualTarget");
        Require((bool)isVisual.Invoke(null, [AccessTools.Method(typeof(NCard), "_EnterTree")])!,
            "未选中的已接管卡图包仍保留 EnterTree 呈现回调，拿起卡牌会重新覆盖卡面。");
        Require(!(bool)isVisual.Invoke(null, [AccessTools.Method(typeof(NCard), "_ExitTree")])!,
            "不得连带移除卡牌退出时的清理回调。");
        var controls = Mod.GetType("STS2SkinChanger.Ui.CardSkinControls", true)!;
        var ready = AccessTools.Method(controls, "HasCurrentCardLayout");
        Require(ready != null, "卡牌重入缺少已初始化且模型一致的布局检查。");
        var card = (NCard)RuntimeHelpers.GetUninitializedObject(typeof(NCard));
        var model = new Wither();
        var modelField = AccessTools.Field(typeof(NCard), "_model");
        modelField.SetValue(card, model);
        Require(!(bool)ready!.Invoke(null, [card])!, "首次 EnterTree 时不能读取尚未 Ready 的卡面节点。");
        var layouts = AccessTools.Field(controls, "BaselineLayouts").GetValue(null)!;
        var layoutType = controls.GetNestedType("CardLayoutState", BindingFlags.NonPublic)!;
        var itemType = controls.GetNestedType("CanvasItemState", BindingFlags.NonPublic)!;
        var layout = Activator.CreateInstance(layoutType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [model, Array.CreateInstance(itemType, 0), new HashSet<ulong>()], null)!;
        layouts.GetType().GetMethod("Add")!.Invoke(layouts, [card, layout]);
        try
        {
            Require((bool)ready.Invoke(null, [card])!, "已初始化的同一张牌在手牌/拖拽容器之间转移必须再次应用当前卡面。");
            modelField.SetValue(card, new Wither());
            Require(!(bool)ready.Invoke(null, [card])!, "对象池绑定另一张卡时不能恢复前一张卡的布局。");
            var patch = Mod.GetType("STS2SkinChanger.Ui.CardTreeReentrySkinPatch");
            Require(patch != null, "遗漏 NCard._EnterTree，拿起牌时外部卡包仍可强制覆盖。");
            var harmony = new Harmony("SkinChanger.Tests.CardTreeBoundary");
            try
            {
                harmony.CreateClassProcessor(patch!).Patch();
                Require(Harmony.GetPatchInfo(AccessTools.Method(typeof(NCard), "_EnterTree"))?.Postfixes
                    .Any(p => p.owner == harmony.Id) == true, "必须挂到实际卡牌重入入口。");
                var calls = PatchProcessor.GetOriginalInstructions(AccessTools.Method(patch, "Postfix"))
                    .Select(i => i.operand).OfType<MethodInfo>().ToArray();
                Require(calls.Contains(AccessTools.Method(controls, "ReapplyCardAfterTreeEntry")), "树重入必须使用相同卡牌所有权和最终资源流程。");
            }
            finally { harmony.UnpatchAll(harmony.Id); }
        }
        finally { layouts.GetType().GetMethod("Remove")!.Invoke(layouts, [card]); }
    }

    public static void Audit(string gamePack, string root)
    {
        var scanner = Mod.GetType("STS2SkinChanger.Catalog.ManagedCharacterAssetReplacementScanner", true)!;
        var dll = Directory.GetFiles(root, "*.dll").Single();
        var result = AccessTools.Method(scanner, "ScanButtonIcon").Invoke(null, [dll]);
        Require(result != null, "实包的按钮 Init 图片声明未提取。");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        var descriptor = Mod.GetType("STS2SkinChanger.Catalog.SkinModDescriptor", true)!;
        var id = Path.GetFileNameWithoutExtension(dll);
        var descriptors = Array.CreateInstance(descriptor, 1);
        descriptors.SetValue(Activator.CreateInstance(descriptor, id, id, Path.Combine(root,id+".pck"),false,root,true,null),0);
        var catalogType = Mod.GetType("STS2SkinChanger.Catalog.SkinCatalog",true)!;
        using var catalog = (IDisposable)AccessTools.Method(catalogType,"Build").Invoke(null,[gamePack,descriptors])!;
        var path = "res://images/packed/character_select/char_select_necrobinder.png";
        var overlay = AccessTools.Method(catalogType,"BuildRuntimeResourceOverlay").Invoke(catalog,["necrobinder",id,new[]{path},"cold-icon",false,false])!;
        var resources = (IDictionary)overlay.GetType().GetProperty("ResourcePaths")!.GetValue(overlay)!;
        var files = (IDictionary)overlay.GetType().GetProperty("Files")!.GetValue(overlay)!;
        Require(resources.Contains(path) && files.Keys.Cast<string>().Any(p => p.Contains("character_icon_hollow",StringComparison.OrdinalIgnoreCase)),
            "首次图标隔离资源包必须来自选中皮肤的头像依赖，不能退回游戏原图。");
        Require(!files.Keys.Cast<string>().Any(p=>p.EndsWith(".skel")||p.EndsWith(".scn")), "按钮请求不得预加载战斗模型。");
        Console.WriteLine("Actual cold character button bundle passed.");
    }

    public static void AuditRelics(string gamePack, string manifestPath)
    {
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = Path.GetDirectoryName(manifestPath)!;
        var id = manifest.RootElement.GetProperty("id").GetString()!;
        var pckName = manifest.RootElement.GetProperty("pck_name").GetString()!;
        var descriptor = Mod.GetType("STS2SkinChanger.Catalog.SkinModDescriptor",true)!;
        var descriptors = Array.CreateInstance(descriptor,1);
        descriptors.SetValue(Activator.CreateInstance(descriptor,id,id,Path.Combine(root,pckName+".pck"),false,root,true,null),0);
        var catalogType = Mod.GetType("STS2SkinChanger.Catalog.SkinCatalog",true)!;
        using var catalog = (IDisposable)AccessTools.Method(catalogType,"Build").Invoke(null,[gamePack,descriptors])!;
        var owns = AccessTools.Method(catalogType,"IsSelectedCharacterStandaloneTexture");
        foreach (var name in new[]{"bound_phylactery","phylactery_unbound","yummy_cookie_necro"})
        {
            var path = "res://images/relics/"+name+".png";
            foreach (var selection in new[]{id,"__base__",id})
                Require((bool)owns.Invoke(catalog,[path,new Dictionary<string,string>{{"necrobinder",selection}}])! == (selection == id),
                    "实包独立遗物纹理归属未跟随选择往返切换："+name);
        }
        Console.WriteLine("Actual Orchis relic ownership passed: three explicit textures and deselection.");
    }

    private static void Require(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
}

internal static class BoundaryIconFixture
{
    public static bool Initialized;
}
internal static class BoundaryIconAssets
{
    public static readonly Texture2D Avatar = ResourceLoader.Load<Texture2D>("res://fixture/avatar.png");
    public static readonly Texture2D Unrelated = ResourceLoader.Load<Texture2D>("res://fixture/background.png");
    static BoundaryIconAssets() { BoundaryIconFixture.Initialized = true; }
}
[HarmonyPatch(typeof(NCharacterSelectButton), nameof(NCharacterSelectButton.Init))]
internal static class BoundaryButtonIconPatch
{
    private static void Postfix(NCharacterSelectButton __instance, CharacterModel character)
    {
        if (!string.Equals(character.Id.Entry, "NECROBINDER", StringComparison.OrdinalIgnoreCase)) return;
        var image = BoundaryIconAssets.Avatar;
        __instance.GetNode<TextureRect>("%Icon").Texture = image;
    }
}
