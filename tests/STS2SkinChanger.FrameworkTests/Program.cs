using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger;

static void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
var original = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args.Single()));
var registry = original.GetType("thunninoiSkinManager.thunninoiSkinManagerCode.SkinRegistry", true)!;
var data = original.GetType("thunninoiSkinManager.thunninoiSkinManagerCode.SkinData", true)!;
var sessionType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.FrameworkRegistrySession")
    ?? throw new InvalidOperationException("原注册表尚未桥接 SC：不能恢复原管理器后仍读写独立皮肤选择。");
var id = new ModelId("CHARACTER", "DEFECT");
var otherId = new ModelId("CHARACTER", "SILENT");
string? selected = "skin-a";
var writes = new List<(ModelId, string)>();
var session = Activator.CreateInstance(sessionType, [original,
    (Func<ModelId, string?>)(key => key == id ? selected : null),
    (Action<ModelId, string>)((key, skin) => { writes.Add((key, skin)); selected = skin; })])!;
sessionType.GetMethod("Install")!.Invoke(session, null);
var ensure = sessionType.GetMethod("EnsureCharacter")!;
ensure.Invoke(session, [id]);
ensure.Invoke(session, [otherId]);
var list = (IList)registry.GetMethod("GetAllSkins")!.Invoke(null, [id])!;
var a = Activator.CreateInstance(data, [id, "skin-a", "A"])!;
var b = Activator.CreateInstance(data, [id, "skin-b", "B"])!;
list.Add(a); list.Add(b);
object? Active(ModelId key) => registry.GetMethod("GetActiveSkin")!.Invoke(null, [key]);
string? SkinId(object? value) => (string?)data.GetProperty("SkinId")!.GetValue(value);
Require(ReferenceEquals(Active(id), a), "原注册表应按 SC 选择返回完整原生 SkinData。");
selected = "skin-b";
Require(ReferenceEquals(Active(id), b), "改变 SC 选择后不能继续读旧全局指针。");
Require(SkinId(Active(otherId)) == "default", "其它角色不能串皮肤。");
selected = "not-framework";
Require(SkinId(Active(id)) == "default", "切离框架皮肤必须恢复 default，不是上次激活皮肤。");
registry.GetMethod("SetActiveSkin", [typeof(ModelId), typeof(string)])!.Invoke(null, [id, "skin-a"]);
Require(writes.Count == 1 && writes[0] == (id, "skin-a"), "原管理器选择应请求 SC，不写旧存档。");
registry.GetMethod("CycleNext")!.Invoke(null, [id]);
Require(selected == "skin-b", "原管理器下一项必须基于 SC 当前选择。");
registry.GetMethod("SetActiveSkin", [typeof(ModelId), typeof(int)])!.Invoke(null, [id, -1]);
Require(writes.Count == 2, "无效索引不得污染 SC。");
registry.GetMethod("SkinDbSetup")!.Invoke(null, null);
ensure.Invoke(session, [id]);
Require(list.Count == 3 && ReferenceEquals(Active(id), b), "重试注册不能清空已有皮肤或重置选择。");
registry.GetMethod("finializeSetup")!.Invoke(null, null);
registry.GetMethod("finializeSetup")!.Invoke(null, null);
registry.GetMethod("Save")!.Invoke(null, null);
registry.GetMethod("Load")!.Invoke(null, null);
Require(writes.Count == 2, "初始化/同步不能反写 SC 或读写旧皮肤存档。");
var enabled = false;
data.GetMethod("RegisterConfig")!.Invoke(a, ["UseCardFrame", (Func<bool>)(() => enabled)]);
var config = sessionType.GetMethod("IsConfigEnabled")!;
Require(!(bool)config.Invoke(session, ["defect", "skin-a", "UseCardFrame"])!, "必须读取指定皮肤作者设置，而非另一活跃皮肤。");
enabled = true;
Require((bool)config.Invoke(session, ["defect", "skin-a", "UseCardFrame"])!, "作者设置改变后不能读陈旧缓存。");
Require((bool)config.Invoke(session, ["defect", "skin-a", "Unknown"])!, "没有声明的设置保持作者默认行为。");
// Real native dictionary and result types, with inert instances: no Godot object or Mod init.
var relicBase = original.GetType("thunninoiSkinManager.thunninoiSkinManagerCode.Patches.RelicSkin", true)!;
var dynamicModule = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
    new AssemblyName("FrameworkTestDescriptors"), System.Reflection.Emit.AssemblyBuilderAccess.Run).DefineDynamicModule("descriptors");
