using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace STS2SkinChanger.Catalog;

/// <summary>
/// Reads exact CardModel type -> portrait resource mappings from common Harmony providers
/// without loading or executing their assembly. This covers providers that keep card art in a
/// private folder and replace CardModel.PortraitPath/GetPortrait through a static dictionary.
/// </summary>
internal static class ManagedCardPortraitReplacementScanner
{
    private static readonly object CacheSync = new();
    private static readonly Dictionary<string, ScannerCacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    public static IReadOnlyDictionary<string, string> Scan(
        string? providerRoot,
        string providerId)
    {
        var portraits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(providerRoot) || !Directory.Exists(providerRoot))
        {
            return portraits;
        }

        foreach (var assemblyPath in GetAssemblyPaths(providerRoot, providerId))
        {
            try
            {
                foreach (var pair in ScanAssemblyCached(assemblyPath))
                {
                    portraits[pair.Key] = pair.Value;
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"无法静态分析卡图映射 DLL {assemblyPath}: {exception.Message}");
            }
        }

        return portraits;
    }

    private static IReadOnlyList<string> GetAssemblyPaths(string providerRoot, string providerId)
    {
        var primaryAssembly = System.IO.Path.Combine(providerRoot, providerId + ".dll");
        if (File.Exists(primaryAssembly))
        {
            return [primaryAssembly];
        }

        // Some old providers name their sole DLL differently from the manifest. Do not use that
        // fallback for Workshop folders containing several bundled Mods: otherwise one card pack
        // would be incorrectly attributed to every sibling manifest in the same directory.
        var assemblyPaths = Directory.EnumerateFiles(
                providerRoot,
                "*.dll",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        var pckPaths = Directory.EnumerateFiles(
                providerRoot,
                "*.pck",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        return assemblyPaths.Length == 1 && pckPaths.Length <= 1
            ? assemblyPaths
            : [];
    }

    private static IReadOnlyDictionary<string, string> ScanAssemblyCached(string assemblyPath)
    {
        var info = new FileInfo(assemblyPath);
        lock (CacheSync)
        {
            if (Cache.TryGetValue(assemblyPath, out var cached) &&
                cached.Length == info.Length &&
                cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                return cached.Portraits;
            }
        }

        var portraits = ScanAssembly(assemblyPath);
        lock (CacheSync)
        {
            Cache[assemblyPath] = new ScannerCacheEntry(
                info.Length,
                info.LastWriteTimeUtc,
                portraits);
        }

        return portraits;
    }

    private static IReadOnlyDictionary<string, string> ScanAssembly(string assemblyPath)
    {
        using var stream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        var portraits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!peReader.HasMetadata)
        {
            return portraits;
        }

        var reader = peReader.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var typePortraits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var typeName = reader.GetString(type.Name);
            var hasPortraitPatchHint =
                typeName.Contains("Portrait", StringComparison.OrdinalIgnoreCase) ||
                HasPortraitPatchAttribute(reader, type.GetCustomAttributes());
            var hasRuntimeTypeLookup = false;
            var hasDictionaryLookup = false;

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                hasPortraitPatchHint |=
                    reader.GetString(method.Name).Contains("Portrait", StringComparison.OrdinalIgnoreCase) ||
                    HasPortraitPatchAttribute(reader, method.GetCustomAttributes());
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                if (il != null)
                {
                    ScanIl(
                        reader,
                        il,
                        typePortraits,
                        ref hasRuntimeTypeLookup,
                        ref hasDictionaryLookup);
                }
            }

            // Exact type/path pairs are not enough by themselves: unrelated UI dictionaries can
            // also contain Type keys and image paths. Require the same patch class to perform the
            // usual card-instance GetType + dictionary TryGetValue lookup and to identify itself
            // as a portrait patch.
            if (!hasPortraitPatchHint || !hasRuntimeTypeLookup || !hasDictionaryLookup)
            {
                continue;
            }

            foreach (var pair in typePortraits)
            {
                portraits[pair.Key] = pair.Value;
            }
        }

        return portraits;
    }

    private static void ScanIl(
        MetadataReader reader,
        byte[] il,
        IDictionary<string, string> portraits,
        ref bool hasRuntimeTypeLookup,
        ref bool hasDictionaryLookup)
    {
        string? pendingType = null;
        string? pendingPath = null;
        var pendingTypeInstruction = -1;
        var pendingPathInstruction = -1;
        var instruction = 0;
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

            if ((opCode.Equals(OpCodes.Ldtoken) || opCode.OperandType == OperandType.InlineType) &&
                operandSize == 4)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                var typeName = ResolveTypeName(reader, MetadataTokens.EntityHandle(token));
                if (!string.IsNullOrWhiteSpace(typeName))
                {
                    pendingType = typeName;
                    pendingPath = null;
                    pendingTypeInstruction = instruction;
                }
            }
            else if (opCode.OperandType == OperandType.InlineString && operandSize == 4)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                try
                {
                    var valueString = reader.GetUserString(
                        MetadataTokens.UserStringHandle(token & 0x00ffffff));
                    if (IsPortraitResource(valueString))
                    {
                        pendingPath = NormalizeResourcePath(valueString);
                        pendingPathInstruction = instruction;
                    }
                }
                catch (Exception exception) when (
                    exception is BadImageFormatException or ArgumentException)
                {
                    // Ignore malformed metadata in third-party providers.
                }
            }
            else if ((opCode.Equals(OpCodes.Call) || opCode.Equals(OpCodes.Callvirt)) &&
                     opCode.OperandType == OperandType.InlineMethod && operandSize == 4)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                var methodName = ResolveMethodName(reader, MetadataTokens.EntityHandle(token));
                if (methodName != null)
                {
                    hasRuntimeTypeLookup |= methodName.Equals("GetType", StringComparison.Ordinal);
                    hasDictionaryLookup |= methodName.Equals("TryGetValue", StringComparison.Ordinal);
                    if (methodName is "set_Item" or "Add" or "TryAdd")
                    {
                        if (!string.IsNullOrWhiteSpace(pendingType) &&
                            !string.IsNullOrWhiteSpace(pendingPath) &&
                            instruction - pendingTypeInstruction <= 24 &&
                            instruction - pendingPathInstruction <= 8)
                        {
                            portraits[pendingType] = pendingPath;
                        }

                        pendingType = null;
                        pendingPath = null;
                    }
                }
            }

            offset += operandSize;
            instruction++;
        }
    }

    private static bool HasPortraitPatchAttribute(
        MetadataReader reader,
        CustomAttributeHandleCollection attributes)
    {
        foreach (var attributeHandle in attributes)
        {
            try
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                var text = Encoding.UTF8.GetString(reader.GetBlobBytes(attribute.Value));
                if (text.Contains("Portrait", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is BadImageFormatException or ArgumentException)
            {
                // Ignore malformed custom attributes in third-party providers.
            }
        }

        return false;
    }

    private static string? ResolveTypeName(MetadataReader reader, EntityHandle handle) =>
        handle.Kind switch
        {
            HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition(
                (TypeDefinitionHandle)handle).Name),
            HandleKind.TypeReference => reader.GetString(reader.GetTypeReference(
                (TypeReferenceHandle)handle).Name),
            _ => null
        };

    private static string? ResolveMethodName(MetadataReader reader, EntityHandle handle) =>
        handle.Kind switch
        {
            HandleKind.MethodDefinition => reader.GetString(reader.GetMethodDefinition(
                (MethodDefinitionHandle)handle).Name),
            HandleKind.MemberReference => reader.GetString(reader.GetMemberReference(
                (MemberReferenceHandle)handle).Name),
            HandleKind.MethodSpecification => ResolveMethodName(
                reader,
                reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method),
            _ => null
        };

    private static bool IsPortraitResource(string value)
    {
        if (!value.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = System.IO.Path.GetExtension(value);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tres", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".res", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeResourcePath(string value) => value.Replace('\\', '/');

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
        IReadOnlyDictionary<string, string> Portraits);
}
