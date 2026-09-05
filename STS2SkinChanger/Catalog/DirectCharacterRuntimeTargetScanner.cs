using System.Buffers.Binary;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Catalog;

internal static class DirectCharacterRuntimeTargetScanner
{
    private static readonly object CacheSync = new();
    private static readonly Dictionary<string, ScannerCacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue =
        typeof(OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    public static IReadOnlySet<string> Scan(
        string? providerRoot,
        string providerId,
        IEnumerable<string> knownCharacterGroupIds)
    {
        if (string.IsNullOrWhiteSpace(providerRoot) || !Directory.Exists(providerRoot))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var primaryAssembly = System.IO.Path.Combine(providerRoot, providerId + ".dll");
        var assemblyPaths = File.Exists(primaryAssembly)
            ? [primaryAssembly]
            : Directory.EnumerateFiles(providerRoot, "*.dll", SearchOption.TopDirectoryOnly)
                .ToArray();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assemblyPath in assemblyPaths)
        {
            try
            {
                targets.UnionWith(ScanAssembly(assemblyPath, knownCharacterGroupIds));
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"无法静态分析完整运行角色皮肤 {assemblyPath}: {exception.Message}");
            }
        }

        return targets;
    }

    internal static IReadOnlySet<string> ScanAssembly(
        string assemblyPath,
        IEnumerable<string> knownCharacterGroupIds)
    {
        var knownGroups = knownCharacterGroupIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value, value => value, StringComparer.OrdinalIgnoreCase);
        var info = new FileInfo(assemblyPath);
        IReadOnlyList<(string FieldName, string Value)> declarations;
        lock (CacheSync)
        {
            if (Cache.TryGetValue(assemblyPath, out var cached) &&
                cached.Length == info.Length &&
                cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                declarations = cached.Declarations;
            }
            else
            {
                declarations = ReadDeclarations(assemblyPath, knownGroups);
                Cache[assemblyPath] = new ScannerCacheEntry(
                    info.Length,
                    info.LastWriteTimeUtc,
                    declarations);
            }
        }

        return DirectCharacterRuntimeTargetPolicy.ResolveTargets(
            declarations,
            knownCharacterGroupIds);
    }

    private static IReadOnlyList<(string FieldName, string Value)> ReadDeclarations(
        string assemblyPath,
        IReadOnlyDictionary<string, string> knownCharacterGroups)
    {
        using var stream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        if (!peReader.HasMetadata)
        {
            return [];
        }

        var reader = peReader.GetMetadataReader();
        var targetFields = new Dictionary<FieldDefinitionHandle, string>();
        var declarations = new List<(string FieldName, string Value)>();
        foreach (var fieldHandle in reader.FieldDefinitions)
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            var fieldName = reader.GetString(field.Name);
            if (!DirectCharacterRuntimeTargetPolicy.IsTargetCharacterField(fieldName))
            {
                continue;
            }

            targetFields[fieldHandle] = fieldName;
            var constantHandle = field.GetDefaultValue();
            if (constantHandle.IsNil)
            {
                continue;
            }

            var constant = reader.GetConstant(constantHandle);
            if (constant.TypeCode != ConstantTypeCode.String)
            {
                continue;
            }

            var blob = reader.GetBlobReader(constant.Value);
            var value = blob.ReadUTF16(blob.Length).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(value))
            {
                declarations.Add((fieldName, value));
            }
        }

        if (targetFields.Count == 0)
        {
            // A utility table listing all game characters is NOT a skin declaration. Only
            // inspect the string -> skin resource dispatch contract used by runtime providers;
            // resource-backed skins without it are already discovered from their PCK assets.
            foreach (var methodHandle in reader.MethodDefinitions)
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0 || !IsSkinPathDispatcher(reader, method))
                {
                    continue;
                }

                var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                if (il != null)
                {
                    ReadCharacterStringLiterals(
                        reader,
                        il,
                        knownCharacterGroups,
                        declarations);
                }
            }

            return declarations.Distinct().ToArray();
        }

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
            if (il != null)
            {
                ReadStaticAssignments(reader, il, targetFields, declarations);
            }
        }

        return declarations.Distinct().ToArray();
    }

    private static bool IsSkinPathDispatcher(MetadataReader reader, MethodDefinition method)
    {
        if (!method.Attributes.HasFlag(System.Reflection.MethodAttributes.Static) ||
            !reader.GetString(method.Name).Contains("SkinPath", StringComparison.OrdinalIgnoreCase)) return false;
        var signature = reader.GetBlobReader(method.Signature);
        var header = signature.ReadSignatureHeader();
        return !header.IsGeneric && signature.ReadCompressedInteger() == 1 &&
               signature.ReadSignatureTypeCode() == SignatureTypeCode.String &&
               signature.ReadSignatureTypeCode() == SignatureTypeCode.String;
    }

    private static void ReadCharacterStringLiterals(
        MetadataReader reader,
        byte[] il,
        IReadOnlyDictionary<string, string> knownCharacterGroups,
        ICollection<(string FieldName, string Value)> declarations)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            var first = il[offset++];
            var value = first == 0xfe && offset < il.Length
                ? (ushort)(0xfe00 | il[offset++])
                : first;
            if (!OpCodesByValue.TryGetValue(value, out var opCode))
            {
                return;
            }

            var operandOffset = offset;
            var operandSize = GetOperandSize(opCode.OperandType, il, operandOffset);
            if (operandSize < 0 || operandOffset + operandSize > il.Length)
            {
                return;
            }

            if (opCode.Equals(OpCodes.Ldstr) && operandSize == 4)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(
                    il.AsSpan(operandOffset, 4));
                try
                {
                    var literal = reader.GetUserString(
                        MetadataTokens.UserStringHandle(token & 0x00ffffff));
                    if (knownCharacterGroups.TryGetValue(literal.Trim(), out var groupId))
                    {
                        declarations.Add(("RuntimeSkinCharacterName", groupId));
                    }
                }
                catch (Exception exception) when (
                    exception is BadImageFormatException or ArgumentException)
                {
                    // Ignore malformed third-party metadata.
                }
            }

            offset += operandSize;
        }
    }

    private static void ReadStaticAssignments(
        MetadataReader reader,
        byte[] il,
        IReadOnlyDictionary<FieldDefinitionHandle, string> targetFields,
        ICollection<(string FieldName, string Value)> declarations)
    {
        string? lastString = null;
        var offset = 0;
        while (offset < il.Length)
        {
            var first = il[offset++];
            var value = first == 0xfe && offset < il.Length
                ? (ushort)(0xfe00 | il[offset++])
                : first;
            if (!OpCodesByValue.TryGetValue(value, out var opCode))
            {
                return;
            }

            var operandOffset = offset;
            var operandSize = GetOperandSize(opCode.OperandType, il, operandOffset);
            if (operandSize < 0 || operandOffset + operandSize > il.Length)
            {
                return;
            }

            if (opCode.Equals(OpCodes.Ldstr) && operandSize == 4)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                try
                {
                    lastString = reader.GetUserString(
                        MetadataTokens.UserStringHandle(token & 0x00ffffff));
                }
                catch (Exception exception) when (
                    exception is BadImageFormatException or ArgumentException)
                {
                    lastString = null;
                }
            }
            else if (opCode.Equals(OpCodes.Stsfld) && operandSize == 4 && lastString != null)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                var handle = MetadataTokens.EntityHandle(token);
                if (handle.Kind == HandleKind.FieldDefinition &&
                    targetFields.TryGetValue((FieldDefinitionHandle)handle, out var fieldName))
                {
                    declarations.Add((fieldName, lastString));
                }

                lastString = null;
            }

            offset += operandSize;
        }
    }

    private static int GetOperandSize(OperandType operandType, byte[] il, int offset) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or
        OperandType.ShortInlineI or
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or
        OperandType.InlineField or
        OperandType.InlineI or
        OperandType.InlineMethod or
        OperandType.InlineSig or
        OperandType.InlineString or
        OperandType.InlineTok or
        OperandType.InlineType or
        OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or
        OperandType.InlineR => 8,
        OperandType.InlineSwitch when offset + 4 <= il.Length =>
            4 + checked(BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4)) * 4),
        _ => -1
    };

    private sealed record ScannerCacheEntry(
        long Length,
        DateTime LastWriteTimeUtc,
        IReadOnlyList<(string FieldName, string Value)> Declarations);
}
