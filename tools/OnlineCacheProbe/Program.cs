using System.Reflection;
using System.Runtime.Loader;

if (args.Length is not (7 or 8))
{
    Console.Error.WriteLine(
        "usage: OnlineCacheProbe <skin-changer.dll> <game-assembly-dir> <game.pck> " +
        "<provider-root> <group-id> <option-id> <output.pck> [<runtime-overlay.pck>]");
    return 2;
}

var modDll = Path.GetFullPath(args[0]);
var gameAssemblyDirectory = Path.GetFullPath(args[1]);
var gamePck = Path.GetFullPath(args[2]);
var providerRoot = Path.GetFullPath(args[3]);
var groupId = args[4];
var optionId = args[5];
var outputPck = Path.GetFullPath(args[6]);

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var candidate = Path.Combine(gameAssemblyDirectory, name.Name + ".dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(modDll);
var descriptorType = RequireType("STS2SkinChanger.Catalog.SkinModDescriptor");
var catalogType = RequireType("STS2SkinChanger.Catalog.SkinCatalog");
var skinServiceType = RequireType("STS2SkinChanger.Core.SkinService");
var onlineCacheType = RequireType("STS2SkinChanger.Core.OnlineSkinCache");
var multiplayerSyncType = RequireType("STS2SkinChanger.Core.MultiplayerSkinSync");
ValidateAdvertisementMetadataDoesNotRegress();
var providerPck = Directory.EnumerateFiles(providerRoot, "*.pck", SearchOption.TopDirectoryOnly)
    .Single();
var providerId = Path.GetFileNameWithoutExtension(providerPck);
var descriptor = Activator.CreateInstance(
    descriptorType,
    providerId,
    providerId,
    providerPck,
    false,
    providerRoot,
    true) ?? throw new InvalidOperationException("Could not construct provider descriptor.");
var descriptors = Array.CreateInstance(descriptorType, 1);
descriptors.SetValue(descriptor, 0);
var catalog = RequireMethod(catalogType, "Build", BindingFlags.Public | BindingFlags.Static)
    .Invoke(null, [gamePck, descriptors]) ??
    throw new InvalidOperationException("Could not build catalog.");
skinServiceType.GetProperty("Catalog", BindingFlags.Public | BindingFlags.Static)!
    .SetValue(null, catalog);

var sourceArgs = new object?[] { groupId, optionId, null, null, null, null };
var found = (bool)(RequireMethod(
    catalogType,
    "TryGetVisualProviderSource",
    BindingFlags.Public | BindingFlags.Instance).Invoke(catalog, sourceArgs) ?? false);
if (!found)
{
    throw new InvalidOperationException("Provider option was not discovered.");
}

var pckPath = (string)sourceArgs[3]!;
var roots = sourceArgs[4]!;
var bindings = sourceArgs[5]!;
var filterRootArgs = new object?[] { roots, 0 };
var filteredRoots = RequireMethod(
        onlineCacheType,
        "FilterLocalSafeResourceRoots",
        BindingFlags.NonPublic | BindingFlags.Static)
    .Invoke(null, filterRootArgs)!;
var filterBindingArgs = new object?[] { bindings, filteredRoots, groupId, pckPath, 0 };
var filteredBindings = RequireMethod(
        onlineCacheType,
        "FilterLocalSafeResourceBindings",
        BindingFlags.NonPublic | BindingFlags.Static)
    .Invoke(null, filterBindingArgs)!;
var effectiveRoots = RequireMethod(
        onlineCacheType,
        "FilterManifestRootsForBindings",
        BindingFlags.NonPublic | BindingFlags.Static)
    .Invoke(null, [filteredRoots, bindings, filteredBindings])!;
var serializedBindings = (string)RequireMethod(
        onlineCacheType,
        "SerializeSafeResourceBindings",
        BindingFlags.NonPublic | BindingFlags.Static)
    .Invoke(null, [filteredBindings])!;
Console.WriteLine(
    $"safe-bindings={((System.Collections.IDictionary)filteredBindings).Count} " +
    $"baseline-scene-fallbacks={filterBindingArgs[4]}");
if (Environment.GetEnvironmentVariable("ONLINE_CACHE_PROBE_VERBOSE") == "1")
{
    Console.WriteLine(serializedBindings);
}
var parseBindingArgs = new object?[] { serializedBindings, groupId, null };
var parsed = (bool)(RequireMethod(
        onlineCacheType,
        "TryParseSafeResourceBindings",
        BindingFlags.NonPublic | BindingFlags.Static)
    .Invoke(null, parseBindingArgs) ?? false);
if (!parsed)
{
    throw new InvalidOperationException("Serialized online resource bindings did not round-trip.");
}
filteredBindings = parseBindingArgs[2]!;
var transforms = RequireMethod(
        skinServiceType,
        "GetSessionCharacterCombatTransforms",
        BindingFlags.NonPublic | BindingFlags.Static)
    .Invoke(null, [groupId, optionId])!;
var transformManifest = (string)RequireMethod(
        multiplayerSyncType,
        "SerializeTransformManifest",
        BindingFlags.NonPublic | BindingFlags.Static)
    .Invoke(null, [transforms])!;
var parseTransformArgs = new object?[] { transformManifest, groupId, null };
var transformsParsed = (bool)(RequireMethod(
        multiplayerSyncType,
        "TryParseTransformManifest",
        BindingFlags.NonPublic | BindingFlags.Static)
    .Invoke(null, parseTransformArgs) ?? false);
if (!transformsParsed || ((System.Collections.IDictionary)parseTransformArgs[2]!).Count == 0)
{
    throw new InvalidOperationException("Multiplayer appearance transforms did not round-trip.");
}

// Exercise a non-default value as well. A default-only probe would miss a serializer regression
// that silently drops init-only UI fields or converts offsets back to zero.
var transformType = RequireType("STS2SkinChanger.Core.CharacterCombatTransform");
var customTransform = Activator.CreateInstance(
    transformType,
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    args: [1.25f, 18f, -11f],
    culture: null) ?? throw new InvalidOperationException("Could not construct transform probe.");
transformType.GetProperty("HealthBarScale")!.SetValue(customTransform, 1.35f);
transformType.GetProperty("HealthBarOffsetX")!.SetValue(customTransform, -7f);
transformType.GetProperty("HealthBarFollowsModelScale")!.SetValue(customTransform, true);
transformType.GetProperty("IntentOffsetY")!.SetValue(customTransform, 9f);
var customMapType = typeof(Dictionary<,>).MakeGenericType(typeof(string), transformType);
var customMap = (System.Collections.IDictionary)Activator.CreateInstance(customMapType)!;
customMap.Add(groupId, customTransform);
var customManifest = (string)RequireMethod(
        multiplayerSyncType,
        "SerializeTransformManifest",
        BindingFlags.NonPublic | BindingFlags.Static)
    .Invoke(null, [customMap])!;
var customParseArgs = new object?[] { customManifest, groupId, null };
var customParsed = (bool)(RequireMethod(
        multiplayerSyncType,
        "TryParseTransformManifest",
        BindingFlags.NonPublic | BindingFlags.Static)
    .Invoke(null, customParseArgs) ?? false);
if (!customParsed)
{
    throw new InvalidOperationException("Non-default multiplayer transform manifest did not parse.");
}
var parsedCustomTransform = ((System.Collections.IDictionary)customParseArgs[2]!)[groupId] ??
                            throw new InvalidOperationException("Transform probe lost its group entry.");
if (Math.Abs((float)transformType.GetProperty("Scale")!.GetValue(parsedCustomTransform)! - 1.25f) > 0.001f ||
    Math.Abs((float)transformType.GetProperty("OffsetX")!.GetValue(parsedCustomTransform)! - 18f) > 0.001f ||
    Math.Abs((float)transformType.GetProperty("OffsetY")!.GetValue(parsedCustomTransform)! + 11f) > 0.001f ||
    Math.Abs((float)transformType.GetProperty("HealthBarScale")!.GetValue(parsedCustomTransform)! - 1.35f) > 0.001f ||
    !(bool)transformType.GetProperty("HealthBarFollowsModelScale")!.GetValue(parsedCustomTransform)!)
{
    throw new InvalidOperationException("Non-default multiplayer transform values changed during round-trip.");
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPck)!);
var package = RequireMethod(
        onlineCacheType,
        "BuildSafePackage",
        BindingFlags.NonPublic | BindingFlags.Static)
    .Invoke(null, [pckPath, groupId, effectiveRoots, filteredBindings, outputPck]);
