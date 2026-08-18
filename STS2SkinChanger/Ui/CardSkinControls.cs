using Godot;
using HarmonyLib;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class CardSkinControls
{
    private const string SelectorName = "STS2CardSkinSelector";
    private const string DropdownName = "CardSkinDropdown";
    private const string AvailabilityFilterName = "STS2SkinnedCardsOnly";
    private const string AvailabilityFilterMeta = "sts2_skinned_cards_only";
    private const string GroupMeta = "sts2_card_skin_group";
    private const string UpdatingMeta = "sts2_card_skin_updating";
    private static readonly System.Reflection.MethodInfo ReloadCardMethod =
        AccessTools.Method(typeof(NCard), "Reload");
    private static readonly System.Reflection.MethodInfo UpdateLibraryFilterMethod =
        AccessTools.Method(typeof(NCardLibrary), "UpdateFilter", [typeof(bool)]);
    private static readonly ConditionalWeakTable<NCard, CardLayoutState> BaselineLayouts = new();
    private static readonly ConditionalWeakTable<NCard, CardPresentationState> PresentationLayouts = new();

    public static void Attach(NCardLibrary screen)
    {
        SkinService.InitializeCardGroupsAfterModels();
        VisualPatchGuard.RemoveProviderVisualPatches(ManagedSkinModLoader.ProviderRoots);
        var bottom = screen.GetNodeOrNull<VBoxContainer>("Sidebar/MarginContainer/BottomVBox");
        if (bottom == null || bottom.GetNodeOrNull<HBoxContainer>(SelectorName) != null)
        {
            return;
        }

        var selector = new HBoxContainer
        {
            Name = SelectorName,
            CustomMinimumSize = new Vector2(0, 40),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false
        };
        var dropdown = new OptionButton
        {
            Name = DropdownName,
            CustomMinimumSize = new Vector2(256, 40),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FitToLongestItem = false,
            ClipText = true,
            Alignment = HorizontalAlignment.Center
        };
        ContextualSkinControls.ApplyGameTheme(dropdown);
        dropdown.AddThemeFontSizeOverride("font_size", 19);
        dropdown.GetPopup().AddThemeFontSizeOverride("font_size", 19);
        dropdown.ItemSelected += index => ApplySelection(
            screen,
            selector,
            dropdown,
            checked((int)index));
        selector.AddChild(dropdown);
        bottom.AddChild(selector);
        bottom.MoveChild(selector, 0);

        var filterScene = ResourceLoader.Load<PackedScene>(
            "res://scenes/screens/card_library/card_library_tickbox.tscn");
        if (filterScene != null)
        {
            var availabilityFilter = filterScene.Instantiate<NLibraryStatTickbox>(
                PackedScene.GenEditState.Disabled);
            availabilityFilter.Name = AvailabilityFilterName;
            bottom.AddChild(availabilityFilter);
            bottom.MoveChild(availabilityFilter, 1);
            availabilityFilter.SetLabel("仅显示有皮肤的卡牌");
            availabilityFilter.IsTicked = false;
            availabilityFilter.Connect(
                NTickbox.SignalName.Toggled,
                Callable.From<NTickbox>(tickbox =>
                    SetAvailabilityFilter(screen, tickbox.IsTicked)));
        }

        ShowFirstAvailableGroup(selector);
    }

    public static void ShowForFilter(NCardLibrary screen, NCardPoolFilter filter)
    {
        if (!filter.IsSelected)
        {
            return;
        }

        var selector = screen.GetNodeOrNull<HBoxContainer>(
            $"Sidebar/MarginContainer/BottomVBox/{SelectorName}");
        if (selector == null)
        {
            return;
        }

        Populate(selector, GetGroupId(filter));
    }

    public static void SyncToSelectedFilter(NCardLibrary screen)
    {
        var selector = screen.GetNodeOrNull<HBoxContainer>(
            $"Sidebar/MarginContainer/BottomVBox/{SelectorName}");
        if (selector == null)
        {
            return;
        }

        var selected = Descendants(screen)
            .OfType<NCardPoolFilter>()
            .FirstOrDefault(filter => filter.IsSelected && FindGroup(GetGroupId(filter)) != null);
        if (selected != null)
        {
            Populate(selector, GetGroupId(selected));
        }
        else
        {
            ShowFirstAvailableGroup(selector);
        }
    }

    public static void ReplacePortrait(CardModel card, ref Texture2D result) =>
        SkinService.ReplaceCardPortrait(card, ref result);

    public static void CaptureBaselineLayout(NCard card)
    {
        BaselineLayouts.Remove(card);
        BaselineLayouts.Add(card, CardLayoutState.Capture(card));
    }

    public static void RestoreBaselineLayout(NCard card)
    {
        if (PresentationLayouts.TryGetValue(card, out var presentation))
        {
            foreach (var addedNode in presentation.AddedNodes)
            {
                if (!GodotObject.IsInstanceValid(addedNode))
                {
                    continue;
                }

                addedNode.GetParent()?.RemoveChild(addedNode);
                addedNode.QueueFree();
            }

            PresentationLayouts.Remove(card);
        }

        if (BaselineLayouts.TryGetValue(card, out var state))
        {
            state.Restore();
        }
    }

    public static void ReplaySelectedPresentation(
        NCard card,
        System.Reflection.MethodBase originalMethod,
        object[] originalArguments)
    {
        if (card.Model == null)
        {
            return;
        }

        var providerRoot = SkinService.GetCardPresentationProviderRoot(card.Model);
        if (!SkinService.PrepareCardPresentationProvider(providerRoot))
        {
            PresentationLayouts.Remove(card);
            return;
        }
        var replayed = VisualPatchGuard.ReplaySelectedCardPostfixes(
            card,
            originalMethod,
            originalArguments,
            providerRoot);
        if (providerRoot == null)
        {
            PresentationLayouts.Remove(card);
            return;
        }

        if (replayed > 0)
        {
            if (!BaselineLayouts.TryGetValue(card, out var baseline))
            {
                baseline = CardLayoutState.Capture(card);
            }

            PresentationLayouts.Remove(card);
            PresentationLayouts.Add(
                card,
                new CardPresentationState(baseline.FindAddedNodes(card)));
            return;
        }

        PresentationLayouts.Remove(card);
    }

    public static void ApplySelectedPortraitToNode(NCard card)
    {
        if (card.Model == null)
        {
            return;
        }

        // CardModel.Portrait 的 getter 已被 Priority.Last 的 Postfix 接管，这里拿到的已是换肤后的贴图。
        var portrait = card.Model.Portrait;
        var targetPath = card.Model.Rarity == CardRarity.Ancient
            ? "%AncientPortrait"
            : "%Portrait";
        var target = card.GetNodeOrNull<TextureRect>(targetPath);
        if (target != null)
        {
            target.Texture = portrait;
        }
    }

    public static void ResetAvailabilityFilter(NCardLibrary screen)
    {
        screen.SetMeta(AvailabilityFilterMeta, false);
        var filter = screen.GetNodeOrNull<NLibraryStatTickbox>(
            $"Sidebar/MarginContainer/BottomVBox/{AvailabilityFilterName}");
        if (filter != null)
        {
            filter.IsTicked = false;
        }
    }

    public static void ApplyAvailabilityFilter(
        NCardLibraryGrid grid,
        ref Func<CardModel, bool> filter)
    {
        Node? current = grid;
        while (current != null && current is not NCardLibrary)
        {
            current = current.GetParent();
        }

        if (current is not NCardLibrary library ||
            !library.GetMeta(AvailabilityFilterMeta, false).AsBool())
        {
            return;
        }

        var original = filter;
        filter = card => original(card) && SkinService.HasCardSkin(card);
    }

    private static void SetAvailabilityFilter(NCardLibrary screen, bool enabled)
    {
        screen.SetMeta(AvailabilityFilterMeta, enabled);
        try
        {
            UpdateLibraryFilterMethod.Invoke(screen, [false]);
        }
        catch (Exception exception)
        {
            ModLog.Error("刷新有皮肤卡牌筛选失败：" + exception);
        }
    }

    private static void ShowFirstAvailableGroup(HBoxContainer selector)
    {
        var preferred = FindGroup("ironclad") ?? SkinService.Catalog?.CardGroups.FirstOrDefault();
        Populate(selector, preferred?.Id);
    }

    private static void Populate(HBoxContainer selector, string? groupId)
    {
        var dropdown = selector.GetNode<OptionButton>(DropdownName);
        var group = groupId == null ? null : FindGroup(groupId);
        if (group == null || group.Options.Count == 0)
        {
            selector.Visible = false;
            dropdown.Clear();
            return;
        }

        selector.SetMeta(UpdatingMeta, true);
        selector.SetMeta(GroupMeta, group.Id);
        dropdown.Clear();
        dropdown.TooltipText = group.DisplayName + "卡牌皮肤";
        dropdown.AddItem("游戏默认");
        dropdown.SetItemMetadata(0, SkinCatalog.BaseOptionId);
        foreach (var option in group.Options)
        {
            var index = dropdown.ItemCount;
            dropdown.AddItem(option.Name);
            dropdown.SetItemMetadata(index, option.Id);
        }

        var selected = SkinService.GetCardSelection(group.Id);
        var selectedIndex = Enumerable.Range(0, dropdown.ItemCount)
            .FirstOrDefault(index => dropdown.GetItemMetadata(index).AsString() == selected);
        dropdown.Select(selectedIndex);
        selector.SetMeta(UpdatingMeta, false);
        selector.Visible = true;
    }

    private static void ApplySelection(
        NCardLibrary screen,
        HBoxContainer selector,
        OptionButton dropdown,
        int index)
    {
        if (selector.GetMeta(UpdatingMeta, false).AsBool())
        {
            return;
        }

        var groupId = selector.GetMeta(GroupMeta, string.Empty).AsString();
        var optionId = dropdown.GetItemMetadata(index).AsString();
        if (!SkinService.ApplyCardSelection(groupId, optionId))
        {
            ModLog.Error($"卡牌皮肤界面切换失败：{SkinService.LastError}");
            Populate(selector, groupId);
            return;
        }

        Callable.From(() => RefreshVisibleCards(screen, groupId)).CallDeferred();
    }

    private static void RefreshVisibleCards(NCardLibrary screen, string groupId)
    {
        try
        {
            foreach (var card in Descendants(screen).OfType<NCard>())
            {
                if (card.Model == null ||
                    !SkinService.CardBelongsToGroup(card.Model, groupId))
                {
                    continue;
                }

                ReloadCardMethod.Invoke(card, null);
            }
        }
        catch (Exception exception)
        {
            ModLog.Error("刷新卡牌总览皮肤失败：" + exception);
        }
    }

    private static CardSkinGroup? FindGroup(string? groupId) =>
        groupId == null
            ? null
            : SkinService.Catalog?.CardGroups.FirstOrDefault(group =>
                group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));

    private static string GetGroupId(NCardPoolFilter filter)
    {
        var id = filter.Name.ToString();
        return id.EndsWith("Pool", StringComparison.OrdinalIgnoreCase)
            ? id[..^4].ToLowerInvariant()
            : id.ToLowerInvariant();
    }

    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class CardLayoutState(
        IReadOnlyList<CanvasItemState> items,
        IReadOnlySet<ulong> baselineNodeIds)
    {
        private static readonly string[] NodePaths =
        [
            "%Portrait",
            "%PortraitShadow",
            "%PortraitBorder",
            "%Frame",
            "%TitleBanner",
            "%AncientPortrait",
            "%AncientBorderGlassOverlay",
            "%AncientBorder",
            "%AncientTextBg",
            "%AncientBanner",
            "%PortraitCanvasGroup"
        ];

        public static CardLayoutState Capture(NCard card)
        {
            var states = NodePaths
                .Select(path => card.GetNodeOrNull<CanvasItem>(path))
                .Where(item => item != null)
                .Cast<CanvasItem>()
                .Select(item => new CanvasItemState(
                    item,
                    item.Visible,
                    item.Material,
                    item.Modulate,
                    item.SelfModulate,
                    item.ZIndex,
                    (item as Control)?.Position,
                    (item as Control)?.Size,
                    (item as Control)?.Scale,
                    (item as Control)?.Rotation,
                    (item as Control)?.PivotOffset,
                    (item as TextureRect)?.Texture,
                    (item as TextureRect)?.ExpandMode,
                    (item as TextureRect)?.StretchMode,
                    (item as TextureRect)?.FlipH,
                    (item as TextureRect)?.FlipV))
                .ToArray();
            var nodeIds = Descendants(card)
                .Select(node => node.GetInstanceId())
                .ToHashSet();
            return new CardLayoutState(states, nodeIds);
        }

        public IReadOnlyList<Node> FindAddedNodes(NCard card) =>
            Descendants(card)
                .Where(node => !baselineNodeIds.Contains(node.GetInstanceId()))
                .Where(node => node.GetParent() == card ||
                               (node.GetParent() is { } parent &&
                                baselineNodeIds.Contains(parent.GetInstanceId())))
                .ToArray();

        public void Restore()
        {
            foreach (var state in items)
            {
                if (!GodotObject.IsInstanceValid(state.Item))
                {
                    continue;
                }

                state.Item.Visible = state.Visible;
                if (state.Material == null || GodotObject.IsInstanceValid(state.Material))
                {
                    state.Item.Material = state.Material;
                }
                state.Item.Modulate = state.Modulate;
                state.Item.SelfModulate = state.SelfModulate;
                state.Item.ZIndex = state.ZIndex;
                if (state.Item is Control control &&
                    state.Position is { } position &&
                    state.Size is { } size &&
                    state.Scale is { } scale &&
                    state.Rotation is { } rotation &&
                    state.PivotOffset is { } pivotOffset)
                {
                    control.Position = position;
                    control.Size = size;
                    control.Scale = scale;
                    control.Rotation = rotation;
                    control.PivotOffset = pivotOffset;
                }
                if (state.Item is TextureRect textureRect)
                {
                    if (state.Texture == null || GodotObject.IsInstanceValid(state.Texture))
                    {
                        textureRect.Texture = state.Texture;
                    }
                    if (state.ExpandMode is { } expandMode)
                    {
                        textureRect.ExpandMode = expandMode;
                    }
                    if (state.StretchMode is { } stretchMode)
                    {
                        textureRect.StretchMode = stretchMode;
                    }
                    if (state.FlipH is { } flipH)
                    {
                        textureRect.FlipH = flipH;
                    }
                    if (state.FlipV is { } flipV)
                    {
                        textureRect.FlipV = flipV;
                    }
                }
            }
        }
    }

    private sealed record CanvasItemState(
        CanvasItem Item,
        bool Visible,
        Material? Material,
        Color Modulate,
        Color SelfModulate,
        int ZIndex,
        Vector2? Position,
        Vector2? Size,
        Vector2? Scale,
        float? Rotation,
        Vector2? PivotOffset,
        Texture2D? Texture,
        TextureRect.ExpandModeEnum? ExpandMode,
        TextureRect.StretchModeEnum? StretchMode,
        bool? FlipH,
        bool? FlipV);

    private sealed record CardPresentationState(
        IReadOnlyList<Node> AddedNodes);
}

