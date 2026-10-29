using System.Reflection;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger;

internal static class ProviderLookupTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    // Optional real-package audit: scan the same descriptors as startup, register every probe,
    // then verify each manifest resolves to its own probe and its own DLL without running it.
    internal static void AuditFolder(string root)
    {
        var assembly = typeof(Entry).Assembly;
        var loader = assembly.GetType("STS2SkinChanger.Core.ManagedSkinModLoader", true)!;
        var descriptorType = assembly.GetType("STS2SkinChanger.Catalog.SkinModDescriptor", true)!;
        var catalog = assembly.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
        var manifests = Directory.EnumerateFiles(root, "*.json").Select(path =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var json = document.RootElement;
            return new Mod
            {
                path = root,
                manifest = new ModManifest
                {
                    id = json.GetProperty("id").GetString(),
                    name = json.GetProperty("name").GetString(),
                    hasPck = json.GetProperty("has_pck").GetBoolean(),
                    hasDll = json.GetProperty("has_dll").GetBoolean(),
                    affectsGameplay = json.GetProperty("affects_gameplay").GetBoolean()
                }
            };
        }).ToArray();
        var descriptors = Array.CreateInstance(descriptorType, manifests.Length);
        for (var i = 0; i < manifests.Length; i++)
        {
            var manifest = manifests[i].manifest!;
            descriptors.SetValue(Activator.CreateInstance(descriptorType,
                manifest.id, manifest.name, manifest.hasPck ? Path.Combine(root, manifest.id + ".pck") : null,
                manifest.affectsGameplay, root, manifest.hasDll, null), i);
        }
        var probes = ((System.Collections.IEnumerable)catalog.GetMethod("ProbeSkinProviders", Static)!
            .Invoke(null, [descriptors, null, true])!).Cast<object>().ToArray();
        var remember = loader.GetMethod("RememberProviderProbe", Static)!;
        foreach (var probe in probes) remember.Invoke(null, [probe]);
        var lookup = loader.GetMethod("IsManagedProvider", Static)!;
        var rememberAssembly = loader.GetMethod("RememberProviderAssembly", Static)!;
        var registered = (System.Collections.IDictionary)loader.GetField("ProviderAssemblies", Static)!.GetValue(null)!;
        foreach (var probe in probes)
        {
            var id = (string)probe.GetType().GetProperty("ResourceNamespaceId")!.GetValue(probe)!;
            var providerId = (string)probe.GetType().GetProperty("Id")!.GetValue(probe)!;
            var mod = manifests.Single(candidate => candidate.manifest!.id == id);
            object?[] arguments = [mod, null];
            Require((bool)lookup.Invoke(null, arguments)! && ReferenceEquals(arguments[1], probe),
                $"启动探测后 {id} 被同目录其它探测记录覆盖。");
            rememberAssembly.Invoke(null, [mod, probe]);
            if (!mod.manifest!.hasDll) continue;
            var value = registered[providerId];
            var registeredPath = value?.GetType().GetProperty("AssemblyPath")!.GetValue(value) as string;
            Require(registeredPath == Path.GetFullPath(Path.Combine(root, id + ".dll")),
                $"{id} 没有绑定到自己的 DLL：{registeredPath}");
            Require((bool)value!.GetType().GetProperty("CanActivateBehavior")!.GetValue(value)!,
                $"{id} 的行为代码因程序集身份冲突被禁用。");
            Console.WriteLine($"Provider binding verified: {providerId} -> {Path.GetFileName(registeredPath)}");
        }
        Require(probes.Length > 1, "此审计必须使用包含多个提供者的文件夹。");
    }

    // Read-only sweep over installed packages: report how many of each package's resources the
    // catalog can serve, and whether the package stays a managed skin provider. Never runs a
    // provider initializer.
    internal static void AuditServing(string gamePck, params string[] modRoots)
    {
        var assembly = typeof(Entry).Assembly;
        var descriptorType = assembly.GetType("STS2SkinChanger.Catalog.SkinModDescriptor", true)!;
        var catalog = assembly.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
        var probeMethod = catalog.GetMethod("ProbeSkinProviders", Static)!;
        // The game logger writes through native Godot; print the same lines instead.
        var harmony = new Harmony("Gurio.SkinChanger.Tests.ProviderServing");
        harmony.Patch(
            assembly.GetType("STS2SkinChanger.Core.ModLog", true)!.GetMethod("Info", Static)!,
            prefix: new HarmonyMethod(typeof(ProviderLookupTests), nameof(PrintLog)));
        try
        {
            var manifests = modRoots
                .SelectMany(root => Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
                .Select(TryRead)
                .Where(manifest => manifest != null)
                .Select(manifest => manifest!)
                .GroupBy(manifest => manifest.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var descriptors = Array.CreateInstance(descriptorType, manifests.Length);
            for (var i = 0; i < manifests.Length; i++)
            {
                var manifest = manifests[i];
                descriptors.SetValue(Activator.CreateInstance(descriptorType,
                    manifest.Id,
                    manifest.Name,
                    manifest.PckPath,
                    manifest.AffectsGameplay,
                    manifest.RootPath,
                    manifest.HasDll,
                    null), i);
            }

            var probes = ((System.Collections.IEnumerable)probeMethod
                .Invoke(null, [descriptors, gamePck])!).Cast<object>().ToArray();
            var cosmetic = probes
                .Where(probe => Value(probe, "VisualGroupCount") > 0 ||
                                Value(probe, "CardAssetCount") > 0 ||
                                Value(probe, "CardPresentationCount") > 0 ||
                                Value(probe, "RuntimeImageCount") > 0 ||
                                Value(probe, "BaselinePartGroupCount") > 0)
                .ToArray();
            Console.WriteLine($"packages={manifests.Length} cosmetic={cosmetic.Length} managed={probes.Length}");
            foreach (var probe in cosmetic
                         .OrderByDescending(probe => Value(probe, "PackResourceCount")))
            {
                var pack = Value(probe, "PackResourceCount");
                var served = Value(probe, "ServedResourceCount");
                var ratio = pack == 0 ? 1d : (double)served / pack;
                Console.WriteLine(
                    $"pack={pack,5} served={served,5} {ratio,6:P1} " +
                    $"groups={Value(probe, "VisualGroupCount"),3} " +
                    $"parts={Value(probe, "BaselinePartGroupCount"),2} " +
                    $"cards={Value(probe, "CardAssetCount"),4} " +
                    $"presentations={Value(probe, "CardPresentationCount"),4} " +
                    $"images={Value(probe, "RuntimeImageCount"),3} " +
                    $"{Id(probe)}");
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    private static int Value(object probe, string name) =>
        (int)probe.GetType().GetProperty(name)!.GetValue(probe)!;

    private static string Id(object probe) =>
        (string)probe.GetType().GetProperty("ResourceNamespaceId")!.GetValue(probe)!;

    private static InstalledManifest? TryRead(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path).TrimStart('\uFEFF'));
            var json = document.RootElement;
            if (!json.TryGetProperty("id", out var idValue) || idValue.GetString() is not { Length: > 0 } id)
            {
                return null;
            }

            var rootPath = Path.GetDirectoryName(path)!;
            var hasPck = json.TryGetProperty("has_pck", out var hasPckValue) && hasPckValue.GetBoolean();
            var pckName = json.TryGetProperty("pck_name", out var pckNameValue)
                ? pckNameValue.GetString() ?? id
                : id;
            var pckPath = hasPck ? Path.Combine(rootPath, pckName + ".pck") : null;
            return new InstalledManifest(
                id,
                json.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? id : id,
                pckPath != null && File.Exists(pckPath) ? pckPath : null,
                !json.TryGetProperty("affects_gameplay", out var gameplayValue) || gameplayValue.GetBoolean(),
                rootPath,
                json.TryGetProperty("has_dll", out var hasDllValue) && hasDllValue.GetBoolean());
        }
        catch
        {
            return null;
        }
    }

    // Read-only detail dump for one installed package: which resources the catalog would serve
    // for a selection, and which ones it cannot attribute. Never runs a provider initializer.
    internal static void AuditDetails(string gamePck, string modRoot, int limit = 40)
    {
        var assembly = typeof(Entry).Assembly;
        var descriptorType = assembly.GetType("STS2SkinChanger.Catalog.SkinModDescriptor", true)!;
        var catalog = assembly.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
        var harmony = new Harmony("Gurio.SkinChanger.Tests.ProviderDetails");
        harmony.Patch(
            assembly.GetType("STS2SkinChanger.Core.ModLog", true)!.GetMethod("Info", Static)!,
            prefix: new HarmonyMethod(typeof(ProviderLookupTests), nameof(PrintLog)));
        try
        {
            var manifest = Directory.EnumerateFiles(modRoot, "*.json", SearchOption.AllDirectories)
                .Select(TryRead)
                .FirstOrDefault(candidate => candidate != null)!;
            var descriptor = Activator.CreateInstance(descriptorType,
                manifest.Id, manifest.Name, manifest.PckPath, manifest.AffectsGameplay,
                manifest.RootPath, manifest.HasDll, null)!;
            var array = Array.CreateInstance(descriptorType, 1);
            array.SetValue(descriptor, 0);
            var probe = ((System.Collections.IEnumerable)catalog
                    .GetMethod("ProbeSkinProviders", Static)!
                    .Invoke(null, [array, gamePck])!)
                .Cast<object>()
                .FirstOrDefault();
            if (probe == null)
            {
                Console.WriteLine($"{manifest.Id}: 未被识别为外观提供者。");
                return;
            }

            var pack = Value(probe, "PackResourceCount");
            var served = Value(probe, "ServedResourceCount");
            var servedPaths = ((System.Collections.IEnumerable)probe.GetType()
                    .GetProperty("ServedSourcePaths")!.GetValue(probe)!)
                .Cast<string>()
                .ToArray();
            var groups = ((System.Collections.IEnumerable)probe.GetType()
                    .GetProperty("ServedGroupIds")!.GetValue(probe)!)
                .Cast<string>()
                .ToArray();
            var partGroups = (probe.GetType()
                    .GetProperty("BaselinePartGroupIds")?
                    .GetValue(probe) as System.Collections.IEnumerable)
                ?.Cast<string>()
                .ToArray() ?? [];
            Console.WriteLine(
                $"{manifest.Id}: pack={pack} served={served} groups=[{string.Join(", ", groups)}]" +
                (partGroups.Length > 0
                    ? $" 部件接管=" +
                      $"{probe.GetType().GetProperty("IsBaselinePartTakeover")?.GetValue(probe) is true} " +
                      $"部件分组=[{string.Join(", ", partGroups)}]"
                    : string.Empty));
            foreach (var path in servedPaths.Take(limit))
            {
                Console.WriteLine("  served   " + path);
            }

            if (manifest.PckPath == null)
            {
                return;
            }

            var archive = assembly.GetType("STS2SkinChanger.Pck.PckArchive", true)!
                .GetMethod("Open", Static)!
                .Invoke(null, [manifest.PckPath, 2_000_000u])!;
            var allPaths = ((System.Collections.IEnumerable)archive.GetType().GetProperty("Paths")!.GetValue(archive)!)
                .Cast<string>()
                .ToArray();
            var servedSet = servedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var notServed = allPaths
                .Where(path => !path.EndsWith(".import", StringComparison.OrdinalIgnoreCase) &&
                               !path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase) &&
                               !path.StartsWith("res://.godot/", StringComparison.OrdinalIgnoreCase) &&
                               !servedSet.Contains(path))
                .Take(limit)
                .ToArray();
            foreach (var path in notServed)
            {
                Console.WriteLine("  unserved " + path);
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    private sealed record InstalledManifest(
        string Id,
        string Name,
        string? PckPath,
        bool AffectsGameplay,
        string RootPath,
        bool HasDll);

    private static bool PrintLog(string message)
    {
        Console.WriteLine("[SC] " + message);
        return false;
    }

    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var loader = assembly.GetType("STS2SkinChanger.Core.ManagedSkinModLoader", true)!;
        var lookup = loader.GetMethod("IsManagedProvider", Static)!;
        var exposes = loader.GetMethod("ExposesSelectableCosmetics", Static)!;
        var display = loader.GetMethod("IsManagedProviderForDisplay", Static)!;
        var write = assembly.GetType("STS2SkinChanger.Pck.PckArchive", true)!.GetMethod("Write", Static)!;
        var root = Directory.CreateTempSubdirectory("skinchanger-provider-lookup-").FullName;
        var harmony = new Harmony("Gurio.SkinChanger.Tests.ProviderLookup");
        try
        {
            // The game logger writes through native Godot. Keep discovery, PCK scanning and
            // loader caches real; only silence that external output in the headless runner.
            harmony.Patch(assembly.GetType("STS2SkinChanger.Core.ModLog", true)!.GetMethod("Info", Static)!,
                prefix: new HarmonyMethod(typeof(ProviderLookupTests), nameof(SkipLog)));

            foreach (var reverse in new[] { false, true })
            {
                var directory = Directory.CreateDirectory(Path.Combine(root, reverse ? "reverse" : "forward")).FullName;
                var first = Create("OwnerSkin", directory, skin: true);
                var second = Create("CompanionSkin", directory, skin: true);
                var utility = Create("GameplayUtility", directory, skin: false);
                foreach (var mod in reverse ? new[] { second, first } : new[] { first, second })
                {
                    var probe = Lookup(mod);
                    var id = probe?.GetType().GetProperty("ResourceNamespaceId")!.GetValue(probe) as string;
                    Require(id == mod.manifest!.id,
                        $"同目录 Mod 必须保留自己的提供者身份，不能覆盖或借用兄弟 Mod：期望 {mod.manifest.id}，实际 {id}。");
                }
                Require(Lookup(utility) == null, "同目录的非皮肤 Mod 不得因为旁边有皮肤而被接管。");
                Require(!(bool)display.Invoke(null, [utility])!, "同目录的非皮肤 Mod 不得误标为 [SC]。");
                Require((bool)display.Invoke(null, [first])!, "真正被接管的皮肤必须保留 [SC] 标记。");
                Require((bool)exposes.Invoke(null, [first])! && !(bool)exposes.Invoke(null, [utility])!,
                    "可选外观证据不能从同目录的另一个 Mod 借用。");
            }

            var negativeDirectory = Directory.CreateDirectory(Path.Combine(root, "negative-first")).FullName;
            var empty = Create("EmptyPack", negativeDirectory, skin: false);
            Require(Lookup(empty) == null, "空包不应被接管。");
            Require(!(bool)exposes.Invoke(null, [empty])!, "空包没有可选外观证据。");
            var skinAfterEmpty = Create("SkinAfterEmpty", negativeDirectory, skin: true);
            Require((bool)exposes.Invoke(null, [skinAfterEmpty])!, "同目录的负外观证据不能阻止皮肤作为可选外观。");
            Require(Lookup(skinAfterEmpty) != null, "同目录的负缓存不能阻止另一个皮肤被识别。");
            Require(Lookup(Create("OwnerSkin", Path.Combine(root, "other-copy"), skin: false)) == null,
                "不同目录下的同 ID 空包不能借用已识别皮肤的身份。");

            // A whole UI/style overhaul can override one part of a game presentation (the merchant
            // room's hand animation) without being a skin. Isolating it would unmount the rest of
            // its package, so it must stay with the game's own loader.
            var overhaulDirectory = Directory.CreateDirectory(Path.Combine(root, "ui-overhaul")).FullName;
            var overhaul = CreateParts(
                "UiOverhaul",
                overhaulDirectory,
                ["res://animations/backgrounds/merchant_room/hand/merchanthand.skel"],
                extraFiles: 40);
            Require(Lookup(overhaul) == null, "整体风格包不能因为一个手部动画被当成商人皮肤接管。");
            Require(!(bool)exposes.Invoke(null, [overhaul])!, "整体风格包没有可呈现的皮肤资源。");

            // The merchant body/room presentation is the anchor: a pack that replaces it is a
            // merchant appearance even when it also ships the hand.
            var merchantDirectory = Directory.CreateDirectory(Path.Combine(root, "merchant-skin")).FullName;
            var merchant = CreateParts(
                "MerchantSkin",
                merchantDirectory,
                [
                    "res://animations/backgrounds/merchant_room/top/merchantbody.skel",
                    "res://animations/backgrounds/merchant_room/hand/merchanthand.skel"
                ],
                extraFiles: 0);
            Require(Lookup(merchant) != null, "替换商人体型的包必须继续作为可选择的商人外观接管。");
            Require((bool)exposes.Invoke(null, [merchant])!, "商人皮肤必须保留可选外观证据。");

            Console.WriteLine("Provider lookup passed: shared folders, independent positive/negative evidence and display.");

            object? Lookup(Mod mod)
            {
                object?[] arguments = [mod, null];
                return (bool)lookup.Invoke(null, arguments)! ? arguments[1] : null;
            }

            Mod Create(string id, string directory, bool skin)
            {
                Directory.CreateDirectory(directory);
                var files = new Dictionary<string, byte[]>();
                if (skin)
                    files["res://animations/characters/necrobinder/skin.tres"] =
                        Encoding.UTF8.GetBytes("[gd_resource type=\"Resource\" format=3]\n");
                write.Invoke(null, [Path.Combine(directory, id + ".pck"), files]);
                return new Mod
                {
                    path = directory,
                    manifest = new ModManifest { id = id, name = id, hasPck = true, affectsGameplay = false }
                };
            }

            Mod CreateParts(string id, string directory, string[] parts, int extraFiles)
            {
                var files = new Dictionary<string, byte[]>();
                foreach (var part in parts)
                {
                    files[part] = Encoding.UTF8.GetBytes("skel");
                    // Real exports register every source file through its .import companion.
                    files[part + ".import"] = Encoding.UTF8.GetBytes("[remap]\n");
                }

                // Real exports carry one .import payload per source file; unrelated package
                // resources are what a whole-overhaul PCK is mostly made of.
                for (var i = 0; i < extraFiles; i++)
                {
                    files[$"res://{id}/images/asset{i}.png.import"] =
                        Encoding.UTF8.GetBytes("[remap]\n");
                }

                write.Invoke(null, [Path.Combine(directory, id + ".pck"), files]);
                return new Mod
                {
                    path = directory,
                    manifest = new ModManifest { id = id, name = id, hasPck = true, affectsGameplay = false }
                };
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool SkipLog() => false;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
