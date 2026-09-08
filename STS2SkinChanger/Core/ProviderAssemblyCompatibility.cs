using HarmonyLib;
using System.Collections;
using System.Reflection;

namespace STS2SkinChanger.Core;

/// <summary>
/// Rewrites only verified equivalent game API changes in a cosmetic DLL's in-memory IL.
/// The CLR includes return types in member signatures, so even a source-compatible change
/// can need a bridge. Unknown game references are reported, never guessed or deleted.
/// </summary>
internal static partial class ProviderAssemblyCompatibility
{
    private const string MegaAnimationStateTypeName =
        "MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaAnimationState";
    private const string SpineAnimationAccessTypeName =
        "MegaCrit.Sts2.Core.Bindings.MegaSpine.SpineAnimationAccess";
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
        var prepared = PrepareForCurrentGame(assemblyPath);
        rewrittenAssembly = prepared.Assembly;
        rewrittenCalls = prepared.Report.Changes.Count;
        failure = prepared.Report.Failure;
        return rewrittenAssembly != null;
    }

    private static MethodInfo[] FindRuntimeMegaAnimationMethods()
    {
        return new[] { MegaAnimationStateTypeName, SpineAnimationAccessTypeName }
            .Select(name => GameAssembly.GetType(name))
            .Where(type => type != null)
            .SelectMany(type => type!.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .ToArray();
    }

    private static bool TryRewriteRuntimeDrift(
        string assemblyPath,
        IReadOnlyList<MethodInfo> runtimeMethods,
        ProviderCompatibilityReport report,
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
            RewriteKnownTypeMoves(module, report);
            var importReference = module.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Single(method =>
                    method.Name == "ImportReference" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(MethodBase));
            var importedRuntimeMethods = new Dictionary<MethodInfo, object>();
            var trackedSetAdapters = new Dictionary<object, object>();

            foreach (var type in EnumerateTypes(module))
            {
                // A compatibility adapter may be appended to this type while rewriting it.
                foreach (var method in Enumerate(GetRequiredProperty(type, "Methods")).ToArray())
                {
                    if (!(bool)GetRequiredProperty(method, "HasBody"))
                    {
                        continue;
                    }

                    var body = GetRequiredProperty(method, "Body");
                    var callsBeforeMethod = rewrittenCalls;
                    var instructions = Enumerate(GetRequiredProperty(body, "Instructions")).ToArray();
                    var processor = body.GetType()
                        .GetMethod("GetILProcessor", BindingFlags.Instance | BindingFlags.Public)
                        ?.Invoke(body, null);
                    if (processor == null)
                    {
                        throw new InvalidOperationException("无法取得皮肤 DLL 的 IL 编辑器");
                    }

                    for (var instructionIndex = 0; instructionIndex < instructions.Length; instructionIndex++)
                    {
                        var instruction = instructions[instructionIndex];
                        var operandProperty = instruction.GetType().GetProperty("Operand", BindingFlags.Instance | BindingFlags.Public);
                        var operand = operandProperty?.GetValue(instruction);
                        if (operand == null || GetInstructionOpCodeName(instruction) is not ("call" or "callvirt") ||
                            !methodReferenceType.IsInstanceOfType(operand))
                        {
                            continue;
                        }
                        if (instructionIndex > 0 && GetRequiredProperty(
                                GetRequiredProperty(instructions[instructionIndex - 1], "OpCode"), "OpCodeType").ToString() == "Prefix")
                            continue; // tail./constrained. require a different stack/receiver contract.

                        if (TryRewriteTaskReturn(module, instruction, operand, report))
                        {
                            rewrittenCalls++;
                            continue;
                        }

                        if (TryRewriteUnsupportedDispose(
                                method,
                                body,
                                instructions,
                                instructionIndex,
                                operand,
                                module,
                                cecilAssembly,
                                opCodesType))
                        {
                            rewrittenCalls++;
                            report.Changes.Add($"Spine wrapper cleanup: {operand}");
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

                        // Every changed call below has a checked signature and an equivalent
                        // stack contract. Function pointers / delegate construction are not calls.
                        var callsBefore = rewrittenCalls;
                        try
                        {
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
                                TryReplaceFollowingPopWithNop(body, instructions, instruction, nopOpCode))
                            {
                                operandProperty!.SetValue(instruction, runtimeReference);
                                rewrittenCalls++;
                                continue;
                            }

                            // Old SetAnimation callers may configure the returned track instead of
                            // discarding it. v0.111's equivalent is SetAnimation + GetCurrent(trackId).
                            // Keep the original stack/result contract via a private provider-local
                            // adapter; never fake a null result or drop the author's track settings.
                            if (GetInstructionOpCodeName(instruction) is "call" or "callvirt" &&
                                runtimeMethod.DeclaringType?.FullName == MegaAnimationStateTypeName &&
                                runtimeMethod.Name == "SetAnimation" &&
                                runtimeMethod.ReturnType == typeof(void) &&
                                providerReturnName == "MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaTrackEntry" &&
                                runtimeMethod.GetParameters().Select(parameter => parameter.ParameterType)
                                    .SequenceEqual(new[] { typeof(string), typeof(bool), typeof(int) }))
                            {
                                var getCurrent = runtimeMethods.SingleOrDefault(candidate =>
                                    candidate.Name == "GetCurrent" &&
                                    candidate.DeclaringType == runtimeMethod.DeclaringType &&
                                    candidate.ReturnType.FullName == providerReturnName &&
                                    candidate.GetParameters().Select(parameter => parameter.ParameterType)
                                        .SequenceEqual(new[] { typeof(int) }));
                                if (getCurrent == null)
                                    continue;

                                if (!trackedSetAdapters.TryGetValue(type, out var adapter))
                                {
                                    var getCurrentReference = importReference.Invoke(module, [getCurrent])!;
                                    adapter = CreateTrackedSetAnimationAdapter(
                                        cecilAssembly, type, runtimeReference, getCurrentReference, opCodesType);
                                    trackedSetAdapters.Add(type, adapter);
                                }

                                SetInstruction(instruction, opCodesType.GetField("Call")!.GetValue(null)!, adapter);
                                rewrittenCalls++;
                            }
                            else if (runtimeMethod.DeclaringType?.FullName == SpineAnimationAccessTypeName &&
                                     runtimeMethod.ReturnType == typeof(void) &&
                                     providerReturnName == "MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaTrackEntry" &&
                                     runtimeMethod.Name is "SetAnimation" or "AddAnimation")
                            {
                                var adapter = CreateAccessTrackAdapter(module, type, runtimeMethod, cecilAssembly, opCodesType);
                                SetInstruction(instruction, opCodesType.GetField("Call")!.GetValue(null)!, adapter);
                                rewrittenCalls++;
                            }
                        }
                        finally
                        {
                            if (rewrittenCalls != callsBefore)
                                report.Changes.Add($"Animation signature: {operand} -> {runtimeMethod.DeclaringType?.FullName}.{runtimeMethod.Name}");
                        }
                    }

                    // Inserted pop/call instructions may push a pre-existing short branch out
                    // of its signed-byte range. Keep labels and exception boundaries intact.
                    if (rewrittenCalls != callsBeforeMethod) ExpandShortBranches(body, opCodesType);
                }
            }

            if (report.Changes.Count == 0)
            {
                AuditGameReferences(module, report);
                return false;
            }

            rewrittenAssembly = new MemoryStream();
            writeAssembly.Invoke(definition, [rewrittenAssembly]);
            rewrittenAssembly.Position = 0;
            // Cecil's original MemberRef table retains detached references after replacing
            // call operands. Audit the written image, not that stale source table.
            var preparedDefinition = readAssembly.Invoke(null, [rewrittenAssembly])!;
            try { AuditGameReferences(GetRequiredProperty(preparedDefinition, "MainModule"), report); }
            finally { (preparedDefinition as IDisposable)?.Dispose(); }
            rewrittenAssembly.Position = 0;
            return true;
        }
        finally
        {
            (definition as IDisposable)?.Dispose();
        }
    }

    private static object CreateTrackedSetAnimationAdapter(
        Assembly cecilAssembly,
        object ownerType,
        object setAnimation,
        object getCurrent,
        Type opCodesType)
    {
        var methodType = cecilAssembly.GetType("Mono.Cecil.MethodDefinition", true)!;
        var attributesType = cecilAssembly.GetType("Mono.Cecil.MethodAttributes", true)!;
        var parameterType = cecilAssembly.GetType("Mono.Cecil.ParameterDefinition", true)!;
        var instructionType = cecilAssembly.GetType("Mono.Cecil.Cil.Instruction", true)!;
        var methods = GetRequiredProperty(ownerType, "Methods");
        var existingNames = Enumerate(methods)
            .Select(method => (string)GetRequiredProperty(method, "Name"))
            .ToHashSet(StringComparer.Ordinal);
        var name = "__SkinChanger_SetAnimationTracked";
        for (var suffix = 1; existingNames.Contains(name); suffix++)
            name = "__SkinChanger_SetAnimationTracked_" + suffix;
        var adapter = Activator.CreateInstance(methodType, name,
            Enum.Parse(attributesType, "Private, Static, HideBySig"),
            GetRequiredProperty(getCurrent, "ReturnType"))!;
        var parameters = GetRequiredProperty(adapter, "Parameters");
        var argumentTypes = new[] { GetRequiredProperty(setAnimation, "DeclaringType") }
            .Concat(Enumerate(GetRequiredProperty(setAnimation, "Parameters"))
                .Select(parameter => GetRequiredProperty(parameter, "ParameterType")));
        foreach (var argumentType in argumentTypes)
        {
            InvokeProcessor(parameters, "Add", Activator.CreateInstance(parameterType, argumentType)!);
        }
        InvokeProcessor(methods, "Add", adapter);
        var body = GetRequiredProperty(adapter, "Body");
        var processor = body.GetType().GetMethod("GetILProcessor")!.Invoke(body, null)!;
        void Emit(string opCodeName, object? operand = null)
        {
            var opCode = opCodesType.GetField(opCodeName)!.GetValue(null)!;
            object[] arguments = operand == null ? [opCode] : [opCode, operand];
            var factory = instructionType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(candidate => candidate.Name == "Create" &&
                    candidate.GetParameters().Length == arguments.Length &&
                    candidate.GetParameters().Select((parameter, index) =>
                        parameter.ParameterType.IsInstanceOfType(arguments[index])).All(matches => matches));
            InvokeProcessor(processor, "Append", factory.Invoke(null, arguments)!);
        }
        Emit("Ldarg_0"); // original receiver, name, loop, trackId
        Emit("Ldarg_1");
        Emit("Ldarg_2");
        Emit("Ldarg_3");
        Emit("Callvirt", setAnimation);
        Emit("Ldarg_0");
        Emit("Ldarg_3"); // query the same track, not hard-coded track 0
        Emit("Callvirt", getCurrent);
        Emit("Ret");
        return adapter;
    }

    private static bool TryRewriteUnsupportedDispose(
        object method,
        object body,
        IReadOnlyList<object> instructions,
        int instructionIndex,
        object methodReference,
        object module,
        Assembly cecilAssembly,
        Type opCodesType)
    {
        if (!string.Equals(GetRequiredProperty(methodReference, "Name") as string, "Dispose", StringComparison.Ordinal) ||
            !(bool)GetRequiredProperty(methodReference, "HasThis") ||
            GetTypeFullName(GetRequiredProperty(methodReference, "ReturnType")) != "System.Void" ||
            Enumerate(GetRequiredProperty(methodReference, "Parameters")).Any())
        {
            return false;
        }

        var sourceIndex = instructionIndex - 1;
        if (sourceIndex >= 0 &&
            string.Equals(GetInstructionOpCodeName(instructions[sourceIndex]), "constrained.", StringComparison.Ordinal))
        {
            return false; // A constrained receiver can be a managed pointer, not a wrapper.
        }

        if (sourceIndex < 0)
        {
            return false;
        }

        var receiverReference = TryGetLoadedValueType(method, body, instructions[sourceIndex]);
        if (receiverReference == null || !IsGameTypeReference(receiverReference))
        {
            return false;
        }

        var runtimeType = GameAssembly.GetType(GetTypeFullName(receiverReference));
        var bindingType = GameAssembly.GetType("MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaSpineBinding");
        var declaringReference = GetRequiredProperty(methodReference, "DeclaringType");
        var declaringName = GetTypeFullName(declaringReference);
        var scopeName = (string)GetRequiredProperty(GetRequiredProperty(declaringReference, "Scope"), "Name");
        var knownDispose = (declaringName == typeof(IDisposable).FullName &&
                            scopeName is "System.Private.CoreLib" or "System.Runtime" or "mscorlib" or "netstandard") ||
            (IsGameTypeReference(declaringReference) && GameAssembly.GetType(declaringName) is { } declaringType &&
             bindingType?.IsAssignableFrom(declaringType) == true);
        if (!knownDispose || runtimeType == null || bindingType?.IsAssignableFrom(runtimeType) != true ||
            typeof(IDisposable).IsAssignableFrom(runtimeType))
        {
            return false;
        }

        // v0.111's binding Dispose releases BoundObject on the calling thread. v0.107
        // exposes that same object but has no IDisposable wrapper. Preserve the cleanup;
        // never delete arbitrary IDisposable calls from third-party libraries.
        var getter = bindingType.GetProperty("BoundObject")?.GetMethod;
        var dispose = getter?.ReturnType.GetMethod("Dispose", Type.EmptyTypes);
        if (getter == null || dispose == null) return false;
        var adapter = NewAdapter(module, GetRequiredProperty(method, "DeclaringType"), "DisposeSpine",
            Import(module, typeof(void)), [Import(module, bindingType)], cecilAssembly);
        Emit(adapter, "Ldarg_0", cecilAssembly, opCodesType);
        Emit(adapter, "Callvirt", cecilAssembly, opCodesType, Import(module, getter));
        Emit(adapter, "Callvirt", cecilAssembly, opCodesType, Import(module, dispose));
        Emit(adapter, "Ret", cecilAssembly, opCodesType);
        SetInstruction(instructions[instructionIndex], opCodesType.GetField("Call")!.GetValue(null)!, adapter);

        return true;
    }

    private static object? TryGetLoadedValueType(object method, object body, object instruction)
    {
        var opCodeName = GetInstructionOpCodeName(instruction);
        if (opCodeName.StartsWith("ldloca", StringComparison.Ordinal) ||
            opCodeName.StartsWith("ldarga", StringComparison.Ordinal) || opCodeName == "ldflda")
            return null;
        var operand = instruction.GetType()
            .GetProperty("Operand", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(instruction);

        if (opCodeName.StartsWith("ldloc", StringComparison.Ordinal))
        {
            var variable = operand ?? TryGetIndexedItem(
                GetRequiredProperty(body, "Variables"),
                TryParseShortFormIndex(opCodeName, "ldloc."));
            return variable == null
                ? null
                : GetRequiredProperty(variable, "VariableType");
        }

        if (opCodeName.StartsWith("ldarg", StringComparison.Ordinal))
        {
            var parameter = operand;
            if (parameter == null)
            {
                var argumentIndex = TryParseShortFormIndex(opCodeName, "ldarg.");
                var hasThis = (bool)GetRequiredProperty(method, "HasThis");
                if (argumentIndex == 0 && hasThis)
                {
                    return GetRequiredProperty(method, "DeclaringType");
                }

                if (argumentIndex >= 0)
                {
                    parameter = TryGetIndexedItem(
                        GetRequiredProperty(method, "Parameters"),
                        argumentIndex - (hasThis ? 1 : 0));
                }
            }

            return parameter == null
                ? null
                : GetRequiredProperty(parameter, "ParameterType");
        }

        if ((opCodeName.StartsWith("call", StringComparison.Ordinal) ||
             opCodeName == "newobj") && operand != null)
        {
            return opCodeName == "newobj"
                ? GetRequiredProperty(operand, "DeclaringType")
                : GetRequiredProperty(operand, "ReturnType");
        }

        if (opCodeName.StartsWith("ldfld", StringComparison.Ordinal) && operand != null)
        {
            return GetRequiredProperty(operand, "FieldType");
        }

        return null;
    }

    private static int TryParseShortFormIndex(string opCodeName, string prefix)
    {
        return opCodeName.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(opCodeName[prefix.Length..], out var index)
            ? index
            : -1;
    }

    private static object? TryGetIndexedItem(object collection, int index)
    {
        return index < 0
            ? null
            : Enumerate(collection).ElementAtOrDefault(index);
    }

    private static string GetInstructionOpCodeName(object instruction)
    {
        var opCode = GetRequiredProperty(instruction, "OpCode");
        return GetRequiredProperty(opCode, "Name") as string ?? string.Empty;
    }

    private static void SetInstruction(object instruction, object opCode, object? operand)
    {
        instruction.GetType()
            .GetProperty("OpCode", BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(instruction, opCode);
        instruction.GetType()
            .GetProperty("Operand", BindingFlags.Instance | BindingFlags.Public)
            ?.SetValue(instruction, operand);
    }

    private static MethodInfo? FindMatchingRuntimeMethod(
        object methodReference,
        IReadOnlyList<MethodInfo> runtimeMethods)
    {
        var declaring = GetRequiredProperty(methodReference, "DeclaringType");
        var declaringName = GetTypeFullName(declaring);
        if (!IsGameTypeReference(declaring) || !(bool)GetRequiredProperty(methodReference, "HasThis") ||
            (bool)GetRequiredProperty(methodReference, "HasGenericParameters") ||
            (bool)GetRequiredProperty(methodReference, "IsGenericInstance") ||
            declaringName is not (MegaAnimationStateTypeName or SpineAnimationAccessTypeName))
        {
            return null;
        }

        var methodName = GetRequiredProperty(methodReference, "Name") as string;
        var providerReturnName = GetTypeFullName(GetRequiredProperty(methodReference, "ReturnType"));
        if (methodName is not ("SetAnimation" or "AddAnimation" or "AddAnimationTracked" or "AddEmptyAnimation") ||
            providerReturnName is not ("System.Void" or "MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaTrackEntry"))
            return null;
        runtimeMethods = runtimeMethods.Where(method => method.DeclaringType?.FullName == declaringName && !method.IsStatic).ToArray();
        var parameterNames = Enumerate(GetRequiredProperty(methodReference, "Parameters"))
            .Select(parameter => GetTypeFullName(GetRequiredProperty(parameter, "ParameterType")))
            .ToArray();
        string[] expectedParameters = methodName switch
        {
            "SetAnimation" => ["System.String", "System.Boolean", "System.Int32"],
            "AddAnimation" or "AddAnimationTracked" => ["System.String", "System.Single", "System.Boolean", "System.Int32"],
            "AddEmptyAnimation" => ["System.Int32"],
            _ => []
        };
        if (!parameterNames.SequenceEqual(expectedParameters, StringComparer.Ordinal)) return null;
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
        object body,
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
        // Another path or handler may share this pop. Removing it is safe only when the
        // rewritten call is its sole predecessor and both instructions share a region.
        if (instructions.Any(item =>
                item.GetType().GetProperty("Operand")?.GetValue(item) is { } target &&
                (ReferenceEquals(target, next) || target is IEnumerable targets && targets.Cast<object>().Contains(next))) ||
            Enumerate(GetRequiredProperty(body, "ExceptionHandlers")).Any(handler =>
                new[] { "TryStart", "TryEnd", "HandlerStart", "HandlerEnd", "FilterStart" }.Any(name =>
                    ReferenceEquals(handler.GetType().GetProperty(name)?.GetValue(handler), next))))
            return false;
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
