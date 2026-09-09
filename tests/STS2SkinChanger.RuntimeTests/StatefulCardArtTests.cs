using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;
using HarmonyLib;
using System.Collections;
using System.Reflection;
using STS2SkinChanger.Core;
using STS2SkinChanger.Ui;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Localization;
using System.Runtime.CompilerServices;
using System.Text;

internal static class StatefulCardArtTests
{
    public static void Audit(string gamePack, string providerRoot)
    {
        // An integration fixture supplied by the caller: inspect bytes, never initialize the Mod.
        using var catalog = SkinCatalog.Build(gamePack,
        [new SkinModDescriptor("HideDetailsMod", "Rixian's MSPain",
            Path.Combine(providerRoot, "HideDetailsMod.pck"), false, providerRoot, true)]);
        catalog.FinalizeCardGroups(
        [
            new("DefendSilent", "res://images/packed/card_portraits/silent/defend_silent.png", "silent", "silent", "silent"),
            new("Wither", "res://images/packed/card_portraits/status/wither1.png", "status", "misc", "misc"),
            new("MadScience", "res://images/packed/card_portraits/event/mad_science_attack.png", "event", "misc", "misc"),
            new("StrikeIronclad", "res://images/packed/card_portraits/ironclad/strike_ironclad.png", "ironclad", "ironclad", "ironclad")
        ]);
        Require(catalog.CardGroups.Any(group => group.Id == "silent" &&
            group.Options.Any(option => option.NormalPortraits.ContainsKey("DefendSilent"))),
            "private AtlasTexture card portraits are not registered for their actual card category");
        var misc = catalog.CardGroups.Single(group => group.Id == "misc").Options.Single();
        Require(misc.NormalPortraits.ContainsKey("Wither") && misc.NormalPortraits.ContainsKey("MadScience"),
            "state-dependent cards without a plain filename must remain selectable");
        Require(!catalog.CardGroups.Any(group => group.Id == "ironclad"),
            "unreleased character art must not be advertised");
        foreach (var character in new[] { "silent", "regent", "necrobinder" })
            Require(catalog.Groups.Any(group => group.Id == character && group.Options.Any(option => option.Id == "HideDetailsMod")),
                "missing released character background/icon: " + character);
        Require(!catalog.Groups.Any(group => group.Id is "ironclad" or "defect"),
            "files intentionally disabled by the provider's release gates must stay disabled");
        CheckRuntimeIsolation(providerRoot);
        CheckPortraitBoundary(misc.StatefulArt!);
        CheckResourceClosure(catalog, misc);
        CheckLocalizationFiles(providerRoot, misc.StatefulArt!);
        CheckObservers();
        var probe = SkinCatalog.ProbeSkinProviders([new SkinModDescriptor("HideDetailsMod", "Rixian's MSPain",
            Path.Combine(providerRoot, "HideDetailsMod.pck"), false, providerRoot, true)], gamePack).Single();
        Require(probe.CardAssetCount >= 501 && probe.HasResourceBackedCosmetics, "startup takeover does not see private card atlases");
        Console.WriteLine("Stateful card-art integration passed: private atlases, state-only cards, released character scenes and ownership boundaries.");
    }

