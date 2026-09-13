using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using STS2SkinChanger;

// A provider DLL may only be taken over when its code can actually run on the game that is
// running. The audit JIT-compiles every method of the shipped file without executing anything.
internal static class ProviderRuntimeAuditTests
{
    private static readonly Assembly Cecil = typeof(Harmony).Assembly;
    private static readonly Type Runtime = typeof(Entry).Assembly
        .GetType("STS2SkinChanger.Core.ProviderAssemblyCompatibility", true)!;

    public static void Run()
    {
        var runnable = Path.Combine(Path.GetTempPath(), "sc-audit-runnable-" + Guid.NewGuid().ToString("N") + ".dll");
        var broken = Path.Combine(Path.GetTempPath(), "sc-audit-broken-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            WriteFixture(runnable, resolvable: true);
            WriteFixture(broken, resolvable: false);

            var accepted = Audit(runnable);
            Require((bool)Property(accepted, "CanRun")!,
                "静态引用都能解析的皮肤 DLL 必须被判定为可在当前游戏运行：" + Property(accepted, "Failure"));
            Require((int)Property(accepted, "PreparedMethods")! > 0,
                "核验必须真的编译皮肤方法，不能只看元数据或直接放行。");
            // The audited file must stay replaceable: provider folders are swapped while the game runs.
            File.Delete(runnable);
            Require(!File.Exists(runnable), "核验不能锁住被检查的皮肤 DLL。");

            var rejected = Audit(broken);
            Require(!(bool)Property(rejected, "CanRun")!,
                "引用了当前游戏不存在的方法时不能放行，否则接管后必然在 JIT 阶段失败。");
            var failure = (string)Property(rejected, "Failure")!;
            Require(failure.Contains("UnknownEntry", StringComparison.Ordinal),
                "拒绝原因必须指向具体方法，用户才能判断是作者问题还是游戏版本问题：" + failure);
            Console.WriteLine("Provider runtime audit passed: runnable DLLs accepted, unresolved members rejected by JIT.");
        }
        finally
        {
            foreach (var path in new[] { runnable, broken })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private static object Audit(string path) =>
        Runtime.GetMethod("AuditForCurrentGame")!.Invoke(null, [path])!;

    internal static void WriteFixture(string path, bool resolvable)
    {
        var definition = InvokeStatic(Cecil.GetType("Mono.Cecil.AssemblyDefinition")!, "CreateAssembly",
            Activator.CreateInstance(Cecil.GetType("Mono.Cecil.AssemblyNameDefinition")!,
                "AuditFixture" + Guid.NewGuid().ToString("N"), new Version(1, 0))!,
            "AuditFixture", Enum.Parse(Cecil.GetType("Mono.Cecil.ModuleKind")!, "Dll"));
        try
        {
            var module = Property(definition, "MainModule")!;
            var owner = ((IEnumerable)Property(module, "Types")!).Cast<object>().First();
            var method = Activator.CreateInstance(Cecil.GetType("Mono.Cecil.MethodDefinition")!, "Probe",
                Enum.Parse(Cecil.GetType("Mono.Cecil.MethodAttributes")!, "Public, Static"),
                Import(module, typeof(void)))!;
            Invoke(Property(owner, "Methods")!, "Add", method);
            var operand = resolvable
                ? Import(module, typeof(MegaSprite).GetMethod("GetSkeleton")!)
                : Activator.CreateInstance(Cecil.GetType("Mono.Cecil.MethodReference")!, "UnknownEntry",
                    Import(module, typeof(MegaSkeleton)), Import(module, typeof(MegaSprite)))!;
            if (resolvable)
            {
                Emit(method, "Ldnull");
                Emit(method, "Callvirt", operand);
                Emit(method, "Pop");
            }
            else
            {
                Emit(method, "Ldnull");
                Emit(method, "Callvirt", operand);
            }
            Emit(method, "Ret");
            using var stream = File.Create(path);
            Invoke(definition, "Write", stream);
        }
        finally
        {
            ((IDisposable)definition).Dispose();
        }
    }

    private static object Emit(object method, string code, object? operand = null)
    {
        var opCodes = Cecil.GetType("Mono.Cecil.Cil.OpCodes")!;
        var op = opCodes.GetField(code)!.GetValue(null)!;
        object[] arguments = operand == null ? [op] : [op, operand];
        var instruction = InvokeStatic(Cecil.GetType("Mono.Cecil.Cil.Instruction")!, "Create", arguments);
        Invoke(Invoke(Property(method, "Body")!, "GetILProcessor"), "Append", instruction);
        return instruction;
    }

    private static object Import(object module, object value) => Invoke(module, "ImportReference", value);

    private static object? Property(object value, string name) => value.GetType().GetProperties()
        .First(property => property.Name == name && property.GetIndexParameters().Length == 0).GetValue(value);
    private static object Invoke(object target, string name, params object[] arguments) =>
        Find(target.GetType(), name, arguments).Invoke(target, arguments)!;
    private static object InvokeStatic(Type type, string name, params object[] arguments) =>
        Find(type, name, arguments).Invoke(null, arguments)!;
    private static MethodInfo Find(Type type, string name, object[] args) => type.GetMethods().Single(method =>
        method.Name == name && method.GetParameters().Length == args.Length &&
        method.GetParameters().Select((parameter, index) => parameter.ParameterType.IsInstanceOfType(args[index])).All(x => x));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
