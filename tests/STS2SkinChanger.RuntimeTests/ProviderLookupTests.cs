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
            .Invoke(null, [descriptors, null])!).Cast<object>().ToArray();
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