    internal static void CheckObservers()
    {
        CheckLocalizationBoundary();
        var assembly = typeof(STS2SkinChanger.Entry).Assembly;
        foreach (var name in new[] { "StatefulCardPlayCosmeticPatch", "StatefulDamageCosmeticPatch", "StatefulSideTurnCosmeticPatch" })
        {
            var type = assembly.GetType("STS2SkinChanger.Ui." + name, true)!;
            var method = AccessTools.Method(type, name == "StatefulCardPlayCosmeticPatch" ? "Prefix" : "Postfix");
            Require(method.ReturnType == typeof(void) && method.GetParameters().All(parameter =>
                !parameter.ParameterType.IsByRef && parameter.Name != "__result"), "cosmetic observer can replace a game action task");
        }
        foreach (var (type, member) in new (Type, string)[]
                 { (typeof(NCard), "Reload"), (typeof(NCard), "UpdateVisuals"), (typeof(NCard), "ActivateRewardScreenGlow"),
                   (typeof(Neurosurge), "OnPlay"), (typeof(Clash), "OnPlay"), (typeof(CardModel), "CreateOverlay"),
                   (typeof(AbstractModel), "AfterDamageGiven"), (typeof(AbstractModel), "AfterSideTurnStart"),
                   (typeof(NExhaustPileButton), "Initialize"), (typeof(NCombatCardPile), "AnimOut") })
            Require(AccessTools.Method(type, member) != null, "missing native runtime boundary: " + type.Name + "." + member);
        Require(AccessTools.PropertySetter(typeof(NCard), "Model") != null, "pooled-card release boundary missing");
        // A structurally unrelated asset-only pack must never execute this runtime.
        var plain = new CardSkinOption("plain", "plain", new Dictionary<string, string>(), new Dictionary<string, AncientCardPortrait>());
        Require(StatefulCardArtRuntime.For(plain) == null, "non-contract card options activate the stateful runtime");
        var harmony = new Harmony("Gurio.SkinChanger.tests.stateful-boundaries");
        try
        {
            foreach (var type in assembly.GetTypes().Where(type => type.Namespace == "STS2SkinChanger.Ui" &&
                         type.Name.StartsWith("Stateful", StringComparison.Ordinal) &&
                         type.GetCustomAttributes<HarmonyPatch>().Any()))
                Require(harmony.CreateClassProcessor(type).Patch().Count > 0, "stateful patch was not installed: " + type.Name);
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static void CheckLocalizationFiles(string providerRoot, StatefulCardArtContract contract)
    {
        using var pack = PckArchive.Open(Path.Combine(providerRoot, "HideDetailsMod.pck"));
        var files = pack.Paths.Where(path => path.StartsWith(contract.ResourceRoot + "/localization/", StringComparison.Ordinal) &&
            new[] { "artists.json", "usernames.json", "event_chatter.json" }.Contains(Path.GetFileName(path)))
            .ToDictionary(path => path, pack.ReadFile, StringComparer.Ordinal);
        Require(files.Count > 0, "actual localization fixture missing");
        foreach (var language in files.Keys.Select(path => path.Split('/')[^2]).Distinct())
            WithLocalizationRuntime(contract, files, language, tables =>
                Require(tables[contract.LocalizationPrefix + "artists"].Keys.Any(), "shipped artist credits were not installed"));
        Console.WriteLine($"Stateful localization package passed: {files.Count} shipped tables, including UTF-8 BOM input.");
    }

    private static void CheckLocalizationBoundary()
    {
        var contract = new StatefulCardArtContract("unused.dll", "Art", "Img", "Settings",
            "res://test/images/atlases/card_atlas.sprites/", new Dictionary<string, string[]>(), new HashSet<string>());
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["res://test/localization/eng/artists.json"] = Encoding.UTF8.GetBytes("{\"name\":\"Artist\",\"fallback\":\"English\"}"),
            ["res://test/localization/zhs/artists.json"] = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("{\"name\":\"画师\"}")],
            ["res://test/localization/eng/usernames.json"] = Encoding.UTF8.GetBytes("{\"author\":\"Author\"}")
        };
        WithLocalizationRuntime(contract, files, "zhs", tables =>
        {
            Require(tables[contract.LocalizationPrefix + "artists"].GetRawText("name") == "画师", "BOM translation lost");
            Require(tables[contract.LocalizationPrefix + "artists"].GetRawText("fallback") == "English", "English fallback lost");
        });
        files["res://test/localization/zhs/artists.json"] = Encoding.UTF8.GetBytes("{broken");
        WithLocalizationRuntime(contract, files, "zhs", tables =>
        {
            Require(tables[contract.LocalizationPrefix + "artists"].GetRawText("name") == "Artist", "bad optional locale erased fallback");
            Require(tables[contract.LocalizationPrefix + "usernames"].GetRawText("author") == "Author", "bad locale blocked another table");
        });
    }

    private static void WithLocalizationRuntime(StatefulCardArtContract contract, Dictionary<string, byte[]> files,
        string language, Action<Dictionary<string, LocTable>> verify)
    {
        // Exercise the production installation path without starting Godot, loading provider
        // code or reading/writing any player configuration. Restore the game's test singleton.
        var original = LocManager.Instance;
        var manager = (LocManager)RuntimeHelpers.GetUninitializedObject(typeof(LocManager));
        var unrelated = new LocTable("artists", new() { ["name"] = "Other provider" });
        var tables = new Dictionary<string, LocTable> { ["artists"] = unrelated };
        AccessTools.Field(typeof(LocManager), "_tables").SetValue(manager, tables);
        AccessTools.PropertySetter(typeof(LocManager), "Language").Invoke(manager, [language]);
        var runtime = (StatefulCardArtRuntime.Runtime)RuntimeHelpers.GetUninitializedObject(typeof(StatefulCardArtRuntime.Runtime));
        AccessTools.Field(runtime.GetType(), "<Contract>k__BackingField").SetValue(runtime, contract);
        AccessTools.Field(runtime.GetType(), "_localizations").SetValue(runtime, files);
        var logging = new Harmony("Gurio.SkinChanger.tests.stateful-localization-log");
        try
        {
            logging.Patch(AccessTools.Method(typeof(ModLog), nameof(ModLog.Warn)),
                prefix: new HarmonyMethod(typeof(StatefulCardArtTests), nameof(LogWithoutEngine)));
            AccessTools.PropertySetter(typeof(LocManager), "Instance").Invoke(null, [manager]);
            runtime.InstallLocalization();
            verify(tables);
            Require(ReferenceEquals(tables["artists"], unrelated), "provider overwrote an unowned localization table");
        }
        finally
        {
            AccessTools.PropertySetter(typeof(LocManager), "Instance").Invoke(null, [original]);
            logging.UnpatchAll(logging.Id);
        }
    }

    private static bool LogWithoutEngine(string message) { Console.WriteLine(message); return false; }