var builder = dynamicModule.DefineType("TestRelicDescriptor", TypeAttributes.Public, relicBase);
foreach (var method in relicBase.GetMethods().Where(method => method.IsAbstract))
{
    var implementation = builder.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual,
        method.ReturnType, method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    var il = implementation.GetILGenerator();
    il.Emit(System.Reflection.Emit.OpCodes.Ldnull);
    il.Emit(System.Reflection.Emit.OpCodes.Ret);
    builder.DefineMethodOverride(implementation, method);
}
var inertType = builder.CreateType()!;
var relicA = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(inertType);
var relicB = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(inertType);
var relicId = new ModelId("RELIC", "TEST");
var relicDictionary = data.GetProperty("RelicSkinDict", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
var mapA = (IDictionary)relicDictionary.GetValue(a)!;
var mapB = (IDictionary)relicDictionary.GetValue(b)!;
mapA[relicId] = relicA; mapB[relicId] = relicB;
var resolveRelic = registry.GetMethod("ResolveRelic", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
selected = "skin-a";
Require(ReferenceEquals(relicA, resolveRelic.Invoke(null, [relicId])), "遗物不能读取原框架旧全局缓存。");
selected = "skin-b";
Require(ReferenceEquals(relicB, resolveRelic.Invoke(null, [relicId])), "切换后遗物需立即跟随对应 SkinData。");
selected = null;
Require(resolveRelic.Invoke(null, [relicId]) == null, "切回非框架皮肤不能残留上个皮肤的遗物。");
var contractType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Catalog.FrameworkCharacterSkinContract", true)!;
var modelContractType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Catalog.FrameworkModelSkinContract", true)!;
var resources = new Dictionary<string, string> { ["CombatVisual"] = "combat", ["CardTrail"] = "trail",
    ["CardFrameMaterial"] = "frame", ["HandPoint"] = "hand", ["EnergyIcon"] = "energy" };
var contract = Activator.CreateInstance(contractType, ["provider", "thunninoiSkinManager", "defect", "option", "skin-a", "A", "Descriptor",
    resources, new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, string>(),
    Array.CreateInstance(modelContractType, 0), Array.CreateInstance(modelContractType, 0)])!;
var filter = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.FrameworkRegistryCooperation", true)!
    .GetMethod("Filter", [contractType, typeof(Func<string, bool>)])!;
var filtered = filter.Invoke(null, [contract, (Func<string, bool>)(_ => false)])!;
var filteredResources = (IReadOnlyDictionary<string, string>)contractType.GetProperty("CharacterResources")!.GetValue(filtered)!;
Require(filteredResources.Count == 1 && filteredResources["CombatVisual"] == "combat",
    "作者关闭卡框/轨迹/手势/能量时只保留人物模型，不能仅关闭卡框而漏掉轨迹。");
Require(ReferenceEquals(contract, filter.Invoke(null, [contract, (Func<string, bool>)(_ => true)])),
    "重新启用作者设置必须恢复原合同，不能永久删素材。");
var cooperationType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.FrameworkRegistryCooperation", true)!;
var sessionField = cooperationType.GetField("_session", BindingFlags.Static | BindingFlags.NonPublic)!;
sessionField.SetValue(null, session);
try
{
    enabled = false; // native skin-a's UseCardFrame delegate, not a simulated config reader
    var assetType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Catalog.ResourceAsset", true)!;
    var optionType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Catalog.SkinOption", true)!;
    var assets = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), assetType))!;
    const string trailPath = "res://scenes/vfx/card_trail_defect.tscn";
    assets[trailPath] = Activator.CreateInstance(assetType, ["trail"])!;
    var constructor = optionType.GetConstructors().Single();
    var optionArgs = constructor.GetParameters().Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
    optionArgs[0] = "option"; optionArgs[1] = "A"; optionArgs[2] = assets;
    optionArgs[Array.FindIndex(constructor.GetParameters(), parameter => parameter.Name == "FrameworkContract")] = contract;
    var option = constructor.Invoke(optionArgs);
    var filterAssets = cooperationType.GetMethod("FilterAssets")!;
    var result = filterAssets.Invoke(null, [option])!;
    Require(!((IDictionary)optionType.GetProperty("Assets")!.GetValue(result)!).Contains(trailPath),
        "关闭作者卡框配置必须从实际资源映射移除自己的轨迹，不只是停掉 getter。");
    assets[trailPath] = Activator.CreateInstance(assetType, ["different-provider-trail"])!;
    result = filterAssets.Invoke(null, [option])!;
    Require(((IDictionary)optionType.GetProperty("Assets")!.GetValue(result)!).Contains(trailPath),
        "合并皮肤中来自其它提供者的轨迹，不能被这个框架的设置删除。");
}
finally { sessionField.SetValue(null, null); }
var controlsType = original.GetType("thunninoiSkinManager.thunninoiSkinManagerCode.SkinSelector", true)!;
var bridgeType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.FrameworkRegistryCooperation", true)!;
var uiHarmony = new HarmonyLib.Harmony("tests.native-framework-control-signatures");
foreach (var (name, prefix) in new[] { ("Refresh", "RefreshControl"), ("LoadPreview", "RefreshControl"),
             ("OnPrevPressed", "CycleControl"), ("OnNextPressed", "CycleControl") })
    uiHarmony.Patch(HarmonyLib.AccessTools.Method(controlsType, name),
        prefix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(bridgeType, prefix)));
uiHarmony.Patch(HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen), "SelectCharacter"),
    postfix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(bridgeType, "AttachControl")));
uiHarmony.UnpatchAll(uiHarmony.Id);
Console.WriteLine("Original framework cooperation passed: native identity, scoped reads, SC writes, idempotent setup, no legacy persistence, live author config.");
