using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace STS2SkinChanger.Core;

/// <summary>
/// Legacy merchant-settings adapter. Replace only the audited whole-tree hand/leg entry points
/// in memory, before any command .cctor can execute. Unknown console DLLs remain untouched.
/// </summary>
internal static class MerchantSettingsAssemblyCompatibility
{
    internal const string CommandBase = "MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.AbstractConsoleCmd";

    internal static MemoryStream? Rewrite(Stream input, out int changed)
    {
        changed = 0;
        var cecil = typeof(Harmony).Assembly;
        var definitionType = cecil.GetType("Mono.Cecil.AssemblyDefinition", true)!;
        var read = definitionType.GetMethod("ReadAssembly", [typeof(Stream)])!;
        var originalPosition = input.Position;
        input.Position = 0;
        var definition = read.Invoke(null, [input])!;
        try
        {
            var module = Get(definition, "MainModule");
            var types = Items(Get(module, "Types")).ToArray();
            foreach (var type in types.Where(type => Text(type, "BaseType") == CommandBase))
            {
                var methods = Items(Get(type, "Methods")).ToArray();
                var hands = Find(methods, "ApplyToExistingHands", "System.Int32");
                var legs = Find(methods, "UpdateLegVisibility", "System.Void", "System.Boolean");
                var oneHand = Find(methods, "TryApplyToHand", "System.Boolean",
                    "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantHand");
                var legTree = Find(methods, "UpdateLegVisibilityStatic", "System.Void", "Godot.Node", "System.Boolean");
                // Names alone are insufficient. Require the actual world-walk contract and the
                // paired, local-node helper. A new layout must be audited rather than guessed.
                if (hands == null || legs == null || oneHand == null || legTree == null ||
                    !Calls(hands, "Godot.Engine", "GetMainLoop") ||
                    !Calls(legs, "Godot.Engine", "GetMainLoop") ||
                    !Calls(hands, Text(type, "FullName"), "FindAndApplyRecursive") ||
                    !Calls(legs, Text(type, "FullName"), "UpdateLegVisibilityStatic") ||
                    !Items(Get(Get(legTree, "Body"), "Instructions")).Any(instruction =>
                        (GetNullable(instruction, "Operand") as string) == "MerchantInventoryLeg")) continue;

                var import = module.GetType().GetMethod("ImportReference", [typeof(MethodBase)])!;
                var getType = import.Invoke(module, [typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!])!;
                var refresh = import.Invoke(module, [typeof(ProviderSettingsApi).GetMethod(nameof(ProviderSettingsApi.Refresh))!])!;
                foreach (var method in new[] { hands, legs })
                {
                    var body = Get(method, "Body");
                    Get(body, "ExceptionHandlers").GetType().GetMethod("Clear")!.Invoke(Get(body, "ExceptionHandlers"), null);
                    Get(body, "Variables").GetType().GetMethod("Clear")!.Invoke(Get(body, "Variables"), null);
                    var instructions = Get(body, "Instructions");
                    instructions.GetType().GetMethod("Clear")!.Invoke(instructions, null);
                    var processor = body.GetType().GetMethod("GetILProcessor")!.Invoke(body, null)!;
                    Emit(processor, "Ldtoken", type);
                    Emit(processor, "Call", getType);
                    Emit(processor, "Call", refresh);
                    if (ReferenceEquals(method, legs)) Emit(processor, "Pop");
                    Emit(processor, "Ret");
                    changed++;
                }
            }
            if (changed == 0) return null;
            var output = new MemoryStream();
            try
            {
                definitionType.GetMethod("Write", [typeof(Stream)])!.Invoke(definition, [output]);
                output.Position = 0;
                return output;
            }
            catch { output.Dispose(); throw; }
        }
        finally
        {
            (definition as IDisposable)?.Dispose();
            input.Position = originalPosition;
        }
    }

    private static object? Find(IEnumerable<object> methods, string name, string result, params string[] parameters) =>
        methods.SingleOrDefault(method => Text(method, "Name") == name && (bool)Get(method, "IsStatic") &&
            (bool)Get(method, "HasBody") && Text(method, "ReturnType") == result &&
            Items(Get(method, "Parameters")).Select(parameter => Text(parameter, "ParameterType")).SequenceEqual(parameters));

    private static bool Calls(object method, string type, string name) =>
        Items(Get(Get(method, "Body"), "Instructions")).Select(instruction => GetNullable(instruction, "Operand"))
            .Any(operand => operand != null && Text(operand, "Name") == name && Text(operand, "DeclaringType") == type);

    private static void Emit(object processor, string opcode, object? operand = null)
    {
        var code = typeof(Harmony).Assembly.GetType("Mono.Cecil.Cil.OpCodes", true)!.GetField(opcode)!.GetValue(null)!;
        var args = operand == null ? new[] { code } : new[] { code, operand };
        var emit = processor.GetType().GetMethods().Single(method => method.Name == "Emit" &&
            method.GetParameters().Length == args.Length && method.GetParameters()
                .Select((parameter, index) => parameter.ParameterType.IsInstanceOfType(args[index])).All(match => match));
        emit.Invoke(processor, args);
    }

    private static IEnumerable<object> Items(object value) => ((IEnumerable)value).Cast<object>();
    private static object? GetNullable(object value, string name) => value.GetType().GetProperties()
        .FirstOrDefault(property => property.Name == name && property.GetIndexParameters().Length == 0)?.GetValue(value);
    private static object Get(object value, string name) => GetNullable(value, name) ?? throw new MissingMemberException(value.GetType().Name, name);
    private static string Text(object value, string name) => GetNullable(value, name)?.ToString() ?? "";
}
