using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;
using STS2SkinChanger.Ui;

internal static class CardPresentationLifecycleTests
{
    private static readonly Type Controls = typeof(CardSkinControls);
    private static readonly Dictionary<CanvasItem, bool> Visible = new();
    private static NCard _card = null!;
    private static Control _description = null!, _plaque = null!, _foreign = null!;
    private static readonly List<Node> Removed = [];
    private static CardPresentationDefinition? _presentation;
    private static object Table(string name) => AccessTools.Field(Controls, name).GetValue(null)!;
    private static void Remove(string name) => Table(name).GetType().GetMethod("Remove")!.Invoke(Table(name), [_card]);
    private static T Bare<T>() where T : GodotObject
    {
        var value = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        GC.SuppressFinalize(value);
        return value;
    }

    internal static void Run(bool ownershipOnly = false)
    {
        var harmony = new Harmony("SkinChanger.Tests.CardPresentationLifecycle");
        try
        {
            // Only substitute Godot native drawing/tree access. Our snapshot restoration,
            // ownership bookkeeping and lifecycle patch callbacks execute for real.
            void Patch(MethodInfo method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(CardPresentationLifecycleTests), prefix));
            Patch(AccessTools.Method(typeof(GodotObject), nameof(GodotObject.IsInstanceValid)), nameof(Valid));
            Patch(AccessTools.PropertySetter(typeof(CanvasItem), "Visible"), nameof(SetVisible));
            foreach (var property in new[] { "Material", "Modulate", "SelfModulate", "ZIndex" })
                Patch(AccessTools.PropertySetter(typeof(CanvasItem), property), nameof(Skip));
            Patch(typeof(Node).GetMethods().Single(method => method.Name == nameof(Node.GetParent) && !method.IsGenericMethod), nameof(Parent));
            Patch(AccessTools.Method(typeof(GodotObject), nameof(GodotObject.GetInstanceId)), nameof(Id));
            Patch(AccessTools.Method(typeof(Node), nameof(Node.RemoveChild)), nameof(RemoveChild));
            Patch(AccessTools.Method(typeof(Node), nameof(Node.QueueFree)), nameof(QueueFree));
            Patch(AccessTools.Method(typeof(GodotObject), nameof(GodotObject.IsQueuedForDeletion)), nameof(NotQueued));
            Patch(AccessTools.Method(Controls, "Descendants"), nameof(Descendants));
            Patch(AccessTools.Method(Controls, "ApplyBuiltInOverlay"), nameof(Skip));
            Patch(AccessTools.Method(Controls, "ApplyManagedCardPresentation"), nameof(Skip));
            Patch(AccessTools.Method(typeof(SkinService), nameof(SkinService.GetCardPresentation)), nameof(Presentation));
            _card = Bare<NCard>(); _description = Bare<Control>(); _plaque = Bare<Control>(); _foreign = Bare<Control>();
            if (!ownershipOnly) CheckReuse();
            CheckForeignNodes();
            CheckDisableOnExistingNode();
        }
        finally
        {
            if (_card != null) { Remove("BaselineLayouts"); Remove("PresentationLayouts"); }
            Visible.Clear(); Removed.Clear(); harmony.UnpatchAll(harmony.Id);
        }
        Console.WriteLine("Card presentation lifecycle passed: pooled text/type reset, same-model preservation and foreign UI ownership.");
    }

    private static void Seed(bool managed)
    {
        Remove("BaselineLayouts"); Remove("PresentationLayouts"); Removed.Clear();
        AccessTools.FieldRefAccess<NCard, CardModel?>("_model")(_card) = new Wither();
        Visible[_description] = false; Visible[_plaque] = false;
        _presentation = new(DescriptionVisible: false, TypePlaqueVisible: false);
        var itemType = Controls.GetNestedType("CanvasItemState", BindingFlags.NonPublic)!;
        var items = Array.CreateInstance(itemType, 2);
        var nodes = new[] { _description, _plaque };
        for (var i = 0; i < nodes.Length; i++)
            items.SetValue(Activator.CreateInstance(itemType, [nodes[i], true, null, Colors.White, Colors.White, 0,
                null, null, null, null, null, null]), i);
        var layoutType = Controls.GetNestedType("CardLayoutState", BindingFlags.NonPublic)!;
        var layout = Activator.CreateInstance(layoutType, [_card.Model, items]);
        Table("BaselineLayouts").GetType().GetMethod("Add")!.Invoke(Table("BaselineLayouts"), [_card, layout]);
        if (managed)
        {
            var state = Activator.CreateInstance(Controls.GetNestedType("CardPresentationState", BindingFlags.NonPublic)!, [new List<Node>()]);
            Table("PresentationLayouts").GetType().GetMethod("Add")!.Invoke(Table("PresentationLayouts"), [_card, state]);
        }
    }

    private static void CheckReuse()
    {
        var assembly = Controls.Assembly;
        var rebind = assembly.GetType("STS2SkinChanger.Ui.CardLayoutModelRebindPatch")!;
        var prefix = AccessTools.Method(rebind, "Prefix");
        for (var pass = 0; pass < 3; pass++)
        {
            Seed(managed: true);
            prefix.Invoke(null, [_card, _card.Model]);
            Require(!Visible[_description] && !Visible[_plaque], "same-model assignment must not strip its selected skin");
            prefix.Invoke(null, [_card, new Burn()]);
            Require(Visible[_description] && Visible[_plaque], "pooled card retained the previous skin's hidden description/type plaque");
            Require(!CardSkinControls.HasCurrentCardLayout(_card), "old model snapshot must be forgotten before binding another card");
        }
        Seed(managed: false);
        prefix.Invoke(null, [_card, new Burn()]);
        Require(!Visible[_description] && !Visible[_plaque], "unmodified cards must keep UI modifications owned by other mods");
        var poolPatch = assembly.GetType("STS2SkinChanger.Ui.CardLayoutPoolReleasePatch");
        Require(poolPatch != null, "native pool return clears _model directly and bypasses its setter");
        Seed(managed: true);
        AccessTools.Method(poolPatch, "Prefix").Invoke(null, [_card]);
        Require(Visible[_description] && Visible[_plaque], "pool boundary must restore before native code clears the old model");
        var installer = new Harmony("SkinChanger.Tests.CardPoolBoundary");
        try
        {
            Require(installer.CreateClassProcessor(poolPatch).Patch().Count == 2, "both pool lifecycle boundaries must be installed");
            Require(installer.CreateClassProcessor(rebind).Patch().Count == 1, "model rebind boundary must be installed");
        }
        finally { installer.UnpatchAll(installer.Id); }
    }

    private static void CheckForeignNodes()
    {
        Seed(managed: false);
        // This foreign UI root was added AFTER the native Reload baseline was captured.
        // A later SC presentation must not adopt it and destroy it on the next refresh.
        CardSkinControls.ApplySelectedPresentation(_card, default);
        var owned = Bare<Control>();
        AccessTools.Method(Controls, "TrackAddedPresentationNode").Invoke(null, [_card, owned]);
        // UpdateVisuals and drag tree re-entry can apply presentation more than once.
        CardSkinControls.ApplySelectedPresentation(_card, default);
        CardSkinControls.RestoreBaselineLayout(_card);
        Require(!Removed.Contains(_foreign), "presentation cleanup destroyed another mod's late-added UI root");
        Require(Removed.Contains(owned), "repeated refresh lost ownership of our own overlay");
        foreach (var factory in new[] { "ApplyManagedFullFrameArt", "ApplyNormalTextBackground" })
            Require(PatchProcessor.GetOriginalInstructions(AccessTools.Method(Controls, factory))
                .Any(instruction => Equals(instruction.operand, AccessTools.Method(Controls, "TrackAddedPresentationNode"))),
                factory + " must register the nodes it actually creates");
    }

    private static void CheckDisableOnExistingNode()
    {
        Seed(managed: true);
        _presentation = null;
        CardSkinControls.ApplySelectedPresentation(_card, default);
        Require(Visible[_description] && Visible[_plaque], "disabling a live provider dropped its lease without restoring text/type");
        Visible[_description] = false; Visible[_plaque] = false;
        CardSkinControls.ApplySelectedPresentation(_card, default);
        Require(!Visible[_description] && !Visible[_plaque], "no selected presentation must not continuously overwrite external UI");
    }

    private static bool Skip() => false;
    private static bool Valid(GodotObject instance, ref bool __result) { __result = instance != null; return false; }
    private static bool SetVisible(CanvasItem __instance, bool value) { Visible[__instance] = value; return false; }
    private static bool Parent(ref Node __result) { __result = _card; return false; }
    private static bool Id(ref ulong __result) { __result = 42; return false; }
    private static bool RemoveChild(Node node) { Removed.Add(node); return false; }
    private static bool QueueFree(Node __instance) { Removed.Add(__instance); return false; }
    private static bool NotQueued(ref bool __result) { __result = false; return false; }
    private static bool Descendants(ref IEnumerable<Node> __result) { __result = [_foreign]; return false; }
    private static bool Presentation(ref CardPresentationDefinition? __result) { __result = _presentation; return false; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
