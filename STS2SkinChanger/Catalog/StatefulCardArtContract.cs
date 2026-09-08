using System.Collections;
using System.Text;
using System.Text.Json;
using HarmonyLib;

namespace STS2SkinChanger.Catalog;

// A stateful atlas contract, not a collection of independent filename variants. Inspect metadata
// without running provider constructors. Names alone or an arbitrary card UI patch are not enough.
internal sealed record StatefulCardArtContract(
    string AssemblyPath, string ArtType, string ImageType, string SettingsType, string AtlasRoot,
    IReadOnlyDictionary<string, string[]> SpecialPortraits, IReadOnlySet<string> DisabledCharacters)
{
    public IReadOnlySet<string> AvailablePortraits { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public string LocalizationPrefix => "sc_stateful_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(ResourceRoot)))[..12].ToLowerInvariant() + "_";
    private static readonly Dictionary<string, (long Size, DateTime Stamp, DateTime ManifestStamp, StatefulCardArtContract? Contract)> Cache = new();
    public string ResourceRoot => AtlasRoot[..AtlasRoot.IndexOf("/images/", StringComparison.Ordinal)];

    public static StatefulCardArtContract? Scan(SkinModDescriptor mod) => mod.HasDll && mod.RootPath != null
        ? Read(SkinPackagePaths.Resolve(mod.RootPath, mod.ResourceNamespaceId, ".dll")) : null;

    internal static StatefulCardArtContract? Read(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 32 * 1024 * 1024) return null;
            var manifestStamp = File.GetLastWriteTimeUtc(Path.ChangeExtension(path, ".json"));
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var entry) && entry.Size == file.Length && entry.Stamp == file.LastWriteTimeUtc &&
                    entry.ManifestStamp == manifestStamp)
                    return entry.Contract;
                var result = Inspect(path);
                Cache[path] = (file.Length, file.LastWriteTimeUtc, manifestStamp, result);
                return result;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("Stateful card contract: " + exception.GetBaseException().Message);
            return null;
        }
    }

    private static StatefulCardArtContract? Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        var definitionType = typeof(Harmony).Assembly.GetType("Mono.Cecil.AssemblyDefinition", true)!;
        var definition = definitionType.GetMethod("ReadAssembly", [typeof(Stream)])!.Invoke(null, [stream])!;
        try
        {
            var types = Items(Get(Get(definition, "MainModule"), "Types")).ToArray();
            var art = types.SingleOrDefault(type => Text(type, "Name") == "AlternateCardArt" &&
                HasMethods(type, "Get", "GetAll", "GetSplit", "AfterNCardUpdateVisuals", "OnNCardSubscribed", "OnNCardUnsubscribed"));
            if (art == null) return null;
            var image = types.SingleOrDefault(type => Text(type, "Name") == "CardImg" &&
                HasMethods(type, "get_PortraitPath", "get_Path", "Upgraded", "UpgradedIfExists"));
            if (image == null) return null;
            var settings = types.SingleOrDefault(type => HasMethods(type, "get_UseCustomArt", "get_UseSimpleMode",
                "get_HideTitle", "get_HideDescription", "get_HideEnergy", "Init"));
            if (settings == null ||
                !types.Any(type => Text(type, "Name") == "AlternateCardArt`1" && Text(type, "BaseType") == Text(art, "FullName"))) return null;
            var portraitGetter = Items(Get(image, "Methods")).Single(method => Text(method, "Name") == "get_PortraitPath");
            var root = Strings(portraitGetter).SingleOrDefault(value => value.StartsWith("res://", StringComparison.Ordinal) &&
                value.EndsWith("/images/atlases/card_atlas.sprites/", StringComparison.Ordinal));
            if (root == null || !Strings(portraitGetter).Contains(".tres")) return null;
            var special = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in types)
            {
                var baseType = Get(type, "BaseType");
                if (baseType == null || !Text(baseType, "FullName").StartsWith(Text(art, "FullName") + "`1<", StringComparison.Ordinal)) continue;
                var argument = Items(Get(baseType, "GenericArguments")).SingleOrDefault();
                if (argument == null || !Text(argument, "FullName").StartsWith("MegaCrit.Sts2.Core.Models.Cards.", StringComparison.Ordinal)) continue;
                special[Text(argument, "Name")] = Items(Get(type, "Methods")).SelectMany(Strings)
                    .Where(value => value.Contains('/') && !value.Contains(':') && !value.Contains("..", StringComparison.Ordinal))
                    .Select(value => root + value + ".tres").Distinct(StringComparer.Ordinal).ToArray();
            }
            var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var minor = ReadMinor(Path.ChangeExtension(path, ".json"));
            foreach (var method in types.SelectMany(type => Items(Get(type, "Methods"))))
            {
                var name = Text(method, "Name");
                if (!name.StartsWith("get_", StringComparison.Ordinal) || !name.EndsWith("SetActive", StringComparison.Ordinal)) continue;
                var instructions = Instructions(method).ToArray();
                if (!instructions.Any(instruction => Text(Get(instruction, "Operand"), "Name") == "get_Minor")) continue;
                var threshold = instructions.Select(instruction => Text(instruction, "OpCode"))
                    .Where(opcode => opcode.StartsWith("ldc.i4.", StringComparison.Ordinal))
                    .Select(opcode => int.TryParse(opcode[7..], out var number) ? number : 0).DefaultIfEmpty().Max();
                if (minor == null || minor < threshold) disabled.Add(name[4..^9].ToLowerInvariant());
            }
            return new(path, Text(art, "FullName"), Text(image, "FullName"), Text(settings, "FullName"), root, special, disabled);
        }
        finally { (definition as IDisposable)?.Dispose(); }
    }

    public IReadOnlyDictionary<string, ResourceAsset> ReadAtlas(PckResourceIndex index) => index.Archive.Paths
        .Where(path => path.StartsWith(AtlasRoot, StringComparison.Ordinal) && path.EndsWith(".tres", StringComparison.Ordinal))
        .Where(path => Encoding.UTF8.GetString(index.Archive.ReadFile(path)).Contains("type=\"AtlasTexture\"", StringComparison.Ordinal))
        .Select(index.TryBuildAsset).OfType<ResourceAsset>()
        .ToDictionary(asset => asset.SourcePath, StringComparer.OrdinalIgnoreCase);

    public string? DefaultPortrait(CardCatalogEntry card, IReadOnlyDictionary<string, ResourceAsset> assets)
    {
        if (DisabledCharacters.Contains(card.PoolGroupId)) return null;
        var stem = Path.GetFileNameWithoutExtension(card.PortraitPath);
        var directory = card.PortraitPath[..card.PortraitPath.LastIndexOf('/')];
        var category = directory[(directory.LastIndexOf('/') + 1)..];
        foreach (var pool in new[] { category, card.PoolGroupId }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var exact = AtlasRoot + pool + "/" + stem + ".tres";
            if (assets.ContainsKey(exact)) return exact;
            var match = assets.Keys.Where(path => path.StartsWith(AtlasRoot + pool + "/", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(path => Token(Path.GetFileNameWithoutExtension(path)) == Token(card.TypeName));
            if (match != null) return match;
        }
        return SpecialPortraits.GetValueOrDefault(card.TypeName)?.FirstOrDefault(assets.ContainsKey);
    }

    public IEnumerable<(string GroupId, IReadOnlyDictionary<string, ResourceAsset> Assets)> CharacterAssets(PckResourceIndex index)
    {
        foreach (var path in index.Archive.Paths.Where(path => path.StartsWith(ResourceRoot + "/scenes/character_select/", StringComparison.Ordinal) &&
                     path.EndsWith("_bg.tscn", StringComparison.Ordinal)))
        {
            var group = Path.GetFileName(path)[..^8];
            if (DisabledCharacters.Contains(group)) continue;
            var icon = index.TryBuildAsset(ResourceRoot + "/images/character_select/" + group + "_icon.png");
            var scene = index.TryBuildAsset(path);
            if (icon == null || scene == null) continue;
            yield return (group, new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase)
            {
                [$"res://scenes/screens/char_select/char_select_bg_{group}.tscn"] = scene,
                [$"res://images/packed/character_select/char_select_{group}.png"] = icon
            });
        }
    }

    private static int? ReadMinor(string manifest)
    {
        if (!File.Exists(manifest)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        return document.RootElement.TryGetProperty("version", out var version) &&
               Version.TryParse(version.GetString()?.TrimStart('v'), out var value) ? value.Minor : null;
    }
    private static string Token(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static bool HasMethods(object type, params string[] names) => names.All(name =>
        Items(Get(type, "Methods")).Any(method => Text(method, "Name") == name));
    private static IEnumerable<object> Instructions(object method) => Get(method, "HasBody") is true
        ? Items(Get(Get(method, "Body"), "Instructions")) : [];
    private static IEnumerable<string> Strings(object method) => Instructions(method)
        .Where(instruction => Text(instruction, "OpCode") == "ldstr").Select(instruction => Get(instruction, "Operand")).OfType<string>();
    private static object? Get(object? value, string name) => value?.GetType().GetProperty(name)?.GetValue(value);
    private static string Text(object? value, string name) => Get(value, name)?.ToString() ?? string.Empty;
    private static IEnumerable<object> Items(object? value) => value is IEnumerable list ? list.Cast<object>() : [];
}
