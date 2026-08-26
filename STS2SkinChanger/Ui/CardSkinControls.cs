using Godot;
using HarmonyLib;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class CardSkinControls
{
    private const string SelectorName = "STS2CardSkinSelector";
    private const string PriorityButtonName = "CardSkinPriorityButton";
    private const string PriorityOverlayName = "STS2CardSkinPriorityOverlay";
    private const string PriorityPanelName = "Panel";
    private const string PriorityPanelMarginName = "Margin";
    private const string PriorityContentName = "PriorityContent";
    private const string AvailabilityFilterName = "STS2SkinnedCardsOnly";
    private const string AvailabilityFilterMeta = "sts2_skinned_cards_only";
    private const string GroupMeta = "sts2_card_skin_group";
    private const string SourceIndicatorName = "STS2CardSkinSources";
    private const string SourceSignatureMeta = "sts2_card_skin_source_signature";
    private const float CardVisualRightEdge = 150f;
    private const float CardVisualBottomEdge = 211f;
    private const string NormalTextBackgroundOverlayName = "STS2ManagedNormalTextBackgroundOverlay";
    private const string FullFrameOverlayName = "STS2ManagedFullFrameOverlay";
    private const string AncientBorderPath =
        "res://images/atlases/compressed.sprites/card_template/ancient_card_border.tres";
    private const string AncientBannerPath =
        "res://images/atlases/ui_atlas.sprites/card/ancient_banner.tres";
    private const string AncientBannerMaterialPath =
        "res://materials/cards/banners/card_banner_ancient_mat.tres";
    private static readonly System.Reflection.MethodInfo ReloadCardMethod =
        AccessTools.Method(typeof(NCard), "Reload");
    private static readonly System.Reflection.MethodInfo UpdateLibraryFilterMethod =
        AccessTools.Method(typeof(NCardLibrary), "UpdateFilter", [typeof(bool)]);
    private static readonly ConditionalWeakTable<NCard, CardLayoutState> BaselineLayouts = new();
    private static readonly ConditionalWeakTable<NCard, CardPresentationState> PresentationLayouts = new();
    private static readonly System.Reflection.FieldInfo? HighlightShaderMaterialField =
        AccessTools.Field(typeof(NCardHighlight), "_shaderMaterial");
    private static Texture2D? _normalTextBackgroundCoverTexture;

    public static void Attach(NCardLibrary screen)
    {
        SkinService.InitializeCardGroupsAfterModels();
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
        var priorityOverlay = CreatePriorityOverlay();
        screen.AddChild(priorityOverlay);
        priorityOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var priorityButton = new Button
        {
            Name = PriorityButtonName,
            CustomMinimumSize = new Vector2(256, 40),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Alignment = HorizontalAlignment.Center
        };
        ContextualSkinControls.ApplyGameTheme(priorityButton);
        priorityButton.AddThemeFontSizeOverride("font_size", 19);
        priorityButton.Pressed += () => OpenPriorityOverlay(screen, selector, priorityOverlay);
        selector.AddChild(priorityButton);

        bottom.AddChild(selector);
        bottom.MoveChild(selector, 0);

        NLibraryStatTickbox? availabilityFilter = null;
        var filterScene = ResourceLoader.Load<PackedScene>(
            "res://scenes/screens/card_library/card_library_tickbox.tscn");
        if (filterScene != null)
        {
            availabilityFilter = filterScene.Instantiate<NLibraryStatTickbox>(
                PackedScene.GenEditState.Disabled);
            availabilityFilter.Name = AvailabilityFilterName;
            bottom.AddChild(availabilityFilter);
            bottom.MoveChild(availabilityFilter, 1);
            availabilityFilter.SetLabel(ModLocalization.Get(ModText.SkinnedCardsOnly));
            availabilityFilter.IsTicked = false;
            availabilityFilter.Connect(
                NTickbox.SignalName.Toggled,
                Callable.From<NTickbox>(tickbox =>
                    SetAvailabilityFilter(screen, AvailabilityFilterMeta, tickbox.IsTicked)));
        }

        ShowFirstAvailableGroup(selector);
        ModLocalization.Bind(screen, () =>
        {
            availabilityFilter?.SetLabel(ModLocalization.Get(ModText.SkinnedCardsOnly));
            var groupId = selector.GetMeta(GroupMeta, string.Empty).AsString();
            if (string.IsNullOrWhiteSpace(groupId))
            {
                ShowFirstAvailableGroup(selector);
            }
            else
            {
                Populate(selector, groupId);
            }

            if (priorityOverlay.Visible)
            {
                BuildPriorityOverlay(screen, selector, priorityOverlay);
            }

            RefreshSourceIndicators(screen);
        });
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

    public static void UpdateLibrarySourceIndicators(NCard card)
    {
        if (card.Model == null ||
            HasAncestor<NInspectCardScreen>(card) ||
            !HasAncestor<NCardLibrary>(card))
        {
            RemoveSourceIndicators(card);
            return;
        }

        var sources = SkinService.GetCardSkinSources(card.Model);
        if (sources.Count == 0)
        {
            RemoveSourceIndicators(card);
            return;
        }

        var signature = ModLocalization.CurrentLanguage + "\n" +
                        string.Join("\n", sources.Select(source =>
                            $"{source.OptionId}:{source.Enabled}:{source.ColorIndex}:{source.IsCurrent}"));
        var indicatorParent = card.GetNodeOrNull<Control>("%CardContainer") ?? card;
        var existing = indicatorParent.GetNodeOrNull<Control>(SourceIndicatorName);
        if (existing != null &&
            existing.GetMeta(SourceSignatureMeta, string.Empty).AsString() == signature)
        {
            return;
        }

        RemoveSourceIndicators(card);
        var tooltip = BuildSourceTooltip(sources);
        const float tabHeight = 12f;
        const float tabBottomInset = 36f;
        var height = sources.Count * tabHeight;
        var bottom = CardVisualBottomEdge - tabBottomInset;
        var indicator = new VBoxContainer
        {
            Name = SourceIndicatorName,
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = tooltip,
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 0f,
            AnchorBottom = 0f,
            OffsetLeft = CardVisualRightEdge - 4,
            OffsetTop = bottom - height,
            OffsetRight = CardVisualRightEdge + 36,
            OffsetBottom = bottom
        };
        indicator.AddThemeConstantOverride("separation", 0);
        indicator.SetMeta(SourceSignatureMeta, signature);

        foreach (var source in sources.Reverse())
        {
            var color = SourceColor(source.ColorIndex);
            color.A = source.Enabled || source.IsCurrent ? 1f : 0.45f;
            var tab = new Panel
            {
                CustomMinimumSize = new Vector2(source.IsCurrent ? 22 : 15, tabHeight),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
                MouseFilter = Control.MouseFilterEnum.Stop,
                TooltipText = tooltip
            };
            tab.AddThemeStyleboxOverride("panel", CreateSourceTabStyle(color));
            indicator.AddChild(tab);
        }

        indicatorParent.AddChild(indicator);
        var shadow = indicatorParent.GetNodeOrNull<CanvasItem>("Shadow");
        if (shadow != null)
        {
            indicatorParent.MoveChild(indicator, shadow.GetIndex() + 1);
        }
    }

    private static string BuildSourceTooltip(IReadOnlyList<CardSkinSourceState> sources)
    {
        var lines = new List<string>();
        var current = sources.FirstOrDefault(source => source.IsCurrent);
        if (current != null)
        {
            lines.Add(string.Format(
                ModLocalization.Get(ModText.CurrentCardSource),
                ModLocalization.DisplayOptionName(current.Name)));
        }

        lines.Add(string.Format(
            ModLocalization.Get(ModText.AvailableCardSources),
            string.Join(" · ", sources.Select(source =>
                ModLocalization.DisplayOptionName(source.Name)))));
        return string.Join("\n", lines);
    }

    private static Color SourceColor(int index)
    {
        const float goldenRatioConjugate = 0.61803398875f;
        var hue = (0.12f + index * goldenRatioConjugate) % 1f;
        return Color.FromHsv(hue, 0.72f, 0.96f);
    }

    private static StyleBoxFlat CreateSourceTabStyle(Color color) => new()
    {
        BgColor = color,
        CornerRadiusTopLeft = 6,
        CornerRadiusTopRight = 6,
        CornerRadiusBottomRight = 6,
        CornerRadiusBottomLeft = 6
    };

    private static void RemoveSourceIndicators(NCard card)
    {
        var indicatorParent = card.GetNodeOrNull<Control>("%CardContainer") ?? card;
        var indicator = indicatorParent.GetNodeOrNull<Control>(SourceIndicatorName);
        if (indicator == null)
        {
            return;
        }

        indicator.GetParent()?.RemoveChild(indicator);
        indicator.QueueFree();
    }

    private static bool HasAncestor<T>(Node node)
        where T : Node
    {
        for (var current = node.GetParent(); current != null; current = current.GetParent())
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }

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
            // NCard comes from a node pool, and the beta combat queue also rebinds an existing
            // node to the network-resolved CardModel just before execution. Never restore a
            // previous model's portrait/frame state into the newly assigned model.
            if (state.BelongsTo(card.Model))
            {
                state.Restore();
            }
            else
            {
                BaselineLayouts.Remove(card);
            }
        }
    }

    public static void ReapplyQueuedCardPortraits(NCardPlayQueue queue)
    {
        foreach (var card in queue.GetChildren().OfType<NCard>())
        {
            if (!GodotObject.IsInstanceValid(card) || card.Model == null)
            {
                continue;
            }

            // v0.111 refreshes the portrait again while cards enter and execute from the play
            // queue. Reassert the exact per-card selection after that queue-owned refresh so a
            // pooled TextureRect cannot keep another card's art.
            ApplySelectedPortraitToNode(card, ExternalCardVisualBridge.GetOwnership(card.Model));
        }
    }

    public static void ApplySelectedPresentation(
        NCard card,
        ExternalCardVisualOwnership externalOwnership)
    {
        if (card.Model == null)
        {
            return;
        }

        // A live card editor owns the frame/layout layer only after the player has
        // explicitly configured that card there. Other cards remain managed here.
        if (externalOwnership.Frame)
        {
            PresentationLayouts.Remove(card);
            return;
        }

        var presentation = SkinService.GetCardPresentation(card.Model);
        if (presentation == null)
        {
            PresentationLayouts.Remove(card);
            return;
        }

        PresentationLayouts.Remove(card);
        ApplyManagedCardPresentation(card, presentation, externalOwnership.Text);
        if (BaselineLayouts.TryGetValue(card, out var baseline))
        {
            PresentationLayouts.Add(
                card,
                new CardPresentationState(baseline.FindAddedNodes(card)));
        }
    }

    private static void ApplyManagedCardPresentation(
        NCard card,
        CardPresentationDefinition presentation,
        bool preserveExternalText)
    {
        var portrait = card.GetNodeOrNull<TextureRect>("%Portrait");
        var portraitBorder = card.GetNodeOrNull<TextureRect>("%PortraitBorder");
        var frame = card.GetNodeOrNull<TextureRect>("%Frame");
        var banner = card.GetNodeOrNull<TextureRect>("%TitleBanner");
        var ancientPortrait = card.GetNodeOrNull<TextureRect>("%AncientPortrait");
        var ancientGlass = card.GetNodeOrNull<TextureRect>("%AncientBorderGlassOverlay");
        var ancientBorder = card.GetNodeOrNull<TextureRect>("%AncientBorder");
        var ancientTextBg = card.GetNodeOrNull<TextureRect>("%AncientTextBg");
        var ancientBanner = card.GetNodeOrNull<TextureRect>("%AncientBanner");
        var ancientHighlight = card.GetNodeOrNull<TextureRect>("%AncientHighlight") ??
                               FindNodeByName(card, "AncientHighlight") as TextureRect;
        var portraitCanvas = card.GetNodeOrNull<CanvasGroup>("%PortraitCanvasGroup");
        var energyIcon = card.GetNodeOrNull<TextureRect>("%EnergyIcon");
        var highlight = card.GetNodeOrNull<TextureRect>("%Highlight");
        var fire = FindNodeByName(card, "Fire") as AnimatedSprite2D;
        if (presentation.UseFullFrameArt)
        {
            ApplyManagedFullFrameArt(
                card,
                presentation,
                portrait,
                portraitBorder,
                frame,
                banner,
                ancientPortrait,
                ancientGlass,
                ancientBorder,
                ancientTextBg,
                ancientBanner);
            return;
        }

        var useAncientLayout = presentation.UseAncientLayout ||
                               card.Model!.Rarity == CardRarity.Ancient;

        if (useAncientLayout)
        {
            SetVisible(portrait, false);
            SetVisible(portraitBorder, false);
            SetVisible(frame, false);
            SetVisible(banner, false);
            SetVisible(ancientPortrait, true);
            SetVisible(ancientGlass, true);
            SetVisible(ancientBorder, true);
            SetVisible(ancientTextBg, true);
            SetVisible(ancientBanner, true);
            if (fire != null)
            {
                fire.Visible = true;
                fire.Play();
            }

            if (ancientBorder is { Texture: null } border)
            {
                border.Texture = SkinService.LoadCardPresentationResource<Texture2D>(
                    card.Model!,
                    AncientBorderPath);
            }
            if (ancientBanner is { Texture: null } ancientBannerWithoutTexture)
            {
                ancientBannerWithoutTexture.Texture = SkinService.LoadCardPresentationResource<Texture2D>(
                    card.Model!,
                    AncientBannerPath);
            }
            if (ancientBanner is { Material: null } ancientBannerWithoutMaterial)
            {
                ancientBannerWithoutMaterial.Material = SkinService.LoadCardPresentationResource<Material>(
                    card.Model!,
                    AncientBannerMaterialPath);
            }
            if (ancientTextBg is { Texture: null } textBackground)
            {
                textBackground.Texture = SkinService.LoadCardPresentationResource<Texture2D>(
                    card.Model!,
                    DefaultAncientTextBackgroundPath(card.Model!));
            }
            if (portraitCanvas != null)
            {
                var maskMaterialPath = card.Visibility == ModelVisibility.Visible
                    ? "res://scenes/cards/card_canvas_group_mask_material.tres"
                    : "res://scenes/cards/card_canvas_group_mask_blur_material.tres";
                portraitCanvas.Material = SkinService.LoadCardPresentationResource<Material>(
                    card.Model!,
                    maskMaterialPath);
            }
        }

        ApplyTexture(card, frame, presentation.Frame);
        ApplyTexture(card, ancientBorder, presentation.Frame);
        ApplyMaterial(card, frame, presentation.FrameMaterial);
        ApplyMaterial(card, ancientBorder, presentation.FrameMaterial);
        ApplyTexture(card, banner, presentation.BannerTexture);
        ApplyTexture(card, ancientBanner, presentation.BannerTexture);
        ApplyMaterial(card, banner, presentation.BannerMaterial);
        ApplyMaterial(card, ancientBanner, presentation.BannerMaterial);
        ApplyTexture(card, portraitBorder, presentation.PortraitBorder);
        ApplyMaterial(card, portraitBorder, presentation.PortraitBorderMaterial);
        ApplyTexture(card, ancientTextBg, presentation.AncientTextBackground);
        ApplyMaterial(card, ancientTextBg, presentation.TextBackgroundMaterial);
        ApplyTexture(card, energyIcon, presentation.EnergyIcon);
        ApplyTexture(card, highlight, presentation.Highlight);
        ApplyTexture(card, ancientHighlight, presentation.Highlight);
        ApplyMaterial(card, highlight, presentation.HighlightMaterial);
        ApplyMaterial(card, ancientHighlight, presentation.HighlightMaterial);

        if (presentation.FrameVisible is { } frameVisible)
        {
            SetVisible(frame, frameVisible && !useAncientLayout);
            SetVisible(ancientBorder, frameVisible && useAncientLayout);
        }
        if (presentation.BannerVisible is { } bannerVisible)
        {
            SetVisible(banner, bannerVisible && !useAncientLayout);
            SetVisible(ancientBanner, bannerVisible && useAncientLayout);
        }
        if (useAncientLayout)
        {
            SetVisible(ancientTextBg, presentation.TextBackgroundVisible);
        }
        else
        {
            ApplyNormalTextBackground(card, presentation);
        }
        if (presentation.PortraitBorderVisible is { } portraitBorderVisible)
        {
            SetVisible(portraitBorder, portraitBorderVisible && !useAncientLayout);
        }
        if (presentation.PortraitVisible is { } portraitVisible)
        {
            SetVisible(portrait, portraitVisible && !useAncientLayout);
            SetVisible(ancientPortrait, portraitVisible && useAncientLayout);
        }
        SetVisible(energyIcon, presentation.EnergyIconVisible);
        if (presentation.HighlightVisible is { } highlightVisible)
        {
            SetVisible(highlight, highlightVisible && !useAncientLayout);
            SetVisible(ancientHighlight, highlightVisible && useAncientLayout);
        }
        if (!preserveExternalText)
        {
            ApplyTypePlaqueVisibility(card, presentation);
            SetVisible(
                card.GetNodeOrNull<CanvasItem>("%DescriptionLabel"),
                presentation.DescriptionVisible);
        }
        SetVisible(
            FindNodeByName(card, "Infection") as CanvasItem,
            presentation.InfectionOverlayVisible);
    }

    private static void ApplyManagedFullFrameArt(
        NCard card,
        CardPresentationDefinition presentation,
        TextureRect? portrait,
        TextureRect? portraitBorder,
        TextureRect? frame,
        TextureRect? banner,
        TextureRect? ancientPortrait,
        TextureRect? ancientGlass,
        TextureRect? ancientBorder,
        TextureRect? ancientTextBg,
        TextureRect? ancientBanner)
    {
        ApplyTexture(card, frame, presentation.Frame);
        if (frame != null)
        {
            frame.Material = null;
        }

        SetVisible(frame, presentation.FrameVisible ?? true);
        SetVisible(portrait, presentation.PortraitVisible ?? false);
        SetVisible(portraitBorder, presentation.PortraitBorderVisible ?? false);
        SetVisible(ancientPortrait, false);
        SetVisible(ancientGlass, false);
        SetVisible(ancientBanner, false);
        SetVisible(banner, presentation.BannerVisible ?? true);

        var isNativeAncient = card.Model!.Rarity == CardRarity.Ancient;
        SetVisible(ancientBorder, isNativeAncient);
        SetVisible(ancientTextBg, presentation.TextBackgroundVisible ?? true);
        if (ancientTextBg != null)
        {
            ancientTextBg.Texture = SkinService.LoadCardPresentationResource<Texture2D>(
                card.Model,
                presentation.AncientTextBackground ?? DefaultAncientTextBackgroundPath(card.Model));
        }

        var parent = ancientBorder?.GetParent();
        TextureRect? overlay = null;
        if (!isNativeAncient &&
            parent != null &&
            !string.IsNullOrWhiteSpace(presentation.FrameOverlay))
        {
            overlay = new TextureRect
            {
                Name = FullFrameOverlayName,
                Texture = SkinService.LoadCardPresentationResource<Texture2D>(
                    card.Model,
                    presentation.FrameOverlay),
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ZIndex = ancientBorder!.ZIndex,
                AnchorLeft = 0.5f,
                AnchorTop = 0.5f,
                AnchorRight = 0.5f,
                AnchorBottom = 0.5f,
                GrowHorizontal = Control.GrowDirection.Both,
                GrowVertical = Control.GrowDirection.Both,
                PivotOffset = new Vector2(CardVisualRightEdge, CardVisualBottomEdge),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                OffsetTop = presentation.FrameOverlayOffsetTop ?? -CardVisualBottomEdge,
                OffsetBottom = presentation.FrameOverlayOffsetBottom ?? CardVisualBottomEdge,
                OffsetLeft = presentation.FrameOverlayOffsetLeft ?? -CardVisualRightEdge,
                OffsetRight = presentation.FrameOverlayOffsetRight ?? CardVisualRightEdge,
                Scale = new Vector2(
                    presentation.FrameOverlayScaleX ?? 1f,
                    presentation.FrameOverlayScaleY ?? 1f)
            };
            parent.AddChild(overlay);
        }

        if (parent == null)
        {
            return;
        }

        foreach (var node in new Node?[]
                 {
                     ancientBorder,
                     ancientTextBg,
                     overlay,
                     card.GetNodeOrNull<Node>("%TypePlaque"),
                     card.GetNodeOrNull<Node>("%DescriptionLabel"),
                     banner,
                     FindNodeByName(card, "TitleLabel"),
                     card.GetNodeOrNull<Node>("%EnergyIcon"),
                     FindNodeByName(card, "StarIcon")
                 })
        {
            if (node?.GetParent() == parent)
            {
                parent.MoveChild(node, parent.GetChildCount() - 1);
            }
        }
    }

    private static string DefaultAncientTextBackgroundPath(CardModel card)
    {
        var type = card.Type switch
        {
            CardType.Attack => "attack",
            CardType.Power => "power",
            _ => "skill"
        };
        return $"res://images/atlases/compressed.sprites/card_template/ancient_card_text_bg_{type}.tres";
    }

    private static void ApplyTexture(
        NCard card,
        TextureRect? target,
        string? resourcePath)
    {
        if (target != null && !string.IsNullOrWhiteSpace(resourcePath))
        {
            var texture = SkinService.LoadCardPresentationResource<Texture2D>(
                card.Model!,
                resourcePath);
            if (texture != null)
            {
                target.Texture = texture;
            }
        }
    }

    private static void ApplyMaterial(
        NCard card,
        CanvasItem? target,
        string? resourcePath)
    {
        if (target != null && !string.IsNullOrWhiteSpace(resourcePath))
        {
            var material = SkinService.LoadCardPresentationResource<Material>(
                card.Model!,
                resourcePath);
            if (material != null)
            {
                target.Material = material;
                SyncHighlightShaderMaterial(target, material);
            }
        }
    }

    private static void ApplyNormalTextBackground(
        NCard card,
        CardPresentationDefinition presentation)
    {
        if (presentation.AncientTextBackground == null &&
            presentation.TextBackgroundMaterial == null &&
            presentation.TextBackgroundVisible == null)
        {
            return;
        }

        var body = card.Body;
        var description = card.GetNodeOrNull<Control>("%DescriptionLabel");
        if (body == null || description == null)
        {
            return;
        }

        var overlay = body.GetNodeOrNull<TextureRect>(NormalTextBackgroundOverlayName);
        if (overlay == null)
        {
            overlay = new TextureRect
            {
                Name = NormalTextBackgroundOverlayName,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                TextureFilter = CanvasItem.TextureFilterEnum.Linear,
                Visible = false
            };
            body.AddChild(overlay);
            body.MoveChild(overlay, Math.Max(0, description.GetIndex()));
        }

        var plaque = card.GetNodeOrNull<Control>("%TypePlaque");
        var left = description.Position.X - 14f;
        var top = description.Position.Y - 18f;
        var right = description.Position.X + description.Size.X + 14f;
        var bottom = description.Position.Y + description.Size.Y + 12f;
        if (plaque != null)
        {
            left = MathF.Min(left, plaque.Position.X - 18f);
            top = MathF.Min(top, plaque.Position.Y - 18f);
            right = MathF.Max(right, plaque.Position.X + plaque.Size.X + 18f);
        }
        overlay.Position = new Vector2(left, top);
        overlay.Size = new Vector2(MathF.Max(1f, right - left), MathF.Max(1f, bottom - top));

        var visible = presentation.TextBackgroundVisible ?? true;
        overlay.Texture = visible && presentation.AncientTextBackground != null
            ? SkinService.LoadCardPresentationResource<Texture2D>(
                card.Model!,
                presentation.AncientTextBackground)
            : visible
                ? null
                : GetNormalTextBackgroundCoverTexture();
        overlay.Material = visible && presentation.TextBackgroundMaterial != null
            ? SkinService.LoadCardPresentationResource<Material>(
                card.Model!,
                presentation.TextBackgroundMaterial)
            : null;
        overlay.Visible = overlay.Texture != null;
    }

    private static Texture2D GetNormalTextBackgroundCoverTexture()
    {
        if (_normalTextBackgroundCoverTexture != null &&
            GodotObject.IsInstanceValid(_normalTextBackgroundCoverTexture))
        {
            return _normalTextBackgroundCoverTexture;
        }

        using var image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
        image.Fill(new Color(0.11f, 0.10f, 0.08f, 0.92f));
        _normalTextBackgroundCoverTexture = ImageTexture.CreateFromImage(image);
        return _normalTextBackgroundCoverTexture;
    }

    private static void ApplyTypePlaqueVisibility(
        NCard card,
        CardPresentationDefinition presentation)
    {
        if (presentation.TypePlaqueVisible == null && presentation.TypeLabelVisible == null)
        {
            return;
        }

        var plaque = card.GetNodeOrNull<Control>("%TypePlaque");
        var label = card.GetNodeOrNull<CanvasItem>("%TypeLabel");
        var plaqueVisible = presentation.TypePlaqueVisible ?? true;
        var labelVisible = presentation.TypeLabelVisible ?? true;
        if (plaque != null)
        {
            plaque.Visible = plaqueVisible || labelVisible;
            plaque.SelfModulate = plaqueVisible
                ? Colors.White
                : new Color(1f, 1f, 1f, 0f);
        }
        SetVisible(label, labelVisible);
    }

    private static Node? FindNodeByName(Node root, string name)
    {
        foreach (var child in root.GetChildren())
        {
            if (child.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
            var nested = FindNodeByName(child, name);
            if (nested != null)
            {
                return nested;
            }
        }
        return null;
    }

    private static void SyncHighlightShaderMaterial(CanvasItem target, Material? material)
    {
        if (target is NCardHighlight highlight && material is ShaderMaterial shaderMaterial)
        {
            HighlightShaderMaterialField?.SetValue(highlight, shaderMaterial);
        }
    }

    private static void SetVisible(CanvasItem? target, bool? visible)
    {
        if (target != null && visible is { } value)
        {
            target.Visible = value;
        }
    }

    public static void ApplySelectedPortraitToNode(
        NCard card,
        ExternalCardVisualOwnership externalOwnership)
    {
        if (card.Model == null)
        {
            return;
        }

        // Do not overwrite a still-running editor's explicit static/GIF portrait.
        if (externalOwnership.Portrait)
        {
            return;
        }

        // CardModel.Portrait 的 getter 已被 Priority.Last 的 Postfix 接管，这里拿到的已是换肤后的贴图。
        var portrait = card.Model.Portrait;
        var targetPath = card.Model.Rarity == CardRarity.Ancient ||
                         SkinService.GetCardPresentation(card.Model)?.UseAncientLayout == true
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
        var availabilityFilter = screen.GetNodeOrNull<NLibraryStatTickbox>(
            $"Sidebar/MarginContainer/BottomVBox/{AvailabilityFilterName}");
        if (availabilityFilter != null)
        {
            availabilityFilter.IsTicked = false;
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

        if (current is not NCardLibrary library)
        {
            return;
        }

        var skinsOnly = library.GetMeta(AvailabilityFilterMeta, false).AsBool();
        if (!skinsOnly)
        {
            return;
        }

        var original = filter;
        filter = card => original(card) && SkinService.HasCardSkin(card);
    }

    private static void SetAvailabilityFilter(NCardLibrary screen, string meta, bool enabled)
    {
        screen.SetMeta(meta, enabled);
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
        var button = selector.GetNode<Button>(PriorityButtonName);
        var group = groupId == null ? null : FindGroup(groupId);
        if (group == null || group.Options.Count == 0)
        {
            selector.Visible = false;
            button.Text = string.Empty;
            return;
        }

        selector.SetMeta(GroupMeta, group.Id);
        var options = SkinService.GetCardPriorityOptions(group.Id);
        button.Text = string.Format(
            ModLocalization.Get(ModText.CardSkinPriority),
            options.Count(option => option.Enabled));
        button.TooltipText = ModLocalization.Get(ModText.CardPriorityTooltip);
        selector.Visible = true;
    }

    private static Control CreatePriorityOverlay()
    {
        var overlay = new Control
        {
            Name = PriorityOverlayName,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 2000
        };
        var mask = new ColorRect
        {
            Name = "Mask",
            Color = new Color(0f, 0f, 0f, 0.68f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        mask.GuiInput += input =>
        {
            if (input is not InputEventMouseButton
                {
                    Pressed: true,
                    ButtonIndex: MouseButton.Left
                })
            {
                return;
            }

            overlay.Visible = false;
            mask.AcceptEvent();
        };
        overlay.AddChild(mask);
        mask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var panel = new PanelContainer
        {
            Name = PriorityPanelName,
            MouseFilter = Control.MouseFilterEnum.Stop,
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -360,
            OffsetTop = -240,
            OffsetRight = 360,
            OffsetBottom = 240
        };
        panel.AddThemeStyleboxOverride(
            "panel",
            ContextualSkinControls.CreateStyleBox(new Color("241a30"), new Color("79547e"), 2));
        overlay.AddChild(panel);
        var margin = new MarginContainer { Name = PriorityPanelMarginName };
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(margin);
        var content = new VBoxContainer { Name = PriorityContentName };
        content.AddThemeConstantOverride("separation", 10);
        margin.AddChild(content);
        return overlay;
    }

    private static void OpenPriorityOverlay(
        NCardLibrary screen,
        HBoxContainer selector,
        Control overlay)
    {
        BuildPriorityOverlay(screen, selector, overlay);
        overlay.Visible = true;
        overlay.MoveToFront();
    }

    private static void BuildPriorityOverlay(
        NCardLibrary screen,
        HBoxContainer selector,
        Control overlay)
    {
        var groupId = selector.GetMeta(GroupMeta, string.Empty).AsString();
        var group = FindGroup(groupId);
        var content = overlay.GetNode<VBoxContainer>(
            $"{PriorityPanelName}/{PriorityPanelMarginName}/{PriorityContentName}");
        foreach (var child in content.GetChildren())
        {
            content.RemoveChild(child);
            child.QueueFree();
        }

        if (group == null)
        {
            return;
        }

        var title = new Label
        {
            Text = string.Format(
                ModLocalization.Get(ModText.CardSkinPriority),
                SkinService.GetCardPriorityOptions(groupId).Count(option => option.Enabled)),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        title.AddThemeFontSizeOverride("font_size", 25);
        title.AddThemeColorOverride("font_color", new Color("efc850"));
        content.AddChild(title);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(670, 350),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        content.AddChild(scroll);
        var rows = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        rows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(rows);

        var options = SkinService.GetCardPriorityOptions(groupId);
        foreach (var option in options)
        {
            var row = new HBoxContainer
            {
                CustomMinimumSize = new Vector2(650, 42),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            row.AddThemeConstantOverride("separation", 8);
            rows.AddChild(row);

            row.AddChild(new ColorRect
            {
                Color = SourceColor(option.ColorIndex),
                CustomMinimumSize = new Vector2(13, 32),
                MouseFilter = Control.MouseFilterEnum.Ignore
            });
            var enabled = new CheckBox
            {
                ButtonPressed = option.Enabled,
                Text = ModLocalization.Get(ModText.EnabledForCategory),
                CustomMinimumSize = new Vector2(88, 32),
                TooltipText = ModLocalization.DisplayOptionName(option.Name)
            };
            ContextualSkinControls.ApplyGameTheme(enabled);
            enabled.AddThemeFontSizeOverride("font_size", 17);
            enabled.AddThemeColorOverride("font_color", new Color("fff6e2"));
            enabled.AddThemeColorOverride("font_hover_color", Colors.White);
            enabled.Toggled += value => QueuePriorityChange(
                screen,
                selector,
                overlay,
                groupId,
                () => SkinService.SetCardPriorityEnabled(groupId, option.OptionId, value));
            row.AddChild(enabled);

            var name = new Label
            {
                Text = ModLocalization.DisplayOptionName(option.Name),
                ClipText = true,
                CustomMinimumSize = new Vector2(270, 36),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
                TooltipText = ModLocalization.DisplayOptionName(option.Name)
            };
            name.AddThemeFontSizeOverride("font_size", 18);
            name.AddThemeColorOverride("font_color", new Color("fff6e2"));
            row.AddChild(name);

            var coverage = new Label
            {
                Text = $"{option.Coverage}/{option.TotalCards}",
                CustomMinimumSize = new Vector2(70, 36),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            coverage.AddThemeFontSizeOverride("font_size", 16);
            coverage.AddThemeColorOverride("font_color", new Color("afcdde"));
            row.AddChild(coverage);

            var up = new Button
            {
                Text = "↑",
                Disabled = option == options[0],
                CustomMinimumSize = new Vector2(42, 34)
            };
            ContextualSkinControls.ApplyGameTheme(up);
            up.AddThemeFontSizeOverride("font_size", 18);
            up.Pressed += () => QueuePriorityChange(
                screen,
                selector,
                overlay,
                groupId,
                () => SkinService.MoveCardPriority(groupId, option.OptionId, -1));
            row.AddChild(up);

            var down = new Button
            {
                Text = "↓",
                Disabled = option == options[^1],
                CustomMinimumSize = new Vector2(42, 34)
            };
            ContextualSkinControls.ApplyGameTheme(down);
            down.AddThemeFontSizeOverride("font_size", 18);
            down.Pressed += () => QueuePriorityChange(
                screen,
                selector,
                overlay,
                groupId,
                () => SkinService.MoveCardPriority(groupId, option.OptionId, 1));
            row.AddChild(down);
        }

        var close = new Button
        {
            Text = ModLocalization.Get(ModText.Close),
            CustomMinimumSize = new Vector2(180, 42),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter
        };
        ContextualSkinControls.ApplyGameTheme(close);
        close.AddThemeFontSizeOverride("font_size", 19);
        close.Pressed += () => overlay.Visible = false;
        content.AddChild(close);
    }

    private static void QueuePriorityChange(
        NCardLibrary screen,
        HBoxContainer selector,
        Control overlay,
        string groupId,
        Func<bool> change)
    {
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(screen) || !change())
            {
                ModLog.Error($"调整卡牌皮肤优先级失败：{SkinService.LastError}");
                return;
            }

            Populate(selector, groupId);
            BuildPriorityOverlay(screen, selector, overlay);
            RefreshVisibleCards(screen, groupId);
        }).CallDeferred();
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

    private static void RefreshSourceIndicators(NCardLibrary screen)
    {
        foreach (var card in Descendants(screen).OfType<NCard>())
        {
            UpdateLibrarySourceIndicators(card);
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
        CardModel? model,
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
            "%AncientHighlight",
            "%PortraitCanvasGroup",
            "%EnergyIcon",
            "%Highlight",
            "%TypePlaque",
            "%TypeLabel",
            "%DescriptionLabel"
        ];

        public static CardLayoutState Capture(NCard card)
        {
            var states = NodePaths
                .Select(path => card.GetNodeOrNull<CanvasItem>(path))
                .Concat(Descendants(card)
                    .OfType<CanvasItem>()
                    .Where(item =>
                        item.Name.ToString().Contains(
                            "Infection",
                            StringComparison.OrdinalIgnoreCase) ||
                        item.Name.ToString().Equals(
                            "Fire",
                            StringComparison.OrdinalIgnoreCase)))
                .Where(item => item != null)
                .Cast<CanvasItem>()
                .DistinctBy(item => item.GetInstanceId())
                .Select(item => new CanvasItemState(
                    item,
                    item.Visible,
                    item.Material,
                    item.Modulate,
                    item.SelfModulate,
                    item.ZIndex,
                    (item as TextureRect)?.Texture,
                    (item as TextureRect)?.ExpandMode,
                    (item as TextureRect)?.StretchMode,
                    (item as TextureRect)?.FlipH,
                    (item as TextureRect)?.FlipV,
                    (item as AnimatedSprite2D)?.IsPlaying()))
                .ToArray();
            var nodeIds = Descendants(card)
                .Select(node => node.GetInstanceId())
                .ToHashSet();
            return new CardLayoutState(card.Model, states, nodeIds);
        }

        public bool BelongsTo(CardModel? currentModel) => ReferenceEquals(model, currentModel);

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
                if (state.Item is AnimatedSprite2D animated && state.WasPlaying is { } wasPlaying)
                {
                    if (wasPlaying)
                    {
                        animated.Play();
                    }
                    else
                    {
                        animated.Stop();
                    }
                }
                SyncHighlightShaderMaterial(state.Item, state.Material);
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
        Texture2D? Texture,
        TextureRect.ExpandModeEnum? ExpandMode,
        TextureRect.StretchModeEnum? StretchMode,
        bool? FlipH,
        bool? FlipV,
        bool? WasPlaying);

    private sealed record CardPresentationState(
        IReadOnlyList<Node> AddedNodes);
}

internal static class CardInspectSkinControls
{
    private const string SelectorName = "STS2IndividualCardSkinSelector";
    private const string DropdownName = "IndividualCardSkinDropdown";
    private const string UpdatingMeta = "sts2_individual_card_skin_updating";
    private const string PreviewCardMeta = "sts2_individual_card_skin_preview_card";
    private const string PreviewOptionMeta = "sts2_individual_card_skin_preview_option";
    private static readonly System.Reflection.MethodInfo ReloadCardMethod =
        AccessTools.Method(typeof(NCard), "Reload");

    public static void Attach(NInspectCardScreen screen)
    {
        SkinService.InitializeCardGroupsAfterModels();
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
        var popup = dropdown.GetPopup();
        popup.AddThemeFontSizeOverride("font_size", 20);
        popup.IdFocused += id => PreviewSelection(
            screen,
            dropdown,
            popup.GetItemIndex(checked((int)id)));
        popup.WindowInput += inputEvent =>
        {
            if (inputEvent is not InputEventMouseMotion)
            {
                return;
            }

            // PopupMenu updates its focused row during the same mouse event. Read it on the next
            // idle step so mouse hover and keyboard focus share the same preview path.
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(screen) &&
                    GodotObject.IsInstanceValid(popup) &&
                    popup.Visible)
                {
                    PreviewSelection(screen, dropdown, popup.GetFocusedItem());
                }
            }).CallDeferred();
        };
        popup.PopupHide += () => RestorePreview(screen);
        dropdown.ItemSelected += index => ApplySelection(
            screen,
            selector,
            dropdown,
            checked((int)index));
        selector.AddChild(dropdown);
        screen.AddChild(selector);
        ModLocalization.Bind(screen, () => Sync(screen));
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
        dropdown.TooltipText = ModLocalization.Get(ModText.IndividualCardTooltip);
        dropdown.AddItem(ModLocalization.Get(ModText.FollowCategory));
        dropdown.SetItemMetadata(0, SkinService.InheritCardSelectionId);
        dropdown.AddItem(ModLocalization.Get(ModText.GameOriginal));
        dropdown.SetItemMetadata(1, SkinCatalog.BaseOptionId);
        foreach (var option in options)
        {
            var index = dropdown.ItemCount;
            dropdown.AddItem(ModLocalization.DisplayOptionName(option.Name));
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

    private static void PreviewSelection(
        NInspectCardScreen screen,
        OptionButton dropdown,
        int index)
    {
        if (index < 0 || index >= dropdown.ItemCount)
        {
            RestorePreview(screen);
            return;
        }

        var cardNode = screen.GetNodeOrNull<NCard>("Card");
        var card = cardNode?.Model;
        if (cardNode == null || card == null)
        {
            return;
        }

        var cardId = card.Id.ToString();
        var optionId = dropdown.GetItemMetadata(index).AsString();
        if (screen.GetMeta(PreviewCardMeta, string.Empty).AsString().Equals(
                cardId,
                StringComparison.OrdinalIgnoreCase) &&
            screen.GetMeta(PreviewOptionMeta, string.Empty).AsString().Equals(
                optionId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        screen.SetMeta(PreviewCardMeta, cardId);
        screen.SetMeta(PreviewOptionMeta, optionId);
        try
        {
            SkinService.WithCardPreviewSelection(
                card,
                optionId,
                () => ReloadCardMethod.Invoke(cardNode, null));
        }
        catch (Exception exception)
        {
            screen.RemoveMeta(PreviewCardMeta);
            screen.RemoveMeta(PreviewOptionMeta);
            ModLog.Error("预览单卡皮肤失败：" + exception);
        }
    }

    private static void RestorePreview(NInspectCardScreen screen)
    {
        if (!screen.HasMeta(PreviewOptionMeta))
        {
            return;
        }

        var previewCardId = screen.GetMeta(PreviewCardMeta, string.Empty).AsString();
        screen.RemoveMeta(PreviewCardMeta);
        screen.RemoveMeta(PreviewOptionMeta);

        var cardNode = screen.GetNodeOrNull<NCard>("Card");
        if (cardNode?.Model?.Id.ToString().Equals(
                previewCardId,
                StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        try
        {
            ReloadCardMethod.Invoke(cardNode, null);
        }
        catch (Exception exception)
        {
            ModLog.Error("恢复单卡皮肤预览失败：" + exception);
        }
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

[HarmonyPatch(typeof(NCardLibraryGrid), "InitGrid")]
internal static class CardLibraryInitialPortraitPreloadPatch
{
    private const int InitialCardPreloadCount = 36;

    [HarmonyPriority(Priority.First)]
    private static void Prefix(NCardLibraryGrid __instance) =>
        SkinService.PreloadCardPortraits(
            __instance.VisibleCards.Take(InitialCardPreloadCount));
}

[HarmonyPatch(typeof(NCardLibraryGrid), "AssignCardsToRow")]
internal static class CardLibraryRowPortraitPreloadPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(
        NCardLibraryGrid __instance,
        List<NGridCardHolder> row,
        int startIndex) =>
        SkinService.PreloadCardPortraits(
            __instance.VisibleCards.Skip(startIndex).Take(row.Count));
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
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(typeof(NCard), "Reload");

    [HarmonyPriority(Priority.First)]
    private static void Prefix(NCard __instance) =>
        CardSkinControls.RestoreBaselineLayout(__instance);
}

[HarmonyPatch]
internal static class CardLayoutBaselineCapturePatch
{
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(typeof(NCard), "Reload");

    [HarmonyPriority(Priority.First)]
    private static void Postfix(NCard __instance) =>
        CardSkinControls.CaptureBaselineLayout(__instance);
}

[HarmonyPatch]
internal static class CardLayoutFinalPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NCard), "Reload");
        yield return AccessTools.Method(typeof(NCard), nameof(NCard.UpdateVisuals));
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCard __instance)
    {
        // 基线已在 Priority.First 的 Postfix 中捕获，避免把其他 Mod 后置
        // 修改过的卡框误当成原版。这里最后只做当前所有者的呈现。
        var externalOwnership = __instance.Model == null
            ? default
            : ExternalCardVisualBridge.GetOwnership(__instance.Model);
        CardSkinControls.ApplySelectedPresentation(__instance, externalOwnership);
        CardSkinControls.ApplySelectedPortraitToNode(__instance, externalOwnership);
        CardSkinControls.UpdateLibrarySourceIndicators(__instance);
    }
}

[HarmonyPatch(typeof(NCardPlayQueue), nameof(NCardPlayQueue.OnLocalCardPlayed))]
internal static class LocalCardPlayQueuePortraitPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCardPlayQueue __instance) =>
        CardSkinControls.ReapplyQueuedCardPortraits(__instance);
}

[HarmonyPatch(typeof(NCardPlayQueue), nameof(NCardPlayQueue.UpdateCardBeforeExecution))]
internal static class CardPlayQueueRebindPortraitPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCardPlayQueue __instance) =>
        CardSkinControls.ReapplyQueuedCardPortraits(__instance);
}
