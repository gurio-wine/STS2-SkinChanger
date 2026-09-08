using System.Reflection;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;

namespace STS2SkinChanger.Core;

internal sealed record ProviderCompatibilityResult(MemoryStream? Assembly, ProviderCompatibilityReport Report);

internal sealed class ProviderCompatibilityReport
{
    public string Target { get; init; } = string.Empty;
    public List<string> Changes { get; } = [];
    public List<string> UnresolvedReferences { get; } = [];
    public int UncheckedGenericReferences { get; set; }
    public string? Failure { get; set; }
    public override string ToString() =>
        $"目标 {Target}；转换 {Changes.Count} 项；未解析 {UnresolvedReferences.Count} 项；" +
        $"泛型引用未验证 {UncheckedGenericReferences} 项；失败：{Failure ?? "无"}\n" +
        string.Join("\n", Changes.Concat(UnresolvedReferences.Select(item => "未处理：" + item)));
}

internal static partial class ProviderAssemblyCompatibility
{
    private static readonly Assembly GameAssembly = typeof(MegaSprite).Assembly;

    public static ProviderCompatibilityResult PrepareForCurrentGame(string assemblyPath)
    {
        var report = new ProviderCompatibilityReport
        {
            Target = $"{GameAssembly.GetName().Name}/{GameAssembly.ManifestModule.ModuleVersionId}"
        };
        MemoryStream? stream = null;
        try
        {
            TryRewriteRuntimeDrift(assemblyPath, FindRuntimeMegaAnimationMethods(), report,
                out stream, out _, out var failure);
            report.Failure = failure;
        }
        catch (Exception exception)
        {
            report.Failure = exception.GetBaseException().Message;
        }
        if (report.Failure != null)
        {
            stream?.Dispose();
            stream = null;
            report.Changes.Clear(); // No partially rewritten output can escape on failure.
        }
        return new(stream, report);
    }

    private static bool IsGameTypeReference(object type)
    {
        // Name alone is not ownership: another DLL may declare an identically named type.
        var scope = GetRequiredProperty(type, "Scope");
        return scope.GetType().FullName == "Mono.Cecil.AssemblyNameReference" &&
               (string)GetRequiredProperty(scope, "Name") == GameAssembly.GetName().Name;
    }

    private static object[] References(object module, string method) =>
        Enumerate(module.GetType().GetMethod(method, Type.EmptyTypes)!.Invoke(module, null)!).ToArray();

    private static void RewriteKnownTypeMoves(object module, ProviderCompatibilityReport report)
    {
        // Only these two type moves have equivalent factories/behaviour in both snapshots.
        // Do not generalize this to all Vfx types: several were split and need new arguments.
        foreach (var reference in References(module, "GetTypeReferences"))
        {
            if (!IsGameTypeReference(reference)) continue;
            var before = GetTypeFullName(reference);
            if (GameAssembly.GetType(before) != null) continue;
            var name = (string)GetRequiredProperty(reference, "Name");
            var ns = (string)GetRequiredProperty(reference, "Namespace");
            if (name is not ("NPowerAppliedBuffVfx" or "NPowerAppliedDebuffVfx") ||
                ns is not ("MegaCrit.Sts2.Core.Nodes.Vfx" or "MegaCrit.Sts2.Core.Nodes.Vfx.Ui")) continue;
            var targetNamespace = ns.EndsWith(".Ui", StringComparison.Ordinal)
                ? "MegaCrit.Sts2.Core.Nodes.Vfx" : "MegaCrit.Sts2.Core.Nodes.Vfx.Ui";
            if (GameAssembly.GetType(targetNamespace + "." + name) == null) continue;
            reference.GetType().GetProperty("Namespace")!.SetValue(reference, targetNamespace);
            report.Changes.Add($"Type relocation: {before} -> {GetTypeFullName(reference)}");
        }
    }