var registerArgs = new object?[]
{
    "__online_probe__",
    "Online probe",
    outputPck,
    groupId,
    filteredBindings,
    null
};
var registered = (bool)(RequireMethod(
        catalogType,
        "TryAddSessionVisualProvider",
        BindingFlags.Public | BindingFlags.Instance)
    .Invoke(catalog, registerArgs) ?? false);
if (!registered)
{
    throw new InvalidOperationException((string?)registerArgs[5] ?? "Online option registration failed.");
}

if (args.Length == 8)
{
    // Request the original targets as the game would. Targets whose unsafe provider scenes were
    // filtered out must resolve through the baseline game scene while retaining the accepted
    // static model/texture bindings from the session provider.
    var bindingKeys = ((System.Collections.IDictionary)bindings).Keys
        .Cast<string>()
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var overlay = RequireMethod(
            catalogType,
            "BuildRuntimeResourceOverlay",
            BindingFlags.Public | BindingFlags.Instance)
        .Invoke(catalog, [groupId, "__online_probe__", bindingKeys, "probe/runtime", true])!;
    var overlayFiles = (IReadOnlyDictionary<string, byte[]>)overlay.GetType()
        .GetProperty("Files", BindingFlags.Public | BindingFlags.Instance)!
        .GetValue(overlay)!;
    RequireMethod(
            RequireType("STS2SkinChanger.Pck.PckArchive"),
            "Write",
            BindingFlags.Public | BindingFlags.Static)
        .Invoke(null, [Path.GetFullPath(args[7]), overlayFiles]);
    Console.WriteLine($"runtime-overlay={Path.GetFullPath(args[7])} files={overlayFiles.Count}");
}

