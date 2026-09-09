using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.addons.mega_text;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// Leases belong to an NCard AND its current model/provider, not a card type or a shared Texture.
// Restore before native refresh and dispose when pooled/rebound. Animation nodes never attach
// through the provider's global AddedNode factory.
internal static class StatefulCardArtView
{
    private static readonly ConditionalWeakTable<NCard, Lease> Leases = new();

    internal static void BeforeRefresh(NCard card, bool reload)
    {
        StatefulCardArtRuntime.Bind(card);
        if (!Leases.TryGetValue(card, out var lease)) return;
        lease.Restore();
        var selected = card.Model == null ? null : SkinService.GetSelectedCardOption(card.Model);
        if (reload || !ReferenceEquals(card.Model, lease.Model) || selected?.ProviderId != lease.Provider ||
            !lease.Runtime.Enabled || lease.Simple != lease.Runtime.Simple) Release(card);
    }

    internal static void Release(NCard card)
    {
        if (!Leases.TryGetValue(card, out var lease)) return;
        Leases.Remove(card);
        try { lease.Dispose(); }
        catch (Exception exception) { StatefulCardArtRuntime.Warn("release card node", exception); }
    }

    internal static void Apply(NCard card, ExternalCardVisualOwnership external, CardPreviewMode preview = CardPreviewMode.Normal)
    {
        StatefulCardArtRuntime.Bind(card);
        if (card.Model == null || !card.IsInsideTree() || !card.IsNodeReady()) return;
        var selected = SkinService.GetSelectedCardOption(card.Model);
        var runtime = StatefulCardArtRuntime.For(selected);
        if (runtime is not { Enabled: true }) { Release(card); return; }
        try
        {
            if (!Leases.TryGetValue(card, out var lease))
            {
                lease = new Lease(card, runtime, selected!.ProviderId!);
                Leases.Add(card, lease);
            }
            lease.Apply(external, preview);
        }
        catch (Exception exception)
        {
            Release(card);
            StatefulCardArtRuntime.Warn("node " + card.Model.GetType().Name, exception);
        }
    }

    private sealed class Lease
    {
        internal CardModel Model { get; }
        internal string Provider { get; }
        internal StatefulCardArtRuntime.Runtime Runtime { get; }
        internal bool Simple { get; }
        private readonly NCard _card;
        private readonly List<Node> _added = [];
        private readonly Dictionary<CanvasItem, (bool Visible, Color Modulate)> _items = new();
        private readonly Dictionary<Control, float> _rotations = new();
        private readonly Dictionary<MegaLabel, string> _labels = new();
        private bool _animationsAttached;
        private bool _watcherAttached;
        private StatefulCardArtWatcher? _watcher;