internal static class CardInspectSkinControls
{
    private const string SelectorName = "STS2IndividualCardSkinSelector";
    private const string DropdownName = "IndividualCardSkinDropdown";
    private const string UpdatingMeta = "sts2_individual_card_skin_updating";
    private static readonly System.Reflection.MethodInfo ReloadCardMethod =
        AccessTools.Method(typeof(NCard), "Reload");

    public static void Attach(NInspectCardScreen screen)
    {
        SkinService.InitializeCardGroupsAfterModels();
        VisualPatchGuard.RemoveProviderVisualPatches(ManagedSkinModLoader.ProviderRoots);
        if (screen.GetNodeOrNull<HBoxContainer>(SelectorName) != null)
        {
            return;
        }

        var selector = new HBoxContainer
        {
            Name = SelectorName,
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -154f,
            OffsetTop = -526f,
            OffsetRight = 154f,
            OffsetBottom = -478f,
            GrowHorizontal = Control.GrowDirection.Both,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 20,
            Visible = false
        };
        var dropdown = new OptionButton
        {
            Name = DropdownName,
            CustomMinimumSize = new Vector2(308, 48),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FitToLongestItem = false,
            ClipText = true,
            Alignment = HorizontalAlignment.Center
        };
        ContextualSkinControls.ApplyGameTheme(dropdown);
        dropdown.AddThemeFontSizeOverride("font_size", 20);
        dropdown.GetPopup().AddThemeFontSizeOverride("font_size", 20);
        dropdown.ItemSelected += index => ApplySelection(
            screen,
            selector,
            dropdown,
            checked((int)index));
        selector.AddChild(dropdown);
        screen.AddChild(selector);
        Sync(screen);
    }

