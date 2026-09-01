using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace STS2SkinChanger.Catalog;

/// <summary>
/// Reads presentation intent from managed providers without loading or executing them.
/// This covers DLL-only visual patches that either request the game's Ancient layout or
/// borrow its full-height portrait node for ordinary alternate art, while keeping provider
/// initializers and Harmony patches disabled.
/// </summary>
internal static class ManagedCardPresentationScanner
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    public static IReadOnlyDictionary<string, CardPresentationDefinition> Scan(
        string? providerRoot,
        IReadOnlyCollection<string> knownCardStems,
        IReadOnlyCollection<string>? registryCardStems = null)
    {
        var presentations = new Dictionary<string, CardPresentationDefinition>(
            StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(providerRoot) || !Directory.Exists(providerRoot))
        {
            return presentations;
        }

        var normalizedStems = knownCardStems
            .Where(stem => !string.IsNullOrWhiteSpace(stem))
            .GroupBy(NormalizeToken, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        if (normalizedStems.Count == 0)
        {
            return presentations;
        }
        var normalizedRegistryStems = (registryCardStems ?? [])
            .Where(stem => !string.IsNullOrWhiteSpace(stem))
            .GroupBy(NormalizeToken, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var dllPath in Directory.EnumerateFiles(
                     providerRoot,
                     "*.dll",
                     SearchOption.AllDirectories))
        {
            try
            {
                ScanAssembly(
                    dllPath,
                    normalizedStems,
                    normalizedRegistryStems,
                    presentations);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"无法静态分析卡牌呈现 DLL {dllPath}: {exception.Message}");
            }
        }

        return presentations;
    }

    private static void ScanAssembly(
        string dllPath,
        IReadOnlyDictionary<string, string> knownCardStems,
        IReadOnlyDictionary<string, string> registryCardStems,
        IDictionary<string, CardPresentationDefinition> presentations)
    {
        using var stream = new FileStream(
            dllPath,
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
            var strings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referencedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referencedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
                var il = body.GetILBytes();
                if (il != null)
                {
                    ScanIl(reader, il, strings, referencedTypes, referencedMembers);
                }
            }

            if (!LooksLikeAncientLayoutPatch(strings))
            {
                continue;
            }

            var cardTypes = referencedTypes
                .Select(typeName => (TypeName: typeName, Token: NormalizeToken(typeName)))
                .Where(candidate => MatchesKnownCardStem(candidate.Token, knownCardStems.Keys))
                .Select(candidate => candidate.TypeName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var useExpandedPortraitLayout = false;
            if (cardTypes.Length == 0)
            {
                // Registry-driven exporters do not reference every concrete CardModel type from
                // their layout patch. They resolve Model.GetType().FullName at runtime and apply
                // the Ancient nodes only to portraits declared by their own manifest. The caller
                // supplies exactly that provider-owned set. Require the same patch type to call
                // CardReplacementRegistry.TryGetTexture: generic frame patches also mention all
                // Ancient nodes but only apply them to entries explicitly marked Ancient.
                if (!ReferencesMember(
                        referencedMembers,
                        "CardReplacementRegistry",
                        "TryGetTexture"))
                {
                    continue;
                }

                cardTypes = registryCardStems.Values
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (cardTypes.Length == 0)
                {
                    continue;
                }

                // A registry-driven portrait patch often reuses AncientPortrait solely for its
                // taller aspect ratio. It is not declaring the game's Ancient card UI; the
                // provider's own frame manifest controls that separately. Keep this intent as a
                // distinct expanded-portrait layout so the candle, Ancient banner and black
                // Ancient text background are not added to ordinary alternate-art cards.
                useExpandedPortraitLayout = true;
            }

            // These are Ancient defaults used by the game's actual layout. A registry portrait
            // patch does not replace the normal banner/text layers, so do not feed those default
            // paths into the expanded-portrait presentation.
            var frame = useExpandedPortraitLayout
                ? null
                : FindResource(strings, "ancient_card_border");
            var textBackground = useExpandedPortraitLayout
                ? null
                : FindResource(strings, "ancient_card_text_bg");
            var banner = useExpandedPortraitLayout
                ? null
                : FindResource(strings, "ancient_banner.tres");
            var bannerMaterial = useExpandedPortraitLayout
                ? null
                : FindResource(strings, "card_banner_ancient_mat");
            var definition = new CardPresentationDefinition(
                UseAncientLayout: !useExpandedPortraitLayout,
                UseExpandedPortraitLayout: useExpandedPortraitLayout,
                Frame: frame,
                BannerTexture: banner,
                BannerMaterial: bannerMaterial,
                AncientTextBackground: textBackground);
            foreach (var cardType in cardTypes)
            {
                presentations.TryAdd(cardType, definition);
            }
        }
    }

    private static bool LooksLikeAncientLayoutPatch(IReadOnlySet<string> strings)
    {
        var ancientNodes = new[]
        {
            "AncientPortrait",
            "AncientBorder",
            "AncientTextBg",
            "AncientBanner",
            "AncientBorderGlassOverlay"
        };
        return ContainsLayoutToken(strings, "AncientPortrait") &&
               ancientNodes.Count(node => ContainsLayoutToken(strings, node)) >= 3 &&
               new[] { "Portrait", "Frame", "TitleBanner" }
                   .Any(node => ContainsLayoutToken(strings, node));
    }

    private static bool ContainsLayoutToken(IEnumerable<string> strings, string expected) =>
        strings.Any(value => NormalizeToken(value).Equals(
            NormalizeToken(expected),
            StringComparison.OrdinalIgnoreCase));

    private static string? FindResource(IEnumerable<string> strings, string token) =>
        strings.FirstOrDefault(value =>
            value.StartsWith("res://", StringComparison.OrdinalIgnoreCase) &&
            value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static void ScanIl(
        MetadataReader reader,
        byte[] il,
        ISet<string> strings,
        ISet<string> referencedTypes,
        ISet<string> referencedMembers)
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

            if (opCode.OperandType == OperandType.InlineString && operandSize == 4)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                try
                {
                    // InlineString stores a full 0x70xxxxxx metadata token, while
                    // UserStringHandle expects the offset inside the #US heap.
                    strings.Add(reader.GetUserString(
                        MetadataTokens.UserStringHandle(token & 0x00ffffff)));
                }
                catch (Exception exception) when (
                    exception is BadImageFormatException or ArgumentException)
                {
                    // Ignore malformed metadata in third-party providers.
                }
            }
            else if (opCode.OperandType == OperandType.InlineType && operandSize == 4)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                var typeName = ResolveTypeName(reader, MetadataTokens.EntityHandle(token));
                if (typeName != null)
                {
                    referencedTypes.Add(typeName);
                }
            }
            else if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineField &&
                     operandSize == 4)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, 4));
                var memberName = ResolveMemberName(reader, MetadataTokens.EntityHandle(token));
                if (memberName != null)
                {
                    referencedMembers.Add(memberName);
                }
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

    private static string? ResolveTypeName(MetadataReader reader, EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition =>
                reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
            HandleKind.TypeReference =>
                reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name),
            _ => null
        };
    }

    private static string? ResolveMemberName(MetadataReader reader, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                var methodType = reader.GetTypeDefinition(method.GetDeclaringType());
                return reader.GetString(methodType.Name) + "." + reader.GetString(method.Name);
            case HandleKind.FieldDefinition:
                var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                var fieldType = reader.GetTypeDefinition(field.GetDeclaringType());
                return reader.GetString(fieldType.Name) + "." + reader.GetString(field.Name);
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                var parentType = ResolveTypeName(reader, member.Parent);
                return parentType == null
                    ? reader.GetString(member.Name)
                    : parentType + "." + reader.GetString(member.Name);
            case HandleKind.MethodSpecification:
                var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                return ResolveMemberName(reader, specification.Method);
            default:
                return null;
        }
    }

    private static bool ReferencesMember(
        IEnumerable<string> members,
        string declaringType,
        string memberName)
    {
        var expected = NormalizeToken(declaringType + memberName);
        return members.Any(member => NormalizeToken(member).EndsWith(
            expected,
            StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool MatchesKnownCardStem(
        string cardTypeToken,
        IEnumerable<string> knownCardStemTokens)
    {
        foreach (var stem in knownCardStemTokens)
        {
            if (stem.Equals(cardTypeToken, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Stateful card art commonly uses numeric suffixes (for example
            // Wither1/2/3) even though the managed card model is simply Wither.
            if (stem.StartsWith(cardTypeToken, StringComparison.OrdinalIgnoreCase) &&
                stem.AsSpan(cardTypeToken.Length).Length > 0 &&
                stem.AsSpan(cardTypeToken.Length).ToString().All(char.IsDigit))
            {
                return true;
            }
        }

        return false;
    }
}
