using System.Reflection;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger;

internal static class DuplicateProviderLoadingTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void AuditInstalled(string gamePck, string baselineRoot, string[] providerRoots)
    {
        var assembly = typeof(Entry).Assembly;
        var loader = assembly.GetType("STS2SkinChanger.Core.ManagedSkinModLoader", true)!;
        var catalogType = assembly.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
        var modsField = AccessTools.Field(typeof(ModManager), "_mods");
        var settingsField = AccessTools.Field(typeof(ModManager), "_settings");
        var readyField = loader.GetField("_reflectionTargetsReady", Static)!;
        var originalMods = modsField.GetValue(null);
        var originalSettings = settingsField.GetValue(null);
        var originalReady = readyField.GetValue(null);
        var harmony = new Harmony("Gurio.SkinChanger.Tests.InstalledDuplicateSources");
        Dictionary<string, string>? originalIdentities = null;
        try
        {
            settingsField.SetValue(null, new ModSettings { PlayerAgreedToModLoading = true });
            readyField.SetValue(null, true);
            foreach (var level in new[] { "Info", "Warn" })
                harmony.Patch(assembly.GetType("STS2SkinChanger.Core.ModLog", true)!.GetMethod(level, Static)!,
                    prefix: new HarmonyMethod(typeof(DuplicateProviderLoadingTests), nameof(SkipLog)));
            foreach (var reverse in new[] { false, true })
            {
                var baseline = ReadMod(baselineRoot);
                baseline.state = ModLoadState.Loaded;
                var providers = (reverse ? providerRoots.Reverse() : providerRoots).Select(ReadMod).ToArray();
                Require(providers.All(mod => !mod.manifest!.hasDll), "此实包检查只读取纯资源差分，不执行外部 DLL。");
                Require(providers.Select(mod => mod.manifest!.id).Distinct().Count() == 1,
                    "此实包检查必须使用相同 ID 的来源。");
                providers[0].state = ModLoadState.DisabledDuplicate;
                modsField.SetValue(null, new List<Mod> { baseline }.Concat(providers).ToList());
                var descriptors = loader.GetMethod("GetProviderProbeDescriptors", Static)!.Invoke(null, [ModManager.Mods])!;
                Require(((Array)descriptors).Length == providers.Length + 1, "启动扫描漏掉被游戏判重的皮肤来源。");
                foreach (var probe in Items(catalogType.GetMethod("ProbeSkinProviders", Static)!.Invoke(null, [descriptors, gamePck])!))
                    loader.GetMethod("RememberProviderProbe", Static)!.Invoke(null, [probe]);
                foreach (var mod in providers)
                    Require((bool)loader.GetMethod("TryManage", Static)!.Invoke(null, [mod])!,
                        $"无法接管实包：{mod.path} ({mod.state})");
                var loadedDescriptors = loader.GetMethod("GetProviderProbeDescriptors", Static)!
                    .Invoke(null, [ModManager.GetLoadedMods()])!;
                using var catalog = (IDisposable)catalogType.GetMethod("Build", Static)!.Invoke(null, [gamePck, loadedDescriptors])!;
                var group = Items(Property(catalog, "Groups")).Single(item => (string)Property(item, "Id") == "watcher");
                var visualOptions = Items(Property(group, "Options")).ToArray();
                var cardOptions = Items(Property(catalog, "PckCardOptions")).ToArray();
                Require(visualOptions.Length == providers.Length && cardOptions.Length == providers.Length,
                    $"实包必须分别生成角色和卡图选项：角色={visualOptions.Length}，卡图={cardOptions.Length}，来源={providers.Length}。");
                var identities = CheckOwnership(visualOptions);
                CheckOwnership(cardOptions);
                Require(originalIdentities == null || originalIdentities.All(pair => identities.GetValueOrDefault(pair.Key) == pair.Value),
                    "反向加载实包后，来源标识或资源归属发生变化。");
                originalIdentities = identities;
                foreach (var pair in identities)
                    Console.WriteLine($"Installed source verified ({(reverse ? "reverse" : "forward")}): {Path.GetFileName(pair.Key)} -> {pair.Value}");

                Dictionary<string, string> CheckOwnership(object[] options)
                {
                    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var option in options)
                    {
                        var id = (string)Property(option, "Id");
                        var files = Items(Property(option, "Assets"))
                            .SelectMany(pair => Items(Property(Property(pair, "Value"), "Files")));
                        var roots = files.Select(file => Path.GetDirectoryName((string)Property(Property(file, "Archive"), "Path"))!)
                            .Where(providerRoots.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        Require(roots.Length == 1, $"{id} 必须读取自己的包，不能混用其它差分来源。");
                        Require(result.TryAdd(roots[0], id), "同一个来源重复覆盖了其它选项。");
                    }
                    return result;
                }
            }
            Console.WriteLine("Installed duplicate audit passed: startup admission, native loaded list, character/card ownership and order stability.");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            modsField.SetValue(null, originalMods);
            settingsField.SetValue(null, originalSettings);
            readyField.SetValue(null, originalReady);
        }

        static Mod ReadMod(string root) => new()
        {
            path = root,
            manifest = Directory.EnumerateFiles(root, "*.json")
                .Select(path => JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(path), new JsonSerializerOptions { IncludeFields = true }))
                .First(manifest => manifest?.id != null)
        };
        static object Property(object instance, string name) => instance.GetType().GetProperty(name)!.GetValue(instance)!;
        static IEnumerable<object> Items(object collection) => ((System.Collections.IEnumerable)collection).Cast<object>();
    }

    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var loader = assembly.GetType("STS2SkinChanger.Core.ManagedSkinModLoader", true)!;
        var manage = loader.GetMethod("TryManage", Static)!;
        var ready = loader.GetField("_reflectionTargetsReady", Static)!;
        var originalReady = ready.GetValue(null);
        var modsField = AccessTools.Field(typeof(ModManager), "_mods");
        var settingsField = AccessTools.Field(typeof(ModManager), "_settings");
        var originalMods = modsField.GetValue(null);
        var originalSettings = settingsField.GetValue(null);
        var mods = new List<Mod>();
        var settings = new ModSettings { PlayerAgreedToModLoading = true };
        var write = assembly.GetType("STS2SkinChanger.Pck.PckArchive", true)!.GetMethod("Write", Static)!;
        var root = Directory.CreateTempSubdirectory("skinchanger-duplicate-loading-").FullName;
        var harmony = new Harmony("Gurio.SkinChanger.Tests.DuplicateLoading");
        try
        {
            modsField.SetValue(null, mods);
            settingsField.SetValue(null, settings);
            ready.SetValue(null, true);
            foreach (var level in new[] { "Info", "Warn" })
                harmony.Patch(assembly.GetType("STS2SkinChanger.Core.ModLog", true)!.GetMethod(level, Static)!,
                    prefix: new HarmonyMethod(typeof(DuplicateProviderLoadingTests), nameof(SkipLog)));

            var first = Add("Tests.DuplicateSkin", "first");
            var second = Add("Tests.DuplicateSkin", "second");
            var suppressed = Add("Tests.DuplicateSkin", "suppressed", ModLoadState.DisabledDuplicate);
            Require(Manage(first), "第一个资源皮肤应能正常被接管。");
            Require(Manage(second) && second.state == ModLoadState.Loaded,
                "同 ID 的第二个已确认皮肤不能再被原加载器的同名限制拦截。");
            Require(Manage(suppressed) && suppressed.state == ModLoadState.Loaded,
                "只因重复 ID 被游戏跳过的独立皮肤来源仍应交给切换器。");
            Require(mods.Count(mod => mod.state == ModLoadState.Loaded) == 3,
                "三个来源必须同时进入游戏的已加载列表，后续才能建立三套独立选项。");

            foreach (var state in new[] { ModLoadState.Disabled, ModLoadState.Failed, ModLoadState.AddedAtRuntime })
            {
                var excluded = Add("Tests.DuplicateSkin", state.ToString(), state);
                Require(!Manage(excluded) && excluded.state == state, "不能擅自启用关闭、失败或运行中添加的 Mod。");
            }
            var gameplay = Add("Tests.Gameplay", "gameplay", ModLoadState.DisabledDuplicate);
            gameplay.manifest!.affectsGameplay = true;
            Require(!Manage(gameplay) && gameplay.state == ModLoadState.DisabledDuplicate,
                "玩法 Mod 不得套用皮肤的重复加载例外。");
            var missingDependency = Add("Tests.MissingDependency", "missing", ModLoadState.DisabledDuplicate);
            missingDependency.manifest!.dependencies = [new ModDependency("Missing.Library")];
            Require(!Manage(missingDependency) && missingDependency.state == ModLoadState.DisabledDuplicate,
                "恢复差分不能绕过前置依赖检查。");
            var native = Add("Tests.NativeLoaded", "native", ModLoadState.Loaded);
            var shadow = Add("Tests.NativeLoaded", "shadow");
            Require(!Manage(shadow) && native.state == ModLoadState.Loaded,
                "不能在原加载器已执行同名 Mod 之后再假装实现了完整隔离。");
            var required = Add("Tests.RequiredLibrary", "required");
            Add("Tests.Dependent", "dependent").manifest!.dependencies = [new ModDependency("Tests.RequiredLibrary")];
            Require(!Manage(required), "作为真实前置的库仍必须交回原加载器。");

            var userDisabled = Add("Tests.UserDisabled", "user-disabled", ModLoadState.DisabledDuplicate);
            settings.ModList.Add(new SettingsSaveMod(userDisabled) { IsEnabled = false });
            Require(!Manage(userDisabled), "即使状态显示为重复，玩家显式禁用仍优先。");

            var mirror = Add("Tests.Snapshot", "workshop/content/2868840/1234567890", ModLoadState.DisabledDuplicate);
            mirror.modSource = ModSource.SteamWorkshop;
            var activeSnapshot = Add("Tests.Snapshot", "formal/mods/_workshop_formal_cache/1234567890");
            Require(!Manage(mirror) && Manage(activeSnapshot), "同一工坊物品的正式/测试快照应保持原游戏选中的那份。");
            var otherItem = Add("Tests.Snapshot", "workshop/content/2868840/9876543210", ModLoadState.DisabledDuplicate);
            otherItem.modSource = ModSource.SteamWorkshop;
            Require(Manage(otherItem), "不同工坊物品共用 ID 的独立差分仍应接管。");
            Console.WriteLine("Duplicate provider loading passed: managed coexistence, disabled/dependency guards, snapshot isolation.");

            bool Manage(Mod mod) => (bool)manage.Invoke(null, [mod])!;
            Mod Add(string id, string subdirectory, ModLoadState state = ModLoadState.None)
            {
                var directory = Directory.CreateDirectory(Path.Combine(root, subdirectory)).FullName;
                write.Invoke(null, [Path.Combine(directory, id + ".pck"), new Dictionary<string, byte[]>
                {
                    ["res://animations/characters/necrobinder/skin.tres"] =
                        Encoding.UTF8.GetBytes("[gd_resource type=\"Resource\" format=3]\n")
                }]);
                var mod = new Mod
                {
                    path = directory, state = state,
                    manifest = new ModManifest { id = id, name = id, hasPck = true, affectsGameplay = false }
                };
                mods.Add(mod);
                return mod;
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            ready.SetValue(null, originalReady);
            modsField.SetValue(null, originalMods);
            settingsField.SetValue(null, originalSettings);
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool SkipLog() => false;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
