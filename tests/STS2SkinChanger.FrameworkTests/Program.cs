using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger;
using HarmonyLib;
using System.Reflection.Emit;

static void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
var ioHarmony = new Harmony("tests.native-framework-engine-boundary");
var logType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ModLog", true)!;
foreach (var method in new[] { "Info", "Warn", "Error" })
    ioHarmony.Patch(AccessTools.Method(logType, method), prefix: new HarmonyMethod(typeof(NativeIo), nameof(NativeIo.NoLog)));
var originalPath = Path.GetFullPath(args.Single());
var sourceHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(originalPath));
var loaderType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.NativeFrameworkAssemblyLoader", true)!;
var loadArgs = new object?[] { AssemblyLoadContext.Default, originalPath, null };
var adapted = (bool)loaderType.GetMethod("TryLoadCompatible")!.Invoke(null, loadArgs)!;
var original = adapted ? (Assembly)loadArgs[2]! : AssemblyLoadContext.Default.LoadFromAssemblyPath(originalPath);
Require(sourceHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(originalPath))),
    "跨版本桥接不能修改安装目录中的原 DLL。");
if (adapted)
{
    Require(ReferenceEquals(original, loaderType.GetMethod("Find")!.Invoke(null, [originalPath])),
        "测试版没有 Mod.assembly 字段，必须通过原路径准确找到内存适配的原管理器。");
    var entryBody = PatchProcessor.GetOriginalInstructions(original.GetType("thunninoiSkinManager.modEntry", true)!.TypeInitializer!);
    Require(entryBody.Any(instruction => Equals(instruction.operand, originalPath)) &&
            !entryBody.Any(instruction => instruction.operand is MethodInfo method && method.Name == "get_Location"),
        "内存适配不能把原管理器的 ModDirectory 变为空路径。");
}
var displayPolicy = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ManagedProviderDisplayPolicy", true)!;
var display = displayPolicy.GetMethod("IsManaged")!;
Require(display.GetParameters().Length == 3,
    "[SC] 必须区分功能宿主与皮肤提供者，不能复用加载顺序探测结果。");
Require(!(bool)display.Invoke(null, ["manager", new[] { "manager", "skin" }, true])! &&
        (bool)display.Invoke(null, ["skin", new[] { "manager", "skin" }, false])!,
    "保留运行的管理器不标 [SC]，被接管的内容仍需标记。");
var registry = original.GetType("thunninoiSkinManager.thunninoiSkinManagerCode.SkinRegistry", true)!;
var data = original.GetType("thunninoiSkinManager.thunninoiSkinManagerCode.SkinData", true)!;
// Run real registry mutation/serialization. Only the engine IO and logging boundary is inert.
foreach (var method in new[] { "Save", "Load", "RefreshSkinCache", "finializeSetup" })
    ioHarmony.Patch(AccessTools.Method(registry, method), transpiler: new HarmonyMethod(typeof(NativeIo), nameof(NativeIo.ReplaceIo)));
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
var publish = sessionType.GetMethod("PublishSelection")
    ?? throw new InvalidOperationException("缺少回写原管理器的事务：原 setter/cache/save 不能永远被停用。");