    public static void Sync(NInspectCardScreen screen)
    {
        var selector = screen.GetNodeOrNull<HBoxContainer>(SelectorName);
        var card = screen.GetNodeOrNull<NCard>("Card")?.Model;
        if (selector == null || card == null)
        {
            return;
        }

        var dropdown = selector.GetNode<OptionButton>(DropdownName);
        var options = SkinService.GetCardOptions(card);
        if (options.Count == 0)
        {
            selector.Visible = false;
            dropdown.Clear();
            return;
        }

        selector.SetMeta(UpdatingMeta, true);
        dropdown.Clear();
        dropdown.TooltipText = "为这张卡单独选择卡图；跟随分类时使用卡牌总览中的设置";
        dropdown.AddItem("跟随分类");
        dropdown.SetItemMetadata(0, SkinService.InheritCardSelectionId);
        dropdown.AddItem("游戏原版");
        dropdown.SetItemMetadata(1, SkinCatalog.BaseOptionId);
        foreach (var option in options)
        {
            var index = dropdown.ItemCount;
            dropdown.AddItem(option.Name);
            dropdown.SetItemMetadata(index, option.Id);
        }

        var selected = SkinService.GetCardOverrideSelection(card);
        var selectedIndex = Enumerable.Range(0, dropdown.ItemCount)
            .FirstOrDefault(index => dropdown.GetItemMetadata(index).AsString()
                .Equals(selected, StringComparison.OrdinalIgnoreCase));
        dropdown.Select(selectedIndex);
        selector.SetMeta(UpdatingMeta, false);
        selector.Visible = true;
    }

