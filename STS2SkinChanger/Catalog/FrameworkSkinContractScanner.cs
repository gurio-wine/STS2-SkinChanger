using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace STS2SkinChanger.Catalog;

/// <summary>
/// Reads framework-style skin declarations without loading or executing a third-party DLL.
/// A declaration is recognized from its generic capability base (CharacterSkin&lt;T&gt;,
/// OrbSkin&lt;T&gt; or RelicSkin&lt;T&gt;), constant resource getters and SkinData registration IL.
/// No provider or skin Mod names are matched.
/// </summary>
internal static class FrameworkSkinContractScanner
{
    private static readonly object CacheSync = new();
    private static readonly Dictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    public static IReadOnlyList<FrameworkCharacterSkinContract> Scan(
        string? providerRoot,
        string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerRoot) || !Directory.Exists(providerRoot))
        {
            return [];
        }

        var primaryAssembly = Path.Combine(providerRoot, providerId + ".dll");
        var assemblyPaths = File.Exists(primaryAssembly)
            ? [primaryAssembly]
            : Directory.EnumerateFiles(providerRoot, "*.dll", SearchOption.TopDirectoryOnly)
                .ToArray();
        return assemblyPaths
            .SelectMany(path => ScanAssemblyCached(path, providerId))
            .ToArray();
    }

    private static IReadOnlyList<FrameworkCharacterSkinContract> ScanAssemblyCached(
        string assemblyPath,
        string providerId)
    {
        var info = new FileInfo(assemblyPath);
        lock (CacheSync)
        {
            if (Cache.TryGetValue(assemblyPath, out var cached) &&
                cached.Length == info.Length &&
                cached.LastWriteTimeUtc == info.LastWriteTimeUtc &&
                cached.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            {
                return cached.Contracts;
            }
        }

        IReadOnlyList<FrameworkCharacterSkinContract> contracts;
        try
        {
            contracts = ScanAssembly(assemblyPath, providerId);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"无法静态读取框架皮肤契约 {assemblyPath}: {exception.Message}");
            contracts = [];
        }

        lock (CacheSync)
        {
            Cache[assemblyPath] = new CacheEntry(
                info.Length,
                info.LastWriteTimeUtc,
                providerId,
                contracts);
        }

        return contracts;
    }

    private static IReadOnlyList<FrameworkCharacterSkinContract> ScanAssembly(
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
        var assemblyName = reader.IsAssembly
            ? reader.GetString(reader.GetAssemblyDefinition().Name)
            : Path.GetFileNameWithoutExtension(assemblyPath);
        var typeProvider = new MetadataTypeProvider(assemblyName);
        var characterDeclarations = new Dictionary<string, RawDeclaration>(
            StringComparer.Ordinal);
        var orbDeclarations = new List<FrameworkModelSkinContract>();
        var relicDeclarations = new List<FrameworkModelSkinContract>();

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var typeName = GetTypeDefinitionFullName(reader, typeHandle);
            var baseType = DecodeType(reader, type.BaseType, typeProvider);
            if (baseType == null || baseType.GenericArguments.Count != 1)
            {
                continue;
            }

            var capability = TrimGenericArity(baseType.SimpleName);
            var targetModelName = TrimGenericArity(baseType.GenericArguments[0].SimpleName);
            var scalarResources = new Dictionary<string, string>(StringComparer.Ordinal);
            var listResources = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var scalarValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var propertyHandle in type.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle);
                var getterHandle = property.GetAccessors().Getter;
                if (getterHandle.IsNil)
                {
                    continue;
                }

                var getter = reader.GetMethodDefinition(getterHandle);
                if (getter.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var propertyName = reader.GetString(property.Name);
                var userStrings = ReadUserStrings(peReader, reader, getter).ToArray();
                var values = userStrings
                    .Where(IsResourcePath)
                    .Select(NormalizeResourcePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (values.Length == 1)
                {
                    scalarResources[propertyName] = values[0];
                }
                else if (values.Length > 1)
                {
                    listResources[propertyName] = values;
                }
                else if (propertyName.EndsWith("Color", StringComparison.Ordinal) &&
                         userStrings.LastOrDefault(IsColorLiteral) is { } color)
                {
                    scalarValues[propertyName] = color;
                }
            }

            var frameworkAssemblyName = baseType.AssemblyName ?? string.Empty;
            if (capability.Equals("CharacterSkin", StringComparison.Ordinal) &&
                scalarResources.Count + listResources.Count > 0)
            {
                characterDeclarations[typeName] = new RawDeclaration(
                    typeName,
                    frameworkAssemblyName,
                    NormalizeToken(targetModelName),
                    scalarResources,
                    listResources,
                    scalarValues);
            }
            else if (capability.Equals("OrbSkin", StringComparison.Ordinal) &&
                     scalarResources.Count + listResources.Count > 0)
            {
                orbDeclarations.Add(new FrameworkModelSkinContract(
                    targetModelName,
                    scalarResources,
                    listResources,
                    scalarValues));
            }
            else if (capability.Equals("RelicSkin", StringComparison.Ordinal) &&
                     scalarResources.Count + listResources.Count > 0)
            {
                relicDeclarations.Add(new FrameworkModelSkinContract(
                    targetModelName,
                    scalarResources,
                    listResources,
                    scalarValues));
            }
        }

        if (characterDeclarations.Count == 0)
        {
            return [];
        }

        var registrations = ReadRegistrations(
            peReader,
            reader,
            typeProvider,
            characterDeclarations.Keys);
        var targetGroupCount = characterDeclarations.Values
            .Select(declaration => declaration.TargetGroupId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var sharedOrbs = targetGroupCount == 1 ? orbDeclarations : [];
        var sharedRelics = targetGroupCount == 1 ? relicDeclarations : [];
        var contracts = new List<FrameworkCharacterSkinContract>();
        foreach (var declaration in characterDeclarations.Values)
        {
            var registration = registrations.FirstOrDefault(candidate =>
                candidate.DescriptorTypeName.Equals(
                    declaration.DescriptorTypeName,
                    StringComparison.Ordinal));
            var descriptorName = declaration.DescriptorTypeName[(declaration.DescriptorTypeName.LastIndexOf('.') + 1)..];
            var skinId = registration?.SkinId ?? descriptorName;
            var displayName = registration?.DisplayName ?? SplitPascalCase(descriptorName);
            contracts.Add(new FrameworkCharacterSkinContract(
                ProviderId: providerId,
                FrameworkAssemblyName: declaration.FrameworkAssemblyName,
                TargetGroupId: declaration.TargetGroupId,
                OptionId: providerId + "::" + NormalizeOptionToken(skinId),
                SkinId: skinId,
                DisplayName: displayName,
                DescriptorTypeName: declaration.DescriptorTypeName,
                CharacterResources: declaration.ScalarResources,
                CharacterResourceLists: declaration.ListResources,
                CharacterValues: declaration.ScalarValues,
                Orbs: sharedOrbs,
                Relics: sharedRelics));
        }

        return contracts
            .OrderBy(contract => registrations.FindIndex(registration =>
                registration.DescriptorTypeName.Equals(
                    contract.DescriptorTypeName,
                    StringComparison.Ordinal)))
            .ThenBy(contract => contract.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static List<Registration> ReadRegistrations(
        PEReader peReader,
        MetadataReader reader,
        MetadataTypeProvider typeProvider,
        IEnumerable<string> descriptorTypeNames)
    {
        var descriptorNames = descriptorTypeNames.ToHashSet(StringComparer.Ordinal);
        var registrations = new List<Registration>();
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

                var recentStrings = new List<string>();
                var pending = new Queue<(string SkinId, string DisplayName)>();
                foreach (var instruction in ReadInstructions(peReader, reader, method))
                {
                    if (instruction.UserString != null && !IsResourcePath(instruction.UserString))
                    {
                        recentStrings.Add(instruction.UserString);
                        if (recentStrings.Count > 8)
                        {
                            recentStrings.RemoveAt(0);
                        }
                    }

                    if (!instruction.OpCode.Equals(OpCodes.Newobj) || instruction.Entity.IsNil)
                    {
                        continue;
                    }

                    var declaringType = ResolveMethodDeclaringType(
                        reader,
                        instruction.Entity,
                        typeProvider);
                    if (declaringType == null)
                    {
                        continue;
                    }

                    if (TrimGenericArity(declaringType.SimpleName).Equals(
                            "SkinData",
                            StringComparison.Ordinal) &&
                        recentStrings.Count >= 2)
                    {
                        pending.Enqueue((
                            recentStrings[^2],
                            recentStrings[^1]));
                        continue;
                    }

                    if (descriptorNames.Contains(declaringType.FullName) && pending.Count > 0)
                    {
                        var registration = pending.Dequeue();
                        registrations.Add(new Registration(
                            declaringType.FullName,
                            registration.SkinId,
                            registration.DisplayName));
                    }
                }
            }
        }

        return registrations
            .DistinctBy(registration => registration.DescriptorTypeName, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> ReadUserStrings(
        PEReader peReader,
        MetadataReader reader,
        MethodDefinition method) =>
        ReadInstructions(peReader, reader, method)
            .Where(instruction => instruction.UserString != null)
            .Select(instruction => instruction.UserString!);

    private static IEnumerable<IlInstruction> ReadInstructions(
        PEReader peReader,
        MetadataReader reader,
        MethodDefinition method)
    {
        var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
        if (il == null)
        {
            yield break;
        }

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

            string? userString = null;
            EntityHandle entity = default;
            if (opCode.OperandType == OperandType.InlineString && operandSize == 4)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                try
                {
                    userString = reader.GetUserString(
                        MetadataTokens.UserStringHandle(token & 0x00ffffff));
                }
                catch (Exception exception) when (
                    exception is BadImageFormatException or ArgumentException)
                {
                    // Ignore malformed third-party metadata.
                }
            }
            else if (opCode.OperandType is OperandType.InlineMethod or
                     OperandType.InlineType or
                     OperandType.InlineTok && operandSize == 4)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                try
                {
                    entity = MetadataTokens.EntityHandle(token);
                }
                catch (ArgumentException)
                {
                    entity = default;
                }
            }

            yield return new IlInstruction(opCode, entity, userString);
            offset += operandSize;
        }
    }

    private static MetadataType? ResolveMethodDeclaringType(
        MetadataReader reader,
        EntityHandle methodHandle,
        MetadataTypeProvider typeProvider)
    {
        if (methodHandle.Kind == HandleKind.MethodSpecification)
        {
            methodHandle = reader.GetMethodSpecification(
                (MethodSpecificationHandle)methodHandle).Method;
        }

        return methodHandle.Kind switch
        {
            HandleKind.MethodDefinition => DecodeType(
                reader,
                reader.GetMethodDefinition((MethodDefinitionHandle)methodHandle).GetDeclaringType(),
                typeProvider),
            HandleKind.MemberReference => DecodeType(
                reader,
                reader.GetMemberReference((MemberReferenceHandle)methodHandle).Parent,
                typeProvider),
            _ => null
        };
    }

    private static MetadataType? DecodeType(
        MetadataReader reader,
        EntityHandle handle,
        MetadataTypeProvider provider)
    {
        if (handle.IsNil)
        {
            return null;
        }

        return handle.Kind switch
        {
            HandleKind.TypeDefinition => provider.GetTypeFromDefinition(
                reader,
                (TypeDefinitionHandle)handle,
                0),
            HandleKind.TypeReference => provider.GetTypeFromReference(
                reader,
                (TypeReferenceHandle)handle,
                0),
            HandleKind.TypeSpecification => reader.GetTypeSpecification(
                (TypeSpecificationHandle)handle).DecodeSignature(provider, genericContext: null),
            _ => null
        };
    }

    private static string GetTypeDefinitionFullName(
        MetadataReader reader,
        TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return GetTypeDefinitionFullName(reader, declaringType) + "+" + name;
        }

        var typeNamespace = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(typeNamespace) ? name : typeNamespace + "." + name;
    }

    private static int GetOperandSize(OperandType operandType, byte[] il, int offset) =>
        operandType switch
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

    private static bool IsResourcePath(string value) =>
        value.StartsWith("res://", StringComparison.OrdinalIgnoreCase);

    private static bool IsColorLiteral(string value) =>
        value.Length is 6 or 8 && value.All(Uri.IsHexDigit);

    private static string NormalizeResourcePath(string value) => value.Replace('\\', '/');

    private static string NormalizeToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string NormalizeOptionToken(string value)
    {
        var normalized = new string(value
            .Where(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "skin" : normalized;
    }

    private static string TrimGenericArity(string value)
    {
        var index = value.IndexOf('`');
        return index < 0 ? value : value[..index];
    }

    private static string SplitPascalCase(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");

    private sealed record RawDeclaration(
        string DescriptorTypeName,
        string FrameworkAssemblyName,
        string TargetGroupId,
        IReadOnlyDictionary<string, string> ScalarResources,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ListResources,
        IReadOnlyDictionary<string, string> ScalarValues);

    private sealed record Registration(
        string DescriptorTypeName,
        string SkinId,
        string DisplayName);

    private sealed record IlInstruction(
        OpCode OpCode,
        EntityHandle Entity,
        string? UserString);

    private sealed record CacheEntry(
        long Length,
        DateTime LastWriteTimeUtc,
        string ProviderId,
        IReadOnlyList<FrameworkCharacterSkinContract> Contracts);

    private sealed record MetadataType(
        string FullName,
        string? AssemblyName,
        IReadOnlyList<MetadataType> GenericArguments)
    {
        public string SimpleName
        {
            get
            {
                var separator = Math.Max(FullName.LastIndexOf('.'), FullName.LastIndexOf('+'));
                return separator < 0 ? FullName : FullName[(separator + 1)..];
            }
        }
    }

    private sealed class MetadataTypeProvider(string currentAssemblyName) :
        ISignatureTypeProvider<MetadataType, object?>
    {
        public MetadataType GetArrayType(MetadataType elementType, ArrayShape shape) =>
            new(elementType.FullName + "[]", elementType.AssemblyName, [elementType]);

        public MetadataType GetByReferenceType(MetadataType elementType) =>
            new(elementType.FullName + "&", elementType.AssemblyName, [elementType]);

        public MetadataType GetFunctionPointerType(MethodSignature<MetadataType> signature) =>
            new("methodptr", null, []);

        public MetadataType GetGenericInstantiation(
            MetadataType genericType,
            ImmutableArray<MetadataType> typeArguments) =>
            genericType with { GenericArguments = typeArguments };

        public MetadataType GetGenericMethodParameter(object? genericContext, int index) =>
            new("!!" + index, null, []);

        public MetadataType GetGenericTypeParameter(object? genericContext, int index) =>
            new("!" + index, null, []);

        public MetadataType GetModifiedType(
            MetadataType modifier,
            MetadataType unmodifiedType,
            bool isRequired) => unmodifiedType;

        public MetadataType GetPinnedType(MetadataType elementType) => elementType;

        public MetadataType GetPointerType(MetadataType elementType) =>
            new(elementType.FullName + "*", elementType.AssemblyName, [elementType]);

        public MetadataType GetPrimitiveType(PrimitiveTypeCode typeCode) =>
            new(typeCode.ToString(), "System.Private.CoreLib", []);

        public MetadataType GetSZArrayType(MetadataType elementType) =>
            new(elementType.FullName + "[]", elementType.AssemblyName, [elementType]);

        public MetadataType GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) =>
            new(GetTypeDefinitionFullName(reader, handle), currentAssemblyName, []);

        public MetadataType GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            var typeNamespace = reader.GetString(type.Namespace);
            var name = reader.GetString(type.Name);
            var fullName = string.IsNullOrEmpty(typeNamespace)
                ? name
                : typeNamespace + "." + name;
            string? assemblyName = type.ResolutionScope.Kind == HandleKind.AssemblyReference
                ? reader.GetString(reader.GetAssemblyReference(
                    (AssemblyReferenceHandle)type.ResolutionScope).Name)
                : null;
            return new MetadataType(fullName, assemblyName, []);
        }

        public MetadataType GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}

internal sealed record FrameworkCharacterSkinContract(
    string ProviderId,
    string FrameworkAssemblyName,
    string TargetGroupId,
    string OptionId,
    string SkinId,
    string DisplayName,
    string DescriptorTypeName,
    IReadOnlyDictionary<string, string> CharacterResources,
    IReadOnlyDictionary<string, IReadOnlyList<string>> CharacterResourceLists,
    IReadOnlyDictionary<string, string> CharacterValues,
    IReadOnlyList<FrameworkModelSkinContract> Orbs,
    IReadOnlyList<FrameworkModelSkinContract> Relics)
{
    public IReadOnlySet<string> ResourcePaths => CharacterResources.Values
        .Concat(CharacterResourceLists.Values.SelectMany(paths => paths))
        .Concat(Orbs.SelectMany(orb => orb.ResourcePaths))
        .Concat(Relics.SelectMany(relic => relic.ResourcePaths))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

internal sealed record FrameworkModelSkinContract(
    string TargetModelName,
    IReadOnlyDictionary<string, string> Resources,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ResourceLists,
    IReadOnlyDictionary<string, string> Values)
{
    public IEnumerable<string> ResourcePaths =>
        Resources.Values.Concat(ResourceLists.Values.SelectMany(paths => paths));
}
