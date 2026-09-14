using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using STS2SkinChanger;

// "3D Card Effects" ships Microsoft.ML.OnnxRuntime.dll beside its own DLL and dies with
// "Could not load file or assembly 'Microsoft.ML.OnnxRuntime'" ten times in the reported log. The
// game loads every Mod DLL with AssemblyLoadContext.LoadFromAssemblyPath, whose dependency probing
// covers the game folder only, and its AssemblyResolve fallback answers for sts2 and 0Harmony alone.
// This test reproduces that exact loader behaviour with a generated fixture pair: the same layout
// must fail before Skin Changer installs its resolver and resolve after.
internal static class ModPrivateDependencyResolverTests
{
    private static readonly Assembly Cecil = typeof(Harmony).Assembly;
    private static readonly Type Resolver = typeof(Entry).Assembly.GetType(
        "STS2SkinChanger.Core.ModPrivateDependencyResolver", true)!;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "sc-mod-dependency-" + suffix);
        var elsewhere = Path.Combine(Path.GetTempPath(), "sc-mod-dependency-elsewhere-" + suffix);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(elsewhere);
        try
        {
            var context = AssemblyLoadContext.GetLoadContext(typeof(Entry).Assembly)
                          ?? AssemblyLoadContext.Default;

            // 1. The loader as the game uses it: a dependency the Mod ships in the very folder it is
            //    loaded from is NOT probed, so the call fails at runtime.
            var absentDependency = "ScAbsentDep" + suffix;
            var absentSource = Path.Combine(elsewhere, absentDependency + ".dll");
            WriteDependency(absentSource, "ScFixture.Dependency", "absent");
            var absentConsumer = Path.Combine(root, "ScAbsentConsumer" + suffix + ".dll");
            WriteConsumer(absentConsumer, "ScFixture.AbsentEntry", absentSource);
            RequireFails(
                () => Read(context, absentConsumer, "ScFixture.AbsentEntry"),
                absentDependency,
                "游戏加载器不探测 Mod 自己的目录这一前提不成立，后面的修复就无法证明。");

            // 2. Skin Changer's resolver, installed through the production entry point. The offline
            //    host has no game logger, so the reporting channels are captured here instead; the
            //    resolution itself is exactly the production path.
            var infos = new List<string>();
            var warnings = new List<string>();
            Resolver.GetMethod("Install", Static)!.Invoke(null, [((Action<string>)infos.Add)!, (Action<string>)warnings.Add]);
            var presentDependency = "ScPresentDep" + suffix;
            var presentSource = Path.Combine(root, presentDependency + ".dll");
            WriteDependency(presentSource, "ScFixture.Dependency", "resolved");
            var presentConsumer = Path.Combine(root, "ScPresentConsumer" + suffix + ".dll");
            WriteConsumer(presentConsumer, "ScFixture.PresentEntry", presentSource);
            var value = Read(context, presentConsumer, "ScFixture.PresentEntry");
            Require(value == "resolved",
                "按 Mod 自身目录解析后，随包依赖必须真的能调用，实际得到：" + (value ?? "<null>"));
            Require(infos.Count == 1 && infos[0].Contains(presentDependency, StringComparison.Ordinal) &&
                    warnings.Count == 0,
                "解析成功必须正好报告一次并点名依赖，用户才能在日志里看到这个兼容动作；实际：" +
                string.Join(" / ", infos.Concat(warnings)));

            // 3. The resolved dependency needs its own native probing: OnnxRuntime loads
            //    onnxruntime.dll by DllImport from the same package folder.
            var probes = (IEnumerable)Resolver.GetField("NativeProbes", Static)!.GetValue(null)!;
            var attached = probes.Cast<object>().Any(entry => ((Assembly)entry.GetType()
                    .GetProperty("Key")!.GetValue(entry)!).GetName().Name == presentDependency);
            Require(attached,
                "解析成功的随包依赖必须同时获得原生探测，否则它的 onnxruntime.dll 仍然找不到。");

            var probe = Resolver.GetMethod("TryLoadNativeProbe", Static)!;
            Require((IntPtr)probe.Invoke(null, [root, "sc_absent_native_" + suffix])! == IntPtr.Zero,
                "随包目录里没有这个原生库时必须交回默认探测，不能谎报成功。");
            var systemVersion = Path.Combine(Environment.SystemDirectory, "version.dll");
            if (File.Exists(systemVersion))
            {
                var native = "sc_native_probe_" + suffix;
                File.Copy(systemVersion, Path.Combine(root, native + ".dll"));
                Require((IntPtr)probe.Invoke(null, [root, native])! != IntPtr.Zero,
                    "随包目录里确实存在的原生库必须被加载。");
            }

            Console.WriteLine(
                "Mod private dependency resolution passed: the game's own loader fails on a bundled dependency, Skin Changer resolves it plus its native probe.");
        }
        finally
        {
            // The fixture assemblies stay loaded in the default context on purpose (that is how the
            // game loads Mods), so Windows keeps their files open; the temp folder is disposable.
            foreach (var directory in new[] { root, elsewhere })
            {
                try { Directory.Delete(directory, recursive: true); } catch (Exception) { }
            }
        }
    }

    private static string? Read(AssemblyLoadContext context, string consumerPath, string typeName)
    {
        var assembly = context.LoadFromAssemblyPath(consumerPath);
        var method = assembly.GetType(typeName, throwOnError: true)!
            .GetMethod("Read", BindingFlags.Public | BindingFlags.Static)!;
        try
        {
            return (string?)method.Invoke(null, null);
        }
        catch (TargetInvocationException exception)
        {
            throw exception.GetBaseException();
        }
    }

    private static void RequireFails(Func<object?> action, string dependencyName, string reason)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            var reported = exception.GetBaseException();
            Require(reported is FileNotFoundException &&
                    reported.Message.Contains(dependencyName, StringComparison.OrdinalIgnoreCase),
                reason + " 实际异常：" + reported.GetType().Name + "：" + reported.Message);
            return;
        }

        throw new InvalidOperationException(reason + "（依赖竟然解析成功了）");
    }

    private static void WriteDependency(string path, string typeName, string value)
    {
        var definition = CreateAssembly(Path.GetFileNameWithoutExtension(path));
        try
        {
            var module = Property(definition, "MainModule")!;
            var type = CreateType(module, typeName);
            var method = Activator.CreateInstance(Cecil.GetType("Mono.Cecil.MethodDefinition")!, "Value",
                MethodAttributes("Public, Static, HideBySig"), Import(module, typeof(string)))!;
            Invoke(Property(type, "Methods")!, "Add", method);
            Emit(method, "Ldstr", value);
            Emit(method, "Ret");
            Write(definition, path);
        }
        finally
        {
            ((IDisposable)definition).Dispose();
        }
    }

    // The consumer is compiled against the dependency's identity only. The file that carries it is
    // read solely to obtain that reference, exactly like a Mod compiled against a library it ships.
    private static void WriteConsumer(string path, string typeName, string dependencyPath)
    {
        var dependency = ReadAssembly(dependencyPath);
        try
        {
            var dependencyModule = Property(dependency, "MainModule")!;
            var dependencyType = ((IEnumerable)Property(dependencyModule, "Types")!).Cast<object>()
                .First(type => (string)Property(type, "Name")! == "Dependency");
            var dependencyMethod = ((IEnumerable)Property(dependencyType, "Methods")!).Cast<object>()
                .First(method => (string)Property(method, "Name")! == "Value");

            var definition = CreateAssembly(Path.GetFileNameWithoutExtension(path));
            try
            {
                var module = Property(definition, "MainModule")!;
                var type = CreateType(module, typeName);
                var method = Activator.CreateInstance(Cecil.GetType("Mono.Cecil.MethodDefinition")!, "Read",
                    MethodAttributes("Public, Static, HideBySig"), Import(module, typeof(string)))!;
                Invoke(Property(type, "Methods")!, "Add", method);
                Emit(method, "Call", ImportMethod(module, dependencyMethod));
                Emit(method, "Ret");
                Write(definition, path);
            }
            finally
            {
                ((IDisposable)definition).Dispose();
            }
        }
        finally
        {
            ((IDisposable)dependency).Dispose();
        }
    }

    private static object CreateAssembly(string name)
    {
        var assemblyName = Activator.CreateInstance(
            Cecil.GetType("Mono.Cecil.AssemblyNameDefinition")!, name, new Version(1, 0))!;
        return InvokeStatic(Cecil.GetType("Mono.Cecil.AssemblyDefinition")!, "CreateAssembly",
            assemblyName, name, Enum.Parse(Cecil.GetType("Mono.Cecil.ModuleKind")!, "Dll"));
    }

    private static object ReadAssembly(string path) =>
        InvokeStatic(Cecil.GetType("Mono.Cecil.AssemblyDefinition")!, "ReadAssembly", path);

    private static object CreateType(object module, string fullName)
    {
        var separator = fullName.LastIndexOf('.');
        var type = Activator.CreateInstance(Cecil.GetType("Mono.Cecil.TypeDefinition")!,
            separator < 0 ? string.Empty : fullName[..separator],
            separator < 0 ? fullName : fullName[(separator + 1)..],
            Enum.Parse(Cecil.GetType("Mono.Cecil.TypeAttributes")!,
                "Public, Class, Abstract, Sealed, BeforeFieldInit"))!;
        Invoke(Property(module, "Types")!, "Add", type);
        type.GetType().GetProperty("BaseType")!.SetValue(type, Import(module, typeof(object)));
        return type;
    }

    private static object MethodAttributes(string names) =>
        Enum.Parse(Cecil.GetType("Mono.Cecil.MethodAttributes")!, names);

    private static void Write(object definition, string path)
    {
        using var stream = File.Create(path);
        Invoke(definition, "Write", stream);
    }

    private static object Emit(object method, string code, object? operand = null)
    {
        var op = Cecil.GetType("Mono.Cecil.Cil.OpCodes")!.GetField(code)!.GetValue(null)!;
        object[] arguments = operand == null ? [op] : [op, operand];
        var instruction = InvokeStatic(Cecil.GetType("Mono.Cecil.Cil.Instruction")!, "Create", arguments);
        Invoke(Invoke(Property(method, "Body")!, "GetILProcessor"), "Append", instruction);
        return instruction;
    }

    private static object Import(object module, object value) => Invoke(module, "ImportReference", value);

    private static object ImportMethod(object module, object methodDefinition) =>
        module.GetType().GetMethods().Single(method => method.Name == "ImportReference" &&
            method.GetParameters().Length == 1 &&
            method.GetParameters()[0].ParameterType.Name == "MethodReference")
            .Invoke(module, [methodDefinition])!;

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
