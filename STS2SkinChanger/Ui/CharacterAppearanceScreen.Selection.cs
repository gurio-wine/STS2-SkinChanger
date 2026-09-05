using Godot;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class CharacterAppearanceScreen
{
    private Godot.Timer _selectionHintTimer = null!;

    private sealed record SelectableAppearanceTarget(
        AppearanceTargetKind Kind, Rect2 Rect, int Priority, Func<Vector2, bool> Select);

    // Both the hint and the click handler use these same visible, supported hit regions.
    private IEnumerable<SelectableAppearanceTarget> GetSelectableTargets() =>
        EnumerateSelectableTargets().Where(target =>
            target.Rect.Intersects(new Rect2(Vector2.Zero, _dragSurface.Size)));

    private IEnumerable<SelectableAppearanceTarget> EnumerateSelectableTargets()
    {
        if (NMapScreen.Instance is { } map && GodotObject.IsInstanceValid(map) && map.IsVisibleInTree())
        {
            foreach (var point in EnumerateDescendants<NBossMapPoint>(map))
            {
                if (GodotObject.IsInstanceValid(point) && point.IsVisibleInTree() &&
                    CharacterAppearanceRuntime.TryGetBossMapAppearance(point, out _, out _) &&
                    _dragSurface.TryGetCanvasRect(point, 8f, out var rect))
                    yield return new(AppearanceTargetKind.MapBoss, rect, 4,
                        _ => SelectBossMapTarget(point, rect.GetCenter()));
            }
            // Room nodes can remain visible behind the map. They are not clickable through it.
            yield break;
        }

        foreach (var creature in NCombatRoom.Instance?.CreatureNodes ?? [])
        {
            if (!GodotObject.IsInstanceValid(creature) || !creature.IsVisibleInTree() ||
                creature.IsPlayingDeathAnimation ||
                !CharacterAppearanceRuntime.TryGetCreatureAppearance(creature, out _) ||
                !_dragSurface.TryGetCreatureTargetRect(creature, out var rect)) continue;
            var kind = creature.Entity.IsPlayer ? AppearanceTargetKind.Character :
                creature.Entity.PetOwner != null ? AppearanceTargetKind.Companion : AppearanceTargetKind.Monster;
            yield return new(kind, rect, 0, _ => SelectCreatureTarget(creature, rect.GetCenter()));
        }

        var shopPlayer = MerchantRuntimeAppearance.GetLocalPlayerVisual();
        if (_player != null && shopPlayer != null && GodotObject.IsInstanceValid(shopPlayer) &&
            shopPlayer.IsVisibleInTree() && ContextualSkinControls.FindGroup(
                _player.Character.Id.Entry, _player.Character.GetType().Name) != null &&
            _dragSurface.TryGetNode2DTargetRect(shopPlayer,
                new Rect2(-190f, -450f, 380f, 520f), out var playerRect))
            yield return new(AppearanceTargetKind.Character, playerRect, 1,
                _ => SelectShopPlayerTarget(shopPlayer, playerRect.GetCenter()));

        var merchant = NMerchantRoom.Instance?.MerchantButton;
        if (merchant != null && GodotObject.IsInstanceValid(merchant) && merchant.IsVisibleInTree() &&
            SkinService.Catalog?.Groups.Any(group => group.Id.Equals(
                MerchantRuntimeAppearance.GroupId, StringComparison.OrdinalIgnoreCase)) == true &&
            _dragSurface.TryGetCanvasRect(merchant, 8f, out var merchantRect))
            yield return new(AppearanceTargetKind.Merchant, merchantRect, 2,
                _ => SelectMerchantTarget(merchant, merchantRect.GetCenter()));

        var fakeMerchant = NEventRoom.Instance?.CustomEventNode as NFakeMerchant;
        var fakeButton = fakeMerchant?.MerchantButton;
        if (fakeButton != null && GodotObject.IsInstanceValid(fakeButton) && fakeButton.IsVisibleInTree() &&
            SkinService.Catalog?.Groups.Any(group => group.Id.Equals(
                "fake_merchant_monster", StringComparison.OrdinalIgnoreCase)) == true &&
            _dragSurface.TryGetCanvasRect(fakeButton, 8f, out var fakeRect))
            yield return new(AppearanceTargetKind.Merchant, fakeRect, 3,
                _ => SelectMerchantTarget(fakeButton, fakeRect.GetCenter(), "fake_merchant_monster", fakeMerchant));

        if (AncientRuntimeAppearance.TryGetCurrent(out var ancient, out var layout, out var ancientGroup))
        {
            var background = AncientRuntimeAppearance.GetBackgroundTarget(layout);
            if (background != null && GodotObject.IsInstanceValid(background) && background.IsVisibleInTree() &&
                _dragSurface.TryGetCanvasRect(background, 0f, out var ancientRect))
                yield return new(AppearanceTargetKind.Ancient, ancientRect, 5,
                    position => SelectAncientTarget(ancient, ancientGroup, position));
        }
    }

    private void RefreshSelectionHint()
    {
        if (!_selectionMode || _selectionHint == null || !IsVisibleInTree()) return;
        var text = ModLocalization.FormatAppearanceTargetHint(GetSelectableTargets().Select(target => target.Kind));
        if (_selectionHint.Text != text) _selectionHint.Text = text;
    }

    private void UpdateSelectionHintRefresh()
    {
        if (_selectionMode && IsVisibleInTree())
        {
            RefreshSelectionHint();
            if (_selectionHintTimer.IsStopped()) _selectionHintTimer.Start();
        }
        else _selectionHintTimer.Stop();
    }
}
