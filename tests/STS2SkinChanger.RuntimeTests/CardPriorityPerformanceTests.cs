using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;
using STS2SkinChanger.Pck;
using STS2SkinChanger.Ui;

internal static class CardPriorityPerformanceTests
{
    private static int _reads;
    private static int _samples;
    private static int _indicators;
    private static bool _failSelection;
    private static Dictionary<string, ResourceFile> _files = [];
    private static readonly List<IReadOnlyDictionary<string, ResourceFile>> Mounts = [];
    private static NCard[] _cards = [];
    private static readonly List<NCard> Refreshed = [];
    private static readonly Dictionary<CardModel, string> Selections = [];
    private static T Bare<T>() where T : GodotObject
    {
        var result = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        GC.SuppressFinalize(result);
        return result;
    }

    internal static void Run()
    {
        var root = Directory.CreateTempSubdirectory("sc-card-priority-test-");
        var harmony = new Harmony("SkinChanger.Tests.CardPriorityPerformance");
        var catalogProperty = AccessTools.Property(typeof(SkinService), "Catalog");
        var oldCatalog = catalogProperty.GetValue(null);
        try
        {
            var paths = new[] { Path.Combine(root.FullName, "one.pck"), Path.Combine(root.FullName, "two.pck") };
            PckArchive.Write(paths[0], new Dictionary<string, byte[]>
            {
                ["res://testprovider/scene.tscn"] = Encoding.UTF8.GetBytes("[gd_scene load_steps=2 format=3]\n[ext_resource type=\"Resource\" path=\"res://shared/data.tres\" id=\"1\"]"),
                ["res://shared/data.tres"] = Encoding.UTF8.GetBytes("[gd_resource type=\"Resource\" format=3]\n[resource]")
            });
            PckArchive.Write(paths[1], new Dictionary<string, byte[]> { ["res://testprovider/new.png"] = [1, 2] });
            using var first = PckArchive.Open(paths[0]);
            using var second = PckArchive.Open(paths[1]);
            var index = PckResourceIndex.Build(new("testprovider", "Test", paths[0], false, root.FullName), first, [], null);
            var collect = AccessTools.Method(typeof(SkinCatalog), "CollectProviderNamespaceFiles");
            harmony.Patch(AccessTools.Method(typeof(PckArchive), nameof(PckArchive.ReadFile)),
                prefix: new HarmonyMethod(typeof(CardPriorityPerformanceTests), nameof(CountRead)));
            _reads = 0;
            var files = (IReadOnlyCollection<ResourceFile>)collect.Invoke(null, [index, "testprovider"])!;
            Require(files.Select(file => file.Path).ToHashSet().SetEquals(["res://testprovider/scene.tscn", "res://shared/data.tres"]), "namespace discovery lost recursive dependencies");
            Require(_reads > 0, "cold dependency scan must actually read the source pack");
            _reads = 0;
            collect.Invoke(null, [index, "testprovider"]);
            Require(_reads == 0, "every toggle reparses an unchanged provider's resources");
            Require(((IReadOnlyCollection<ResourceFile>)collect.Invoke(null, [index, "unrelated"])!).Count == 0,
                "namespace cache mixed providers sharing one index");
            var nextIndex = PckResourceIndex.Build(new("testprovider", "Test", paths[1], false, root.FullName), second, [], null);
            var nextFiles = (IReadOnlyCollection<ResourceFile>)collect.Invoke(null, [nextIndex, "testprovider"])!;
            Require(nextFiles.Single().Path == "res://testprovider/new.png", "a new archive snapshot must not reuse the previous provider cache");

            // Capture the archive-writing boundary, keeping MountCardOverlay's partition and
            // canonical-owner reset real. No game, player cache or native Godot mount is used.
            var catalog = (SkinCatalog)RuntimeHelpers.GetUninitializedObject(typeof(SkinCatalog));
            AccessTools.Field(typeof(SkinCatalog), "_cardGroups").SetValue(catalog, new List<CardSkinGroup>());
            catalogProperty.SetValue(null, catalog);
            _files = files.ToDictionary(file => file.Path);
            _files.Add("res://testprovider/new.png", nextFiles.Single());
            _files.Add("res://alias/portrait.tres", files.Single(file => file.Path == "res://shared/data.tres"));
            harmony.Patch(AccessTools.Method(typeof(SkinCatalog), "BuildCardOverlay"), prefix: new HarmonyMethod(typeof(CardPriorityPerformanceTests), nameof(Overlay)));
            harmony.Patch(AccessTools.Method(typeof(SkinService), "MountArchiveOverlay"), prefix: new HarmonyMethod(typeof(CardPriorityPerformanceTests), nameof(Mount)));
            Mounts.Clear();
            AccessTools.Method(typeof(SkinService), "MountCardOverlay").Invoke(null, [new HashSet<string>()]);
            Require(Mounts.Count == 2 && Mounts.All(batch => batch.Values.Select(file => file.Archive).Distinct().Count() == 1), "toggling one source rewrites a combined pack containing unrelated providers");
            var mounted = Mounts.SelectMany(batch => batch).ToDictionary(pair => pair.Key, pair => pair.Value);
            Require(mounted.Count == 4 && _files.All(pair => mounted[pair.Key] == pair.Value), "partitioning lost resolved resource aliases or ownership");
            CheckChangedCards(harmony);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            catalogProperty.SetValue(null, oldCatalog);
            _cards = []; _files = []; Mounts.Clear(); Refreshed.Clear(); Selections.Clear();
            root.Delete(true);
        }
        Console.WriteLine("Card priority performance passed: warm dependency scans, reusable archive slices and changed-card-only refresh.");
    }

