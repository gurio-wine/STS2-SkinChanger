using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger;

internal static class RequiredLibraryVisualGuardTests
{
    // Optional integration check against an installed framework. No third-party initializer,
    // Godot scene, config file or actual game process is executed or modified.
    public static void Run(string[] args)
    {
        if (args.Length != 3 || args[0] != "--required-library")
        {
            return;
        }

        var libraryRoot = Path.GetFullPath(args[1]);
        var libraryId = args[2];
        var library = new Mod
        {
            path = libraryRoot,
            state = ModLoadState.Loaded,
            manifest = new ModManifest
            {
                id = libraryId, name = libraryId, affectsGameplay = false, hasDll = true, hasPck = true
            }
        };
        var dependent = new Mod
        {
            path = Path.Combine(Path.GetTempPath(), "skin-changer-gameplay-dependent"),
            state = ModLoadState.Loaded,
            manifest = new ModManifest
            {
                id = "Tests.GameplayCharacter", affectsGameplay = true,
                dependencies = [new ModDependency(libraryId)]
            }
        };
        var loader = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ManagedSkinModLoader")!;
        var baseline = loader.GetMethod("ShouldTreatAsGameplayBaseline")!;
        if (baseline.Invoke(null, [library, new[] { library, dependent }]) is not true)
        {
            throw new InvalidOperationException(
                $"{libraryId} 的通用视觉补丁被误当成可选皮肤，导致角色前置库未保留为基线。");
        }

        var preservedRoots = loader.GetMethod("GetPreservedRuntimeRoots")!
            .Invoke(null, [new[] { library, dependent }]) as IEnumerable<string>;
        if (preservedRoots == null || !preservedRoots.Contains(libraryRoot, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("被玩法角色依赖的 DLL 根目录未受到补丁清理保护。");
        }

        var cosmeticRoot = Path.Combine(Path.GetTempPath(), "skin-changer-required-art-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cosmeticRoot);
        try
        {
            var cosmetic = new Mod
            {
                path = cosmeticRoot, state = ModLoadState.Loaded,
                manifest = new ModManifest
                {
                    id = "Tests.SharedCardArt", name = "Shared Card Art", affectsGameplay = false, hasPck = true
                }
            };
            typeof(Entry).Assembly.GetType("STS2SkinChanger.Pck.PckArchive")!.GetMethod("Write")!.Invoke(null,
                [Path.Combine(cosmeticRoot, "Tests.SharedCardArt.pck"), new Dictionary<string, byte[]>
                {
                    ["res://generated/card_replacements.json"] = System.Text.Encoding.UTF8.GetBytes(
                        """
                        {"entries":[{"cardId":"MegaCrit.Sts2.Core.Models.Cards.EscapePlan",
                        "kind":"image","image":"res://generated/escape_plan.png"}]}
                        """),
                    ["res://generated/escape_plan.png"] = new byte[] { 1, 2, 3 }
                }]);
            var dependentArt = new Mod
            {
                path = Path.Combine(cosmeticRoot, "compat"), state = ModLoadState.Loaded,
                manifest = new ModManifest
                {
                    id = "Tests.CardArtCompatibility", affectsGameplay = true,
                    dependencies = [new ModDependency(cosmetic.manifest.id)]
                }
            };
            var mods = new[] { cosmetic, dependentArt };
            var protectedArtRoots = (IEnumerable<string>)loader.GetMethod("GetPreservedRuntimeRoots")!
                .Invoke(null, [mods])!;
            if (baseline.Invoke(null, [cosmetic, mods]) is not false ||
                protectedArtRoots.Contains(cosmeticRoot, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("带实际卡图的皮肤包不能仅因被兼容补丁依赖就失去接管能力。");
            }
        }
        finally
        {
            Directory.Delete(cosmeticRoot, recursive: true);
        }

        var context = AssemblyLoadContext.GetLoadContext(typeof(Entry).Assembly)!;
        var assembly = context.LoadFromAssemblyPath(Path.Combine(libraryRoot, libraryId + ".dll"));
        var callback = assembly.GetType("BaseLib.Abstracts.CharacterSelectIconPath")?
            .GetMethod("Custom", BindingFlags.Static | BindingFlags.NonPublic);
        if (callback == null)
        {
            throw new InvalidOperationException("审计包缺少预期的自定义角色头像路由补丁。");
        }
        var target = AccessTools.PropertyGetter(typeof(CharacterModel), "CharacterSelectIconPath");
        var harmony = new Harmony("SkinChanger.Tests.RequiredLibrary");
        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(callback));
            var guard = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.VisualPatchGuard")!;
            // Deliberately include the framework as a candidate, simulating stale/bad detection.
            // Even then, a preserved runtime dependency must retain its callbacks.
            guard.GetMethod("RemoveProviderVisualPatches")!
                .Invoke(null, [new[] { libraryRoot }, preservedRoots]);
            if (Harmony.GetPatchInfo(target)?.Prefixes.Any(patch => patch.PatchMethod == callback) != true)
            {
                throw new InvalidOperationException("前置库的自定义角色头像路由被视觉清理器卸载。");
            }
            var patch = Harmony.GetPatchInfo(target)!.Prefixes.Single(patch => patch.PatchMethod == callback);
            var resolveOwner = guard.GetMethod("GetProviderRoot", BindingFlags.Static | BindingFlags.NonPublic)!;
            var protectedSet = preservedRoots.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var parentRoot = Path.GetDirectoryName(libraryRoot)!;
            if (resolveOwner.Invoke(null, [patch, new[] { parentRoot }, protectedSet]) != null)
            {
                throw new InvalidOperationException("嵌套前置库错误继承了父目录的皮肤补丁移除权限。");
            }
            if (resolveOwner.Invoke(null,
                    [patch, new[] { libraryRoot }, new HashSet<string>(StringComparer.OrdinalIgnoreCase)])
                is not string cosmeticOwner || cosmeticOwner != libraryRoot)
            {
                throw new InvalidOperationException("普通、非保护皮肤仍应能按自己的程序集路径接管。");
            }
        }
        finally
        {
            harmony.Unpatch(target, callback);
        }
        Console.WriteLine($"Required-library resource evidence and live Harmony preservation passed: {libraryId}.");
    }
}
