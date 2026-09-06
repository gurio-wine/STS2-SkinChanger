using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using STS2SkinChanger;

internal static class CharacterCatalogOwnershipTests
{
    private static readonly Assembly ModAssembly = typeof(Entry).Assembly;
    private static readonly Type CatalogType = ModAssembly.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
    private static readonly Type DescriptorType = ModAssembly.GetType("STS2SkinChanger.Catalog.SkinModDescriptor", true)!;

    // Exercise the whole catalog, including the later private-resource mapping pass. Testing
    // ResolveEligibleGroups alone missed options being reintroduced after the initial filter.
    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("sc-character-ownership-");
        try
        {
            var gameFiles = new Dictionary<string, byte[]>();
            foreach (var character in new[] { "regent", "defect", "silent", "modded_character" })
                AddModel(gameFiles, character);
            gameFiles["res://animations/monsters/audit_beetle/model.tres"] = Resource();
            var gamePack = Path.Combine(directory.FullName, "game.pck");
            WritePack(gamePack, gameFiles);

            Check("single-model-with-stray-icons", files =>
            {
                AddModel(files, "regent");
                AddIcon(files, "defect");
            }, ["regent"]);
            Check("genuine-icon-only-pack", files =>
            {
                AddIcon(files, "regent");
                AddIcon(files, "defect");
            }, ["regent", "defect"], iconOnly: true);
            Check("genuine-multi-model-pack", files =>
            {
                AddModel(files, "regent");
                AddModel(files, "defect");
                AddIcon(files, "silent");
            }, ["regent", "defect"]);
            Check("private-model-with-stray-icons", files =>
            {
                AddModel(files, "regent", "res://custom/");
                AddIcon(files, "defect");
            }, ["regent"]);
            Check("private-icon-only-pack", files =>
            {
                AddIcon(files, "regent", "res://custom/");
                AddIcon(files, "defect", "res://custom/");
            }, ["regent", "defect"], iconOnly: true);
            Check("character-and-monster-pack", files =>
            {
                AddModel(files, "regent");
                AddIcon(files, "defect");
                files["res://animations/monsters/audit_beetle/model.tres"] = Resource();
            }, ["regent", "audit_beetle"]);
            Check("modded-character-with-stray-icons", files =>
            {
                AddModel(files, "modded_character");
                AddIcon(files, "defect");
            }, ["modded_character"]);

            // Ownership is per provider, not per catalog: a separate icon-only provider must
            // survive even when a different provider owns a complete model for another role.
            var independentIcons = new Dictionary<string, byte[]>();
            AddIcon(independentIcons, "defect");
            var independentPack = Path.Combine(directory.FullName, "independent-icons.pck");
            WritePack(independentPack, independentIcons);
            (string Id, string Pack, string Root, bool HasDll)[] providers =
            [
                ("single-model-with-stray-icons", Path.Combine(directory.FullName, "single-model-with-stray-icons.pck"), directory.FullName, false),
                ("independent-icons", independentPack, directory.FullName, false)
            ];
            foreach (var order in new[] { providers, providers.Reverse().ToArray() })
            {
                using var catalog = Build(gamePack, order);
                AssertGroups(catalog, "single-model-with-stray-icons", ["regent"], iconOnly: false);
                AssertGroups(catalog, "independent-icons", ["defect"], iconOnly: true);
            }

            void Check(string id, Action<Dictionary<string, byte[]>> populate,
                string[] expectedGroups, bool iconOnly = false)
            {
                var files = new Dictionary<string, byte[]>();
                populate(files);
                var pack = Path.Combine(directory.FullName, id + ".pck");
                WritePack(pack, files);
                using var catalog = Build(gamePack, id, pack, directory.FullName, hasDll: false);
                AssertGroups(catalog, id, expectedGroups, iconOnly);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
        Console.WriteLine("Character catalog ownership passed: complete discovery, private paths, icon-only and multi-model packs.");
    }

    // Read the exact manifest, not every JSON in the provider folder. Never execute a provider
    // initializer or create Godot nodes; this audits the same catalog used by the installed DLL.
    internal static void Audit(string gamePack, string manifestPath, string expectedGroups)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath).TrimStart('\uFEFF'));
        var json = manifest.RootElement;
        var id = json.GetProperty("id").GetString()!;
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var packName = json.TryGetProperty("pck_name", out var value) ? value.GetString()! : id;
        var hasDll = json.TryGetProperty("has_dll", out value) && value.GetBoolean();
        using var catalog = Build(gamePack, id, Path.Combine(root, packName + ".pck"), root, hasDll);
        AssertGroups(catalog, id, expectedGroups.Split(','), iconOnly: null);
        Console.WriteLine($"Installed character ownership verified: {id} -> {expectedGroups}");
    }

    private static IDisposable Build(string gamePack, string id, string pack, string root, bool hasDll)
        => Build(gamePack, [(id, pack, root, hasDll)]);

    private static IDisposable Build(string gamePack,
        (string Id, string Pack, string Root, bool HasDll)[] providers)
    {
        var descriptors = Array.CreateInstance(DescriptorType, providers.Length);
        for (var index = 0; index < providers.Length; index++)
        {
            var provider = providers[index];
            descriptors.SetValue(Activator.CreateInstance(DescriptorType,
                provider.Id, provider.Id, provider.Pack, false, provider.Root, provider.HasDll, null), index);
        }
        return (IDisposable)CatalogType.GetMethod("Build")!.Invoke(null, [gamePack, descriptors])!;
    }

    private static void AssertGroups(object catalog, string id, string[] expectedGroups, bool? iconOnly)
    {
        var options = Items(Property(catalog, "Groups"))
            .SelectMany(group => Items(Property(group, "Options"))
                .Where(option => (string)Property(option, "Id") == id)
                .Select(option => (Group: (string)Property(group, "Id"), Option: option)))
            .ToArray();
        var actual = options.Select(pair => pair.Group).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Require(actual.SetEquals(expectedGroups),
            $"{id} 角色归属错误：实际 [{string.Join(',', actual)}]，预期 [{string.Join(',', expectedGroups)}]。");
        Require(options.Length == expectedGroups.Length, "同一个来源不能重复进入同一分组。");
        foreach (var pair in options)
        {
            if (iconOnly != null)
                Require((bool)Property(pair.Option, "IsCharacterIconOnly") == iconOnly,
                    $"{id}/{pair.Group} 的纯头像标记错误，会使其被当成完整模型提供者。");
            if (!expectedGroups.Contains("defect"))
                Require(!Items(Property(pair.Option, "Assets")).Any(asset =>
                        ((string)Property(asset, "Key")).Contains("character_icon_defect")),
                    "被排除的机器人头像不能转嫁给其它角色或怪物的选项。");
        }
    }

    private static void AddModel(Dictionary<string, byte[]> files, string character, string prefix = "res://")
    {
        var source = $"{prefix}animations/characters/{character}/model.tres";
        var payload = $"res://.godot/exported/{character}-model.res";
        files[source + ".remap"] = Encoding.UTF8.GetBytes($"[remap]\npath=\"{payload}\"\n");
        files[payload] = Resource();
    }

    private static void AddIcon(Dictionary<string, byte[]> files, string character, string prefix = "res://")
    {
        foreach (var suffix in new[] { "", "_outline" })
        {
            var source = $"{prefix}images/ui/top_panel/character_icon_{character}{suffix}.png";
            var payload = $"res://.godot/imported/{character}{suffix}.ctex";
            files[source + ".import"] = Encoding.UTF8.GetBytes($"[remap]\npath=\"{payload}\"\n");
            files[payload] = [1, 2, 3]; // Only catalog ownership is tested, not native texture decoding.
        }
    }

    private static byte[] Resource() => Encoding.UTF8.GetBytes("[gd_resource type=\"Resource\" format=3]\n[resource]\n");
    private static void WritePack(string path, Dictionary<string, byte[]> files) =>
        ModAssembly.GetType("STS2SkinChanger.Pck.PckArchive", true)!.GetMethod("Write")!.Invoke(null, [path, files]);
    private static object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
    private static IEnumerable<object> Items(object value) => ((IEnumerable)value).Cast<object>();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
