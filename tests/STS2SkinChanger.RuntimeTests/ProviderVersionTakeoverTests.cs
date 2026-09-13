using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger;

// The game refuses every Mod whose declared minimum game version is newer than the running game
// (ModManager.TryLoadMod). A cosmetic provider that Skin Changer loads itself must not be lost that
// way, but it may only be taken over once its shipped DLL was proven to run on this game version.
internal static class ProviderVersionTakeoverTests
{
    private const BindingFlags NonPublicStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var loader = assembly.GetType("STS2SkinChanger.Core.ManagedSkinModLoader", true)!;
        var canBypass = loader.GetMethod("CanBypassOriginalLoader", NonPublicStatic)!;
        var modsField = AccessTools.Field(typeof(ModManager), "_mods");
        var settingsField = AccessTools.Field(typeof(ModManager), "_settings");
        var versionField = AccessTools.Field(typeof(ModManager), "_gameVersion");
        var originalMods = modsField.GetValue(null);
        var originalSettings = settingsField.GetValue(null);
        var originalVersion = versionField.GetValue(null);
        var mods = new List<Mod>();
        var settings = new ModSettings { PlayerAgreedToModLoading = true };
        var root = Directory.CreateTempSubdirectory("skinchanger-version-takeover-").FullName;
        var harmony = new Harmony("Gurio.SkinChanger.Tests.VersionTakeover");
        try
        {
            modsField.SetValue(null, mods);
            settingsField.SetValue(null, settings);
            foreach (var level in new[] { "Info", "Warn" })
            {
                harmony.Patch(assembly.GetType("STS2SkinChanger.Core.ModLog", true)!.GetMethod(level, AnyStatic)!,
                    prefix: new HarmonyMethod(typeof(ProviderVersionTakeoverTests), nameof(SkipLog)));
            }

            var runnable = Path.Combine(root, "runnable.dll");
            var broken = Path.Combine(root, "broken.dll");
            ProviderRuntimeAuditTests.WriteFixture(runnable, resolvable: true);
            ProviderRuntimeAuditTests.WriteFixture(broken, resolvable: false);

            SetVersion("0.0.1");
            var verified = Add("Tests.BetaDeclaredSkin", hasDll: true, runnable, "0.111.0");
            Require(Bypass(verified),
                "声明了更高游戏版本、但代码能在当前版本编译的皮肤必须被接管，" +
                "不能只因为清单里的数字就丢掉整款皮肤。");
            var unsupported = Add("Tests.BrokenBetaSkin", hasDll: true, broken, "0.111.0");
            Require(!Bypass(unsupported),
                "代码无法在当前版本运行的皮肤不能接管：接管只会把必然失败的加载推迟到选中那一刻。");
            var unreadable = Add("Tests.UnreadableRuleSkin", hasDll: true, runnable, "banana");
            Require(!Bypass(unreadable), "读不懂的版本约束不能靠接管绕过，保持游戏原加载器的判断。");
            var resourceOnly = Add("Tests.ResourceOnlyBetaSkin", hasDll: false, null, "0.111.0");
            Require(Bypass(resourceOnly), "纯资源包没有依赖新版本的代码，必须照常接管。");

            // A satisfied declaration keeps the previous path; the missing DLL proves the audit was
            // not consulted (a consulted audit would refuse this package).
            SetVersion("9.9.9");
            var satisfied = Add("Tests.DeclaredOlderSkin", hasDll: true, null, "0.111.0");
            Require(Bypass(satisfied), "声明已被满足时必须保持原有加载路径，不做多余核验。");
            var undeclared = Add("Tests.UndeclaredSkin", hasDll: true, null, null);
            Require(Bypass(undeclared), "没有版本声明时保持既有行为，与游戏自身的判断一致。");
            Console.WriteLine("Provider version takeover passed: verified beta-declared skins taken over, unverifiable ones refused.");

            bool Bypass(Mod mod) => (bool)canBypass.Invoke(null, [mod])!;
            void SetVersion(string value)
            {
                Require(SemanticVersion.TryFromString(value, out var parsed) && parsed != null,
                    "测试无法构造版本号：" + value);
                versionField.SetValue(null, parsed);
            }
            Mod Add(string id, bool hasDll, string? fixture, string? minimum)
            {
                var directory = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
                if (fixture != null) File.Copy(fixture, Path.Combine(directory, id + ".dll"), overwrite: true);
                var mod = new Mod
                {
                    path = directory,
                    state = ModLoadState.None,
                    manifest = new ModManifest
                    {
                        id = id, name = id, hasPck = true, hasDll = hasDll,
                        affectsGameplay = false, minGameVersion = minimum
                    }
                };
                mods.Add(mod);
                return mod;
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            versionField.SetValue(null, originalVersion);
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