    private static void AuditGameReferences(object module, ProviderCompatibilityReport report)
    {
        // Metadata only: no provider code is loaded or invoked. These are unresolved *static*
        // references, not proof that a guarded/unused path will run. Do not block the entire
        // skin based on this list; strings/reflection and behavioural changes are not proved.
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var types = new Dictionary<string, Type?>(StringComparer.Ordinal);
        Type? Resolve(object reference)
        {
            var name = GetTypeFullName(reference).Replace('/', '+');
            if (!types.TryGetValue(name, out var type)) types[name] = type = GameAssembly.GetType(name);
            return type;
        }
        foreach (var reference in References(module, "GetTypeReferences"))
            if (IsGameTypeReference(reference) && Resolve(reference) == null)
                unresolved.Add("Type: " + GetTypeFullName(reference));

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                   BindingFlags.Static | BindingFlags.FlattenHierarchy;
        foreach (var member in References(module, "GetMemberReferences"))
        {
            var owner = GetRequiredProperty(member, "DeclaringType");
            if (!IsGameTypeReference(owner)) continue;
            if (GetTypeFullName(owner).Contains('<') || (bool)GetRequiredProperty(member, "ContainsGenericParameter") ||
                member.GetType().GetProperty("HasGenericParameters")?.GetValue(member) is true)
            {
                report.UncheckedGenericReferences++;
                continue;
            }
            var type = Resolve(owner);
            if (type == null) continue; // Already listed as a missing type.
            var name = (string)GetRequiredProperty(member, "Name");
            bool found;
            if (member.GetType().GetProperty("ReturnType") != null)
            {
                var parameters = Enumerate(GetRequiredProperty(member, "Parameters"))
                    .Select(parameter => GetTypeFullName(GetRequiredProperty(parameter, "ParameterType"))).ToArray();
                var returns = GetTypeFullName(GetRequiredProperty(member, "ReturnType"));
                var hasThis = (bool)GetRequiredProperty(member, "HasThis");
                IEnumerable<MethodBase> candidates = name is ".ctor" or ".cctor"
                    ? type.GetConstructors(flags) : type.GetMethods(flags).Where(method => method.Name == name);
                found = candidates.Any(method => !method.IsStatic == hasThis && !method.IsGenericMethod &&
                    RuntimeTypeName(method is MethodInfo info ? info.ReturnType : typeof(void)) == returns &&
                    method.GetParameters().Select(p => RuntimeTypeName(p.ParameterType)).SequenceEqual(parameters));
            }
            else
            {
                found = type.GetFields(flags).Any(field => field.Name == name &&
                    RuntimeTypeName(field.FieldType) == GetTypeFullName(GetRequiredProperty(member, "FieldType")));
            }
            if (!found) unresolved.Add(member.ToString()!);
        }
        report.UnresolvedReferences.AddRange(unresolved);
    }

