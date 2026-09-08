using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using STS2SkinChanger;

internal static class ProviderCompatibilityTests
{
    private static readonly Assembly Cecil = typeof(Harmony).Assembly;
    private static readonly Type Runtime = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ProviderAssemblyCompatibility", true)!;

    internal static void Run()
    {
        Require(Runtime.GetMethod("PrepareForCurrentGame") != null,
            "缺少统一的跨版本准备结果：不能区分已转换和仍然缺失的接口。");
        CheckFixture();
        CheckReturnedTrackAdapters();
        CheckDisposeAndOwnership();
        Console.WriteLine("Provider compatibility passed: type relocation, value-type animation calls, unresolved references and source preservation.");
    }

    private static void CheckDisposeAndOwnership()
    {
        var definition = CreateAssembly();
        var module = Property(definition, "MainModule")!;
        var owner = Items(Property(module, "Types")!).First();
        var dispose = Import(module, typeof(IDisposable).GetMethod("Dispose")!);
        foreach (var (name, type) in new[] { ("SpineCleanup", typeof(MegaAnimationState)), ("OtherCleanup", typeof(AnimationTrackProbe)) })
        {
            var method = Method(owner, module, name, typeof(void));
            Invoke(Property(method, "Parameters")!, "Add",
                Activator.CreateInstance(Cecil.GetType("Mono.Cecil.ParameterDefinition")!, Import(module, type))!);
            Emit(method, "Ldarg_0"); Emit(method, "Callvirt", dispose); Emit(method, "Ret");
        }
        var foreignScope = Activator.CreateInstance(Cecil.GetType("Mono.Cecil.AssemblyNameReference")!, "NotSts2", new Version(1, 0))!;
        Invoke(Property(module, "AssemblyReferences")!, "Add", foreignScope);
        var impostor = Activator.CreateInstance(Cecil.GetType("Mono.Cecil.TypeReference")!,
            typeof(MegaAnimationState).Namespace!, typeof(MegaAnimationState).Name, module, foreignScope)!;
        var other = Import(module, typeof(MegaAnimationState).GetMethod("SetAnimation")!);
        Set(other, "DeclaringType", impostor);
        Set(other, "ReturnType", Import(module, typeof(MegaTrackEntry)));
        var foreign = Method(owner, module, "ForeignAnimation", typeof(void));
        Emit(foreign, "Ldnull"); Emit(foreign, "Ldstr", "idle"); Emit(foreign, "Ldc_I4_1"); Emit(foreign, "Ldc_I4_0");
        Emit(foreign, "Callvirt", other); Emit(foreign, "Pop"); Emit(foreign, "Ret");
        var path = Path.Combine(Path.GetTempPath(), "sc-api-boundaries-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            using var bytes = new MemoryStream();
            Invoke(definition, "Write", bytes);
            File.WriteAllBytes(path, bytes.ToArray());
            var result = Runtime.GetMethod("PrepareForCurrentGame")!.Invoke(null, [path])!;
            var report = Property(result, "Report")!;
            Require(Property(report, "Failure") == null, "边界转换失败：" + report);
            using var converted = (MemoryStream?)Property(result, "Assembly");
            using var original = new MemoryStream(bytes.ToArray());
            var prepared = InvokeStatic(Cecil.GetType("Mono.Cecil.AssemblyDefinition")!, "ReadAssembly", converted ?? original);
            try
            {
                var methods = Items(Property(Items(Property(Property(prepared, "MainModule")!, "Types")!).First(), "Methods")!).ToArray();
                object[] Instructions(string name) => Items(Property(Property(methods.Single(m => (string)Property(m, "Name")! == name), "Body")!, "Instructions")!).ToArray();
                Require(Instructions("OtherCleanup").Any(i => Property(i, "Operand")?.ToString()?.Contains("System.IDisposable::Dispose") == true),
                    "不能删除其它库的 Dispose 调用。");
                var reference = Instructions("ForeignAnimation").Select(i => Property(i, "Operand"))
                    .First(x => x?.ToString()?.Contains("::SetAnimation") == true)!;
                Require((string)Property(Property(Property(reference, "DeclaringType")!, "Scope")!, "Name")! == "NotSts2",
                    "同名但不属于游戏程序集的接口不能被转换：" + Property(Property(reference, "DeclaringType")!, "Scope") + "; " + report);
                var convertedDispose = Instructions("SpineCleanup").Any(i => Property(i, "Operand")?.ToString()?.Contains("__SkinChanger_DisposeSpine") == true);
                Require(convertedDispose == !typeof(IDisposable).IsAssignableFrom(typeof(MegaAnimationState)),
                    "只在旧版缺少 wrapper Dispose 时生成等价的 BoundObject 清理。");
                if (convertedDispose)
                {
                    var cleanup = methods.Single(m => ((string)Property(m, "Name")!).StartsWith("__SkinChanger_DisposeSpine"));
                    var calls = Items(Property(Property(cleanup, "Body")!, "Instructions")!).Select(i => Property(i, "Operand")?.ToString()).ToArray();
                    Require(calls.Any(x => x?.Contains("::get_BoundObject") == true) && calls.Any(x => x?.Contains("Godot.GodotObject::Dispose") == true),
                        "兼容清理必须保留原生包装器释放，不能替换成 pop。");
                }
            }
            finally { ((IDisposable)prepared).Dispose(); }
        }
        finally { ((IDisposable)definition).Dispose(); File.Delete(path); }
    }

