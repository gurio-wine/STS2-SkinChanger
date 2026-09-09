using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using STS2SkinChanger.Core;
using STS2SkinChanger.Ui;
using STS2SkinChanger.Pck;
using System.Text;
using STS2SkinChanger.Catalog;

internal static class ExportedCardSurfaceTests
{
    internal static void Run(string? providerAssembly = null)
    {
        var scan = typeof(ManagedCardPresentationScanner).GetMethod("ScanExportedSurfaceAssembly",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Require(scan != null, "exported portrait stretch/banner intent is not collected");
        var surface = scan!.Invoke(null, [providerAssembly ?? typeof(ExportedCardSurfaceTests).Assembly.Location]);
        Require(surface != null, "exporter intent missing");
        Require((int?)surface!.GetType().GetProperty("PortraitStretchMode")!.GetValue(surface) == 6,
            "must read KeepAspectCovered from the setter operand, not infer Ancient layout");
        var banner = surface.GetType().GetProperty("TinyBannerColor")!.GetValue(surface);
        Require(banner != null, "explicit tiny banner color missing");
        Require(Math.Abs((float)banner!.GetType().GetProperty("R")!.GetValue(banner)! - .82f) < .0001f,
            "wrong banner constant");
        Require(scan.Invoke(null, [typeof(CardModel).Assembly.Location]) == null,
            "native card UI must not be mistaken for exporter intent");
        if (providerAssembly != null)
            Require((int?)surface.GetType().GetProperty("ExcludedBannerRarity")!.GetValue(surface) == 5,
                "provider's rarity exclusion must be preserved without changing the card's rarity");
        CheckCatalog();
        CheckNodeLifetime();
        Console.WriteLine("Exported card surface metadata passed: explicit crop/color operands and native negative control.");
    }

    private static void CheckCatalog()
    {
        var root = Directory.CreateTempSubdirectory("sc-exported-surface-test-");
        try
        {
            var provider = Directory.CreateDirectory(Path.Combine(root.FullName, "exporter"));
            var baseline = Path.Combine(root.FullName, "game.pck");
            var pack = Path.Combine(provider.FullName, "exporter.pck");
            PckArchive.Write(baseline, new Dictionary<string, byte[]>());
            PckArchive.Write(pack, new Dictionary<string, byte[]>
            {
                ["res://generated/card_replacements.json"] = Encoding.UTF8.GetBytes("""
                    {"entries":[{"cardId":"MegaCrit.Sts2.Core.Models.Cards.Wither","kind":"image","image":"res://generated/wither.png"}]}
                    """),
                ["res://generated/wither.png"] = [1, 2, 3],
                ["res://images/packed/card_portraits/status/burn.png"] = [4, 5, 6]
            });
            File.Copy(typeof(ExportedCardSurfaceTests).Assembly.Location, Path.Combine(provider.FullName, "exporter.dll"));
            using (var catalog = SkinCatalog.Build(baseline, [new("exporter", "Fixture", pack, false, provider.FullName, true)]))
            {
                var option = catalog.PckCardOptions.Single();
                Require(option.CardSurfaces.Count == 1 && option.CardSurfaces.ContainsKey("Wither"),
                    "surface intent must be bound only to the exporter's explicitly declared cards");
                Require(option.CardPresentations.Count == 0, "crop-only intent triggered a full-frame/Ancient presentation");
                var merged = option.Merge(option with { Id = "other" });
                Require(merged.CardSurfaces.ContainsKey("Wither"), "group merge lost surface intent");
            }
            // Another primary DLL must not inherit the sibling exporter DLL's cosmetic intent.
            File.Copy(typeof(CardModel).Assembly.Location, Path.Combine(provider.FullName, "unrelated.dll"));
            Require(ManagedCardPresentationScanner.ScanExportedSurface(provider.FullName, "unrelated") == null,
                "sibling assembly contaminated another provider");
            Require(ManagedCardPresentationScanner.ScanExportedSurface(provider.FullName, "missing") == null,
                "ambiguous DLL fallback borrowed another provider");
        }
        finally { root.Delete(recursive: true); }
    }

    private static readonly Dictionary<TextureRect, TextureRect.StretchModeEnum> Stretch = [];
    private static readonly Dictionary<CanvasItem, Color> ColorsByNode = [];
    private static readonly Dictionary<CardModel, CardSurfaceDefinition> Selection = [];
    private static T Bare<T>() where T : GodotObject
    { var value = (T)RuntimeHelpers.GetUninitializedObject(typeof(T)); GC.SuppressFinalize(value); return value; }
    private static void CheckNodeLifetime()
    {
        var harmony = new Harmony("SkinChanger.Tests.ExportedCardSurface");
        NCard? card = null; NTinyCard? tiny = null;
        try
        {
            void Patch(MethodInfo method, string prefix) => harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(ExportedCardSurfaceTests), prefix));
            Patch(AccessTools.PropertyGetter(typeof(TextureRect), "StretchMode"), nameof(GetStretch));
            Patch(AccessTools.PropertySetter(typeof(TextureRect), "StretchMode"), nameof(SetStretch));
            Patch(AccessTools.PropertyGetter(typeof(CanvasItem), "Modulate"), nameof(GetColor));
            Patch(AccessTools.PropertySetter(typeof(CanvasItem), "Modulate"), nameof(SetColor));
            Patch(AccessTools.Method(typeof(GodotObject), nameof(GodotObject.IsInstanceValid)), nameof(Valid));
            Patch(AccessTools.Method(typeof(SkinService), "GetCardSurface"), nameof(Selected));
            var first = new Wither(); var second = new Burn();
            var rarity = first.Rarity;
            var chosen = new CardSurfaceDefinition(6, new(.82f, .68f, .34f, 1f));
            Selection[first] = chosen;
            card = Bare<NCard>(); var rect = Bare<TextureRect>();
            AccessTools.FieldRefAccess<NCard, CardModel?>("_model")(card) = first;
            Stretch[rect] = TextureRect.StretchModeEnum.KeepAspectCentered;
            CardSurfaceView.ApplyPortrait(card, rect);
            Require((int)Stretch[rect] == 6, "selected exporter crop not applied");
            CardSurfaceView.ApplyPortrait(card, rect);
            Selection.Remove(first);
            CardSurfaceView.ApplyPortrait(card, rect);
            Require((int)Stretch[rect] == 5, "repeated update captured the managed crop as vanilla");
            Selection[first] = chosen;
            CardSurfaceView.ApplyPortrait(card, rect);
            CardSkinControls.ReleasePresentationForReuse(card);
            AccessTools.FieldRefAccess<NCard, CardModel?>("_model")(card) = second;
            CardSurfaceView.ApplyPortrait(card, rect);
            Require((int)Stretch[rect] == 5, "pooled model inherited another card's crop");
            AccessTools.FieldRefAccess<NCard, CardModel?>("_model")(card) = first;
            CardSurfaceView.ApplyPortrait(card, rect);
            Stretch[rect] = TextureRect.StretchModeEnum.KeepAspect;
            CardSurfaceView.Release(card);
            Require(Stretch[rect] == TextureRect.StretchModeEnum.KeepAspect, "release overwrote later foreign UI ownership");

            tiny = Bare<NTinyCard>(); var banner = Bare<Control>();
            AccessTools.Field(typeof(NTinyCard), "_cardBanner").SetValue(tiny, banner);
            ColorsByNode[banner] = Colors.Gray;
            CardSurfaceView.BindTinyCard(tiny, first);
            Require(ColorsByNode[banner] == new Color(.82f, .68f, .34f, 1), "selected tiny banner missing");
            Selection.Remove(first); CardSurfaceView.RefreshTinyCards();
            Require(ColorsByNode[banner] == Colors.Gray, "deselection did not restore tiny banner");
            ColorsByNode[banner] = Colors.Red; CardSurfaceView.RefreshTinyCards();
            Require(ColorsByNode[banner] == Colors.Red, "unselected tiny card overwrote another UI mod");
            Selection[first] = chosen with { ExcludedBannerRarity = (int)rarity };
            CardSurfaceView.BindTinyCard(tiny, first);
            Require(ColorsByNode[banner] == Colors.Red, "rarity exclusion ignored");
            Selection[first] = chosen;
            CardSurfaceView.BindTinyCard(tiny, first);
            CardSurfaceView.ForgetTinyCard(tiny); ColorsByNode[banner] = Colors.Green;
            CardSurfaceView.RefreshTinyCards();
            Require(ColorsByNode[banner] == Colors.Green, "anonymous tiny card retained old model ownership");
            CardSurfaceView.BindTinyCard(tiny, second);
            Require(ColorsByNode[banner] == Colors.Green && first.Rarity == rarity,
                "another card inherited presentation, or actual gameplay rarity changed");
            foreach (var patch in new[] { typeof(TinyCardSelectedSurfacePatch), typeof(TinyCardAnonymousSurfacePatch) })
                Require(harmony.CreateClassProcessor(patch).Patch().Count == 1, "tiny card native hook unavailable");
        }
        finally
        {
            if (card != null) CardSurfaceView.Release(card);
            if (tiny != null) CardSurfaceView.ForgetTinyCard(tiny);
            Stretch.Clear(); ColorsByNode.Clear(); Selection.Clear(); harmony.UnpatchAll(harmony.Id);
        }
        Console.WriteLine("Exported surface lifecycle passed: repeat update, disable, model reuse, tiny banner, foreign ownership, native hook installation.");
    }
    private static bool Selected(CardModel card, ref CardSurfaceDefinition? __result)
    { __result = Selection.GetValueOrDefault(card); return false; }
    private static bool Valid(GodotObject instance, ref bool __result) { __result = instance != null; return false; }
    private static bool GetStretch(TextureRect __instance, ref TextureRect.StretchModeEnum __result)
    { __result = Stretch[__instance]; return false; }
    private static bool SetStretch(TextureRect __instance, TextureRect.StretchModeEnum value)
    { Stretch[__instance] = value; return false; }
    private static bool GetColor(CanvasItem __instance, ref Color __result) { __result = ColorsByNode[__instance]; return false; }
    private static bool SetColor(CanvasItem __instance, Color value) { ColorsByNode[__instance] = value; return false; }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    // These are metadata fixtures only. Scanning must NEVER run their methods.
    private static class CardReplacementRegistry
    {
        public static bool TryGetTexture(string id, out Texture2D? texture) => throw new Exception("must not execute provider");
    }
    private static class ExportedPortraitFixture
    {
        public static void Apply(NCard card, TextureRect portrait)
        {
            _ = "_portrait"; _ = "_ancientPortrait";
            if (CardReplacementRegistry.TryGetTexture(card.Model!.GetType().FullName!, out var texture))
            { portrait.Texture = texture; portrait.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered; }
            GC.KeepAlive("_portrait"); GC.KeepAlive("_ancientPortrait");
        }
    }
    [HarmonyPatch(typeof(NTinyCard), "GetBannerColor")]
    private static class ExportedBannerFixture
    {
        [HarmonyPostfix]
        public static void Postfix(ref Color __result) => __result = new Color(.82f, .68f, .34f, 1f);
    }
    private static class UnrelatedBannerFixture
    {
        public static Color Noise() => new Color(.1f, .2f, .3f, 1f);
    }
    [HarmonyPatch(typeof(NTinyCard), "GetBannerColor")]
    private static class ConfigDependentBannerFixture
    {
        public static bool Enabled = false;
        [HarmonyPostfix]
        public static void Postfix(ref Color __result)
        { if (Enabled) __result = new Color(.1f, .2f, .3f, 1f); }
    }
}