    private static void ApplySelection(
        NInspectCardScreen screen,
        HBoxContainer selector,
        OptionButton dropdown,
        int index)
    {
        if (selector.GetMeta(UpdatingMeta, false).AsBool())
        {
            return;
        }

        var card = screen.GetNodeOrNull<NCard>("Card")?.Model;
        if (card == null || index < 0 || index >= dropdown.ItemCount)
        {
            return;
        }

        var optionId = dropdown.GetItemMetadata(index).AsString();
        if (!SkinService.ApplyCardSelection(card, optionId))
        {
            ModLog.Error($"单卡皮肤界面切换失败：{SkinService.LastError}");
            Sync(screen);
            return;
        }

        var cardId = card.Id.ToString();
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(screen))
            {
                return;
            }

            RefreshMatchingCards(screen.GetTree()?.Root, cardId);
            Sync(screen);
        }).CallDeferred();
    }

    private static void RefreshMatchingCards(Node? root, string cardId)
    {
        if (root == null)
        {
            return;
        }

        try
        {
            foreach (var card in Descendants(root).OfType<NCard>())
            {
                if (card.Model?.Id.ToString().Equals(
                        cardId,
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    ReloadCardMethod.Invoke(card, null);
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Error("刷新单卡皮肤失败：" + exception);
        }
    }

    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}

[HarmonyPatch(typeof(NCardLibrary), nameof(NCardLibrary._Ready))]
internal static class CardLibrarySkinReadyPatch
{
    private static void Postfix(NCardLibrary __instance) => CardSkinControls.Attach(__instance);
}

[HarmonyPatch(typeof(NInspectCardScreen), nameof(NInspectCardScreen._Ready))]
internal static class CardInspectSkinReadyPatch
{
    private static void Postfix(NInspectCardScreen __instance) =>
        CardInspectSkinControls.Attach(__instance);
}

[HarmonyPatch(typeof(NInspectCardScreen), "UpdateCardDisplay")]
internal static class CardInspectSkinDisplayPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NInspectCardScreen __instance) =>
        CardInspectSkinControls.Sync(__instance);
}