var nativeSelections = (IDictionary)registry.GetField("_activeSkins", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
publish.Invoke(session, [id, "skin-a"]);
Require((string?)nativeSelections[id] == "skin-a" && writes.Count == 0,
    "SC 发布选择必须执行原注册表状态更新，不能回声触发第二次 SC 请求。");
Require(NativeIo.Opens == 1, "原 Save 必须经过自己的序列化并到达 IO 边界。");
publish.Invoke(session, [id, "skin-a"]);
Require(NativeIo.Opens == 1, "无变化的同步不能反复保存。");
object? Active(ModelId key) => registry.GetMethod("GetActiveSkin")!.Invoke(null, [key]);
string? SkinId(object? value) => (string?)data.GetProperty("SkinId")!.GetValue(value);
Require(ReferenceEquals(Active(id), a), "原注册表应按 SC 选择返回完整原生 SkinData。");
selected = "skin-b";
Require(ReferenceEquals(Active(id), b), "改变 SC 选择后不能继续读旧全局指针。");
Require(SkinId(Active(otherId)) == "default", "其它角色不能串皮肤。");
selected = "not-framework";
Require(SkinId(Active(id)) == "default", "切离框架皮肤必须恢复 default，不是上次激活皮肤。");
registry.GetMethod("SetActiveSkin", [typeof(ModelId), typeof(string)])!.Invoke(null, [id, "skin-a"]);
Require(writes.Count == 1 && writes[0] == (id, "skin-a"), "原管理器选择应先请求 SC，资源成功后才发布。");
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
Require(writes.Count == 2 && NativeIo.Opens == 4 && NativeIo.ExistsChecks == 2,
    "初始化/同步不能反写 SC，但应保留原 Load/Save（仅测试 IO 无落盘）。");
// Each registration callback lives in a different assembly, like independent skin packages.
var setupTarget = AccessTools.Method(registry, "SkinDbSetup");
var providerA = NativeIo.RegistrationProvider("ProviderA");
var providerB = NativeIo.RegistrationProvider("ProviderB");
ioHarmony.Patch(setupTarget, postfix: new HarmonyMethod(providerA.Callback));
ioHarmony.Patch(setupTarget, postfix: new HarmonyMethod(providerB.Callback));
Require((bool)sessionType.GetMethod("HasRegistrationCallbacks")!.Invoke(session, [providerA.Callback.Module.Assembly])! &&
        !(bool)sessionType.GetMethod("HasRegistrationCallbacks")!.Invoke(session, [typeof(Entry).Assembly])!,
    "只将实际登记过框架皮肤的程序集加入补登记队列，普通商人/先古不能触发全量登记。");
sessionType.GetMethod("RegisterProvider")!.Invoke(session, [providerA.Callback.Module.Assembly]);
Require((int)providerA.Count.GetValue(null)! == 1 && (int)providerB.Count.GetValue(null)! == 0 && list.Count == 3,
    "补登记 A 不能重新执行 B 或清空已有皮肤。");
ioHarmony.Unpatch(setupTarget, providerA.Callback);
ioHarmony.Unpatch(setupTarget, providerB.Callback);
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
publish.Invoke(session, [id, "skin-b"]); // local saved B, temporary player scope A
Require(ReferenceEquals(relicA, resolveRelic.Invoke(null, [relicId])), "遗物不能读取原框架旧全局缓存。");
var nativeRelics = (IDictionary)registry.GetField("_activeRelics", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
Require(ReferenceEquals(nativeRelics[relicId], relicB) && writes.Count == 2,
    "原缓存应按发布的本机 B 更新，不能把临时玩家作用域 A 持久化进去。");
NativeIo.FailCache = true;
try
{
    publish.Invoke(session, [id, "skin-a"]);
    throw new InvalidOperationException("没有模拟出原缓存刷新异常。");
}
catch (TargetInvocationException exception) when (exception.GetBaseException().Message == "cache failure") { }
finally { NativeIo.FailCache = false; }
Require((string?)nativeSelections[id] == "skin-b" && ReferenceEquals(relicA, resolveRelic.Invoke(null, [relicId])),
    "原接口失败必须恢复已发布选择和玩家作用域，不能留下发布模式。");
var savesBeforeRetry = NativeIo.Opens;
publish.Invoke(session, [id, "skin-b"]);
Require(NativeIo.Opens == savesBeforeRetry + 1 && ReferenceEquals(nativeRelics[relicId], relicB),
    "刷新失败后即使回退值与旧值相同，也必须重建原缓存，不能误判无变化。");
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
var fallbackType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.FrameworkSkinRuntime", true)!;
var usesFallback = AccessTools.Method(fallbackType, "UsesDeclarativePresentation");
Require((bool)usesFallback.Invoke(null, [contract])!, "未启用原管理器时仍需声明式后备呈现。");
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
var nativeHarmony = new Harmony("tests.original-manager-callbacks");
foreach (var type in new[] { "Patches.PowerIcon", "Patches.CustomOrbSprite", "Patches.SilentShivColor", "SkinSelectorInjector" }
    .Select(name => original.GetType("thunninoiSkinManager.thunninoiSkinManagerCode." + name, true)!)
    .ToArray()) nativeHarmony.CreateClassProcessor(type).Patch();
var retained = Harmony.GetAllPatchedMethods().Where(target => Harmony.GetPatchInfo(target)!.Owners.Contains(nativeHarmony.Id)).ToArray();
Require(retained.Length == 4, "实包的四种功能回调必须实际安装后才检查保留。");
bridgeType.GetMethod("Bind")!.Invoke(null, [original]);
Require(!(bool)usesFallback.Invoke(null, [contract])!, "原管理器负责已登记皮肤时不能再叠加 SC 的同一套能量/登场动画等呈现。");
Require(retained.All(target => Harmony.GetPatchInfo(target)!.Owners.Contains(nativeHarmony.Id)),
    "绑定协作不得移除原能力图标、自定义球、小刀颜色或 UI 注入补丁。");
var injector = original.GetType("thunninoiSkinManager.thunninoiSkinManagerCode.SkinSelectorInjector", true)!;
var injectorBody = PatchProcessor.GetCurrentInstructions(AccessTools.Method(injector, "Postfix"));
Require(injectorBody.Any(instruction => instruction.operand is MethodInfo method && method.Name == "AddChildSafely") &&
        !injectorBody.Any(instruction => Equals(instruction.operand, "CharSelectButtons/ButtonContainer/DEFECT_button/PlayerIconContainer")),
    "应保留原生控件的实际创建调用，只去掉会因其它 UI Mod 路径变化而报错的调试尾部。");
Require(Harmony.GetPatchInfo(AccessTools.Method(controlsType, "Refresh"))!.Prefixes.Count == 1,
    "原 Refresh 只安装冲突保护，不应被多个替代控件入口重复拦截。");
Console.WriteLine("Original framework cooperation passed: scoped reads, native mutation/cache/persistence, no echo, idempotent setup, live config, retained callbacks and original UI injector.");

static class NativeIo
{
    public static int Opens, ExistsChecks;
    public static bool FailCache;
    public static bool NoLog(string __0) { Console.WriteLine(__0); return false; }
    public static void Log(MegaCrit.Sts2.Core.Logging.Logger? logger, string message, int level)
    { if (FailCache && message == "Refreshing skin cache") throw new InvalidOperationException("cache failure"); }
    public static Godot.FileAccess? Open(string path, Godot.FileAccess.ModeFlags mode)
    {
        if (path != "user://thunni_skin_info.json" || mode != Godot.FileAccess.ModeFlags.Write)
            throw new InvalidOperationException("Unexpected native persistence target.");
        Opens++;
        return null;
    }
    public static bool Exists(string path) { ExistsChecks++; return false; }
    public static (MethodInfo Callback, FieldInfo Count) RegistrationProvider(string name)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule(name).DefineType(name, TypeAttributes.Public);
        var field = type.DefineField("Count", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        var method = type.DefineMethod("Register", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, field); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stsfld, field); il.Emit(OpCodes.Ret);
        var result = type.CreateType()!;
        return (result.GetMethod("Register")!, result.GetField("Count")!);
    }
    public static IEnumerable<CodeInstruction> ReplaceIo(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo logMethod)
            {
                if (logMethod.Name == "get_Logger" && logMethod.ReturnType == typeof(MegaCrit.Sts2.Core.Logging.Logger))
                { instruction.opcode = OpCodes.Ldnull; instruction.operand = null; }
                else if (logMethod.DeclaringType == typeof(MegaCrit.Sts2.Core.Logging.Logger) && logMethod.Name is "Info" or "Error")
                { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(NativeIo), nameof(Log)); }
            }
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(Godot.FileAccess))
            {
                if (method.Name == "Open") instruction.operand = AccessTools.Method(typeof(NativeIo), nameof(Open));
                if (method.Name == "FileExists") instruction.operand = AccessTools.Method(typeof(NativeIo), nameof(Exists));
            }
            yield return instruction;
        }
    }
}
