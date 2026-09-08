using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// Cosmetics observe the native action; they never join, replace or await its Task chain.
internal partial class StatefulCombatEffects : Node
{
    private sealed record Effect(Node Node, Node? Driver, NCreatureVisuals Visuals, CardModel Source,
        string Provider, StatefulCardArtRuntime.Runtime Runtime);
    private readonly List<Effect> _effects = [];
    private double _elapsed;
    private const string NodeName = "SCStatefulCombatEffects";

    internal static void Play(CardModel source, Creature target, StatefulCardArtRuntime.Runtime runtime)
    {
        var room = NCombatRoom.Instance;
        var visuals = room?.GetCreatureNode(target)?.Visuals;
        if (room == null || visuals == null) return;
        var controller = room.GetNodeOrNull<StatefulCombatEffects>(NodeName);
        if (controller == null) { controller = new() { Name = NodeName }; room.AddChild(controller); }
        var kind = source.GetType().Name;
        if (NCard.FindOnTable(source) is {} card) StatefulCardPulse.Play(card, runtime, kind);
        // The author intentionally keeps squash/stretch until the next side turn, once per target.
        if (kind != "Rattle" && controller._effects.Any(effect => ReferenceEquals(effect.Visuals, visuals) &&
                effect.Source.GetType() == source.GetType() && GodotObject.IsInstanceValid(effect.Node))) return;
        var type = runtime.Assembly.GetTypes().Single(type => type.Name == "N" + kind + "Vfx");
        var create = type.GetMethods().Single(method => method.Name == "Create" && !method.IsGenericMethod);
        var parameters = create.GetParameters();
        var mode = Enum.Parse(parameters[^1].ParameterType, kind == "Rattle" ? "Timed" : "UntilRevert");
        object?[] args = kind == "Flatten" ? [visuals, null, .4f, mode] : [visuals, kind == "Rattle" ? .5f : .4f, mode];
        if (create.Invoke(null, args) is not Node node) return;
        var driver = AccessTools.Field(type, "_driver")?.GetValue(node) as Node;
        var provider = SkinService.GetSelectedCardOption(source)?.ProviderId;
        if (provider == null) { AccessTools.Method(type, "ForceKill")?.Invoke(node, null); return; }
        controller._effects.Add(new(node, driver, visuals, source, provider, runtime));
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (_elapsed < .2) return;
        _elapsed = 0;
        foreach (var effect in _effects.ToArray())
        {
            try
            {
                if (!GodotObject.IsInstanceValid(effect.Node) || !GodotObject.IsInstanceValid(effect.Visuals) ||
                    !effect.Runtime.Enabled || effect.Runtime.Simple ||
                    SkinService.GetSelectedCardOption(effect.Source)?.ProviderId != effect.Provider)
                    Remove(effect);
            }
            catch (Exception exception) { StatefulCardArtRuntime.Warn("effect cleanup", exception); Remove(effect); }
        }
    }
    private void Remove(Effect effect, bool exiting = false)
    {
        _effects.Remove(effect);
        if (GodotObject.IsInstanceValid(effect.Node))
        {
            AccessTools.Method(effect.Node.GetType(), "ForceKill")?.Invoke(effect.Node, null);
            AccessTools.Field(effect.Node.GetType(), "_driver")?.SetValue(effect.Node, null);
        }
        // A finished provider driver must not retain an old base transform for the next hit or
        // a later appearance edit. Only remove this lease's empty driver, never ClearAll globally.
        if (effect.Driver is {} driver && GodotObject.IsInstanceValid(driver) &&
            AccessTools.Field(driver.GetType(), "_activeModifiers")?.GetValue(driver) is System.Collections.ICollection { Count: 0 })
        {
            if (!exiting) driver.GetParent()?.RemoveChild(driver);
            driver.QueueFree();
        }
    }
    internal static void ClearCurrent()
    {
        var controller = NCombatRoom.Instance?.GetNodeOrNull<StatefulCombatEffects>(NodeName);
        if (controller == null) return;
        foreach (var effect in controller._effects.ToArray())
            try { controller.Remove(effect); } catch (Exception exception) { StatefulCardArtRuntime.Warn("turn effect cleanup", exception); }
    }
    public override void _ExitTree()
    {
        foreach (var effect in _effects.ToArray())
            try { Remove(effect, exiting: true); } catch (Exception exception) { StatefulCardArtRuntime.Warn("combat exit cleanup", exception); }
    }
}