    private static void CheckResourceClosure(SkinCatalog catalog, CardSkinOption option)
    {
        var contract = option.StatefulArt!;
        foreach (var path in new[] { contract.AtlasRoot + "status/wither2.tres", contract.AtlasRoot + "status/wither3.tres",
                     contract.ResourceRoot + "/scenes/cards/osty_dance.tscn",
                     contract.ResourceRoot + "/scenes/cards/bad_apple.tscn",
                     contract.ResourceRoot + "/scenes/cards/overlays/red_infection.tscn",
                     contract.ResourceRoot + "/images/defile_exhaust_icon.tscn" })
        {
            var overlay = catalog.BuildIsolatedCardResources("misc", option.Id, [path], true, "stateful_test");
            Require(overlay.ResourcePaths.ContainsKey(path), "runtime-only card resource is missing: " + path);
            Require(overlay.Files.Count > 1, "card atlas/scene dependencies were not isolated: " + path);
        }
    }

    private static void CheckPortraitBoundary(StatefulCardArtContract contract)
    {
        var type = typeof(STS2SkinChanger.Entry).Assembly.GetType("STS2SkinChanger.Core.StatefulCardArtRuntime");
        Require(type != null, "dynamic portraits have no selected-provider boundary");
        string? Resolve(string? path, bool upgraded) => (string?)AccessTools.Method(type, "ValidatePortrait")
            .Invoke(null, [contract, path, upgraded]);
        var root = contract.AtlasRoot;
        Require(Resolve(root + "status/wither1.tres", false) == root + "status/wither1.tres", "state portrait missing");
        Require(Resolve("res://other/images/card.tres", false) == null, "foreign provider escaped the atlas boundary");
        Require(Resolve(root + "../card.tres", false) == null, "traversal escaped the atlas boundary");
        Require(Resolve(root + "not_shipped.tres", true) == null, "nonexistent variant accepted");
        var plus = contract.AvailablePortraits.First(path => path.EndsWith("_plus.tres", StringComparison.Ordinal) &&
            contract.AvailablePortraits.Contains(path[..^10] + ".tres"));
        Require(Resolve(plus[..^10] + ".tres", true) == plus, "upgrade art not selected");
    }

    private static void CheckRuntimeIsolation(string root)
    {
        var type = typeof(STS2SkinChanger.Entry).Assembly.GetType("STS2SkinChanger.Core.StatefulCardArtAssemblyCompatibility");
        Require(type != null, "stateful scenes still register global AddedNode factories when instantiated");
        using var source = File.OpenRead(Path.Combine(root, "HideDetailsMod.dll"));
        object?[] args = [source, StatefulCardArtContract.Read(source.Name), 0];
        using var rewritten = (MemoryStream?)AccessTools.Method(type, "Rewrite").Invoke(null, args);
        Require(rewritten != null && (int)args[2]! >= 4, "global art registry and automatic node factories were not isolated");
        var cecil = typeof(Harmony).Assembly.GetType("Mono.Cecil.AssemblyDefinition", true)!;
        var definition = cecil.GetMethod("ReadAssembly", [typeof(Stream)])!.Invoke(null, [rewritten])!;
        object? Get(object? value, string name) => value?.GetType().GetProperties().FirstOrDefault(property => property.Name == name)?.GetValue(value);
        IEnumerable<object> Items(object? value) => value is IEnumerable items ? items.Cast<object>() : [];
        try
        {
            var types = Items(Get(Get(definition, "MainModule"), "Types"));
            foreach (var candidate in types.Where(candidate => Get(candidate, "Name")?.ToString() is
                         "AlternateCardArt" or "BadApple" or "InfiniteInfiniteBlades" or "DefileExhaustIcon"))
            {
                var initializer = Items(Get(candidate, "Methods")).Single(method => Get(method, "Name")?.ToString() == ".cctor");
                var instructions = Items(Get(Get(initializer, "Body"), "Instructions")).ToArray();
                Require(instructions.Length == 1 && Get(instructions[0], "OpCode")?.ToString() == "ret",
                    "an automatic all-card registry can still attach effects to unselected cards");
            }
            var effectContainer = types.Single(type => Get(type, "Name")?.ToString() == "NCardCustomVfxContainer");
            var effectInitializer = Items(Get(effectContainer, "Methods")).Single(method => Get(method, "Name")?.ToString() == ".cctor");
            var effectInstructions = Items(Get(Get(effectInitializer, "Body"), "Instructions")).ToArray();
            Require(effectInstructions.All(instruction => !(Get(instruction, "OpCode")?.ToString() == "newobj" &&
                Get(Get(instruction, "Operand"), "DeclaringType")?.ToString()?.StartsWith("BaseLib.Utils.AddedNode`2<") == true)),
                "multi-field static constructor still installs an all-card node factory");
            Require(effectInstructions.Any(instruction => Get(Get(instruction, "Operand"), "Name")?.ToString() == "EffectState"),
                "non-factory static state was removed with the automatic factory");
        }
        finally { (definition as IDisposable)?.Dispose(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
