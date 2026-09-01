using System.Reflection;
using System.Runtime.Loader;
using thunninoiSkinManager.thunninoiSkinManagerCode.Patches;

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "usage: FrameworkCompatProbe <provider.dll> <dependency-directory> [...]");
    return 2;
}

var providerPath = Path.GetFullPath(args[0]);
var dependencyDirectories = args.Skip(1)
    .Select(Path.GetFullPath)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (var directory in dependencyDirectories)
    {
        var path = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(path))
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
    }
    return null;
};

var adapter = typeof(CharacterSkin).Assembly;
var provider = AssemblyLoadContext.Default.LoadFromAssemblyPath(providerPath);
var types = provider.GetTypes();
var characterSkins = types
    .Where(type => !type.IsAbstract && typeof(CharacterSkin).IsAssignableFrom(type))
    .ToArray();
if (characterSkins.Length == 0)
{
    throw new InvalidOperationException("provider exposes no loadable CharacterSkin descriptors");
}

foreach (var type in characterSkins)
{
    var descriptor = (CharacterSkin)Activator.CreateInstance(type)!;
    Console.WriteLine(
        $"{type.FullName}\tcombat={descriptor.CombatVisual}\tselect={descriptor.CharacterSelectBg}");
}

var orbSkins = types
    .Where(type => !type.IsAbstract && typeof(OrbSkin).IsAssignableFrom(type))
    .Select(type => (Type: type, Descriptor: (OrbSkin)Activator.CreateInstance(type)!))
    .ToArray();
var relicSkins = types
    .Where(type => !type.IsAbstract && typeof(RelicSkin).IsAssignableFrom(type))
    .Select(type => (Type: type, Descriptor: (RelicSkin)Activator.CreateInstance(type)!))
    .ToArray();
foreach (var orb in orbSkins)
{
    _ = orb.Descriptor.CustomIconPath;
    _ = orb.Descriptor.CustomSpritePath;
    _ = orb.Descriptor.CustomDarkenedColor;
}
foreach (var relic in relicSkins)
{
    _ = relic.Descriptor.PackedIconPath;
    _ = relic.Descriptor.PackedIconOutlinePath;
    _ = relic.Descriptor.BigIconPath;
}

var godotTypes = types.Count(type =>
    type.BaseType?.FullName?.StartsWith("Godot.", StringComparison.Ordinal) == true ||
    type.GetCustomAttributesData().Any(attribute =>
        attribute.AttributeType.Name.Equals("ScriptPathAttribute", StringComparison.Ordinal)));
Console.WriteLine(
    $"adapter={adapter.GetName().Name} {adapter.GetName().Version}; " +
    $"provider-types={types.Length}; character-skins={characterSkins.Length}; " +
    $"orb-skins={orbSkins.Length}; relic-skins={relicSkins.Length}; " +
    $"godot-types={godotTypes}");
return 0;