    private static string RuntimeTypeName(Type type)
    {
        if (type.IsByRef) return RuntimeTypeName(type.GetElementType()!) + "&";
        if (type.IsArray) return RuntimeTypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsConstructedGenericType)
            return type.GetGenericTypeDefinition().FullName!.Replace('+', '/') + "<" +
                string.Join(",", type.GetGenericArguments().Select(RuntimeTypeName)) + ">";
        return (type.FullName ?? type.Name).Replace('+', '/');
    }

    private static object Import(object module, object value)
    {
        var parameterType = value is Type ? typeof(Type) : typeof(MethodBase);
        return module.GetType().GetMethod("ImportReference", [parameterType])!.Invoke(module, [value])!;
    }

    private static object NewAdapter(object module, object owner, string name, object returns,
        IEnumerable<object> parameterTypes, Assembly cecil)
    {
        var methods = GetRequiredProperty(owner, "Methods");
        var names = Enumerate(methods).Select(m => (string)GetRequiredProperty(m, "Name")).ToHashSet();
        var baseName = "__SkinChanger_" + name;
        name = baseName;
        for (var suffix = 1; names.Contains(name); suffix++) name = baseName + "_" + suffix;
        var method = Activator.CreateInstance(cecil.GetType("Mono.Cecil.MethodDefinition")!, name,
            Enum.Parse(cecil.GetType("Mono.Cecil.MethodAttributes")!, "Private, Static, HideBySig"), returns)!;
        foreach (var parameter in parameterTypes)
            InvokeProcessor(GetRequiredProperty(method, "Parameters"), "Add",
                Activator.CreateInstance(cecil.GetType("Mono.Cecil.ParameterDefinition")!, parameter)!);
        InvokeProcessor(methods, "Add", method);
        return method;
    }

    private static object Instruction(string code, Assembly cecil, Type opCodes, object? operand = null)
    {
        var op = opCodes.GetField(code)!.GetValue(null)!;
        object[] arguments = operand == null ? [op] : [op, operand];
        return cecil.GetType("Mono.Cecil.Cil.Instruction")!.GetMethods().Single(m => m.Name == "Create" &&
            m.GetParameters().Length == arguments.Length && m.GetParameters().Select((p, i) =>
                p.ParameterType.IsInstanceOfType(arguments[i])).All(x => x)).Invoke(null, arguments)!;
    }

    private static object Emit(object method, string code, Assembly cecil, Type opCodes, object? operand = null)
    {
        var body = GetRequiredProperty(method, "Body");
        var processor = body.GetType().GetMethod("GetILProcessor")!.Invoke(body, null)!;
        var instruction = Instruction(code, cecil, opCodes, operand);
        InvokeProcessor(processor, "Append", instruction);
        return instruction;
    }

    private static object CreateAccessTrackAdapter(object module, object owner, MethodInfo runtime,
        Assembly cecil, Type opCodes)
    {
        var accessType = runtime.DeclaringType!;
        var getState = accessType.GetMethod("GetAnimationState")!;
        var stateType = getState.ReturnType;
        // Set returns the current track. Add returns the queued track, which need not be
        // current yet. Calling GetCurrent for Add would silently corrupt author settings.
        var tracked = stateType.GetMethod(runtime.Name == "AddAnimation" ? "AddAnimationTracked" : "SetAnimation",
            runtime.GetParameters().Select(p => p.ParameterType).ToArray())!;
        var returns = Import(module, stateType.GetMethod("GetCurrent", [typeof(int)])!.ReturnType);
        var byRef = Activator.CreateInstance(cecil.GetType("Mono.Cecil.ByReferenceType")!, Import(module, accessType))!;
        var adapter = NewAdapter(module, owner, runtime.Name + "AccessTracked", returns,
            new[] { byRef }.Concat(runtime.GetParameters().Select(p => Import(module, p.ParameterType))), cecil);
        var body = GetRequiredProperty(adapter, "Body");
        body.GetType().GetProperty("InitLocals")!.SetValue(body, true);
        object Local(object type)
        {
            var variable = Activator.CreateInstance(cecil.GetType("Mono.Cecil.Cil.VariableDefinition")!, type)!;
            InvokeProcessor(GetRequiredProperty(body, "Variables"), "Add", variable);
            return variable;
        }
        var state = Local(Import(module, stateType));
        var result = Local(returns);
        object E(string code, object? operand = null) => Emit(adapter, code, cecil, opCodes, operand);
        var finish = Instruction("Ldloc", cecil, opCodes, result);
        E("Ldarg_0");
        E("Call", Import(module, getState));
        E("Stloc", state);
        E("Ldloc", state);
        E("Brfalse", finish); // Invalid access preserves the game's null/no-op contract.
        var start = E("Ldloc", state);
        var parameters = Enumerate(GetRequiredProperty(adapter, "Parameters")).ToArray();
        for (var i = 1; i < parameters.Length; i++) E("Ldarg", parameters[i]);
        E("Callvirt", Import(module, tracked));
        if (tracked.ReturnType == typeof(void))
        {
            E("Ldloc", state);
            E("Ldarg", parameters[^1]); // The author's track, never hard-coded zero.
            E("Callvirt", Import(module, stateType.GetMethod("GetCurrent", [typeof(int)])!));
        }
        E("Stloc", result);
        E("Leave", finish);
        var dispose = stateType.GetMethod("Dispose", Type.EmptyTypes)
            ?? throw new MissingMethodException(stateType.FullName, "Dispose");
        var cleanup = E("Ldloc", state);
        E("Callvirt", Import(module, dispose));
        E("Endfinally");
        InvokeProcessor(body.GetType().GetMethod("GetILProcessor")!.Invoke(body, null)!, "Append", finish);
        E("Ret");
        var handler = Activator.CreateInstance(cecil.GetType("Mono.Cecil.Cil.ExceptionHandler")!,
            Enum.Parse(cecil.GetType("Mono.Cecil.Cil.ExceptionHandlerType")!, "Finally"))!;
        foreach (var (name, value) in new[] { ("TryStart", start), ("TryEnd", cleanup),
                     ("HandlerStart", cleanup), ("HandlerEnd", finish) })
            handler.GetType().GetProperty(name)!.SetValue(handler, value);
        InvokeProcessor(GetRequiredProperty(body, "ExceptionHandlers"), "Add", handler);
        return adapter;
    }

    private static void ExpandShortBranches(object body, Type opCodes)
    {
        var codes = opCodes.GetFields(BindingFlags.Public | BindingFlags.Static).Select(field => field.GetValue(null)!)
            .ToDictionary(op => (string)GetRequiredProperty(op, "Name"), StringComparer.Ordinal);
        foreach (var instruction in Enumerate(GetRequiredProperty(body, "Instructions")))
        {
            var code = GetRequiredProperty(instruction, "OpCode");
            if (GetRequiredProperty(code, "OperandType").ToString() != "ShortInlineBrTarget") continue;
            var name = (string)GetRequiredProperty(code, "Name");
            instruction.GetType().GetProperty("OpCode")!.SetValue(instruction, codes[name[..^2]]);
        }
    }
}