Console.WriteLine(
    $"provider={sourceArgs[2]} roots={((System.Collections.ICollection)effectiveRoots).Count} " +
    $"ignored={filterRootArgs[1]} package={package} registered={registered}");
return 0;

void ValidateAdvertisementMetadataDoesNotRegress()
{
    var messageType = RequireType("STS2SkinChanger.Core.SkinChangerNetMessage");
    var remember = RequireMethod(
        multiplayerSyncType,
        "RememberAdvertisement",
        BindingFlags.NonPublic | BindingFlags.Static);
    var advertisements = (System.Collections.IDictionary)multiplayerSyncType
        .GetField("AdvertisedSelections", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetValue(null)!;
    advertisements.Clear();

    var rich = Activator.CreateInstance(messageType)!;
    Set(rich, "PlayerNetId", 42UL);
    Set(rich, "CharacterId", "IRONCLAD");
    Set(rich, "GroupId", "ironclad");
    Set(rich, "OptionId", "ExampleSkin");
    Set(rich, "ProviderId", "ExampleSkin");
    Set(rich, "WorkshopItemId", 123UL);
    Set(rich, "SafeResourceFingerprint", new string('A', 64));
    Set(rich, "SafeResourceManifest", string.Empty);
    Set(rich, "SafeResourceBindings", string.Empty);
    Set(rich, "TransformManifest", "old-transform");
    remember.Invoke(null, [rich]);

    var ordinarySnapshot = Activator.CreateInstance(messageType)!;
    Set(ordinarySnapshot, "PlayerNetId", 42UL);
    Set(ordinarySnapshot, "CharacterId", "IRONCLAD");
    Set(ordinarySnapshot, "GroupId", "ironclad");
    Set(ordinarySnapshot, "OptionId", "ExampleSkin");
    Set(ordinarySnapshot, "TransformManifest", "new-transform");
    remember.Invoke(null, [ordinarySnapshot]);

    var merged = advertisements[42UL] ??
                 throw new InvalidOperationException("Advertisement merge lost the player entry.");
    if ((string)messageType.GetField("SafeResourceFingerprint")!.GetValue(merged)! !=
            new string('A', 64) ||
        (ulong)messageType.GetField("WorkshopItemId")!.GetValue(merged)! != 123UL ||
        (string)messageType.GetField("TransformManifest")!.GetValue(merged)! != "new-transform")
    {
        throw new InvalidOperationException(
            "An ordinary multiplayer snapshot replaced richer online skin metadata.");
    }
    advertisements.Clear();

    void Set(object target, string field, object value) =>
        messageType.GetField(field)!.SetValue(target, value);
}

Type RequireType(string name) =>
    assembly.GetType(name, throwOnError: true)!;

MethodInfo RequireMethod(Type type, string name, BindingFlags flags) =>
    type.GetMethods(flags).Single(method => method.Name == name);
