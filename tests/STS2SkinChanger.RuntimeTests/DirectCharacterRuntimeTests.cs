using System.Collections;
using System.Reflection;
using System.Text.Json;
using STS2SkinChanger;

internal static class DirectCharacterRuntimeTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    // Real-package regression: keep catalog discovery, config selection and the creature's
    // replay-provider collection real. No Godot scene or third-party initializer is executed.
    internal static void Audit(string gamePack, string modRoot)
        => AuditCore(gamePack, modRoot, null);

    internal static void AuditTargets(string gamePack, string modRoot, string expected)
        => AuditCore(gamePack, modRoot, expected.Split(','));

    private static void AuditCore(string gamePack, string modRoot, string[]? expectedGroups)
    {
        var assembly = typeof(Entry).Assembly;
        var catalogType = assembly.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
        var descriptorType = assembly.GetType("STS2SkinChanger.Catalog.SkinModDescriptor", true)!;
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var runtime = assembly.GetType("STS2SkinChanger.Ui.CharacterAppearanceRuntime", true)!;
        var configType = assembly.GetType("STS2SkinChanger.Core.SkinConfig", true)!;
        var manifestPath = Directory.EnumerateFiles(modRoot, "*.json").Single(path =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("id", out _);
        });
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var id = manifest.RootElement.GetProperty("id").GetString()!;
        var pckName = manifest.RootElement.TryGetProperty("pck_name", out var name) ? name.GetString()! : id;
        var descriptors = Array.CreateInstance(descriptorType, 1);
        descriptors.SetValue(Activator.CreateInstance(descriptorType,
            id, id, Path.Combine(modRoot, pckName + ".pck"), false, modRoot, true, null), 0);
        using var catalog = (IDisposable)catalogType.GetMethod("Build", Static)!
            .Invoke(null, [gamePack, descriptors])!;
        if (expectedGroups != null)
        {
            var groups = ((IEnumerable)catalogType.GetProperty("Groups")!.GetValue(catalog)!).Cast<object>();
            var actual = groups.Where(group => ((IEnumerable)group.GetType().GetProperty("Options")!.GetValue(group)!)
                    .Cast<object>().Any(option => (string)option.GetType().GetProperty("Id")!.GetValue(option)! == id))
                .Select(group => (string)group.GetType().GetProperty("Id")!.GetValue(group)!)
                .Where(group => new[] { "ironclad", "silent", "defect", "necrobinder", "regent" }.Contains(group))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Require(actual.SetEquals(expectedGroups),
                $"{id} 角色归属错误：实际 [{string.Join(",", actual)}]，预期 [{string.Join(",", expectedGroups)}]。");
            Console.WriteLine($"Character catalog ownership passed: {id} -> {string.Join(",", actual)}");
            return;
        }
        var catalogProperty = service.GetProperty("Catalog", Static)!;
        var configProperty = service.GetProperty("Config", Static)!;
        var oldCatalog = catalogProperty.GetValue(null);
        var oldConfig = configProperty.GetValue(null);
        var config = Activator.CreateInstance(configType)!;
        var selections = (IDictionary)configType.GetProperty("Selections")!.GetValue(config)!;
        var collect = runtime.GetMethod("AddSelectedCreatureRuntimeProvider", Static)!;
        var fullProvider = service.GetMethod("GetSelectedFullRuntimeProvider", Static)!;
        var routeSelected = service.GetMethod("IsDirectCharacterRuntimeProviderSelected", Static)!;
        // Resolve actual installed dispatch methods, but do not run ModInitializer/Ready.
        var providerAssembly = Assembly.LoadFrom(Path.Combine(modRoot, id + ".dll"));
        var routes = providerAssembly.GetTypes().SelectMany(type => type.GetMethods(Static)).ToArray();
        var preview = assembly.GetType("STS2SkinChanger.Core.FrameworkModelPreview", true)!;
        var resolvePath = preview.GetMethod("ResolveCombatSpinePath", Static)!;
        try
        {
            catalogProperty.SetValue(null, catalog);
            configProperty.SetValue(null, config);
            Require((bool)catalogType.GetMethod("ProviderUsesDirectCharacterRuntime")!
                    .Invoke(catalog, [id])!, "实包必须被识别为按角色独立运行的皮肤。");
            foreach (var character in new[] { "ironclad", "silent", "defect", "necrobinder", "regent" })
            {
                selections.Clear();
                var ids = new List<string>();
                collect.Invoke(null, [ids, character]);
                Require(ids.Count == 0, "未选中皮肤时不能重放它的模型替换回调。");
                foreach (var selection in new[] { id, "__base__", id })
                {
                    selections[character] = selection;
                    ids.Clear();
                    collect.Invoke(null, [ids, character]);
                    Require(selection == id ? ids.SequenceEqual(new[] { id }) : ids.Count == 0,
                        $"{character} 选择 {selection} 后的模型初始化提供者错误：[{string.Join(",", ids)}]。" +
                        "独立运行时皮肤不能因不属于全局联动包而漏掉骨骼替换。");
                    Require(fullProvider.Invoke(null, [character]) == null,
                        "修复回调不能把独立皮肤重新变成全角色联动包。");
                    Require((bool)routeSelected.Invoke(null, [id, character])! == (selection == id),
                        "原作者路径分发必须跟随当前角色的真实选择。");
                    if (selection == id)
                    {
                        // This installed fixture replaces its skeleton from Ready, with no
                        // canonical scene in option.Assets. Explicit private roots still need
                        // an isolated dependency chain, not a shared ResourceLoader fallback.
                        var stem = character == "necrobinder" ? "necro" : character;
                        var path = $"res://spineskins/{stem}/skeleton.tres";
                        var characterName = char.ToUpperInvariant(character[0]) + character[1..];
                        Require((string?)resolvePath.Invoke(null, [routes, characterName]) == path,
                            $"小模型没有使用实包为 {characterName} 提供的战斗骨骼分发路径。");
                        var overlay = catalogType.GetMethod("BuildRuntimeResourceOverlay")!.Invoke(catalog,
                            [character, id, new[] { path }, "preview-audit", true, false])!;
                        var paths = (IReadOnlyDictionary<string, string>)overlay.GetType().GetProperty("ResourcePaths")!.GetValue(overlay)!;
                        Require(paths.TryGetValue(path, out var alias) && alias != path,
                            $"预览请求的私有骨骼根没有进入隔离资源包：{path}");
                    }
                    var other = character == "silent" ? "ironclad" : "silent";
                    ids.Clear();
                    collect.Invoke(null, [ids, other]);
                    Require(ids.Count == 0 && !(bool)routeSelected.Invoke(null, [id, other])!,
                        "同一个 DLL 的其它角色不能借用当前角色的模型替换。");
                }
            }
            Console.WriteLine($"Direct character runtime audit passed: {id}; five independent selections, deselect/reselect and creature replay routing.");
        }
        finally
        {
            catalogProperty.SetValue(null, oldCatalog);
            configProperty.SetValue(null, oldConfig);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
