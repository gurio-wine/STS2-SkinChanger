using HarmonyLib;
using System.Collections;
using System.Reflection;

namespace STS2SkinChanger.Core;

/// <summary>
/// Rewrites provider IL when a cosmetic DLL was compiled against a game API whose method
/// parameters stayed stable but whose return type changed between supported game branches.
/// The CLR includes the return type in a member-reference signature, so a source-compatible
/// change (for example void -> wrapper object) otherwise becomes MissingMethodException.
/// </summary>
internal static class ProviderAssemblyCompatibility
{
    private const string MegaAnimationStateTypeName =
        "MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaAnimationState";
    private static readonly IReadOnlyDictionary<string, string> MegaAnimationMethodAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // v0.111 split the wrapper-returning API from the fire-and-forget API. v0.107 used
            // AddAnimation for the wrapper-returning form.
            ["AddAnimation"] = "AddAnimationTracked",
            ["AddAnimationTracked"] = "AddAnimation"
        };

    public static bool TryRewriteForCurrentGame(
        string assemblyPath,
        out MemoryStream? rewrittenAssembly,
        out int rewrittenCalls,
        out string? failure)
    {
        rewrittenAssembly = null;
        rewrittenCalls = 0;
        failure = null;

        try
        {
            var runtimeMethods = FindRuntimeMegaAnimationMethods();
            if (runtimeMethods.Length == 0)
            {
                return false;
            }

            return TryRewriteReturnTypeDrift(
                assemblyPath,
                runtimeMethods,
                out rewrittenAssembly,
                out rewrittenCalls,
                out failure);
        }
        catch (Exception exception)
        {
            failure = exception.GetBaseException().Message;
            rewrittenAssembly?.Dispose();
            rewrittenAssembly = null;
            rewrittenCalls = 0;
            return false;
        }
    }

    private static MethodInfo[] FindRuntimeMegaAnimationMethods()
    {
        var animationStateType = AccessTools.TypeByName(MegaAnimationStateTypeName);
        return animationStateType?
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.DeclaringType == animationStateType)
            .ToArray() ?? [];
    }

    private static bool TryRewriteReturnTypeDrift(
        string assemblyPath,
        IReadOnlyList<MethodInfo> runtimeMethods,
        out MemoryStream? rewrittenAssembly,
        out int rewrittenCalls,
        out string? failure)
    {
        rewrittenAssembly = null;
        rewrittenCalls = 0;
        failure = null;

        var cecilAssembly = typeof(Harmony).Assembly;
        var assemblyDefinitionType = cecilAssembly.GetType("Mono.Cecil.AssemblyDefinition");
        var methodReferenceType = cecilAssembly.GetType("Mono.Cecil.MethodReference");
        var instructionType = cecilAssembly.GetType("Mono.Cecil.Cil.Instruction");
        var opCodesType = cecilAssembly.GetType("Mono.Cecil.Cil.OpCodes");
        if (assemblyDefinitionType == null ||
            methodReferenceType == null ||
            instructionType == null ||
            opCodesType == null)
        {
            failure = "当前 Harmony 未提供兼容重写器所需的 IL 元数据接口";
            return false;
        }

        var readAssembly = assemblyDefinitionType.GetMethod(
            "ReadAssembly",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: [typeof(Stream)],
            modifiers: null);
        var writeAssembly = assemblyDefinitionType.GetMethod(
            "Write",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(Stream)],
            modifiers: null);
        var createInstruction = instructionType.GetMethods(BindingFlags.Static | BindingFlags.Public)
            .SingleOrDefault(method =>
                method.Name == "Create" &&
                method.GetParameters().Length == 1 &&
                method.GetParameters()[0].ParameterType.FullName == "Mono.Cecil.Cil.OpCode");
        var popOpCode = opCodesType.GetField("Pop", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        var nopOpCode = opCodesType.GetField("Nop", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        if (readAssembly == null ||
            writeAssembly == null ||
            createInstruction == null ||
            popOpCode == null ||
            nopOpCode == null)
        {
            failure = "当前 Harmony 的 IL 元数据接口与兼容重写器不匹配";
            return false;
        }

        using var input = new MemoryStream(File.ReadAllBytes(assemblyPath), writable: false);
        var definition = readAssembly.Invoke(null, [input]);
        if (definition == null)
        {
            failure = "无法读取皮肤 DLL 元数据";
            return false;
        }

        try
        {
            var module = GetRequiredProperty(definition, "MainModule");
            var importReference = module.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Single(method =>
                    method.Name == "ImportReference" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(MethodBase));
            var importedRuntimeMethods = new Dictionary<MethodInfo, object>();

            foreach (var type in EnumerateTypes(module))
            {
                foreach (var method in Enumerate(GetRequiredProperty(type, "Methods")))
                {
                    if (!(bool)GetRequiredProperty(method, "HasBody"))
                    {
                        continue;
                    }

                    var body = GetRequiredProperty(method, "Body");
                    var instructions = Enumerate(GetRequiredProperty(body, "Instructions")).ToArray();
                    var processor = body.GetType()
                        .GetMethod("GetILProcessor", BindingFlags.Instance | BindingFlags.Public)
                        ?.Invoke(body, null);
                    if (processor == null)
                    {
                        throw new InvalidOperationException("无法取得皮肤 DLL 的 IL 编辑器");
                    }

                    foreach (var instruction in instructions)
                    {
                        var operandProperty = instruction.GetType().GetProperty("Operand", BindingFlags.Instance | BindingFlags.Public);
                        var operand = operandProperty?.GetValue(instruction);
                        if (operand == null ||
                            !methodReferenceType.IsInstanceOfType(operand))
                        {
                            continue;
                        }

                        var runtimeMethod = FindMatchingRuntimeMethod(operand, runtimeMethods);
                        if (runtimeMethod == null)
                        {
                            continue;
                        }

                        var runtimeReturnName = runtimeMethod.ReturnType.FullName ?? runtimeMethod.ReturnType.Name;
                        var providerReturnName = GetTypeFullName(GetRequiredProperty(operand, "ReturnType"));
                        var providerMethodName = GetRequiredProperty(operand, "Name") as string;
                        if (providerReturnName == runtimeReturnName &&
                            string.Equals(providerMethodName, runtimeMethod.Name, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (!importedRuntimeMethods.TryGetValue(runtimeMethod, out var runtimeReference))
                        {
                            runtimeReference = importReference.Invoke(module, [runtimeMethod])
                                ?? throw new InvalidOperationException("无法导入当前游戏的动画接口");
                            importedRuntimeMethods[runtimeMethod] = runtimeReference;
                        }

                        if (providerReturnName == runtimeReturnName)
                        {
                            operandProperty!.SetValue(instruction, runtimeReference);
                            rewrittenCalls++;
                            continue;
                        }

                        if (providerReturnName == typeof(void).FullName && runtimeMethod.ReturnType != typeof(void))
                        {
                            operandProperty!.SetValue(instruction, runtimeReference);
                            var pop = createInstruction.Invoke(null, [popOpCode])
                                ?? throw new InvalidOperationException("无法生成返回值清理指令");
                            InvokeProcessor(processor, "InsertAfter", instruction, pop);
                            rewrittenCalls++;
                            continue;
                        }

                        if (runtimeMethod.ReturnType == typeof(void) &&
                            TryReplaceFollowingPopWithNop(instructions, instruction, nopOpCode))
                        {
                            operandProperty!.SetValue(instruction, runtimeReference);
                            rewrittenCalls++;
                        }
                    }
                }
            }

            if (rewrittenCalls == 0)
            {
                return false;
            }

            rewrittenAssembly = new MemoryStream();
            writeAssembly.Invoke(definition, [rewrittenAssembly]);
            rewrittenAssembly.Position = 0;
            return true;
        }
        finally
        {
            (definition as IDisposable)?.Dispose();
        }
    }

    private static MethodInfo? FindMatchingRuntimeMethod(
        object methodReference,
        IReadOnlyList<MethodInfo> runtimeMethods)
    {
        if (!string.Equals(
                GetTypeFullName(GetRequiredProperty(methodReference, "DeclaringType")),
                MegaAnimationStateTypeName,
                StringComparison.Ordinal))
        {
            return null;
        }

        var methodName = GetRequiredProperty(methodReference, "Name") as string;
        var providerReturnName = GetTypeFullName(GetRequiredProperty(methodReference, "ReturnType"));
        var parameterNames = Enumerate(GetRequiredProperty(methodReference, "Parameters"))
            .Select(parameter => GetTypeFullName(GetRequiredProperty(parameter, "ParameterType")))
            .ToArray();
        var sameName = runtimeMethods.Where(method =>
            string.Equals(method.Name, methodName, StringComparison.Ordinal) &&
            method.GetParameters()
                .Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)
                .SequenceEqual(parameterNames, StringComparer.Ordinal))
            .ToArray();
        var exactReturn = sameName.SingleOrDefault(method =>
            string.Equals(
                method.ReturnType.FullName ?? method.ReturnType.Name,
                providerReturnName,
                StringComparison.Ordinal));
        if (exactReturn != null)
        {
            return exactReturn;
        }

        if (methodName != null &&
            MegaAnimationMethodAliases.TryGetValue(methodName, out var aliasName))
        {
            var alias = runtimeMethods.SingleOrDefault(method =>
                string.Equals(method.Name, aliasName, StringComparison.Ordinal) &&
                string.Equals(
                    method.ReturnType.FullName ?? method.ReturnType.Name,
                    providerReturnName,
                    StringComparison.Ordinal) &&
                method.GetParameters()
                    .Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)
                    .SequenceEqual(parameterNames, StringComparer.Ordinal));
            if (alias != null)
            {
                return alias;
            }
        }

        return sameName.SingleOrDefault();
    }

    private static bool TryReplaceFollowingPopWithNop(
        IReadOnlyList<object> instructions,
        object callInstruction,
        object nopOpCode)
    {
        var index = -1;
        for (var i = 0; i < instructions.Count; i++)
        {
            if (ReferenceEquals(instructions[i], callInstruction))
            {
                index = i;
                break;
            }
        }

        if (index < 0 || index + 1 >= instructions.Count)
        {
            return false;
        }

        var next = instructions[index + 1];
        var opCodeProperty = next.GetType().GetProperty("OpCode", BindingFlags.Instance | BindingFlags.Public);
        var opCode = opCodeProperty?.GetValue(next);
        var name = opCode?.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(opCode) as string;
        if (!string.Equals(name, "pop", StringComparison.Ordinal))
        {
            return false;
        }

        opCodeProperty!.SetValue(next, nopOpCode);
        next.GetType().GetProperty("Operand", BindingFlags.Instance | BindingFlags.Public)?.SetValue(next, null);
        return true;
    }

    private static IEnumerable<object> EnumerateTypes(object module)
    {
        foreach (var type in Enumerate(GetRequiredProperty(module, "Types")))
        {
            foreach (var nested in EnumerateTypeAndNested(type))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<object> EnumerateTypeAndNested(object type)
    {
        yield return type;
        foreach (var nested in Enumerate(GetRequiredProperty(type, "NestedTypes")))
        {
            foreach (var item in EnumerateTypeAndNested(nested))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<object> Enumerate(object value)
    {
        return value is IEnumerable enumerable
            ? enumerable.Cast<object>()
            : throw new InvalidOperationException($"{value.GetType().FullName} 不是可枚举的元数据集合");
    }

    private static object GetRequiredProperty(object value, string name)
    {
        var valueType = value.GetType();
        var property = valueType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(candidate => candidate.Name == name && candidate.GetIndexParameters().Length == 0)
            .OrderBy(candidate => candidate.DeclaringType == valueType ? 0 : 1)
            .FirstOrDefault();
        return property?.GetValue(value)
               ?? throw new MissingMemberException(value.GetType().FullName, name);
    }

    private static string GetTypeFullName(object typeReference)
    {
        return GetRequiredProperty(typeReference, "FullName") as string ?? string.Empty;
    }

    private static void InvokeProcessor(object processor, string methodName, params object[] arguments)
    {
        var method = processor.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(candidate =>
                candidate.Name == methodName &&
                candidate.GetParameters().Length == arguments.Length &&
                candidate.GetParameters()
                    .Select((parameter, index) => parameter.ParameterType.IsInstanceOfType(arguments[index]))
                    .All(matches => matches));
        method.Invoke(processor, arguments);
    }
}