        internal Lease(NCard card, StatefulCardArtRuntime.Runtime runtime, string provider)
        {
            _card = card; Model = card.Model!; Runtime = runtime; Provider = provider; Simple = runtime.Simple;
        }
        private void Remember(CanvasItem? item)
        {
            if (item != null && !_items.ContainsKey(item)) _items[item] = (item.Visible, item.Modulate);
        }
        private void Show(string path, bool visible)
        {
            var item = _card.GetNodeOrNull<CanvasItem>(path);
            Remember(item);
            if (item != null) item.Visible = visible;
        }
        private void Tint(CanvasItem? item, Color color)
        {
            Remember(item);
            if (item != null) item.Modulate = color;
        }
        internal void Restore()
        {
            foreach (var (item, value) in _items)
                TryRestore(() => { if (GodotObject.IsInstanceValid(item)) { item.Visible = value.Visible; item.Modulate = value.Modulate; } });
            foreach (var (item, value) in _labels)
                TryRestore(() => { if (GodotObject.IsInstanceValid(item)) item.SetTextAutoSize(value); });
            foreach (var (item, value) in _rotations)
                TryRestore(() => { if (GodotObject.IsInstanceValid(item)) item.Rotation = value; });
            _items.Clear(); _labels.Clear(); _rotations.Clear();
        }
        private static void TryRestore(Action restore)
        {
            try { restore(); } catch (Exception exception) { StatefulCardArtRuntime.Warn("restore card node", exception); }
        }
        internal void Apply(ExternalCardVisualOwnership external, CardPreviewMode preview)
        {
            if (!_watcherAttached)
            {
                _watcherAttached = true;
                var watcher = _watcher = new StatefulCardArtWatcher { Card = _card, Runtime = Runtime, Provider = Provider };
                _added.Add(watcher);
                _card.AddChild(watcher);
            }
            if (_watcher != null) _watcher.Preview = preview;
            if (!external.Text)
            {
                var title = _card.GetNodeOrNull<MegaLabel>("%TitleLabel");
                if (Runtime.Setting("HideTitle", true) && title != null)
                {
                    _labels.TryAdd(title, title.Text);
                    title.SetTextAutoSize(!Model.IsUpgraded ? "" : Model.MaxUpgradeLevel <= 1 ? "+" : $"+{Model.CurrentUpgradeLevel}");
                }
                if (Runtime.Setting("HideStars", true)) Show("%StarIcon", false);
            }
            if (!external.Frame)
            {
                // Match the author's full-art frame selection without changing Rarity or
                // consulting a template card's currently selected skin.
                var template = Runtime.Setting("HideType", true) ? ModelDb.Card<Apotheosis>() :
                    Model.Rarity == CardRarity.Ancient ? Model : Model.Type switch
                    {
                        CardType.Attack => (CardModel)ModelDb.Card<NeowsFury>(),
                        CardType.Power => ModelDb.Card<Corruption>(),
                        _ => ModelDb.Card<Apotheosis>()
                    };
                var bg = _card.GetNodeOrNull<TextureRect>("%AncientTextBg");
                var bgPath = AccessTools.Property(typeof(CardModel), "AncientTextBgPath")?.GetValue(template) as string;
                if (bg != null && bgPath != null) bg.Texture = SkinService.LoadCardPresentationResource<Texture2D>(Model, bgPath);
                var materialPath = AccessTools.Property(typeof(CardModel), "BannerMaterialPath")?.GetValue(template) as string;
                var ancientBanner = _card.GetNodeOrNull<CanvasItem>("%AncientBanner");
                if (ancientBanner != null && materialPath != null)
                    ancientBanner.Material = SkinService.LoadCardPresentationResource<Material>(Model, materialPath);
                if (!external.Text)
                {
                    Show("%AncientTextBg", !Runtime.Setting("HideDescription", true));
                    Show("%AncientBorderGlassOverlay", !Runtime.Setting("HideDescription", true));
                }
                foreach (var path in new[] { "%TitleBanner", "%AncientBanner" })
                {
                    var item = _card.GetNodeOrNull<CanvasItem>(path);
                    if (item != null) Tint(item, new Color(item.Modulate, 0.7f));
                }
            }
            if (Simple || external.Frame || external.Portrait) return;
            if (!_animationsAttached) { _animationsAttached = true; AttachAnimations(); }
            if (Model.GetType().Name == "Alignment")
            {
                // Only rotate the card body. Never change the hand/drag holder transform.
                _rotations.TryAdd(_card.Body, _card.Body.Rotation);
                _card.Body.Rotation = _rotations[_card.Body] + Mathf.DegToRad(-15);
            }
            else if (Model.GetType().Name is "Glow" or "Luminesce")
            {
                ApplyGlow(preview);
            }
        }