    private static void CheckReturnedTrackAdapters()
    {
        var definition = CreateAssembly();
        var context = new AssemblyLoadContext("sc-access-track-probe", isCollectible: true);
        try
        {
            var module = Property(definition, "MainModule")!;
            var owner = Items(Property(module, "Types")!).First();
            var factory = Runtime.GetMethod("CreateAccessTrackAdapter", BindingFlags.NonPublic | BindingFlags.Static)!;
            var names = new Dictionary<string, string>();
            foreach (var name in new[] { "SetAnimation", "AddAnimation" })
            {
                var adapter = factory.Invoke(null, [module, owner, typeof(AccessTrackProbe).GetMethod(name),
                    Cecil, Cecil.GetType("Mono.Cecil.Cil.OpCodes")])!;
                names[name] = (string)Property(adapter, "Name")!;
            }
            using var stream = new MemoryStream();
            Invoke(definition, "Write", stream);
            stream.Position = 0;
            var loaded = context.LoadFromStream(stream);
            var methods = loaded.ManifestModule.GetMethods(BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var name in names.Keys)
            {
                var method = methods.Single(m => m.Name == names[name]);
                var state = new AccessStateProbe();
                object[] args = name == "SetAnimation"
                    ? [new AccessTrackProbe(state), "idle", false, 3]
                    : [new AccessTrackProbe(state), "queued", .25f, false, 7];
                var returned = method.Invoke(null, args);
                Require(ReferenceEquals(returned, name == "SetAnimation" ? state.Current : state.Queued),
                    "必须返回对应的当前/排队轨道，不能把排队动画变成当前动画。");
                Require(state.Track == (name == "SetAnimation" ? 3 : 7) && !state.Loop &&
                    state.Name == (name == "SetAnimation" ? "idle" : "queued") && state.Disposed == 1 &&
                    (name != "AddAnimation" || state.Delay == .25f), "轨道参数或原生包装器清理不正确。");
                state.Throw = true;
                try { method.Invoke(null, args); throw new InvalidOperationException("动画异常不能被吞掉。"); }
                catch (TargetInvocationException exception) when (exception.InnerException is ApplicationException) { }
                Require(state.Disposed == 2, "动画抛错时也必须释放临时状态包装器。");
                args[0] = default(AccessTrackProbe);
                Require(method.Invoke(null, args) == null, "空动画入口必须返回 null，不能调用原生方法。");
            }
        }
        finally { ((IDisposable)definition).Dispose(); context.Unload(); }
    }

    internal static void Audit(string path)
    {
        var result = Runtime.GetMethod("PrepareForCurrentGame")!.Invoke(null, [path])!;
        using var stream = (MemoryStream?)Property(result, "Assembly");
        Console.WriteLine(Property(result, "Report"));
        if (Property(Property(result, "Report")!, "Failure") is string failure)
            throw new InvalidOperationException(failure);
    }

