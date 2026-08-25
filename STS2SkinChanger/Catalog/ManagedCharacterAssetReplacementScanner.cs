using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace STS2SkinChanger.Catalog;

/// <summary>
/// Reads declarative character asset replacements from framework-based skin providers
/// without loading or executing their assembly. In particular, this recognizes the
/// common CharacterAssetReplacement(characterId, profile) pattern and turns the
/// profile's private resource paths into ordinary Skin Changer overlay entries.
/// </summary>
internal static class ManagedCharacterAssetReplacementScanner
{
    private static readonly object CacheSync = new();
    private static readonly Dictionary<string, ScannerCacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    public static IReadOnlyList<ManagedCharacterAssetReplacement> Scan(
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
        var replacements = new List<ManagedCharacterAssetReplacement>();
        foreach (var assemblyPath in assemblyPaths)
        {
            try
            {
                var replacement = ScanAssemblyCached(assemblyPath);
                if (replacement != null)
                {
                    replacements.Add(replacement);
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"无法静态分析角色资源注册 DLL {assemblyPath}: {exception.Message}");
            }
        }

        return replacements;
    }

    private static ManagedCharacterAssetReplacement? ScanAssemblyCached(string assemblyPath)
    {
        var info = new FileInfo(assemblyPath);
        lock (CacheSync)
        {
            if (Cache.TryGetValue(assemblyPath, out var cached) &&
                cached.Length == info.Length &&
                cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                return cached.Replacement;
            }
        }

        var replacement = ScanAssembly(assemblyPath);
        lock (CacheSync)
        {
            Cache[assemblyPath] = new ScannerCacheEntry(
                info.Length,
                info.LastWriteTimeUtc,
                replacement);
        }

        return replacement;
    }

    private static ManagedCharacterAssetReplacement? ScanAssembly(string assemblyPath)
    {
        using var stream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        if (!peReader.HasMetadata)
        {
            return null;
        }

        var reader = peReader.GetMetadataReader();
        var characterEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providerPathsByProperty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                if (il != null)
                {
                    ScanIl(reader, il, characterEntries, providerPathsByProperty);
                }
            }
        }

        // A single provider assembly normally registers one profile for one character.
        // Refuse to guess when several targets share the same initializer: those need
        // field-level data-flow analysis instead of assigning one profile to every target.
        if (characterEntries.Count != 1 || providerPathsByProperty.Count == 0)
        {
            return null;
        }

        var targetGroupId = NormalizeToken(characterEntries.Single());
        var mappedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in providerPathsByProperty)
        {
            var canonicalPath = GetCanonicalPath(pair.Key, targetGroupId);
            if (canonicalPath != null)
            {
                mappedPaths[NormalizeResourcePath(pair.Value)] = canonicalPath;
            }
        }

        return mappedPaths.Count == 0
            ? null
            : new ManagedCharacterAssetReplacement(targetGroupId, mappedPaths);
    }

    private static void ScanIl(
        MetadataReader reader,
        byte[] il,
        ISet<string> characterEntries,
        IDictionary<string, string> providerPathsByProperty)
    {
        string? lastString = null;
        string? lastResourcePath = null;
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
                    lastString = reader.GetUserString(
                        MetadataTokens.UserStringHandle(token & 0x00ffffff));
                    lastResourcePath = lastString.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
                        ? NormalizeResourcePath(lastString)
                        : null;
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
                    if (methodName.Equals("GetScenePath", StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(lastString))
                    {
                        lastResourcePath = BuildScenePath(lastString);
                    }
                    else if (methodName.Equals("GetImagePath", StringComparison.Ordinal) &&
                             !string.IsNullOrWhiteSpace(lastString))
                    {
                        lastResourcePath = BuildImagePath(lastString);
                    }
                    else if (methodName.StartsWith("set_", StringComparison.Ordinal) &&
                             !string.IsNullOrWhiteSpace(lastResourcePath))
                    {
                        providerPathsByProperty[methodName[4..]] = lastResourcePath;
                        lastResourcePath = null;
                    }
                    else if (methodName.Equals("CharacterAssetReplacement", StringComparison.Ordinal) &&
                             IsPlausibleCharacterEntry(lastString))
                    {
                        characterEntries.Add(lastString!);
                    }
                }
            }

            offset += operandSize;
        }
    }

    private static string BuildScenePath(string value)
    {
        if (value.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeResourcePath(value);
        }

        var relative = value.Trim().TrimStart('/');
        if (!relative.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase))
        {
            relative += ".tscn";
        }

        return NormalizeResourcePath("res://scenes/" + relative);
    }

    private static string BuildImagePath(string value)
    {
        if (value.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeResourcePath(value);
        }

        return NormalizeResourcePath("res://images/" + value.Trim().TrimStart('/'));
    }

    private static string? GetCanonicalPath(string propertyName, string targetGroupId) =>
        propertyName switch
        {
            "VisualsPath" => $"res://scenes/creature_visuals/{targetGroupId}.tscn",
            "EnergyCounterPath" => $"res://scenes/combat/energy_counters/{targetGroupId}_energy_counter.tscn",
            "MerchantAnimPath" => $"res://scenes/merchant/characters/{targetGroupId}_merchant.tscn",
            "RestSiteAnimPath" => $"res://scenes/rest_site/characters/{targetGroupId}_rest_site.tscn",
            "IconTexturePath" => $"res://images/ui/top_panel/character_icon_{targetGroupId}.png",
            "IconOutlineTexturePath" => $"res://images/ui/top_panel/character_icon_{targetGroupId}_outline.png",
            "IconPath" => $"res://scenes/ui/character_icons/{targetGroupId}_icon.tscn",
            "CharacterSelectBgPath" => $"res://scenes/screens/char_select/char_select_bg_{targetGroupId}.tscn",
            "CharacterSelectIconPath" => $"res://images/packed/character_select/char_select_{targetGroupId}.png",
            "CharacterSelectLockedIconPath" => $"res://images/packed/character_select/char_select_{targetGroupId}_locked.png",
            "CharacterSelectTransitionPath" => $"res://materials/transitions/{targetGroupId}_transition_mat.tres",
            "MapMarkerPath" => $"res://images/packed/map/icons/map_marker_{targetGroupId}.png",
            "TrailPath" => $"res://scenes/vfx/card_trail_{targetGroupId}.tscn",
            "CombatSkeletonDataPath" => $"res://animations/characters/{targetGroupId}/{targetGroupId}_skel_data.tres",
            "ArmPointingTexturePath" => $"res://images/ui/hands/multiplayer_hand_{targetGroupId}_point.png",
            "ArmRockTexturePath" => $"res://images/ui/hands/multiplayer_hand_{targetGroupId}_rock.png",
            "ArmPaperTexturePath" => $"res://images/ui/hands/multiplayer_hand_{targetGroupId}_paper.png",
            "ArmScissorsTexturePath" => $"res://images/ui/hands/multiplayer_hand_{targetGroupId}_scissors.png",
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

    private static bool IsPlausibleCharacterEntry(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is >= 3 and <= 64 &&
        !value.StartsWith("res://", StringComparison.OrdinalIgnoreCase) &&
        value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.');

    private static string NormalizeToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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
        ManagedCharacterAssetReplacement? Replacement);
}

internal sealed record ManagedCharacterAssetReplacement(
    string TargetGroupId,
    IReadOnlyDictionary<string, string> CanonicalPathsByProviderPath);
