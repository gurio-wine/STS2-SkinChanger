using System.Collections;
using HarmonyLib;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

// Do not let an instantiated card animation register itself on every NCard through BaseLib.
// Change only the loaded copy and only the audited automatic factories; never edit Workshop files.
internal static class StatefulCardArtAssemblyCompatibility
{
    internal static MemoryStream? Rewrite(Stream input, StatefulCardArtContract? contract, out int changed)
    {
        changed = 0;
        if (contract == null) return null;
        var position = input.Position;
        input.Position = 0;
        var type = typeof(Harmony).Assembly.GetType("Mono.Cecil.AssemblyDefinition", true)!;
        var definition = type.GetMethod("ReadAssembly", [typeof(Stream)])!.Invoke(null, [input])!;
        try
        {
            foreach (var candidate in AllTypes(Get(Get(definition, "MainModule"), "Types")))
            foreach (var method in Items(Get(candidate, "Methods")).Where(method => Get(method, "HasBody") is true))
            foreach (var instruction in Items(Get(Get(method, "Body"), "Instructions")))
            {
                if (Text(instruction, "OpCode") == "ldstr" && Get(instruction, "Operand") is string table &&
                    table is "artists" or "usernames" or "event_chatter")
                {
                    instruction.GetType().GetProperty("Operand")!.SetValue(instruction, contract.LocalizationPrefix + table);
                    changed++;
                }
            }
            foreach (var candidate in Items(Get(Get(definition, "MainModule"), "Types")))
            {
                var fields = Items(Get(candidate, "Fields")).Where(field => Get(field, "IsStatic") is true && Get(field, "IsLiteral") is false).ToArray();
                var automaticFactory = fields.Length == 1 && Text(fields[0], "FieldType").StartsWith("BaseLib.Utils.AddedNode`2<", StringComparison.Ordinal);
                var registry = Text(candidate, "FullName") == contract.ArtType;
                var initializer = Items(Get(candidate, "Methods")).SingleOrDefault(method => Text(method, "Name") == ".cctor");
                if (initializer == null) continue;
                var body = Get(initializer, "Body")!;
                if (!automaticFactory && !registry)
                {
                    // Some scripts have a lazy state field in the same constructor. Preserve
                    // that field; replace only AddedNode construction with a null factory.
                    foreach (var instruction in Items(Get(body, "Instructions")).ToArray())
                    {
                        var ctor = Get(instruction, "Operand");
                        if (Text(instruction, "OpCode") != "newobj" ||
                            !Text(ctor, "DeclaringType").StartsWith("BaseLib.Utils.AddedNode`2<", StringComparison.Ordinal)) continue;
                        var factoryProcessor = body.GetType().GetMethod("GetILProcessor")!.Invoke(body, null)!;
                        var opcodes = typeof(Harmony).Assembly.GetType("Mono.Cecil.Cil.OpCodes", true)!;
                        var instructionType = instruction.GetType();
                        var create = instructionType.GetMethods().Single(method => method.Name == "Create" && method.GetParameters().Length == 1);
                        var insert = factoryProcessor.GetType().GetMethod("InsertAfter", [instructionType, instructionType])!;
                        var parameters = Items(Get(ctor, "Parameters")).Count();
                        instructionType.GetProperty("OpCode")!.SetValue(instruction, opcodes.GetField(parameters == 0 ? "Ldnull" : "Pop")!.GetValue(null));
                        instructionType.GetProperty("Operand")!.SetValue(instruction, null);
                        var previous = instruction;
                        // Keep the first instruction's identity so branch/exception targets
                        // retain the original stack-consumption boundary.
                        for (var index = 1; index <= parameters; index++)
                        {
                            var next = create.Invoke(null, [opcodes.GetField(index == parameters ? "Ldnull" : "Pop")!.GetValue(null)])!;
                            insert.Invoke(factoryProcessor, [previous, next]);
                            previous = next;
                        }
                        changed++;
                    }
                    continue;
                }
                foreach (var collection in new[] { "Instructions", "Variables", "ExceptionHandlers" })
                {
                    var value = Get(body, collection)!;
                    value.GetType().GetMethod("Clear")!.Invoke(value, null);
                }
                var processor = body.GetType().GetMethod("GetILProcessor")!.Invoke(body, null)!;
                var ret = typeof(Harmony).Assembly.GetType("Mono.Cecil.Cil.OpCodes", true)!.GetField("Ret")!.GetValue(null)!;
                processor.GetType().GetMethods().Single(method => method.Name == "Emit" && method.GetParameters().Length == 1)
                    .Invoke(processor, [ret]);
                changed++;
            }
            if (changed == 0) throw new InvalidOperationException("Stateful card contract has no isolatable factory; keep original initializer disabled.");
            var result = new MemoryStream();
            try { type.GetMethod("Write", [typeof(Stream)])!.Invoke(definition, [result]); result.Position = 0; return result; }
            catch { result.Dispose(); throw; }
        }
        finally { (definition as IDisposable)?.Dispose(); input.Position = position; }
    }
    private static object? Get(object? value, string name) => value?.GetType().GetProperties().FirstOrDefault(property => property.Name == name)?.GetValue(value);
    private static string Text(object? value, string name) => Get(value, name)?.ToString() ?? string.Empty;
    private static IEnumerable<object> Items(object? value) => value is IEnumerable items ? items.Cast<object>() : [];
    private static IEnumerable<object> AllTypes(object? collection)
    {
        foreach (var type in Items(collection))
        {
            yield return type;
            foreach (var nested in AllTypes(Get(type, "NestedTypes"))) yield return nested;
        }
    }
}