    private static void CheckFixture()
    {
        var definition = CreateAssembly();
        var module = Property(definition, "MainModule")!;
        var owner = Items(Property(module, "Types")!).First();
        var game = typeof(MegaSprite).Assembly;
        var currentVfx = game.GetType("MegaCrit.Sts2.Core.Nodes.Vfx.Ui.NPowerAppliedBuffVfx")
            ?? game.GetType("MegaCrit.Sts2.Core.Nodes.Vfx.NPowerAppliedBuffVfx")!;
        var oldVfx = Import(module, currentVfx);
        Set(oldVfx, "Namespace", currentVfx.Namespace!.EndsWith(".Ui")
            ? "MegaCrit.Sts2.Core.Nodes.Vfx" : "MegaCrit.Sts2.Core.Nodes.Vfx.Ui");
        var token = Method(owner, module, "TypeToken", typeof(RuntimeTypeHandle));
        Emit(token, "Ldtoken", oldVfx); Emit(token, "Ret");

        // Calls from the OTHER snapshot, not just names that look similar.
        foreach (var name in new[] { "SetAnimation", "AddAnimation" })
        {
            var actual = typeof(SpineAnimationAccess).GetMethod(name)!;
            var reference = Import(module, actual);
            var wantsTrack = actual.ReturnType == typeof(void);
            Set(reference, "ReturnType", Import(module, wantsTrack ? typeof(MegaTrackEntry) : typeof(void)));
            var method = Method(owner, module, name, wantsTrack ? typeof(MegaTrackEntry) : typeof(void));
            var variable = Activator.CreateInstance(Cecil.GetType("Mono.Cecil.Cil.VariableDefinition")!, Import(module, typeof(SpineAnimationAccess)))!;
            Invoke(Property(Property(method, "Body")!, "Variables")!, "Add", variable);
            Set(Property(method, "Body")!, "InitLocals", true);
            Emit(method, "Ldloca_S", variable);
            Emit(method, "Ldstr", "idle");
            if (name == "AddAnimation") Emit(method, "Ldc_R4", .25f);
            Emit(method, "Ldc_I4_1"); Emit(method, "Ldc_I4_3");
            Emit(method, "Call", reference); Emit(method, "Ret");
        }

        var missing = Activator.CreateInstance(Cecil.GetType("Mono.Cecil.MethodReference")!,
            "FutureSkinEntry", Import(module, typeof(void)), Import(module, typeof(MegaSprite)))!;
        Set(missing, "HasThis", true);
        var unknown = Method(owner, module, "Unknown", typeof(void));
        Emit(unknown, "Ldnull"); Emit(unknown, "Callvirt", missing); Emit(unknown, "Ret");
        var shortBranch = Method(owner, module, "ShortBranch", typeof(void));
        var local = Activator.CreateInstance(Cecil.GetType("Mono.Cecil.Cil.VariableDefinition")!, Import(module, typeof(SpineAnimationAccess)))!;
        Invoke(Property(Property(shortBranch, "Body")!, "Variables")!, "Add", local);
        Set(Property(shortBranch, "Body")!, "InitLocals", true);
        var landing = InvokeStatic(Cecil.GetType("Mono.Cecil.Cil.Instruction")!, "Create",
            Cecil.GetType("Mono.Cecil.Cil.OpCodes")!.GetField("Ret")!.GetValue(null)!);
        Emit(shortBranch, "Ldc_I4_0"); Emit(shortBranch, "Brtrue_S", landing);
        Emit(shortBranch, "Ldloca_S", local); Emit(shortBranch, "Ldstr", "idle");
        Emit(shortBranch, "Ldc_I4_1"); Emit(shortBranch, "Ldc_I4_0");
        var set = typeof(SpineAnimationAccess).GetMethod("SetAnimation")!;
        var legacy = Import(module, set);
        Set(legacy, "ReturnType", Import(module, set.ReturnType == typeof(void) ? typeof(MegaTrackEntry) : typeof(void)));
        Emit(shortBranch, "Call", legacy);
        if (set.ReturnType == typeof(void)) Emit(shortBranch, "Pop");
        var occupied = Items(Property(Property(shortBranch, "Body")!, "Instructions")!).Skip(2)
            .Sum(instruction => (int)Invoke(instruction, "GetSize"));
        for (var i = occupied; i < 127; i++) Emit(shortBranch, "Nop");
        Invoke(Invoke(Property(shortBranch, "Body")!, "GetILProcessor"), "Append", landing);
        var path = Path.Combine(Path.GetTempPath(), "sc-api-fixture-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            using (var bytes = new MemoryStream())
            {
                Invoke(definition, "Write", bytes);
                File.WriteAllBytes(path, bytes.ToArray());
            }
            var original = File.ReadAllBytes(path);
            var result = Runtime.GetMethod("PrepareForCurrentGame")!.Invoke(null, [path])!;
            var report = Property(result, "Report")!;
            Require(Property(report, "Failure") == null, "已知接口转换不能整体失败：" + report);
            Require(Items(Property(report, "Changes")!).Count() >= 3, "缺少类型迁移或便捷动画接口规则：" + report);
            Require(Items(Property(report, "UnresolvedReferences")!).Any(item => item.ToString()!.Contains("FutureSkinEntry")),
                "未知接口不能被静默忽略或标记为兼容。");
            Require(Items(Property(report, "UnresolvedReferences")!).Count() == 1,
                "已转换的旧引用不能继续被误报为缺失接口：" + report);
            Require(File.ReadAllBytes(path).SequenceEqual(original), "不能改写订阅的原始 DLL。");
            using var stream = (MemoryStream?)Property(result, "Assembly");
            Require(stream != null, "没有生成隔离的兼容程序集。");
            var context = new AssemblyLoadContext("sc-api-fixture", isCollectible: true);
            try
            {
                var prepared = context.LoadFromStream(stream!);
                var methods = prepared.ManifestModule.GetMethods(BindingFlags.Static | BindingFlags.Public);
                Require(Type.GetTypeFromHandle((RuntimeTypeHandle)methods.Single(m => m.Name == "TypeToken").Invoke(null, null)!) == currentVfx,
                    "类型令牌没有迁移到当前版本的实际类型。");
                foreach (var name in new[] { "SetAnimation", "AddAnimation" })
                    Require(methods.Single(m => m.Name == name).Invoke(null, null) == null,
                        "空 SpineAnimationAccess 必须保留原作者的无操作/null 行为。");
                methods.Single(m => m.Name == "ShortBranch").Invoke(null, null);
            }
            finally { context.Unload(); }
            stream!.Position = 0;
            var saved = stream.ToArray();
            File.WriteAllBytes(path, saved);
            var repeated = Runtime.GetMethod("PrepareForCurrentGame")!.Invoke(null, [path])!;
            using var repeatedStream = (MemoryStream?)Property(repeated, "Assembly");
            Require(repeatedStream == null && !Items(Property(Property(repeated, "Report")!, "Changes")!).Any(),
                "重复处理同一程序集不能再次插入桥接或叠加变换。");
        }
        finally { ((IDisposable)definition).Dispose(); File.Delete(path); }
    }