    private static void CheckChangedCards(Harmony harmony)
    {
        void Patch(Type type, string name, string prefix) => harmony.Patch(AccessTools.Method(type, name), prefix: new HarmonyMethod(typeof(CardPriorityPerformanceTests), prefix));
        Patch(typeof(CardSkinControls), "Descendants", nameof(Descendants));
        Patch(typeof(CardSkinControls), "RefreshCardSkin", nameof(Refresh));
        Patch(typeof(CardSkinControls), "UpdateLibrarySourceIndicators", nameof(Indicator));
        Patch(typeof(CardRefreshDiagnostics), "Begin", nameof(Diagnostic));
        Patch(typeof(CardRefreshDiagnostics), "End", nameof(Skip));
        Patch(typeof(SkinService), "CardBelongsToGroup", nameof(Belongs));
        harmony.Patch(AccessTools.Method(typeof(SkinService), "GetEffectiveCardSelection", [typeof(CardModel)]), prefix: new HarmonyMethod(typeof(CardPriorityPerformanceTests), nameof(Selection)));
        Patch(typeof(GodotObject), "IsInstanceValid", nameof(Valid));
        Patch(typeof(SkinService), "GetCardPresentation", nameof(Presentation));
        Patch(typeof(ModLog), "Info", nameof(Skip));
        Patch(typeof(ModLog), "Warn", nameof(Skip));
        _cards = Enumerable.Range(0, 80).Select(_ => Bare<NCard>()).ToArray();
        foreach (var card in _cards)
        {
            var model = new Wither();
            AccessTools.FieldRefAccess<NCard, CardModel?>("_model")(card) = model;
            Selections[model] = "skin:stable";
        }
        var screen = Bare<NCardLibrary>();
        var capture = AccessTools.Method(typeof(CardSkinControls), "CaptureCardSelections");
        Require(capture != null, "priority refresh does not compare the previous effective selections");
        var before = capture!.Invoke(null, [screen, "ancients"]);
        Selections[_cards[7].Model!] = "__base__";
        _indicators = 0;
        AccessTools.Method(typeof(CardSkinControls), "RefreshVisibleCards").Invoke(null, [screen, "ancients", before]);
        Require(Refreshed.SequenceEqual([_cards[7]]), "one changed card reloads the entire category");
        Require(_indicators == 79, "unchanged card art must still update its source/priority badges");
        Refreshed.Clear();
        before = capture.Invoke(null, [screen, "ancients"]);
        AccessTools.Method(typeof(CardSkinControls), "RefreshVisibleCards").Invoke(null, [screen, "ancients", before]);
        Require(Refreshed.Count == 0, "a lower-priority toggle with no winner changes still reloads cards");
        AccessTools.FieldRefAccess<NCard, CardModel?>("_model")(_cards[0]) = new Burn();
        Selections[_cards[0].Model!] = "skin:stable";
        AccessTools.Method(typeof(CardSkinControls), "RefreshVisibleCards").Invoke(null, [screen, "ancients", before]);
        Require(Refreshed.SequenceEqual([_cards[0]]), "a rebound node must not be skipped merely because the option ID is unchanged");
        before = capture.Invoke(null, [screen, "ancients"]);
        foreach (var model in Selections.Keys.ToArray()) Selections[model] = "skin:new";
        Refreshed.Clear(); _samples = 0;
        AccessTools.Method(typeof(CardSkinControls), "RefreshVisibleCards").Invoke(null, [screen, "ancients", before]);
        Require(Refreshed.Count == 80 && _samples == 3, "large changes must refresh every changed card while bounding expensive diagnostics");
        _failSelection = true;
        try { Require(capture.Invoke(null, [screen, "ancients"]) == null, "snapshot failure must permit the established full-refresh fallback"); }
        finally { _failSelection = false; }
    }

    private static void CountRead() => _reads++;
    private static bool Overlay(ref Dictionary<string, ResourceFile> __result) { __result = _files; return false; }
    private static bool Mount(IReadOnlyDictionary<string, ResourceFile> files) { Mounts.Add(files); return false; }
    private static bool Descendants(ref IEnumerable<Node> __result) { __result = _cards; return false; }
    private static bool Refresh(NCard card) { Refreshed.Add(card); return false; }
    private static bool Selection(CardModel card, ref string __result)
    {
        if (_failSelection) throw new InvalidOperationException("unavailable card identity");
        __result = Selections[card]; return false;
    }
    private static bool Belongs(ref bool __result) { __result = true; return false; }
    private static bool Valid(ref bool __result) { __result = true; return false; }
    private static bool Presentation(ref CardPresentationDefinition? __result) { __result = new(DescriptionVisible: false); return false; }
    private static bool Diagnostic(bool enabled) { if (enabled) _samples++; return false; }
    private static bool Indicator() { _indicators++; return false; }
    private static bool Skip() => false;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
