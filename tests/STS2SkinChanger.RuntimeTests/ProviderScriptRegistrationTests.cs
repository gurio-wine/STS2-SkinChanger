using System.Reflection;
using Godot;
using HarmonyLib;
using STS2SkinChanger;

// Godot's ScriptManagerBridge keys scripts by resource path and inserts with Dictionary.Add, so a
// second registration of a path aborts the whole LookupScriptsInAssembly call. Complete skin packs
// ship an initializer that registers their own scenes - exactly what the manager already did for
// them - and on the reported machines that duplicate-key exception rolled back the provider's whole
// visual session, which the player sees as a skin whose model never appears. The manager therefore
// has to treat a repeated declaration as idempotent and must never let it end a session.
internal static class ProviderScriptRegistrationTests
{
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<string> CapturedWarnings = [];

    public static void Run()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "STS2SkinChanger.ScriptPathFixture.dll");
        Require(File.Exists(fixturePath), "缺少脚本路径夹具程序集：" + fixturePath);

        var skinChanger = typeof(Entry).Assembly;
        var bridgeType = typeof(GodotObject).Assembly.GetType("Godot.Bridge.ScriptManagerBridge", true)!;
        var lookup = FindLookupMethod(bridgeType);
        var pathMapGuard = skinChanger.GetType(
            "STS2SkinChanger.Core.GodotScriptPathRegistrationCompatibilityPatch", true)!;
        var sessionGuard = skinChanger.GetType("STS2SkinChanger.Core.GodotScriptRegistrationSessionGuard");

        var harmony = new Harmony("Gurio.SkinChanger.Tests.ScriptPathRegistration");
        try
        {
            CapturedWarnings.Clear();
            harmony.Patch(
                skinChanger.GetType("STS2SkinChanger.Core.ModLog", true)!.GetMethod("Warn", AnyStatic)!,
                prefix: new HarmonyMethod(typeof(ProviderScriptRegistrationTests), nameof(CaptureWarning)));
            // A plain .NET host has no Godot runtime; only the engine probe at the end of the scan
            // is skipped, everything the registration itself does still runs.
            harmony.Patch(
                FindNativeCheck(),
                prefix: new HarmonyMethod(typeof(ProviderScriptRegistrationTests), nameof(SkipNativeCheck)));
            Patch(harmony, pathMapGuard);

            var declared = Assembly.LoadFrom(fixturePath);
            var paths = DeclaredScriptPaths(declared);
            Require(paths.Length == 2, "夹具必须声明两个脚本路径，重复路径的场景才有意义。");

            Register("首次注册", lookup, declared);
            var initial = ReadPathMap(bridgeType);
            foreach (var path in paths)
            {
                Require(initial.ContainsKey(path), "注册后每个声明路径都必须有映射：" + path);
            }

            // The same declaration seen through a second copy of the pack (another load context or a
            // second install) carries different Type objects for identical paths.
            var copy = Assembly.LoadFile(fixturePath);
            Require(!ReferenceEquals(declared, copy), "第二个副本必须是独立加载的程序集，否则测不到类型身份差异。");
            Require(declared.ManifestModule.ModuleVersionId == copy.ManifestModule.ModuleVersionId,
                "两个副本必须来自同一份编译结果。");
            Register("同一份皮肤第二个副本再注册", lookup, copy);
            var afterCopy = ReadPathMap(bridgeType);
            foreach (var path in paths)
            {
                Require(afterCopy[path] == initial[path],
                    "同一份声明重复注册时必须保留先注册的映射，不能改指向另一个副本：" + path);
            }

            // Whatever Godot still raises has to stay inside the registration call: the pack's own
            // initializer must not be able to roll back its visual session because of a path repeat.
            Require(sessionGuard != null,
                "缺少会话级保护：皮肤初始化器自己再注册一次脚本路径时，重复路径会把整场视觉会话回滚掉，" +
                "玩家看到的就是皮肤没有模型。");
            harmony.Unpatch(FindPathMapAdd(bridgeType), HarmonyPatchType.Prefix, harmony.Id);
            Patch(harmony, sessionGuard!);
            Register("路径映射保护失效时再注册", lookup, Assembly.LoadFile(fixturePath));
            var afterUnprotected = ReadPathMap(bridgeType);
            foreach (var path in paths)
            {
                Require(afterUnprotected[path] == initial[path], "会话保护必须保留已有映射：" + path);
            }

            Require(CapturedWarnings.Any(message => paths.Any(message.Contains)),
                "重复路径必须留下带具体路径的日志，作者和玩家才能据此定位：" +
                string.Join(" | ", CapturedWarnings));
            Console.WriteLine(
                "Provider script registration passed: repeated declarations keep the first mapping and never abort a session.");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    private static void Register(string stage, MethodInfo lookup, Assembly assembly)
    {
        try
        {
            lookup.Invoke(null, [assembly]);
        }
        catch (TargetInvocationException wrapped)
        {
            var inner = wrapped.InnerException ?? wrapped;
            throw new InvalidOperationException($"{stage}失败，注册脚本路径不能中断视觉会话：" + inner.Message, inner);
        }
    }

    private static string[] DeclaredScriptPaths(Assembly assembly) =>
        ((IEnumerable<Type>?)assembly.GetCustomAttributes(false)
                .OfType<AssemblyHasScriptsAttribute>()
                .FirstOrDefault()?.ScriptTypes ?? [])
            .Select(type => type.GetCustomAttributes(false).OfType<ScriptPathAttribute>().FirstOrDefault()?.Path)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToArray();

    private static Dictionary<string, Type> ReadPathMap(Type bridgeType)
    {
        var map = bridgeType.GetField("_pathTypeBiMap", AnyStatic)?.GetValue(null);
        return (Dictionary<string, Type>?)map?.GetType()
                   .GetField("_pathTypeMap", BindingFlags.Instance | BindingFlags.NonPublic)
                   ?.GetValue(map) ??
               throw new InvalidOperationException("Godot 的脚本路径映射不可读，测试无法判断注册结果。");
    }

    private static MethodInfo FindLookupMethod(Type bridgeType) =>
        bridgeType.GetMethod("LookupScriptsInAssembly", AnyStatic, binder: null, types: [typeof(Assembly)], modifiers: null)
        ?? throw new InvalidOperationException("Godot 的脚本注册入口不存在。");

    private static MethodInfo FindPathMapAdd(Type bridgeType) =>
        bridgeType.GetNestedType("PathScriptTypeBiMap", BindingFlags.NonPublic)
            ?.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null,
                types: [typeof(string), typeof(Type)], modifiers: null)
        ?? throw new InvalidOperationException("Godot 的脚本路径映射写入入口不存在。");

    private static MethodInfo FindNativeCheck() =>
        typeof(GodotObject).Assembly.GetType("Godot.NativeInterop.NativeFuncs", true)!
            .GetMethod("godotsharp_dotnet_module_is_initialized", AnyStatic)
        ?? throw new InvalidOperationException("Godot 的模块状态探测入口不存在。");

    private static void Patch(Harmony harmony, Type patchClass) =>
        harmony.CreateClassProcessor(patchClass).Patch();

    private static bool CaptureWarning(string message)
    {
        CapturedWarnings.Add(message);
        return false;
    }

    private static bool SkipNativeCheck() => false;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
