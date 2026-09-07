using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace STS2SkinChanger.Core;

// An omission proof, NOT a sandbox or a heuristic that arbitrary DLLs are safe to run.
// A harmless bootstrap DLL can accompany a full resource skin. Anything with patches,
// events, native imports, field writes or unknown calls still needs normal initialization.
internal static class WorkshopCodeInspection
{
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(op => op.Value);

    public static bool CanOmitExecution(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader == null || (pe.PEHeaders.CorHeader.Flags & CorFlags.ILOnly) == 0) return false;
            var reader = pe.GetMetadataReader();
            // No patch entrypoint may be discarded just because its body uses simple IL.
            foreach (var handle in reader.CustomAttributes)
            {
                var attribute = reader.GetCustomAttribute(handle);
                var owner = MethodOwner(reader, attribute.Constructor);
                if (owner.StartsWith("HarmonyLib.", StringComparison.Ordinal)) return false;
            }
            foreach (var handle in reader.MethodDefinitions)
            {
                var method = reader.GetMethodDefinition(handle);
                if ((method.Attributes & (MethodAttributes.PinvokeImpl | MethodAttributes.Abstract | MethodAttributes.Virtual)) != 0) return false;
                if (method.RelativeVirtualAddress == 0) return false;
                var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                if (body.ExceptionRegions.Length != 0) return false;
                var il = body.GetILBytes()!;
                for (var offset = 0; offset < il.Length;)
                {
                    var value = (short)il[offset++];
                    if (value == 0xfe) value = unchecked((short)(0xfe00 | il[offset++]));
                    if (!Codes.TryGetValue(value, out var code)) return false;
                    if (code == OpCodes.Call || code == OpCodes.Newobj)
                    {
                        var called = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, offset));
                        offset += 4;
                        if (!OmittableCall(reader, called)) return false;
                    }
                    else if (code == OpCodes.Ldstr) offset += 4;
                    else if (code != OpCodes.Nop && code != OpCodes.Ret && code != OpCodes.Pop && code != OpCodes.Dup &&
                             code != OpCodes.Ldarg_0 && code != OpCodes.Ldnull && code != OpCodes.Ldc_I4_0 && code != OpCodes.Ldc_I4_1)
                        return false;
                    if (offset > il.Length) return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or ArgumentException or IndexOutOfRangeException)
        { return false; }
    }

    private static bool OmittableCall(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind != HandleKind.MemberReference) return false;
        var member = reader.GetMemberReference((MemberReferenceHandle)handle);
        var owner = MethodOwner(reader, handle);
        var name = reader.GetString(member.Name);
        return (owner, name) is ("System.Object", ".ctor") or ("System.Attribute", ".ctor") or
            ("System.Console", "WriteLine") or ("Godot.GD", "Print") or
            ("HarmonyLib.Harmony", ".ctor") or ("HarmonyLib.Harmony", "PatchAll") or
            ("System.Reflection.Assembly", "GetExecutingAssembly");
    }

    private static string MethodOwner(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind != HandleKind.MemberReference) return "";
        var parent = reader.GetMemberReference((MemberReferenceHandle)handle).Parent;
        if (parent.Kind != HandleKind.TypeReference) return "";
        var type = reader.GetTypeReference((TypeReferenceHandle)parent);
        return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
    }
}
