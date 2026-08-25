using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace STS2SkinChanger.Catalog;

/// <summary>
/// Discovers a provider-owned monster scene whose managed assembly exposes several visual modes.
/// Detection is capability based: no workshop id, provider name or monster id is special-cased.
/// </summary>
internal static class RuntimeMonsterVisualModeScanner
{
    private static readonly string[] ModeTypeSuffixes =
    [
        "VisualMode",
        "AppearanceMode",
        "SkinMode"
    ];

    private static readonly HashSet<string> SetterNames =
    [
        "SetMode",
        "SetVisualMode",
        "SetAppearanceMode",
        "SetSkinMode"
    ];

    public static IReadOnlyList<RuntimeMonsterVisualMode> Scan(
        PckResourceIndex index,
        string providerId,
        SkinOption option)
    {
        var providerRoot = index.Mod.RootPath;
        if (string.IsNullOrWhiteSpace(providerRoot) ||
            !Directory.Exists(providerRoot) ||
            !LooksLikeMultiModeMonsterScene(option))
        {
            return [];
        }

        foreach (var assemblyPath in EnumerateCandidateAssemblies(providerRoot))
        {
            try
            {
                var modes = ScanAssembly(assemblyPath, providerId);
                if (modes.Count >= 2)
                {
                    return modes.Select(mode => mode with
                        {
                            ResourcePaths = FindModeResourcePaths(mode, option, index)
                        })
                        .ToArray();
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"无法静态分析怪物外观模式 DLL {assemblyPath}: {exception.Message}");
            }
        }

        return [];
    }

    private static IReadOnlyList<string> FindModeResourcePaths(
        RuntimeMonsterVisualMode mode,
        SkinOption option,
        PckResourceIndex index)
    {
        var words = SplitModeWords(mode.ModeName)
            .Where(word => word.Length > 2 && word is not "animated" and not "animation" and not "mode")
            .ToArray();
        if (words.Length == 0)
        {
            return [];
        }

        var creatureScene = option.Assets.Keys
            .Append(option.ManagedMonsterScene?.SourcePath ?? string.Empty)
            .FirstOrDefault(path =>
                path.StartsWith("res://scenes/creature_visuals/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase));
        var modelToken = creatureScene == null
            ? string.Empty
            : Path.GetFileNameWithoutExtension(creatureScene);
        var directResources = index.Archive.Paths
            .Where(path => path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase))
            .Select(SkinCatalog.NormalizeTakeoverPath);
        return option.Assets.Keys
            .Concat(directResources)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase))
            .Where(path => modelToken.Length == 0 ||
                           path.Contains(modelToken, StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var normalizedPath = path.Replace('-', '_').ToLowerInvariant();
                return words.Any(word => normalizedPath.Contains(word, StringComparison.Ordinal));
            })
            .Where(path =>
                path.Contains("spriteframe", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("animation", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("atlas", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> SplitModeWords(string value)
    {
        var words = new List<string>();
        var start = 0;
        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] is '_' or '-' ||
                char.IsUpper(value[index]) && !char.IsUpper(value[index - 1]))
            {
                if (index > start)
                {
                    words.Add(value[start..index].Trim('_', '-').ToLowerInvariant());
                }

                start = value[index] is '_' or '-' ? index + 1 : index;
            }
        }

        if (start < value.Length)
        {
            words.Add(value[start..].Trim('_', '-').ToLowerInvariant());
        }

        return words.Where(word => word.Length > 0).ToArray();
    }

    private static bool LooksLikeMultiModeMonsterScene(SkinOption option)
    {
        var paths = option.Assets.Keys
            .Append(option.ManagedMonsterScene?.SourcePath ?? string.Empty)
            .Where(path => path.Length > 0)
            .ToArray();
        return paths.Any(path =>
                   path.StartsWith("res://scenes/creature_visuals/", StringComparison.OrdinalIgnoreCase) &&
                   path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)) &&
               paths.Any(path =>
                   path.EndsWith("spriteframes.tres", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("/atlas/", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateCandidateAssemblies(string providerRoot)
    {
        // Compatibility builds normally live below lib/<game-version>. Prefer those over the
        // small top-level loader, and prefer the newest directory when several equivalent
        // compatibility DLLs are present.
        return Directory.EnumerateFiles(providerRoot, "*.dll", SearchOption.AllDirectories)
            .OrderByDescending(path => Path.GetRelativePath(providerRoot, path).Count(c => c is '/' or '\\'))
            .ThenByDescending(path => Path.GetRelativePath(providerRoot, path), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<RuntimeMonsterVisualMode> ScanAssembly(
        string assemblyPath,
        string providerId)
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
        var signatureProvider = new MetadataTypeNameProvider();
        var enumCandidates = new Dictionary<string, EnumCandidate>(StringComparer.Ordinal);
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var name = reader.GetString(type.Name);
            if (!ModeTypeSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) ||
                !IsEnum(reader, type.BaseType))
            {
                continue;
            }

            var values = type.GetFields()
                .Select(reader.GetFieldDefinition)
                .Where(field => field.Attributes.HasFlag(FieldAttributes.Literal))
                .Select(field => reader.GetString(field.Name))
                .Where(value => !value.Equals("value__", StringComparison.Ordinal))
                .ToArray();
            if (values.Length is < 2 or > 12)
            {
                continue;
            }

            var fullName = GetTypeName(reader, type);
            enumCandidates[fullName] = new EnumCandidate(fullName, values);
        }

        if (enumCandidates.Count == 0)
        {
            return [];
        }

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var serviceTypeName = GetTypeName(reader, type);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var methodName = reader.GetString(method.Name);
                if (!method.Attributes.HasFlag(MethodAttributes.Static) ||
                    !SetterNames.Contains(methodName))
                {
                    continue;
                }

                var signature = method.DecodeSignature(signatureProvider, reader);
                var enumTypeName = signature.ParameterTypes
                    .FirstOrDefault(enumCandidates.ContainsKey);
                if (enumTypeName == null)
                {
                    continue;
                }

                var candidate = enumCandidates[enumTypeName];
                return candidate.Values.Select(value => new RuntimeMonsterVisualMode(
                        providerId,
                        Path.GetFullPath(assemblyPath),
                        serviceTypeName,
                        candidate.TypeName,
                        methodName,
                        value,
                        DisplayModeName(value)))
                    .ToArray();
            }
        }

        return [];
    }

    private static bool IsEnum(MetadataReader reader, EntityHandle baseType)
    {
        var name = baseType.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeName(reader, reader.GetTypeDefinition(
                (TypeDefinitionHandle)baseType)),
            HandleKind.TypeReference => GetTypeName(reader, reader.GetTypeReference(
                (TypeReferenceHandle)baseType)),
            _ => string.Empty
        };
        return name.Equals("System.Enum", StringComparison.Ordinal);
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

    private static string DisplayModeName(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized switch
        {
            "staticultrahd" => "超高清静态",
            "performanceanimated" => "性能优化动画",
            "originalhdanimated" => "原始高清动画",
            _ => string.Concat(value.Select((character, index) =>
                    index > 0 && char.IsUpper(character) && !char.IsUpper(value[index - 1])
                        ? " " + character
                        : character.ToString()))
                .Replace('_', ' ')
        };
    }

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

    private sealed record EnumCandidate(string TypeName, IReadOnlyList<string> Values);
}
