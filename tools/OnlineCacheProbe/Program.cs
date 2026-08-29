using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 7)
{
    Console.Error.WriteLine(
        "usage: OnlineCacheProbe <skin-changer.dll> <game-assembly-dir> <game.pck> " +
        "<provider-root> <group-id> <option-id> <output.pck>");
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
var filterBindingArgs = new[] { bindings, filteredRoots, groupId };
var filteredBindings = RequireMethod(
        onlineCacheType,
        "FilterLocalSafeResourceBindings",
        BindingFlags.NonPublic | BindingFlags.Static)
    .Invoke(null, filterBindingArgs)!;

Directory.CreateDirectory(Path.GetDirectoryName(outputPck)!);
var package = RequireMethod(
        onlineCacheType,
        "BuildSafePackage",
        BindingFlags.NonPublic | BindingFlags.Static)
    .Invoke(null, [pckPath, groupId, filteredRoots, filteredBindings, outputPck]);
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

Console.WriteLine(
    $"provider={sourceArgs[2]} roots={((System.Collections.ICollection)filteredRoots).Count} " +
    $"ignored={filterRootArgs[1]} package={package} registered={registered}");
return 0;

Type RequireType(string name) =>
    assembly.GetType(name, throwOnError: true)!;

MethodInfo RequireMethod(Type type, string name, BindingFlags flags) =>
    type.GetMethods(flags).Single(method => method.Name == name);