[HarmonyPatch(typeof(NCardLibrary), nameof(NCardLibrary.OnSubmenuOpened))]
internal static class CardLibrarySkinOpenedPatch
{
    private static void Prefix(NCardLibrary __instance) =>
        CardSkinControls.ResetAvailabilityFilter(__instance);

    private static void Postfix(NCardLibrary __instance) =>
        CardSkinControls.SyncToSelectedFilter(__instance);
}

[HarmonyPatch]
internal static class CardLibraryAvailabilityFilterPatch
{
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(
            typeof(NCardLibraryGrid),
            nameof(NCardLibraryGrid.FilterCards),
            [typeof(Func<CardModel, bool>), typeof(List<SortingOrders>)]);

    private static void Prefix(
        NCardLibraryGrid __instance,
        ref Func<CardModel, bool> filter) =>
        CardSkinControls.ApplyAvailabilityFilter(__instance, ref filter);
}

[HarmonyPatch(typeof(NCardLibrary), "UpdateCardPoolFilter")]
internal static class CardLibraryPoolSkinPatch
{
    private static void Postfix(NCardLibrary __instance, NCardPoolFilter filter) =>
        CardSkinControls.ShowForFilter(__instance, filter);
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.Portrait), MethodType.Getter)]
internal static class CardPortraitResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CardModel __instance, ref Texture2D __result) =>
        CardSkinControls.ReplacePortrait(__instance, ref __result);
}

[HarmonyPatch]
internal static class CardLayoutResetPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NCard), nameof(NCard._Ready));
        yield return AccessTools.Method(typeof(NCard), "Reload");
        yield return AccessTools.Method(typeof(NCard), nameof(NCard.UpdateVisuals));
    }

    [HarmonyPriority(Priority.First)]
    private static void Prefix(NCard __instance) =>
        CardSkinControls.RestoreBaselineLayout(__instance);
}

[HarmonyPatch]
internal static class CardLayoutFinalPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NCard), nameof(NCard._Ready));
        yield return AccessTools.Method(typeof(NCard), "Reload");
        yield return AccessTools.Method(typeof(NCard), nameof(NCard.UpdateVisuals));
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(
        NCard __instance,
        System.Reflection.MethodBase __originalMethod,
        object[] __args)
    {
        // 原方法刚刚创建的资源才是本轮有效基线；先重新捕获，再应用所选呈现。
        CardSkinControls.CaptureBaselineLayout(__instance);
        CardSkinControls.ReplaySelectedPresentation(__instance, __originalMethod, __args);
        CardSkinControls.ApplySelectedPortraitToNode(__instance);
    }
}