    private static object CreateAssembly() => InvokeStatic(Cecil.GetType("Mono.Cecil.AssemblyDefinition")!, "CreateAssembly",
        Activator.CreateInstance(Cecil.GetType("Mono.Cecil.AssemblyNameDefinition")!, "ApiFixture" + Guid.NewGuid().ToString("N"), new Version(1, 0))!,
        "ApiFixture", Enum.Parse(Cecil.GetType("Mono.Cecil.ModuleKind")!, "Dll"));
    private static object Method(object owner, object module, string name, Type returns)
    {
        var result = Activator.CreateInstance(Cecil.GetType("Mono.Cecil.MethodDefinition")!, name,
            Enum.Parse(Cecil.GetType("Mono.Cecil.MethodAttributes")!, "Public, Static"), Import(module, returns))!;
        Invoke(Property(owner, "Methods")!, "Add", result);
        return result;
    }
    private static void Emit(object method, string code, object? operand = null)
    {
        var op = Cecil.GetType("Mono.Cecil.Cil.OpCodes")!.GetField(code)!.GetValue(null)!;
        var instruction = InvokeStatic(Cecil.GetType("Mono.Cecil.Cil.Instruction")!, "Create", operand == null ? [op] : [op, operand]);
        Invoke(Invoke(Property(method, "Body")!, "GetILProcessor"), "Append", instruction);
    }
    private static object Import(object module, object value) => Invoke(module, "ImportReference", value);
    private static object? Property(object value, string name) => value.GetType().GetProperties()
        .First(p => p.Name == name && p.GetIndexParameters().Length == 0).GetValue(value);
    private static void Set(object value, string name, object setting) => value.GetType().GetProperties()
        .First(p => p.Name == name && p.GetIndexParameters().Length == 0).SetValue(value, setting);
    private static IEnumerable<object> Items(object value) => ((IEnumerable)value).Cast<object>();
    private static object Invoke(object target, string name, params object[] arguments) => Find(target.GetType(), name, arguments).Invoke(target, arguments)!;
    private static object InvokeStatic(Type type, string name, params object[] arguments) => Find(type, name, arguments).Invoke(null, arguments)!;
    private static MethodInfo Find(Type type, string name, object[] args) => type.GetMethods().Single(m => m.Name == name &&
        m.GetParameters().Length == args.Length && m.GetParameters().Select((p, i) => p.ParameterType.IsInstanceOfType(args[i])).All(x => x));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

// A managed boundary for executing the actual emitted IL without loading Godot natives.
public readonly struct AccessTrackProbe(AccessStateProbe? state)
{
    public AccessStateProbe? GetAnimationState() => state;
    public void SetAnimation(string name, bool loop, int track) => throw new NotSupportedException();
    public void AddAnimation(string name, float delay, bool loop, int track) => throw new NotSupportedException();
}

public sealed class AccessStateProbe : IDisposable
{
    public object Current { get; } = new();
    public object Queued { get; } = new();
    public string? Name;
    public float Delay;
    public bool Loop;
    public int Track;
    public int Disposed;
    public bool Throw;
    public void SetAnimation(string name, bool loop, int track)
    {
        (Name, Loop, Track) = (name, loop, track);
        if (Throw) throw new ApplicationException("animation-probe");
    }
    public object GetCurrent(int track) => track == Track ? Current : throw new InvalidOperationException("wrong track");
    public object AddAnimationTracked(string name, float delay, bool loop, int track)
    {
        (Name, Delay, Loop, Track) = (name, delay, loop, track);
        if (Throw) throw new ApplicationException("animation-probe");
        return Queued;
    }
    public void Dispose() => Disposed++;
}