// A bounded visual-only pulse. Remove only our last applied multiplier/offset, so hand layout,
// drag motion and a skin replacement do not inherit a stale transform snapshot.
internal partial class StatefulCardPulse : Node
{
    private NCard _card = null!;
    private Control _body = null!;
    private CardModel _model = null!;
    private string _provider = "";
    private string _kind = "";
    private double _time;
    private Vector2 _scale = Vector2.One, _offset, _writtenScale, _writtenPosition;
    private bool _applied;
    internal static void Play(NCard card, StatefulCardArtRuntime.Runtime runtime, string kind)
    {
        var old = card.GetNodeOrNull<StatefulCardPulse>("SCStatefulPulse");
        if (old != null) { old.Undo(); card.RemoveChild(old); old.QueueFree(); }
        var pulse = new StatefulCardPulse { Name = "SCStatefulPulse", _card = card, _body = card.Body,
            _model = card.Model!, _provider = SkinService.GetSelectedCardOption(card.Model!)?.ProviderId ?? "", _kind = kind };
        card.AddChild(pulse);
    }
    private void Undo()
    {
        if (!_applied || !GodotObject.IsInstanceValid(_body)) return;
        if (_body.Scale.IsEqualApprox(_writtenScale)) _body.Scale /= _scale;
        if (_body.Position.IsEqualApprox(_writtenPosition)) _body.Position -= _offset;
        _applied = false;
    }
    public override void _Process(double delta)
    {
        Undo(); _time += delta;
        if (!GodotObject.IsInstanceValid(_card) || !GodotObject.IsInstanceValid(_body) ||
            !ReferenceEquals(_model, _card.Model) || _time > (_kind == "Rattle" ? 1 : .5) ||
            SkinService.GetSelectedCardOption(_model)?.ProviderId != _provider)
        { QueueFree(); return; }
        var weight = MathF.Sin((float)_time * MathF.PI / (_kind == "Rattle" ? 1 : .5f));
        _scale = _kind == "Squeeze" ? Vector2.One.Lerp(new(.65f, 1.35f), weight) :
            _kind == "Flatten" ? Vector2.One.Lerp(new(1.5f, .4f), weight) : Vector2.One;
        _offset = _kind == "Rattle" ? new Vector2(MathF.Sin((float)_time * 95) * weight * 10, 0) : Vector2.Zero;
        _body.Scale *= _scale; _body.Position += _offset;
        _writtenScale = _body.Scale; _writtenPosition = _body.Position; _applied = true;
    }
    public override void _ExitTree() => Undo();
}

[HarmonyPatch(typeof(NExhaustPileButton), nameof(NExhaustPileButton.Initialize))]
internal static class StatefulExhaustInitializePatch
{
    private static void Postfix(NExhaustPileButton __instance)
    {
        if (__instance.GetNodeOrNull<StatefulExhaustIcon>("SCStatefulExhaust") == null)
            __instance.AddChild(new StatefulExhaustIcon { Name = "SCStatefulExhaust", Button = __instance });
    }
}

[HarmonyPatch(typeof(NCombatCardPile), nameof(NCombatCardPile.AnimOut))]
internal static class StatefulExhaustHidePatch
{
    private static bool Prefix(NCombatCardPile __instance) => __instance is not NExhaustPileButton ||
        __instance.GetNodeOrNull<StatefulExhaustIcon>("SCStatefulExhaust") is not { Active: true };
}

internal partial class StatefulExhaustIcon : Node
{
    internal NExhaustPileButton Button = null!;
    internal bool Active => _custom != null && GodotObject.IsInstanceValid(_custom) && _custom.Visible;
    private Control? _custom, _original;
    private string? _provider;
    private bool _originalVisible, _forcedButton;
    private bool _exiting;
    private double _elapsed;
    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (_elapsed < .2 || !GodotObject.IsInstanceValid(Button)) return;
        _elapsed = 0;
        try
        {
            var player = AccessTools.Field(typeof(NCombatCardPile), "_localPlayer").GetValue(Button) as Player;
            var card = player == null ? null : CardPile.Get(PileType.Hand, player)?.Cards.FirstOrDefault(card =>
                card.GetType().Name == "Defile" && StatefulCardArtRuntime.For(card) is { Enabled: true, Simple: false });
            var option = card == null ? null : SkinService.GetSelectedCardOption(card);
            if (option?.ProviderId == null) { Restore(); return; }
            if (_provider != option.ProviderId)
            {
                Restore();
                var runtime = StatefulCardArtRuntime.For(option)!;
                var scene = SkinService.LoadCardPresentationResource<PackedScene>(card!, runtime.Contract.ResourceRoot + "/images/defile_exhaust_icon.tscn");
                if (scene == null) return;
                _custom = scene.Instantiate<Control>();
                AccessTools.Field(_custom.GetType(), "Button")!.SetValue(_custom, Button);
                Button.AddChild(_custom);
                _custom.SetProcess(false); // Own predicate checks the selected Defile, not any Defile.
                _custom.MouseFilter = Control.MouseFilterEnum.Ignore;
                _provider = option.ProviderId;
                _original = Button.GetNodeOrNull<Control>("Icon");
                _originalVisible = _original?.Visible ?? false;
            }
            if (_original != null) _original.Visible = false;
            _custom!.Visible = true;
            if (!Button.Visible) { _forcedButton = true; Button.AnimIn(); }
        }
        catch (Exception exception) { Restore(); StatefulCardArtRuntime.Warn("exhaust icon", exception); }
    }
    private void Restore()
    {
        if (_custom != null && GodotObject.IsInstanceValid(_custom)) { _custom.Visible = false; _custom.QueueFree(); }
        if (_original != null && GodotObject.IsInstanceValid(_original)) _original.Visible = _originalVisible;
        _custom = null; _original = null; _provider = null;
        if (_forcedButton && !_exiting && GodotObject.IsInstanceValid(Button) && Button.IsInsideTree())
        {
            _forcedButton = false;
            if (AccessTools.Field(typeof(NCombatCardPile), "_localPlayer").GetValue(Button) is Player player &&
                CardPile.Get(PileType.Exhaust, player)?.Cards.Count == 0) Button.AnimOut();
        }
    }
    public override void _ExitTree() { _exiting = true; Restore(); }
}
