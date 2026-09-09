using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace STS2SkinChanger.Catalog;

// Small node-level intent, deliberately separate from full-frame/Ancient presentation.
internal sealed record CardSurfaceDefinition(int? PortraitStretchMode, CardSurfaceColor? TinyBannerColor,
    int? ExcludedBannerRarity = null);
internal sealed record CardSurfaceColor(float R, float G, float B, float A);

internal static partial class ManagedCardPresentationScanner
{
    private static readonly Dictionary<string, (long Length, DateTime Stamp, CardSurfaceDefinition? Surface)> SurfaceCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static CardSurfaceDefinition? ScanExportedSurface(string? root, string providerId)
    {
        if (root == null || !Directory.Exists(root)) return null;
        var paths = ManagedCardPortraitReplacementScanner.GetAssemblyPaths(root, providerId);
        if (paths.Count != 1) return null;
        try
        {
            var file = new FileInfo(paths[0]);
            lock (SurfaceCache)
            {
                if (SurfaceCache.TryGetValue(file.FullName, out var cached) &&
                    cached.Length == file.Length && cached.Stamp == file.LastWriteTimeUtc) return cached.Surface;
                var result = ScanExportedSurfaceAssembly(file.FullName);
                SurfaceCache[file.FullName] = (file.Length, file.LastWriteTimeUtc, result);
                return result;
            }
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"Card surface metadata skipped: {paths[0]}: {e.Message}");
            return null;
        }
    }

    internal static CardSurfaceDefinition? ScanExportedSurfaceAssembly(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return null;
        var reader = pe.GetMetadataReader();
        var stretches = new HashSet<int>();
        var banners = new HashSet<(CardSurfaceColor Color, int? Excluded)>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var strings = new HashSet<string>();
            var members = new HashSet<string>();
            var methods = new List<(MethodDefinition Method, List<SurfaceInstruction> Code)>();
            foreach (var mh in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(mh);
                if (method.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                if (il == null) continue;
                ScanIl(reader, il, strings, new HashSet<string>(), members);
                methods.Add((method, ReadSurfaceInstructions(reader, il)));
            }
            // Exporter registry + NCard identity + both actual portrait fields, not a class-name guess.
            if (strings.Contains("_portrait") && strings.Contains("_ancientPortrait") &&
                members.Contains("CardReplacementRegistry.TryGetTexture") && members.Contains("NCard.get_Model"))
            {
                foreach (var (_, code) in methods)
                    for (var i = 1; i < code.Count; i++)
                    {
                        var argument = code[i - 1].Op == OpCodes.Conv_I8 && i > 1 ? code[i - 2] : code[i - 1];
                        if (code[i].Member == "TextureRect.set_StretchMode" && argument.Integer is >= 0 and <= 6)
                            stretches.Add(argument.Integer.Value);
                    }
            }
            foreach (var (method, code) in methods)
            {
                if (!HasSurfaceAttribute(reader, method.GetCustomAttributes(), "HarmonyPostfix") ||
                    !(HasTinyBannerTarget(reader, type.GetCustomAttributes()) ||
                      HasTinyBannerTarget(reader, method.GetCustomAttributes()))) continue;
                // Only the exporter constant-output contract. Never evaluate provider code,
                // config getters or arbitrary card predicates to synthesize a global UI patch.
                var parameters = method.GetParameters().Select(reader.GetParameter).Where(p => p.SequenceNumber > 0).ToArray();
                if (parameters.Length is < 1 or > 2 || reader.GetString(parameters[0].Name) != "__result") continue;
                if (code.Any(i => i.Op.FlowControl == FlowControl.Call &&
                    i.Member is not ("Color..ctor" or "CardUiModeSpoofPatch.ShouldSpoofForUi"))) continue;
                int? excluded = null;
                for (var i = 2; i < code.Count; i++)
                    if (code[i].Op == OpCodes.Beq_S || code[i].Op == OpCodes.Beq)
                        if (code[i - 2].Op == OpCodes.Ldarg_1 && code[i - 1].Integer is { } rarity &&
                            parameters.Length == 2 && reader.GetString(parameters[1].Name) == "rarity") excluded = rarity;
                for (var i = 5; i + 1 < code.Count; i++)
                {
                    if (code[i].Op != OpCodes.Newobj || code[i].Member != "Color..ctor" ||
                        code[i - 5].Op != OpCodes.Ldarg_0 || code[i + 1].Op != OpCodes.Stobj ||
                        code[i + 1].Member != "Color") continue;
                    if (i + 3 != code.Count || code[^1].Op != OpCodes.Ret ||
                        !KnownBannerPrefix(code.Take(i - 5).ToArray(), code[^1].Offset, parameters.Length)) continue;
                    var values = code.Skip(i - 4).Take(4).Select(x => x.Float).ToArray();
                    if (values.Any(v => v == null || !float.IsFinite(v.Value) || v is < 0 or > 1)) continue;
                    banners.Add((new(values[0]!.Value, values[1]!.Value, values[2]!.Value, values[3]!.Value), excluded));
                }
            }
        }
        // Conflicting/dynamic setters remain unsupported rather than choosing an arbitrary mode.
        int? stretch = stretches.Count == 1 ? stretches.Single() : null;
        var banner = banners.Count == 1 ? banners.Single() : default;
        return stretch == null && banner.Color == null ? null : new(stretch, banner.Color, banner.Excluded);
    }

    private static bool HasSurfaceAttribute(MetadataReader reader, CustomAttributeHandleCollection attributes, string name) =>
        attributes.Any(h => SurfaceAttributeName(reader, reader.GetCustomAttribute(h)) == "HarmonyLib." + name);

    private static string? SurfaceAttributeName(MetadataReader reader, CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind != HandleKind.MemberReference) return null;
        var ctor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (ctor.Parent.Kind != HandleKind.TypeReference) return null;
        var type = reader.GetTypeReference((TypeReferenceHandle)ctor.Parent);
        return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
    }

    private static bool HasTinyBannerTarget(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        foreach (var h in attributes)
        {
            var a = reader.GetCustomAttribute(h);
            if (SurfaceAttributeName(reader, a) != "HarmonyLib.HarmonyPatch") continue;
            try
            {
                var blob = reader.GetBlobReader(a.Value);
                if (blob.ReadUInt16() == 1 && blob.ReadSerializedString()?.Split(',')[0] ==
                    "MegaCrit.Sts2.Core.Nodes.Cards.NTinyCard" && blob.ReadSerializedString() == "GetBannerColor" &&
                    blob.ReadUInt16() == 0 && blob.RemainingBytes == 0) return true;
            }
            catch (BadImageFormatException) { }
        }
        return false;
    }

    private static bool KnownBannerPrefix(SurfaceInstruction[] prefix, int returnOffset, int parameterCount)
    {
        if (prefix.Length == 0) return parameterCount == 1;
        if (parameterCount != 2 || prefix.Length is not (5 or 9) ||
            prefix[0].Op != OpCodes.Ldarg_1 || prefix[1].Integer == null ||
            prefix[2].Op != OpCodes.Beq_S && prefix[2].Op != OpCodes.Beq ||
            prefix[3].Op != OpCodes.Call || prefix[3].Member != "CardUiModeSpoofPatch.ShouldSpoofForUi") return false;
        static bool IsFalseBranch(SurfaceInstruction i, int target) =>
            (i.Op == OpCodes.Brfalse || i.Op == OpCodes.Brfalse_S) && i.BranchTarget == target;
        if (prefix.Length == 5)
            return prefix[2].BranchTarget == returnOffset && IsFalseBranch(prefix[4], returnOffset);
        // Debug exporter stores the stack-context predicate in a bool local. Read that exact
        // control flow, not arbitrary configuration branches that happen to contain a color.
        return prefix[2].BranchTarget == prefix[5].Offset &&
               (prefix[4].Op == OpCodes.Br || prefix[4].Op == OpCodes.Br_S) && prefix[4].BranchTarget == prefix[6].Offset &&
               prefix[5].Integer == 0 && prefix[6].Op == OpCodes.Stloc_0 && prefix[7].Op == OpCodes.Ldloc_0 &&
               IsFalseBranch(prefix[8], returnOffset);
    }

    private sealed record SurfaceInstruction(OpCode Op, int? Integer, float? Float, string? Member,
        int Offset, int? BranchTarget);
    private static List<SurfaceInstruction> ReadSurfaceInstructions(MetadataReader reader, byte[] il)
    {
        var result = new List<SurfaceInstruction>();
        for (var offset = 0; offset < il.Length;)
        {
            var start = offset;
            var first = il[offset++];
            var value = first == 0xfe ? (ushort)(0xfe00 | il[offset++]) : first;
            if (!OpCodesByValue.TryGetValue(value, out var op)) break;
            var size = GetOperandSize(op.OperandType, il, offset);
            if (size < 0 || offset + size > il.Length) throw new BadImageFormatException();
            int? integer = op.Value >= OpCodes.Ldc_I4_0.Value && op.Value <= OpCodes.Ldc_I4_8.Value
                ? op.Value - OpCodes.Ldc_I4_0.Value : op == OpCodes.Ldc_I4 ? BitConverter.ToInt32(il, offset)
                : op == OpCodes.Ldc_I4_S ? (sbyte)il[offset] : null;
            float? number = op == OpCodes.Ldc_R4 ? BitConverter.ToSingle(il, offset) : null;
            string? member = op.OperandType == OperandType.InlineMethod
                ? ResolveMemberName(reader, MetadataTokens.EntityHandle(BitConverter.ToInt32(il, offset)))
                : op.OperandType == OperandType.InlineType
                    ? ResolveTypeName(reader, MetadataTokens.EntityHandle(BitConverter.ToInt32(il, offset))) : null;
            int? branch = op.OperandType == OperandType.ShortInlineBrTarget ? offset + size + (sbyte)il[offset] :
                op.OperandType == OperandType.InlineBrTarget ? offset + size + BitConverter.ToInt32(il, offset) : null;
            if (op != OpCodes.Nop) result.Add(new(op, integer, number, member, start, branch));
            offset += size;
        }
        return result;
    }
}