        private void ApplyGlow(CardPreviewMode preview)
        {
            // The small audited helpers create native glows; snapshot and own exactly the nodes
            // created by this call, rather than running their all-card Harmony lifecycle.
            var extension = Runtime.Assembly.GetType("NCardExtensions");
            if (extension == null) return;
            var before = Descendants(_card).ToHashSet();
            var color = Model.GetType().Name == "Glow" ? new Color(1, 0.8f, 0.2f) : new Color(.314f, .784f, .471f);
            foreach (var name in new[] { "AssertUncommonGlow", "AssertRareGlow" })
            {
                Remember(AccessTools.Field(typeof(NCard), name == "AssertRareGlow" ? "_rareGlow" : "_uncommonGlow")?.GetValue(_card) as CanvasItem);
                if (AccessTools.Method(extension, name)?.Invoke(null, [_card]) is CanvasItem glow)
                {
                    Tint(glow, color);
                    glow.Visible = Model.GetType().Name == "Glow" || Model.IsUpgraded || preview == CardPreviewMode.Upgrade;
                }
            }
            if (Model.GetType().Name == "Luminesce")
            {
                var sparkles = _card.GetNodeOrNull<CanvasItem>("CardContainer/CardSparkles");
                Tint(sparkles, color);
                if (sparkles != null) sparkles.Visible = Model.IsUpgraded || preview == CardPreviewMode.Upgrade;
            }
            _added.AddRange(Descendants(_card).Where(node => !before.Contains(node) && before.Contains(node.GetParent())));
        }
        private void AttachAnimations()
        {
            var name = Model.GetType().Name;
            if (name is "PullAggro" or "Putrefy")
            {
                var path = Runtime.Contract.ResourceRoot + "/scenes/cards/" + (name == "PullAggro" ? "osty_dance" : "bad_apple") + ".tscn";
                var scene = SkinService.LoadCardPresentationResource<PackedScene>(Model, path);
                if (scene == null) return;
                var node = scene.Instantiate();
                _added.Add(node);
                if (name == "Putrefy")
                {
                    AccessTools.Method(node.GetType(), "SetCard")!.Invoke(node, [_card]);
                    _card.AddChild(node);
                }
                else
                {
                    AccessTools.Field(node.GetType(), "card")!.SetValue(node, _card);
                    _card.GetNode<CanvasGroup>("%PortraitCanvasGroup").AddChild(node);
                }
            }
            else if (name == "InfiniteBlades")
            {
                var type = Runtime.Assembly.GetTypes().Single(type => type.Name == "InfiniteInfiniteBlades");
                var node = (Control)Activator.CreateInstance(type)!;
                _added.Add(node);
                AccessTools.Property(type, "CardNode")!.SetValue(node, _card);
                _card.Body.AddChild(node);
                _card.Body.MoveChild(node, _card.GetNode<CanvasGroup>("%PortraitCanvasGroup").GetIndex() + 1);
            }
        }
        internal void Dispose()
        {
            Restore();
            foreach (var node in _added.Distinct())
            {
                if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion()) continue;
                foreach (var fieldName in new[] { "_rareGlow", "_uncommonGlow" })
                {
                    var field = AccessTools.Field(typeof(NCard), fieldName);
                    if (ReferenceEquals(field?.GetValue(_card), node)) field.SetValue(_card, null);
                }
                node.ProcessMode = Node.ProcessModeEnum.Disabled;
                if (node is CanvasItem canvas) canvas.Visible = false;
                node.QueueFree();
            }
            _added.Clear();
        }
    }
    private static IEnumerable<Node> Descendants(Node root)
    {
        yield return root;
        foreach (var child in root.GetChildren())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}

internal partial class StatefulCardArtWatcher : Node
{
    internal NCard Card = null!;
    internal StatefulCardArtRuntime.Runtime Runtime = null!;
    internal string Provider = "";
    internal CardPreviewMode Preview;
    private double _elapsed;
    private string? _signature;
    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (_elapsed < .2 || !GodotObject.IsInstanceValid(Card) || !Card.IsVisibleInTree() || Card.Model == null) return;
        _elapsed = 0;
        try
        {
            var option = SkinService.GetSelectedCardOption(Card.Model);
            if (option?.ProviderId != Provider) { StatefulCardArtView.Release(Card); return; }
            var path = Runtime.Portrait(Card.Model, option.GetPortraitPath(Card.Model.GetType().Name, false));
            var signature = path + ":" + Runtime.Presentation(Card.Model) + ":" + Runtime.Simple + ":" +
                Runtime.Setting("HideTitle") + Runtime.Setting("HideStars");
            if (_signature != null && signature != _signature)
            {
                _signature = signature;
                var preview = Preview;
                AccessTools.Method(typeof(NCard), "Reload").Invoke(Card, null);
                Card.UpdateVisuals(Card.DisplayingPile, preview);
                return;
            }
            _signature = signature;
        }
        catch (Exception exception) { StatefulCardArtRuntime.Warn("visible refresh", exception); SetProcess(false); }
    }
}

[HarmonyPatch(typeof(NCard), nameof(NCard._ExitTree))]
internal static class StatefulCardTreeExitPatch
{
    private static void Prefix(NCard __instance) => StatefulCardArtView.Release(__instance);
}
