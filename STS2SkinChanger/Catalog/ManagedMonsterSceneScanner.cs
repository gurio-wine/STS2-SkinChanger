using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace STS2SkinChanger.Catalog;

/// <summary>
/// Reads common MonsterModel.VisualsPath replacement intent without loading or
/// executing the provider assembly. This lets private provider scenes become
/// selectable resources while the provider initializer and Harmony patches stay
/// isolated.
/// </summary>
internal static class ManagedMonsterSceneScanner
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    public static IReadOnlyList<ManagedMonsterSceneReplacement> Scan(
        string? providerRoot,
        string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerRoot) || !Directory.Exists(providerRoot))
        {
            return [];
        }

        var primaryAssembly = System.IO.Path.Combine(providerRoot, providerId + ".dll");
        var assemblyPaths = File.Exists(primaryAssembly)
            ? [primaryAssembly]
            : Directory.EnumerateFiles(providerRoot, "*.dll", SearchOption.TopDirectoryOnly)
                .ToArray();
        var replacements = new Dictionary<string, ManagedMonsterSceneReplacement>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var assemblyPath in assemblyPaths)
        {
            try
            {
                ScanAssembly(assemblyPath, replacements);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"无法静态分析怪物场景 DLL {assemblyPath}: {exception.Message}");
            }
        }

        return replacements.Values.ToArray();
    }

    /// <summary>
    /// Finds data-driven monster providers that keep the original creature scene as a host and
    /// construct the replacement body from private animation/model resources at runtime. These
    /// providers do not expose a replacement PackedScene from a VisualsPath patch, so the regular
    /// scene scanner cannot associate their private resource folders with the STS2 monster they
    /// replace. Pairing the canonical creature-scene path and private visual roots declared in the
    /// same profile builder preserves that ownership without executing third-party code.
    /// </summary>
    public static IReadOnlyList<ManagedMonsterRuntimeProfile> ScanRuntimeProfiles(
        string? providerRoot,
        string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerRoot) || !Directory.Exists(providerRoot))
        {
            return [];
        }

        var primaryAssembly = System.IO.Path.Combine(providerRoot, providerId + ".dll");
        var assemblyPaths = File.Exists(primaryAssembly)
            ? [primaryAssembly]
            : Directory.EnumerateFiles(providerRoot, "*.dll", SearchOption.TopDirectoryOnly)
                .ToArray();
        var profiles = new Dictionary<string, ManagedMonsterRuntimeProfile>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var assemblyPath in assemblyPaths)
        {
            try
            {
                ScanRuntimeProfilesAssembly(assemblyPath, profiles);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"无法静态分析运行时怪物配置 DLL {assemblyPath}: {exception.Message}");
            }
        }

        return profiles.Values.ToArray();
    }

    /// <summary>
    /// Reads canonical resources declared by gameplay-mod MonsterModel.AssetPaths overrides.
    /// Data-only skins often replace only these code-selected phase textures and therefore have
    /// no creature scene from which their owning monster can otherwise be inferred.
    /// </summary>
    public static IReadOnlyList<ManagedMonsterAssetDeclaration> ScanDeclaredAssets(
        string? providerRoot,
        string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerRoot) || !Directory.Exists(providerRoot))
        {
            return [];
        }

        var primaryAssembly = System.IO.Path.Combine(providerRoot, providerId + ".dll");
        var assemblyPaths = File.Exists(primaryAssembly)
            ? [primaryAssembly]
            : Directory.EnumerateFiles(providerRoot, "*.dll", SearchOption.TopDirectoryOnly)
                .ToArray();
        var declarations = new Dictionary<string, ManagedMonsterAssetDeclaration>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var assemblyPath in assemblyPaths)
        {
            try
            {
                ScanDeclaredAssetsAssembly(assemblyPath, declarations);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"无法静态分析怪物资源 DLL {assemblyPath}: {exception.Message}");
            }
        }

        return declarations.Values.ToArray();
    }

    private static void ScanDeclaredAssetsAssembly(
        string assemblyPath,
        IDictionary<string, ManagedMonsterAssetDeclaration> declarations)
    {
        using var stream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        if (!peReader.HasMetadata)
        {
            return;
        }

        var reader = peReader.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var baseTypeName = ResolveTypeName(reader, type.BaseType);
            if (baseTypeName == null || !IsMonsterModelType(baseTypeName))
            {
                continue;
            }

            var modelTypeName = reader.GetString(type.Name);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0 ||
                    !reader.GetString(method.Name).Equals("get_AssetPaths", StringComparison.Ordinal))
                {
                    continue;
                }

                var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                if (il == null)
                {
                    continue;
                }

                var resourcePaths = ScanStrings(reader, il)
                    .Select(TryCanonicalizeAssetPath)
                    .Where(path => path != null)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (resourcePaths.Length > 0)
                {
                    declarations[NormalizeToken(modelTypeName)] =
                        new ManagedMonsterAssetDeclaration(modelTypeName, resourcePaths);
                }
            }
        }
    }

    private static void ScanRuntimeProfilesAssembly(
        string assemblyPath,
        IDictionary<string, ManagedMonsterRuntimeProfile> profiles)
    {
        using var stream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        if (!peReader.HasMetadata)
        {
            return;
        }

        var reader = peReader.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                if (il == null)
                {
                    continue;
                }

                var resourcePaths = ScanStrings(reader, il)
                    .Select(NormalizeResourcePath)
                    .Where(path => path != null)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var targetScenes = resourcePaths
                    .Where(IsCanonicalCreatureScenePath)
                    .ToArray();
                if (targetScenes.Length != 1)
                {
                    continue;
                }

                var providerResources = resourcePaths
                    .Where(path => !IsCanonicalCreatureScenePath(path) &&
                                   IsPrivateMonsterVisualResourcePath(path))
                    .ToArray();
                if (providerResources.Length == 0)
                {
                    continue;
                }

                var targetScene = targetScenes[0];
                if (profiles.TryGetValue(targetScene, out var existing))
                {
                    providerResources = existing.ProviderResourcePaths
                        .Concat(providerResources)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }

                profiles[targetScene] = new ManagedMonsterRuntimeProfile(
                    targetScene,
                    providerResources);
            }
        }
    }

    private static void ScanAssembly(
        string assemblyPath,
        IDictionary<string, ManagedMonsterSceneReplacement> replacements)
    {
        using var stream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        if (!peReader.HasMetadata)
        {
            return;
        }

        var reader = peReader.GetMetadataReader();
        var signatureProvider = new MetadataTypeNameProvider();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var patchTypeName = reader.GetString(type.Name);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var signature = method.DecodeSignature(signatureProvider, reader);
                if (signature.ParameterTypes.Any(IsNonMonsterModelType))
                {
                    continue;
                }

                var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
                var il = body.GetILBytes();
                if (il == null)
                {
                    continue;
                }

                var candidates = ScanIl(reader, il);
                if (candidates.Count == 0)
                {
                    continue;
                }

                var hasStringResult = signature.ParameterTypes.Any(IsStringByReferenceType);
                // A scene path plus a `string&` result is not sufficient evidence by itself:
                // character-select/UI patches frequently cast a node to CanvasItem and happen to
                // contain a scene path, which used to create phantom monster groups such as
                // `canvasitem`. Require a model- or VisualsPath-specific signal from the patch
                // type/signature. This keeps the scanner generic while preventing unrelated UI
                // callbacks from claiming a monster scene.
                var isVisualsPathPatch = patchTypeName.Contains(
                                             "VisualsPath",
                                             StringComparison.OrdinalIgnoreCase) ||
                                         patchTypeName.Contains(
                                             "Monster",
                                             StringComparison.OrdinalIgnoreCase) ||
                                         hasStringResult && signature.ParameterTypes.Any(IsMonsterModelType);
                if (!isVisualsPathPatch)
                {
                    continue;
                }

                var fallbackTypeName = GetPatchTargetName(patchTypeName);
                foreach (var candidate in candidates)
                {
                    var modelTypeName = candidate.ModelTypeName ?? fallbackTypeName;
                    if (!IsPlausibleModelType(modelTypeName))
                    {
                        continue;
                    }

                    var simpleTypeName = SimpleTypeName(modelTypeName!);
                    replacements[NormalizeToken(simpleTypeName)] =
                        new ManagedMonsterSceneReplacement(simpleTypeName, candidate.ScenePath);
                }
            }
        }
    }

    private static List<SceneCandidate> ScanIl(MetadataReader reader, byte[] il)
    {
        var result = new List<SceneCandidate>();
        string? lastModelType = null;
        var offset = 0;
        while (offset < il.Length)
        {
            var first = il[offset++];
            var value = first == 0xfe && offset < il.Length
                ? (ushort)(0xfe00 | il[offset++])
                : first;
            if (!OpCodesByValue.TryGetValue(value, out var opCode))
            {
                return result;
            }

            var operandOffset = offset;
            var operandSize = GetOperandSize(opCode.OperandType, il, operandOffset);
            if (operandSize < 0 || operandOffset + operandSize > il.Length)
            {
                return result;
            }

            if (opCode.OperandType == OperandType.InlineType && operandSize == 4 &&
                (opCode.Equals(OpCodes.Isinst) || opCode.Equals(OpCodes.Castclass)))
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                lastModelType = ResolveTypeName(reader, MetadataTokens.EntityHandle(token));
            }
            else if (opCode.OperandType == OperandType.InlineString && operandSize == 4)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                try
                {
                    var valueString = reader.GetUserString(
                        MetadataTokens.UserStringHandle(token & 0x00ffffff));
                    if (IsScenePath(valueString))
                    {
                        result.Add(new SceneCandidate(lastModelType, valueString));
                    }
                }
                catch (Exception exception) when (
                    exception is BadImageFormatException or ArgumentException)
                {
                    // Ignore malformed metadata in third-party providers.
                }
            }

            offset += operandSize;
        }

        return result;
    }

    private static IEnumerable<string> ScanStrings(MetadataReader reader, byte[] il)
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
                yield break;
            }

            var operandOffset = offset;
            var operandSize = GetOperandSize(opCode.OperandType, il, operandOffset);
            if (operandSize < 0 || operandOffset + operandSize > il.Length)
            {
                yield break;
            }

            if (opCode.OperandType == OperandType.InlineString && operandSize == 4)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                string? text = null;
                try
                {
                    text = reader.GetUserString(MetadataTokens.UserStringHandle(token & 0x00ffffff));
                }
                catch (Exception exception) when (
                    exception is BadImageFormatException or ArgumentException)
                {
                    // Ignore malformed metadata in third-party providers.
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return text;
                }
            }

            offset += operandSize;
        }
    }

    private static string? TryCanonicalizeAssetPath(string value)
    {
        var path = value.Replace('\\', '/').Trim();
        if (path.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        path = path.TrimStart('/');
        if (path.StartsWith("images/", StringComparison.OrdinalIgnoreCase))
        {
            return "res://" + path;
        }

        // ImageHelper.GetImagePath receives paths relative to res://images/. Restrict this
        // conversion to image extensions so unrelated localization/audio strings cannot claim
        // ownership of visual resources.
        return path.Contains('/') &&
               (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            ? "res://images/" + path
            : null;
    }

    private static string? NormalizeResourcePath(string value)
    {
        var path = value.Replace('\\', '/').Trim();
        return path.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }

    private static bool IsCanonicalCreatureScenePath(string path) =>
        path.StartsWith("res://scenes/creature_visuals/", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivateMonsterVisualResourcePath(string path)
    {
        if (!path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return new[]
        {
            "/animations/", "/animation/", "/models/", "/model/", "/visuals/", "/visual/"
        }.Any(segment => path.Contains(segment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsScenePath(string value) =>
        value.StartsWith("res://", StringComparison.OrdinalIgnoreCase) &&
        value.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase);

    private static bool IsMonsterModelType(string typeName) =>
        TypeNameEndsWith(typeName, "MonsterModel");

    private static bool IsStringByReferenceType(string typeName) =>
        typeName.Equals("String&", StringComparison.OrdinalIgnoreCase) ||
        typeName.Equals("System.String&", StringComparison.OrdinalIgnoreCase);

    private static bool IsNonMonsterModelType(string typeName) =>
        TypeNameEndsWith(typeName, "CharacterModel") ||
        TypeNameEndsWith(typeName, "CardModel") ||
        TypeNameEndsWith(typeName, "EventModel");

    private static bool TypeNameEndsWith(string typeName, string expected) =>
        typeName.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
        typeName.EndsWith("." + expected, StringComparison.OrdinalIgnoreCase) ||
        typeName.EndsWith("+" + expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsPlausibleModelType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return false;
        }

        var token = NormalizeToken(typeName);
        return token.Length > 2 && token is not
            "monster" and not
            "monstermodel" and not
            "ncreaturevisuals" and not
            "packedscene" and not
            "node" and not
            "node2d";
    }

    private static string? GetPatchTargetName(string patchTypeName)
    {
        var marker = patchTypeName.IndexOf("VisualsPath", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
        {
            return null;
        }

        var prefix = patchTypeName[..marker].TrimEnd('_', '.', '+');
        var separator = Math.Max(prefix.LastIndexOf('.'), prefix.LastIndexOf('+'));
        return separator >= 0 ? prefix[(separator + 1)..].TrimEnd('_') : prefix;
    }

    private static string? ResolveTypeName(MetadataReader reader, EntityHandle handle)
    {
        if (handle.IsNil)
        {
            return null;
        }

        return handle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeName(reader, reader.GetTypeDefinition(
                (TypeDefinitionHandle)handle)),
            HandleKind.TypeReference => GetTypeName(reader, reader.GetTypeReference(
                (TypeReferenceHandle)handle)),
            _ => null
        };
    }

    private static string GetTypeName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        var typeNamespace = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(typeNamespace) ? name : typeNamespace + "." + name;
    }

    private static string GetTypeName(MetadataReader reader, TypeReference type)
    {
        var name = reader.GetString(type.Name);
        var typeNamespace = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(typeNamespace) ? name : typeNamespace + "." + name;
    }

    private static string SimpleTypeName(string typeName)
    {
        var separator = Math.Max(typeName.LastIndexOf('.'), typeName.LastIndexOf('+'));
        return separator >= 0 ? typeName[(separator + 1)..] : typeName;
    }

    private static string NormalizeToken(string value) =>
        new(SimpleTypeName(value)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

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

    private sealed class MetadataTypeNameProvider : ISignatureTypeProvider<string, MetadataReader>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
        public string GetGenericMethodParameter(MetadataReader genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(MetadataReader genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifierType, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) => GetTypeName(reader, reader.GetTypeDefinition(handle));

        public string GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => GetTypeName(reader, reader.GetTypeReference(handle));

        public string GetTypeFromSpecification(
            MetadataReader reader,
            MetadataReader genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    private sealed record SceneCandidate(string? ModelTypeName, string ScenePath);
}

internal sealed record ManagedMonsterSceneReplacement(string ModelTypeName, string ScenePath);

internal sealed record ManagedMonsterRuntimeProfile(
    string TargetScenePath,
    IReadOnlyList<string> ProviderResourcePaths);

internal sealed record ManagedMonsterAssetDeclaration(
    string ModelTypeName,
    IReadOnlyList<string> ResourcePaths);
