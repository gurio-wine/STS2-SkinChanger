using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.Bestiary;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static partial class ContextualSkinControls
{
    private const string SelectorName = "STS2SkinSelector";
    private const string MultiplayerSkinSyncToggleName = "MultiplayerSkinSyncToggle";
    private const string MultiplayerSkinLoadingToggleName = "MultiplayerSkinLoadingToggle";
    private const string DropdownName = "SkinDropdown";
    private const string MonsterScaleSliderName = "MonsterScaleSlider";
    private const string MonsterScaleValueName = "MonsterScaleValue";
    private const string MonsterScaleLabelName = "MonsterScaleLabel";
    private const string MonsterScaleResetName = "MonsterScaleReset";
    private const string CharacterRefreshGenerationMeta = "sts2_skin_character_refresh_generation";
    private const string CharacterLoadingGenerationMeta = "sts2_skin_character_loading_generation";
    private const string CharacterLoadingOverlayName = "STS2CharacterSkinLoadingOverlay";
    private const string CharacterBundlePopupListName = "STS2CharacterBundlePopupList";
    private const string GroupMeta = "sts2_skin_group";
    private const string UpdatingMeta = "sts2_skin_updating";
    private const string MonsterScaleGroupMeta = "sts2_skin_monster_scale_group";
    private const string MonsterBaseScaleMeta = "sts2_skin_monster_base_scale";
    private const string MonsterBaseDefaultScaleMeta = "sts2_skin_monster_base_default_scale";
    private const string MonsterAppliedScaleMeta = "sts2_skin_monster_applied_scale";
    private static readonly Dictionary<ulong, Action> RefreshActions = [];
    private static readonly ConditionalWeakTable<NCharacterSelectScreen, CharacterBackgroundHostLayout>
        CharacterBackgroundHostLayouts = new();
    private static readonly HashSet<string> LoggedMissingMultiplayerIcons =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _refreshingMonsterDisplay;
    private static readonly ReloadingReferenceCache<Font> GameFontCache = new();

    internal static bool IsRefreshingMonsterDisplay => _refreshingMonsterDisplay;

    internal static Font? GameFont => GameFontCache.Get(
        static () => ResourceLoader.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres"),
        static font => GodotObject.IsInstanceValid(font));

    private static readonly System.Reflection.FieldInfo BestiarySelectedEntryField =
        AccessTools.Field(typeof(NBestiary), "_selectedEntry");
    private static readonly System.Reflection.MethodInfo BestiarySelectMonsterMethod =
        AccessTools.Method(typeof(NBestiary), "SelectMonster", [typeof(NBestiaryEntry)]);
    private static readonly System.Reflection.MethodInfo MonsterVisualsPathGetter =
        AccessTools.PropertyGetter(typeof(MonsterModel), "VisualsPath");
    private static readonly System.Reflection.FieldInfo CharacterNameField =
        AccessTools.Field(typeof(NCharacterSelectScreen), "_name");
    private static readonly System.Reflection.FieldInfo CharacterDescriptionField =
        AccessTools.Field(typeof(NCharacterSelectScreen), "_description");
    private static readonly System.Reflection.FieldInfo CharacterRelicTitleField =
        AccessTools.Field(typeof(NCharacterSelectScreen), "_relicTitle");
    private static readonly System.Reflection.FieldInfo CharacterRelicDescriptionField =
        AccessTools.Field(typeof(NCharacterSelectScreen), "_relicDescription");
    private static readonly System.Reflection.FieldInfo CharacterRelicIconField =
        AccessTools.Field(typeof(NCharacterSelectScreen), "_relicIcon");
    private static readonly System.Reflection.FieldInfo CharacterRelicIconOutlineField =
        AccessTools.Field(typeof(NCharacterSelectScreen), "_relicIconOutline");
    private static readonly System.Reflection.MethodInfo? CharacterBackgroundWindowChangeMethod =
        AccessTools.Method(typeof(NCharacterSelectScreenBg), "OnWindowChange");

    // These paths are inputs to our isolated overlay and must not pass through another Mod's Harmony redirect.
    internal static string CanonicalScenePath(string innerPath) =>
        "res://scenes/" + innerPath.TrimStart('/') + ".tscn";

    internal static string CanonicalImagePath(string innerPath) =>
        "res://images/" + innerPath.TrimStart('/');

    public static void ShowCharacter(NCharacterSelectScreen screen, CharacterModel character)
    {
        CaptureCharacterBackgroundHostLayout(screen);
        CancelCharacterDropdownSelection(screen);
        // Restore while the old provider is still active. FocusRuntimeProviderBehaviorsOnCharacters
        // deactivates it immediately afterwards, so waiting for the deferred rebuild can leave a
        // provider-owned toolbar visible over the newly selected character for one or more frames.
        ManagedSkinModLoader.RestoreCharacterPresentation(screen);
        RemoveStaleProviderCharacterSelectControls(screen);
        var selector = EnsureCharacterSelector(screen);
        var group = FindGroup(character.Id.Entry);
        if (group != null)
        {
            SkinService.FocusRuntimeProviderBehaviorsOnGroups(
                [group.Id],
                runEnvironmentProviderIds: [],
                reason: "选角预览");
        }
        RegisterRefresh(selector, group == null ? null : () => RebuildCharacterDisplay(screen, character, group.Id));
        Populate(selector, group);
        Action refreshSelection = () =>
            {
                Populate(selector, group);
                if (group == null)
                {
                    return;
                }

                ScheduleCharacterRefresh(screen, character, group.Id);
                if (IsMultiplayerCharacterSelect(screen))
                {
                    MultiplayerSkinSync.OnLocalCharacterSelectionChanged(group.Id);
                }
            };
        CharacterSkinCompositionControls.Show(screen, group, refreshSelection);
        CharacterSkinBundleControls.ShowForCharacter(screen,
            group?.Id ?? character.Id.Entry.ToLowerInvariant(),
            group?.DisplayName ?? character.Title.GetFormattedText(),
            () => Populate(selector, group));
        RefreshMultiplayerSkinLoadingToggle(screen);
        if (group != null)
        {
            // 游戏的 SelectCharacter 每次点击都会清空 AnimatedBg 并重新实例化原版背景，
            // 所以这里必须每次重建；资源已缓存在 SkinService，重建不会再次写盘或加载。
            ScheduleCharacterRefresh(screen, character, group.Id);
            if (IsMultiplayerCharacterSelect(screen))
            {
                // SelectCharacter is also the point where the lobby changes the local player's
                // character. Publish after that state settles so peers update this row immediately
                // instead of waiting for ready or the next periodic snapshot.
                Callable.From(() =>
                    MultiplayerSkinSync.OnLocalCharacterSelectionChanged(group.Id)).CallDeferred();
            }
        }
    }

    private static bool IsMultiplayerCharacterSelect(NCharacterSelectScreen screen)
    {
        try
        {
            return screen.Lobby?.NetService.Type.IsMultiplayer() == true;
        }
        catch
        {
            return false;
        }
    }

    private static void ScheduleCharacterRefresh(
        NCharacterSelectScreen screen,
        CharacterModel character,
        string groupId)
    {
        var generation = screen.GetMeta(CharacterRefreshGenerationMeta, 0L).AsInt64() + 1L;
        screen.SetMeta(CharacterRefreshGenerationMeta, generation);
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(screen) ||
                screen.GetMeta(CharacterRefreshGenerationMeta, 0L).AsInt64() != generation)
            {
                return;
            }

            RunRefresh(() => RebuildCharacterDisplay(screen, character, groupId));
        }).CallDeferred();
    }

    public static void ShowMonster(NBestiary screen, NBestiaryEntry entry)
    {
        var selector = EnsureMonsterSelector(screen);
        SetMonsterPriorityContext(selector, ResolveMonsterSkinCategory(entry));
        var monster = entry.IsDiscovered ? entry.Entry.monsterModel : null;
        var group = monster == null
            ? null
            : FindGroup(monster.Id.Entry, monster.GetType().Name);
        if (NRun.Instance != null)
        {
            CharacterAppearanceRuntime.FocusRuntimeProviderBehaviorsOnRunContext(
                group == null ? [] : [group.Id],
                reason: "局内怪物图鉴", refreshCurrentRoom: true);
        }
        else
        {
            SkinService.FocusRuntimeProviderBehaviorsOnGroups(
                group == null ? [] : [group.Id],
                runEnvironmentProviderIds: [],
                reason: "怪物图鉴");
        }
        RegisterRefresh(
            selector,
            group == null || monster == null ? null : () => RebuildMonsterDisplay(screen, entry, monster, group.Id));
        Populate(selector, group);
    }

    private static HBoxContainer EnsureCharacterSelector(NCharacterSelectScreen screen)
    {
        var infoPanel = screen.GetNodeOrNull<Control>("InfoPanel");
        if (infoPanel == null)
        {
            ModLog.Error("选角界面缺少 InfoPanel 节点，无法挂载皮肤选择器。");
            return new HBoxContainer();
        }

        var existing = FindCharacterSelector(screen);
        if (existing != null)
        {
            AttachCharacterSelectorDragging(screen, infoPanel, existing);
            EnsureMultiplayerSkinLoadingToggle(screen, infoPanel);
            return existing;
        }

        var selector = BuildSelector();
        screen.AddChild(selector);
        AttachCharacterSelectorDragging(screen, infoPanel, selector);
        EnsureMultiplayerSkinLoadingToggle(screen, infoPanel);
        ModLocalization.Bind(selector, () => RefreshLocalizedText(selector));
        return selector;
    }

    private static HBoxContainer? FindCharacterSelector(NCharacterSelectScreen screen) =>
        screen.GetNodeOrNull<HBoxContainer>(SelectorName) ??
        screen.GetNodeOrNull<HBoxContainer>($"InfoPanel/{SelectorName}");

    private static void AttachCharacterSelectorDragging(
        NCharacterSelectScreen screen, Control infoPanel, HBoxContainer selector) =>
        DraggableSkinControl.Attach(screen, selector, mergeButton: false,
            () => ApplyCharacterSelectorPlacement(screen, infoPanel, selector));

    private static void ApplyCharacterSelectorPlacement(
        NCharacterSelectScreen screen,
        Control infoPanel,
        HBoxContainer selector)
    {
        var placement = CharacterSelectorPlacementPolicy.Resolve(
            SkinService.ShouldPlaceCharacterSelectorTopRight());
        if (placement.Host == CharacterSelectorHost.InfoPanel)
        {
            // Resolve the old default against InfoPanel, but keep one screen parent for the
            // control's entire lifetime. Reparenting during a drag interrupts input capture
            // and removes TreeExited-bound localization/refresh callbacks.
            var transform = screen.GetGlobalTransformWithCanvas().AffineInverse() *
                            infoPanel.GetGlobalTransformWithCanvas();
            var topLeft = transform * new Vector2(
                infoPanel.Size.X * placement.AnchorLeft + placement.OffsetLeft,
                infoPanel.Size.Y * placement.AnchorTop + placement.OffsetTop);
            var bottomRight = transform * new Vector2(
                infoPanel.Size.X * placement.AnchorRight + placement.OffsetRight,
                infoPanel.Size.Y * placement.AnchorBottom + placement.OffsetBottom);
            selector.AnchorLeft = selector.AnchorTop = selector.AnchorRight = selector.AnchorBottom = 0f;
            selector.OffsetLeft = topLeft.X;
            selector.OffsetTop = topLeft.Y;
            selector.OffsetRight = bottomRight.X;
            selector.OffsetBottom = bottomRight.Y;
            selector.ZIndex = 20;
            return;
        }

        selector.AnchorLeft = placement.AnchorLeft;
        selector.AnchorTop = placement.AnchorTop;
        selector.AnchorRight = placement.AnchorRight;
        selector.AnchorBottom = placement.AnchorBottom;
        selector.OffsetLeft = placement.OffsetLeft;
        selector.OffsetTop = placement.OffsetTop;
        selector.OffsetRight = placement.OffsetRight;
        selector.OffsetBottom = placement.OffsetBottom;
        selector.ZIndex = placement.Host == CharacterSelectorHost.Screen ? 20 : 0;
    }

    private static void EnsureMultiplayerSkinLoadingToggle(
        NCharacterSelectScreen screen,
        Control infoPanel)
    {
        EnsureMultiplayerSkinSyncToggle(screen, infoPanel);
        var toggle = infoPanel.GetNodeOrNull<CheckButton>(MultiplayerSkinLoadingToggleName);
        if (!IsMultiplayerCharacterSelect(screen))
        {
            if (toggle != null)
            {
                toggle.Visible = false;
            }
            return;
        }

        if (toggle == null)
        {
            toggle = new CheckButton
            {
                Name = MultiplayerSkinLoadingToggleName,
                AnchorLeft = 0.5f,
                AnchorTop = 0f,
                AnchorRight = 0.5f,
                AnchorBottom = 0f,
                OffsetLeft = -190f,
                OffsetTop = -128f,
                OffsetRight = 190f,
                OffsetBottom = -84f,
                Alignment = HorizontalAlignment.Center,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            toggle.AddThemeColorOverride("font_color", new Color("fff6e2"));
            toggle.AddThemeColorOverride("font_hover_color", Colors.White);
            toggle.AddThemeColorOverride("font_pressed_color", new Color("efc850"));
            toggle.AddThemeColorOverride("font_outline_color", new Color("332f27"));
            toggle.AddThemeConstantOverride("outline_size", 3);
            toggle.AddThemeFontSizeOverride("font_size", 18);
            if (GameFont != null)
            {
                toggle.AddThemeFontOverride("font", GameFont);
            }
            toggle.SetPressedNoSignal(SkinService.ShouldReceiveMultiplayerSkinChanges());
            toggle.Toggled += SkinService.SetReceiveMultiplayerSkinChanges;
            infoPanel.AddChild(toggle);
            ModLocalization.Bind(toggle, () =>
            {
                toggle.Text = ModLocalization.Get(ModText.LoadOtherPlayersCustomSkins);
                toggle.SetPressedNoSignal(SkinService.ShouldReceiveMultiplayerSkinChanges());
            });
        }

        toggle.Visible = true;
        toggle.Disabled = false;
        toggle.SetPressedNoSignal(SkinService.ShouldReceiveMultiplayerSkinChanges());
    }

    private static void EnsureMultiplayerSkinSyncToggle(
        NCharacterSelectScreen screen,
        Control infoPanel)
    {
        var toggle = infoPanel.GetNodeOrNull<CheckButton>(MultiplayerSkinSyncToggleName);
        if (!IsMultiplayerCharacterSelect(screen))
        {
            if (toggle != null)
            {
                toggle.Visible = false;
            }
            return;
        }

        if (toggle == null)
        {
            toggle = new CheckButton
            {
                Name = MultiplayerSkinSyncToggleName,
                AnchorLeft = 0.5f,
                AnchorTop = 0f,
                AnchorRight = 0.5f,
                AnchorBottom = 0f,
                OffsetLeft = -190f,
                OffsetTop = -176f,
                OffsetRight = 190f,
                OffsetBottom = -132f,
                Alignment = HorizontalAlignment.Center,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            toggle.AddThemeColorOverride("font_color", new Color("fff6e2"));
            toggle.AddThemeColorOverride("font_hover_color", Colors.White);
            toggle.AddThemeColorOverride("font_pressed_color", new Color("efc850"));
            toggle.AddThemeColorOverride("font_outline_color", new Color("332f27"));
            toggle.AddThemeConstantOverride("outline_size", 3);
            toggle.AddThemeFontSizeOverride("font_size", 18);
            if (GameFont != null)
            {
                toggle.AddThemeFontOverride("font", GameFont);
            }
            toggle.SetPressedNoSignal(SkinService.ShouldSendMultiplayerSkinChanges());
            toggle.Toggled += enabled =>
            {
                SkinService.SetSendMultiplayerSkinChanges(enabled);
                RefreshMultiplayerSkinLoadingToggle(screen);
            };
            infoPanel.AddChild(toggle);
            ModLocalization.Bind(toggle, () =>
            {
                toggle.Text = ModLocalization.Get(ModText.MultiplayerSkinSync);
                toggle.SetPressedNoSignal(SkinService.ShouldSendMultiplayerSkinChanges());
            });
        }

        toggle.Visible = true;
        toggle.SetPressedNoSignal(SkinService.ShouldSendMultiplayerSkinChanges());
    }

    private static void RefreshMultiplayerSkinLoadingToggle(NCharacterSelectScreen screen)
    {
        var isMultiplayer = IsMultiplayerCharacterSelect(screen);
        var sendEnabled = SkinService.ShouldSendMultiplayerSkinChanges();
        var syncToggle = screen.GetNodeOrNull<CheckButton>(
            $"InfoPanel/{MultiplayerSkinSyncToggleName}");
        if (syncToggle != null)
        {
            syncToggle.Visible = isMultiplayer;
            syncToggle.SetPressedNoSignal(sendEnabled);
        }

        var toggle = screen.GetNodeOrNull<CheckButton>(
            $"InfoPanel/{MultiplayerSkinLoadingToggleName}");
        if (toggle != null)
        {
            toggle.Visible = isMultiplayer;
            toggle.Disabled = false;
            toggle.SetPressedNoSignal(SkinService.ShouldReceiveMultiplayerSkinChanges());
        }
    }

    private static HBoxContainer EnsureMonsterSelector(NBestiary screen)
    {
        var existing = screen.GetNodeOrNull<HBoxContainer>(SelectorName);
        if (existing != null)
        {
            return existing;
        }

        var selector = BuildSelector();
        AttachMonsterPriorityControls(screen, selector);
        AddMonsterScaleControls(screen, selector);
        selector.AnchorLeft = 0.5f;
        selector.AnchorRight = 0.5f;
        selector.OffsetLeft = -350;
        selector.OffsetTop = 168;
        selector.OffsetRight = 350;
        selector.OffsetBottom = 212;
        screen.AddChild(selector);
        ModLocalization.Bind(selector, () => RefreshLocalizedText(selector));
        return selector;
    }

    private static void AddMonsterScaleControls(NBestiary screen, HBoxContainer selector)
    {
        var label = BuildCompactLabel(ModLocalization.Get(ModText.MonsterSize), 60);
        label.Name = MonsterScaleLabelName;
        var slider = new HSlider
        {
            Name = MonsterScaleSliderName,
            MinValue = SkinService.MinimumMonsterScale,
            MaxValue = SkinService.MaximumMonsterScale,
            Step = SkinService.MonsterScaleStep,
            Value = 1d,
            CustomMinimumSize = new Vector2(160, 36),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        var valueLabel = BuildCompactLabel("100%", 62);
        valueLabel.Name = MonsterScaleValueName;
        var reset = new Button
        {
            Name = MonsterScaleResetName,
            Text = ModLocalization.Get(ModText.Reset),
            CustomMinimumSize = new Vector2(108, 38),
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        ApplyCompactButtonTheme(reset);
        slider.ValueChanged += value =>
            OnMonsterScaleChanged(screen, selector, (float)value);
        reset.Pressed += () => slider.Value = 1d;
        selector.AddChild(label);
        selector.AddChild(slider);
        selector.AddChild(valueLabel);
        selector.AddChild(reset);
    }

    private static void RefreshLocalizedText(HBoxContainer selector)
    {
        var groupId = selector.GetMeta(GroupMeta, string.Empty).AsString();
        var group = string.IsNullOrWhiteSpace(groupId) ? null : FindGroup(groupId);
        if (group != null)
        {
            Populate(selector, group);
        }

        var scaleLabel = selector.GetNodeOrNull<Label>(MonsterScaleLabelName);
        if (scaleLabel != null)
        {
            scaleLabel.Text = ModLocalization.Get(ModText.MonsterSize);
        }

        var reset = selector.GetNodeOrNull<Button>(MonsterScaleResetName);
        if (reset != null)
        {
            reset.Text = ModLocalization.Get(ModText.Reset);
        }

        RefreshMonsterPriorityButton(selector);
    }

    private static Label BuildCompactLabel(string text, float width)
    {
        var label = new Label
        {
            Text = text,
            CustomMinimumSize = new Vector2(width, 38),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        label.AddThemeColorOverride("font_color", new Color("fff6e2"));
        label.AddThemeColorOverride("font_outline_color", new Color("332f27"));
        label.AddThemeConstantOverride("outline_size", 4);
        label.AddThemeFontSizeOverride("font_size", 20);
        if (GameFont != null)
        {
            label.AddThemeFontOverride("font", GameFont);
        }

        return label;
    }

    private static void ApplyCompactButtonTheme(Button button)
    {
        button.AddThemeColorOverride("font_color", new Color("fff6e2"));
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", new Color("efc850"));
        button.AddThemeFontSizeOverride("font_size", 19);
        if (GameFont != null)
        {
            button.AddThemeFontOverride("font", GameFont);
        }

        button.AddThemeStyleboxOverride(
            "normal",
            CreateStyleBox(new Color("3c5f82"), new Color("7394ad"), 1));
        button.AddThemeStyleboxOverride(
            "hover",
            CreateStyleBox(new Color("4b7392"), new Color("afcdde"), 1));
        button.AddThemeStyleboxOverride(
            "pressed",
            CreateStyleBox(new Color("45104e"), new Color("efc850"), 2));
    }

    private static HBoxContainer BuildSelector()
    {
        var selector = new HBoxContainer
        {
            Name = SelectorName,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        var dropdown = new OptionButton
        {
            Name = DropdownName,
            CustomMinimumSize = new Vector2(244, 44),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FitToLongestItem = false,
            ClipText = true,
            Alignment = HorizontalAlignment.Center
        };
        ApplyGameTheme(dropdown);
        dropdown.ItemSelected += index => ApplyDropdownSelection(selector, dropdown, checked((int)index));
        selector.AddChild(dropdown);

        selector.TreeExited += () =>
        {
            var id = selector.GetInstanceId();
            RefreshActions.Remove(id);
        };
        return selector;
    }

    internal static void ApplyGameTheme(OptionButton dropdown)
    {
        var font = GameFont;
        var ivory = new Color("fff6e2");
        var gold = new Color("efc850");
        dropdown.AddThemeColorOverride("font_color", ivory);
        dropdown.AddThemeColorOverride("font_hover_color", Colors.White);
        dropdown.AddThemeColorOverride("font_pressed_color", gold);
        dropdown.AddThemeColorOverride("font_focus_color", Colors.White);
        dropdown.AddThemeFontSizeOverride("font_size", 23);
        if (font != null)
        {
            dropdown.AddThemeFontOverride("font", font);
        }

        dropdown.AddThemeStyleboxOverride("normal", CreateStyleBox(new Color("3c5f82"), new Color("7394ad")));
        dropdown.AddThemeStyleboxOverride("hover", CreateStyleBox(new Color("4b7392"), new Color("afcdde")));
        dropdown.AddThemeStyleboxOverride("pressed", CreateStyleBox(new Color("45104e"), gold));
        dropdown.AddThemeStyleboxOverride("focus", CreateStyleBox(new Color("3c5f82"), gold, 2));
        dropdown.AddThemeStyleboxOverride("disabled", CreateStyleBox(new Color("293b4c"), new Color("50606b")));

        var popup = dropdown.GetPopup();
        popup.AddThemeColorOverride("font_color", ivory);
        popup.AddThemeColorOverride("font_hover_color", Colors.White);
        popup.AddThemeColorOverride("font_separator_color", gold);
        popup.AddThemeFontSizeOverride("font_size", 22);
        popup.AddThemeStyleboxOverride("panel", CreateStyleBox(new Color("45104e"), new Color("79547e"), 2));
        popup.AddThemeStyleboxOverride("hover", CreateStyleBox(new Color("2c586f"), new Color("afcdde")));
        if (font != null)
        {
            popup.AddThemeFontOverride("font", font);
        }
    }

    internal static void ApplyGameTheme(Button button)
    {
        var font = GameFont;
        var ivory = new Color("fff6e2");
        var gold = new Color("efc850");
        button.AddThemeColorOverride("font_color", ivory);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", gold);
        button.AddThemeColorOverride("font_hover_pressed_color", Colors.White);
        button.AddThemeColorOverride("font_focus_color", Colors.White);
        button.AddThemeFontSizeOverride("font_size", 21);
        if (font != null)
        {
            button.AddThemeFontOverride("font", font);
        }

        button.AddThemeStyleboxOverride("normal", CreateStyleBox(new Color("3c5f82"), new Color("7394ad")));
        button.AddThemeStyleboxOverride("hover", CreateStyleBox(new Color("4b7392"), new Color("afcdde")));
        button.AddThemeStyleboxOverride("pressed", CreateStyleBox(new Color("45104e"), gold));
        button.AddThemeStyleboxOverride("hover_pressed", CreateStyleBox(new Color("58205f"), gold, 2));
        button.AddThemeStyleboxOverride("focus", CreateStyleBox(new Color("3c5f82"), gold, 2));
        button.AddThemeStyleboxOverride("disabled", CreateStyleBox(new Color("293b4c"), new Color("50606b")));
    }

    internal static void HideCharacterSelector(NCharacterSelectScreen screen)
    {
        CharacterSkinCompositionControls.Hide(screen);
        CharacterSkinBundleControls.Hide(screen);
        var selector = FindCharacterSelector(screen);
        if (selector != null)
        {
            selector.Visible = false;
        }

        var toggle = screen.GetNodeOrNull<Control>(
            $"InfoPanel/{MultiplayerSkinLoadingToggleName}");
        if (toggle != null)
        {
            toggle.Visible = false;
        }

        var syncToggle = screen.GetNodeOrNull<Control>(
            $"InfoPanel/{MultiplayerSkinSyncToggleName}");
        if (syncToggle != null)
        {
            syncToggle.Visible = false;
        }

    }

    internal static bool ShouldIgnoreBackgroundMute(NMuteInBackgroundHandler handler, int notification)
    {
        const int ApplicationFocusOut = 1005;
        return notification == ApplicationFocusOut && handler.GetWindow().HasFocus();
    }

    internal static StyleBoxFlat CreateStyleBox(Color background, Color border, int borderWidth = 1)
    {
        return new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = 12,
            CornerRadiusTopRight = 12,
            CornerRadiusBottomRight = 12,
            CornerRadiusBottomLeft = 12,
            ContentMarginLeft = 12,
            ContentMarginRight = 12
        };
    }

    private static void Populate(HBoxContainer selector, SkinGroup? group)
    {
        var dropdown = selector.GetNode<OptionButton>(DropdownName);
        dropdown.GetPopup().Hide();
        var characterScreen = FindAncestor<NCharacterSelectScreen>(selector);
        var visualOptions = group == null
            ? []
            : SkinService.Catalog?.IsCharacterAppearanceGroup(group.Id) == true
                ? SkinService.GetCharacterSkinOptions(group.Id).ToArray()
                : group.Options.ToArray();
        var bundles = group != null && characterScreen != null && !HasMonsterPriorityContext(selector)
            ? SkinService.GetCharacterSkinBundles(group.Id)
            : [];
        if (group == null || visualOptions.Length == 0 && bundles.Count == 0)
        {
            selector.Visible = false;
            dropdown.Clear();
            ConfigureCharacterBundlePopupList(selector, dropdown, 0);
            return;
        }

        selector.SetMeta(UpdatingMeta, true);
        selector.SetMeta(GroupMeta, group.Id);
        dropdown.Clear();
        var hasMonsterPriorityContext = HasMonsterPriorityContext(selector);
        if (hasMonsterPriorityContext)
        {
            dropdown.AddItem(ModLocalization.Get(ModText.FollowCategory));
            dropdown.SetItemMetadata(0, SkinService.InheritMonsterSelectionId);
        }

        foreach (var bundle in bundles)
        {
            var index = dropdown.ItemCount;
            dropdown.AddItem(CharacterSkinBundlePolicy.CreateSelectionDisplayName(bundle.Name));
            dropdown.SetItemMetadata(index, CharacterSkinBundlePolicy.CreateSelectionOptionId(bundle.Name));
        }

        var defaultIndex = dropdown.ItemCount;
        dropdown.AddItem(ModLocalization.Get(ModText.GameDefault));
        dropdown.SetItemMetadata(defaultIndex, SkinCatalog.BaseOptionId);
        foreach (var option in visualOptions)
        {
            var index = dropdown.ItemCount;
            dropdown.AddItem(CharacterSkinCompositionPolicy.ResolveDisplayName(
                option.Name,
                option.IsComposition,
                ModLocalization.DisplayOptionName));
            dropdown.SetItemMetadata(index, option.Id);
        }
        ConfigureCharacterBundlePopupList(selector, dropdown, bundles.Count);

        var activeBundle = characterScreen != null
            ? SkinService.Config.ActiveCharacterSkinBundles.GetValueOrDefault(group.Id)
            : null;
        var selected = !string.IsNullOrWhiteSpace(activeBundle) &&
                       bundles.Any(bundle => bundle.Name.Equals(activeBundle, StringComparison.OrdinalIgnoreCase))
            ? CharacterSkinBundlePolicy.CreateSelectionOptionId(activeBundle)
            : hasMonsterPriorityContext
                ? SkinService.GetMonsterOverrideSelection(group.Id)
                : SkinService.Config.GetSelection(group.Id);
        if (dropdown.ItemCount > 0)
        {
            var selectedIndex = Enumerable.Range(0, dropdown.ItemCount)
                .FirstOrDefault(index => dropdown.GetItemMetadata(index).AsString()
                    .Equals(selected, StringComparison.OrdinalIgnoreCase));
            dropdown.Select(selectedIndex);
        }
        ApplyCharacterBundleSelectionTheme(
            dropdown,
            CharacterSkinBundlePolicy.TryGetSelectionBundleName(selected, out _));
        dropdown.Visible = true;
        PopulateMonsterScale(selector, group.Id);
        selector.SetMeta(UpdatingMeta, false);
        selector.Visible = true;
        RefreshMonsterPriorityButton(selector);
    }

    private static void ApplyCharacterBundleSelectionTheme(OptionButton dropdown, bool selectedBundle)
    {
        ApplyGameTheme(dropdown);
        if (!selectedBundle)
        {
            return;
        }
        var gold = new Color("efc850");
        foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
        {
            dropdown.AddThemeColorOverride(state, gold);
        }
    }

    private static void ConfigureCharacterBundlePopupList(
        HBoxContainer selector,
        OptionButton dropdown,
        int bundleCount)
    {
        var popup = dropdown.GetPopup();
        var list = popup.GetNodeOrNull<ItemList>(CharacterBundlePopupListName);
        if (bundleCount <= 0 && list == null)
        {
            return;
        }

        if (list == null)
        {
            list = new ItemList
            {
                Name = CharacterBundlePopupListName,
                MouseFilter = Control.MouseFilterEnum.Stop,
                SelectMode = ItemList.SelectModeEnum.Single,
                AllowReselect = true,
                SameColumnWidth = true,
                MaxColumns = 1,
                ZIndex = 100
            };
            list.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            list.AddThemeColorOverride("font_color", new Color("fff6e2"));
            list.AddThemeColorOverride("font_hovered_color", Colors.White);
            list.AddThemeColorOverride("font_selected_color", Colors.White);
            list.AddThemeFontSizeOverride("font_size", 22);
            list.AddThemeStyleboxOverride(
                "panel", CreateStyleBox(new Color("45104e"), new Color("79547e"), 2));
            list.AddThemeStyleboxOverride(
                "hovered", CreateStyleBox(new Color("2c586f"), new Color("afcdde")));
            list.AddThemeStyleboxOverride(
                "selected", CreateStyleBox(new Color("58205f"), new Color("efc850"), 2));
            if (GameFont != null)
            {
                list.AddThemeFontOverride("font", GameFont);
            }
            popup.AddChild(list);

            var popupList = list;
            popupList.ItemClicked += (clickedIndex, _, button) =>
            {
                if (button != (long)MouseButton.Left || clickedIndex < 0 || clickedIndex >= dropdown.ItemCount)
                {
                    return;
                }
                dropdown.Select((int)clickedIndex);
                popup.Hide();
                ApplyDropdownSelection(selector, dropdown, (int)clickedIndex);
            };
            popupList.ItemActivated += activatedIndex =>
            {
                if (activatedIndex < 0 || activatedIndex >= dropdown.ItemCount)
                {
                    return;
                }
                dropdown.Select((int)activatedIndex);
                popup.Hide();
                ApplyDropdownSelection(selector, dropdown, (int)activatedIndex);
            };
            popup.AboutToPopup += () =>
            {
                // The same selector survives character changes. Always read the current native
                // entries; an earlier character's colored bundle list must never reappear.
                if (!RefreshCharacterBundlePopupList(dropdown, popupList))
                {
                    return;
                }
                Callable.From(() =>
                {
                    if (GodotObject.IsInstanceValid(popupList) && popupList.Visible && popup.Visible)
                    {
                        popupList.Position = Vector2.Zero;
                        popupList.Size = popup.Size;
                        popupList.GrabFocus();
                    }
                }).CallDeferred();
            };
        }

        RefreshCharacterBundlePopupList(dropdown, list);
    }

    private static bool RefreshCharacterBundlePopupList(OptionButton dropdown, ItemList list)
    {
        list.Clear();
        list.Visible = false;
        var hasBundles = Enumerable.Range(0, dropdown.ItemCount).Any(index =>
            CharacterSkinBundlePolicy.TryGetSelectionBundleName(
                dropdown.GetItemMetadata(index).AsString(), out _));
        if (!hasBundles)
        {
            return false;
        }

        var gold = new Color("efc850");
        for (var index = 0; index < dropdown.ItemCount; index++)
        {
            list.AddItem(dropdown.GetItemText(index));
            if (CharacterSkinBundlePolicy.TryGetSelectionBundleName(
                    dropdown.GetItemMetadata(index).AsString(), out _))
            {
                list.SetItemCustomFgColor(index, gold);
            }
        }
        if (dropdown.Selected >= 0 && dropdown.Selected < list.ItemCount)
        {
            list.Select(dropdown.Selected);
        }
        list.Visible = true;
        return true;
    }

    private static void PopulateMonsterScale(HBoxContainer selector, string groupId)
    {
        var slider = selector.GetNodeOrNull<HSlider>(MonsterScaleSliderName);
        var valueLabel = selector.GetNodeOrNull<Label>(MonsterScaleValueName);
        if (slider == null || valueLabel == null)
        {
            return;
        }

        var scale = SkinService.GetSelectedMonsterScale(groupId);
        slider.Value = scale;
        valueLabel.Text = $"{Mathf.RoundToInt(scale * 100f)}%";
    }

    private static void OnMonsterScaleChanged(
        NBestiary screen,
        HBoxContainer selector,
        float scale)
    {
        var valueLabel = selector.GetNodeOrNull<Label>(MonsterScaleValueName);
        if (valueLabel != null)
        {
            valueLabel.Text = $"{Mathf.RoundToInt(scale * 100f)}%";
        }

        if (selector.GetMeta(UpdatingMeta, false).AsBool())
        {
            return;
        }

        var groupId = selector.GetMeta(GroupMeta, string.Empty).AsString();
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return;
        }

        try
        {
            SkinService.SetSelectedMonsterScale(groupId, scale);
            ApplyMonsterScaleToDescendants(screen, groupId);
        }
        catch (Exception exception)
        {
            ModLog.Error("保存怪物缩放失败：" + exception.Message);
        }
    }

    private static void ApplyDropdownSelection(HBoxContainer selector, OptionButton dropdown, int index)
    {
        if (selector.GetMeta(UpdatingMeta, false).AsBool())
        {
            return;
        }

        var groupId = selector.GetMeta(GroupMeta, string.Empty).AsString();
        var optionId = dropdown.GetItemMetadata(index).AsString();
        var characterScreen = FindAncestor<NCharacterSelectScreen>(selector);
        if (characterScreen != null &&
            !HasMonsterPriorityContext(selector) &&
            CharacterSkinBundlePolicy.TryGetSelectionBundleName(optionId, out var bundleName))
        {
            var characterOptionId = SkinService.GetCharacterSkinBundleCharacterOption(groupId, bundleName);
            if (characterOptionId == null ||
                !SkinService.SelectCharacterSkinBundle(groupId, bundleName))
            {
                ModLog.Error($"选择皮肤包失败：{SkinService.LastError}");
                Populate(selector, FindGroup(groupId));
                return;
            }
            ApplyCharacterBundleSelectionTheme(dropdown, selectedBundle: true);
            BeginCharacterDropdownSelection(
                characterScreen,
                selector,
                dropdown,
                index,
                groupId,
                characterOptionId,
                preserveCharacterSkinBundle: true);
            return;
        }
        if (characterScreen != null && !HasMonsterPriorityContext(selector))
        {
            BeginCharacterDropdownSelection(
                characterScreen,
                selector,
                dropdown,
                index,
                groupId,
                optionId);
            return;
        }

        ApplyDropdownSelectionNow(selector, dropdown, index, groupId, optionId);
    }

    private static bool ApplyDropdownSelectionNow(
        HBoxContainer selector,
        OptionButton dropdown,
        int index,
        string groupId,
        string optionId,
        bool preserveCharacterSkinBundle = false)
    {
        var previousSelections = new Dictionary<string, string>(
            SkinService.Config.Selections,
            StringComparer.OrdinalIgnoreCase);
        var applied = optionId.Equals(
            SkinService.InheritMonsterSelectionId,
            StringComparison.OrdinalIgnoreCase)
            ? SkinService.FollowMonsterCategoryPriority(groupId)
            : SkinService.ApplySelection(groupId, optionId);
        if (!applied)
        {
            ModLog.Error($"界面切换失败：{SkinService.LastError}");
            var current = HasMonsterPriorityContext(selector)
                ? SkinService.GetMonsterOverrideSelection(groupId)
                : SkinService.Config.GetSelection(groupId);
            var currentIndex = Enumerable.Range(0, dropdown.ItemCount)
                .FirstOrDefault(item => dropdown.GetItemMetadata(item).AsString()
                    .Equals(current, StringComparison.OrdinalIgnoreCase));
            dropdown.Select(currentIndex);
            return false;
        }

        if (FindAncestor<NCharacterSelectScreen>(selector) != null &&
            !HasMonsterPriorityContext(selector) &&
            !preserveCharacterSkinBundle &&
            !SkinService.ClearSelectedCharacterSkinBundle(groupId))
        {
            ModLog.Warn($"清除选角皮肤包选择失败：{SkinService.LastError}");
        }

        selector.SetMeta(UpdatingMeta, true);
        PopulateMonsterScale(selector, groupId);
        selector.SetMeta(UpdatingMeta, false);

        // This method is shared by several selectors. The multiplayer sync method verifies that
        // groupId belongs to the current local character, so non-character selectors are ignored.
        MultiplayerSkinSync.OnLocalCharacterSelectionChanged(groupId);
        var affectedGroups = previousSelections.Keys
            .Concat(SkinService.Config.Selections.Keys)
            .Where(key => !string.Equals(
                previousSelections.GetValueOrDefault(key),
                SkinService.Config.Selections.GetValueOrDefault(key),
                StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        affectedGroups.Add(groupId);
        CharacterAppearanceRuntime.RefreshRunMonsterSelection(
            affectedGroups,
            "局内怪物图鉴");

        if (selector.GetParent() is NBestiary bestiary)
        {
            RefreshBestiaryMonsterNames(bestiary);
        }

        if (RefreshActions.TryGetValue(selector.GetInstanceId(), out var refresh))
        {
            Callable.From(() => RunRefresh(refresh)).CallDeferred();
        }
        return true;
    }

    private static void BeginCharacterDropdownSelection(
        NCharacterSelectScreen screen,
        HBoxContainer selector,
        OptionButton dropdown,
        int index,
        string groupId,
        string optionId,
        bool preserveCharacterSkinBundle = false)
    {
        var generation = screen.GetMeta(CharacterLoadingGenerationMeta, 0L).AsInt64() + 1L;
        screen.SetMeta(CharacterLoadingGenerationMeta, generation);
        selector.SetMeta(UpdatingMeta, true);
        dropdown.Disabled = true;

        var optionName = dropdown.GetItemText(index);
        var overlay = EnsureCharacterLoadingOverlay(screen, optionName);
        var progressValue = 0;
        var packPaths = SkinService.GetSelectionResourcePackPaths(groupId, optionId);
        var warmTask = SkinService.WarmResourcePackFilesAsync(
            packPaths,
            progress => Interlocked.Exchange(
                ref progressValue,
                (int)Math.Round(progress * 78d)));

        Action poll = null!;
        poll = () =>
        {
            if (!GodotObject.IsInstanceValid(screen) ||
                screen.GetMeta(CharacterLoadingGenerationMeta, 0L).AsInt64() != generation)
            {
                return;
            }

            UpdateCharacterLoadingOverlay(overlay, optionName, Volatile.Read(ref progressValue));
            if (!warmTask.IsCompleted)
            {
                ScheduleNextFrame(screen, poll);
                return;
            }

            if (warmTask.IsFaulted)
            {
                ModLog.Warn(
                    $"后台预读 {optionName} 资源包失败，将直接继续挂载：" +
                    warmTask.Exception?.GetBaseException().Message);
            }

            UpdateCharacterLoadingOverlay(overlay, optionName, 82);
            ScheduleNextFrame(screen, () =>
            {
                if (!GodotObject.IsInstanceValid(screen) ||
                    screen.GetMeta(CharacterLoadingGenerationMeta, 0L).AsInt64() != generation)
                {
                    return;
                }

                selector.SetMeta(UpdatingMeta, false);
                var applied = ApplyDropdownSelectionNow(
                    selector,
                    dropdown,
                    index,
                    groupId,
                    optionId,
                    preserveCharacterSkinBundle);
                if (preserveCharacterSkinBundle)
                {
                    if (!applied)
                    {
                        SkinService.ClearSelectedCharacterSkinBundle(groupId);
                    }
                    Populate(selector, FindGroup(groupId));
                }
                UpdateCharacterLoadingOverlay(overlay, optionName, 94);
                ScheduleNextFrame(screen, () =>
                {
                    if (!GodotObject.IsInstanceValid(screen) ||
                        screen.GetMeta(CharacterLoadingGenerationMeta, 0L).AsInt64() != generation)
                    {
                        return;
                    }

                    UpdateCharacterLoadingOverlay(overlay, optionName, 100);
                    dropdown.Disabled = false;
                    selector.SetMeta(UpdatingMeta, false);
                    ScheduleNextFrame(screen, () => HideCharacterLoadingOverlay(screen, overlay));
                });
            });
        };

        ScheduleNextFrame(screen, poll);
    }

    private static void CancelCharacterDropdownSelection(NCharacterSelectScreen screen)
    {
        var overlay = screen.GetNodeOrNull<PanelContainer>(CharacterLoadingOverlayName);
        if (overlay?.Visible != true)
        {
            return;
        }

        screen.SetMeta(
            CharacterLoadingGenerationMeta,
            screen.GetMeta(CharacterLoadingGenerationMeta, 0L).AsInt64() + 1L);
        overlay.Visible = false;
        var selector = FindCharacterSelector(screen);
        if (selector != null)
        {
            selector.SetMeta(UpdatingMeta, false);
            var dropdown = selector.GetNodeOrNull<OptionButton>(DropdownName);
            if (dropdown != null)
            {
                dropdown.Disabled = false;
            }
        }
    }

    private static PanelContainer EnsureCharacterLoadingOverlay(
        NCharacterSelectScreen screen,
        string optionName)
    {
        var existing = screen.GetNodeOrNull<PanelContainer>(CharacterLoadingOverlayName);
        if (existing != null)
        {
            UpdateCharacterLoadingOverlay(existing, optionName, 0);
            existing.Visible = true;
            return existing;
        }

        var overlay = new PanelContainer
        {
            Name = CharacterLoadingOverlayName,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZAsRelative = false,
            ZIndex = 500,
            AnchorLeft = 0.5f,
            AnchorTop = 0,
            AnchorRight = 0.5f,
            AnchorBottom = 0,
            OffsetLeft = -210,
            OffsetTop = 34,
            OffsetRight = 210,
            OffsetBottom = 112
        };
        overlay.AddThemeStyleboxOverride(
            "panel",
            CreateStyleBox(new Color("2a4058e8"), new Color("efc850"), 2));
        var content = new VBoxContainer
        {
            Name = "Content",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        overlay.AddChild(content);
        var label = new Label
        {
            Name = "Label",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        label.AddThemeColorOverride("font_color", new Color("fff6e2"));
        label.AddThemeFontSizeOverride("font_size", 20);
        if (GameFont != null)
        {
            label.AddThemeFontOverride("font", GameFont);
        }
        content.AddChild(label);
        var progress = new ProgressBar
        {
            Name = "Progress",
            MinValue = 0,
            MaxValue = 100,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(380, 16),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        progress.AddThemeStyleboxOverride(
            "background",
            CreateStyleBox(new Color("172433"), new Color("50606b")));
        progress.AddThemeStyleboxOverride(
            "fill",
            CreateStyleBox(new Color("efc850"), new Color("fff1a8")));
        content.AddChild(progress);
        screen.AddChild(overlay);
        UpdateCharacterLoadingOverlay(overlay, optionName, 0);
        return overlay;
    }

    private static void UpdateCharacterLoadingOverlay(
        PanelContainer overlay,
        string optionName,
        int progress)
    {
        if (!GodotObject.IsInstanceValid(overlay))
        {
            return;
        }

        var value = Math.Clamp(progress, 0, 100);
        overlay.GetNode<Label>("Content/Label").Text = $"{optionName}  {value}%";
        overlay.GetNode<ProgressBar>("Content/Progress").Value = value;
    }

    private static void HideCharacterLoadingOverlay(
        NCharacterSelectScreen screen,
        PanelContainer overlay)
    {
        if (GodotObject.IsInstanceValid(screen) && GodotObject.IsInstanceValid(overlay))
        {
            overlay.Visible = false;
        }
    }

    private static T? FindAncestor<T>(Node node) where T : Node
    {
        for (var parent = node.GetParent(); parent != null; parent = parent.GetParent())
        {
            if (parent is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static void ScheduleNextFrame(Node owner, Action action)
    {
        if (!GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree())
        {
            return;
        }

        owner.GetTree().CreateTimer(0.01d).Timeout += action;
    }

    private static void RunRefresh(Action refresh)
    {
        try
        {
            refresh();
        }
        catch (Exception exception)
        {
            ModLog.Error("重建皮肤展示失败：" + exception);
        }
    }

    private static void RegisterRefresh(HBoxContainer selector, Action? refresh)
    {
        var id = selector.GetInstanceId();
        if (refresh == null)
        {
            RefreshActions.Remove(id);
        }
        else
        {
            RefreshActions[id] = refresh;
        }
    }

    private static void RebuildCharacterDisplay(NCharacterSelectScreen screen, CharacterModel character, string groupId)
    {
        // A behavior-driven provider can replace the title/description and hide the original
        // background without changing a canonical scene. Always restore the game presentation
        // first so switching *away* from such a provider is just as complete as switching to it.
        ManagedSkinModLoader.RestoreCharacterPresentation(screen);
        RestoreCharacterBackgroundHostLayout(screen);
        RestoreCharacterInfoText(screen, character);

        if (ShouldSkipExternalRuntimeRedirect(groupId))
        {
            RebuildRuntimeProviderCharacterDisplay(screen, character);
            ReplaySelectedCharacterPresentation(screen, character, groupId);
            RefreshLocalLobbyAvatar(screen);
            return;
        }

        var characterId = character.Id.Entry.ToLowerInvariant();
        var characterSelectPath = CanonicalScenePath("screens/char_select/char_select_bg_" + characterId);
        var characterSelectTextures = new[]
        {
            CanonicalImagePath("packed/character_select/char_select_" + characterId + ".png"),
            CanonicalImagePath("packed/character_select/char_select_" + characterId + "_locked.png")
        };
        // Only the background and button textures are needed on this screen. Combat, rest-site
        // and merchant scenes have their own lazy replacement hooks and caches; eagerly pulling
        // all of them here made the first click decode entire animation sets for some skins.
        var resourcePaths = new[] { characterSelectPath }
            .Concat(characterSelectTextures)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // A complete DLL skin is mounted at its original game-facing paths while it is selected,
        // but that is not enough for a hot switch: Godot may already have the canonical scene,
        // skeleton or atlas cached from the skin that was active at startup. Instantiate the
        // display through the same isolated alias overlay used by resource skins so the whole
        // dependency graph is loaded from the selected provider's files as one coherent set.
        if (SkinService.GetSelectedFullRuntimeProvider(groupId) != null)
        {
            RebuildMountedFullRuntimeCharacterDisplay(
                screen,
                character,
                groupId,
                characterSelectPath,
                characterSelectTextures);
            RefreshLocalLobbyAvatar(screen);
            return;
        }

        // 需要包含提供者依赖：DLL 提供者的场景可能引用其 PCK 里的编译脚本(.gdc)、导出场景(.scn)等。
        SkinService.WithRuntimeResources(
            groupId,
            resourcePaths,
            resources =>
            {
                var scene = resources[characterSelectPath] as PackedScene ??
                            throw new InvalidOperationException($"角色选角资源不是场景：{characterSelectPath}");
                TakeOverCharacterSelectSceneCache(characterSelectPath, scene);
                // The scene must be instantiated before WithRuntimeResources restores canonical
                // dependency paths; otherwise a skeleton/animation resource can come from the
                // previous character skin even though the PackedScene itself loaded correctly.
                ReplaceCharacterBackground(screen, character, scene, resources);
                RefreshCharacterButtonIcon(screen, character);
                // CZN and similar complete packs create their own background variant and toolbar
                // from this callback. Keep the provider overlay mounted while replaying it so
                // those dynamically loaded scenes/textures resolve to the selected pack rather
                // than a stale canonical or previously selected skin.
                ReplaySelectedCharacterPresentation(screen, character, groupId);
                return true;
            },
            includeProviderDependencies: true);
        RefreshLocalLobbyAvatar(screen);
        ModLog.Info($"已完整重建 {character.Id.Entry} 的选角展示。");
    }

    /// <summary>
    /// The game only calls NRemoteLobbyPlayer.RefreshVisuals when the character changes.  A
    /// Skin Changer selection does not change the CharacterModel, so the local row keeps the
    /// texture that was assigned when the lobby was created.  Refresh the row explicitly after
    /// the deferred character rebuild; this is a direct texture assignment and never re-enters
    /// the game's full visual refresh (which can recurse while a lobby is being constructed).
    /// </summary>
    private static void RefreshLocalLobbyAvatar(NCharacterSelectScreen screen)
    {
        try
        {
            if (!IsMultiplayerCharacterSelect(screen))
            {
                return;
            }

            // Do not call StartRunLobby.LocalPlayer here: its return type changed between the
            // formal and public-beta game builds (LobbyPlayer vs StartRunLobbyPlayer), which
            // makes an otherwise valid dual-version DLL fail with MissingMethodException.
            var playerId = screen.Lobby.NetService.NetId;
            RefreshMultiplayerPlayerIcons(playerId);
        }
        catch (Exception exception)
        {
            ModLog.Warn("刷新本机选角头像失败：" + exception.GetBaseException().Message);
        }
    }

    internal static CharacterModel GetLocalLobbyCharacter(StartRunLobby lobby)
    {
        // LocalPlayer returns LobbyPlayer on formal and StartRunLobbyPlayer on beta.
        // Resolve both the getter and player field at runtime, without embedding either ABI.
        var player = AccessTools.Property(typeof(StartRunLobby), "LocalPlayer")?.GetValue(lobby);
        return player != null &&
               AccessTools.Field(player.GetType(), "character")?.GetValue(player) is CharacterModel character
            ? character
            : throw new InvalidOperationException("选角大厅中没有可读取的本机角色。");
    }

    private static void RebuildMountedFullRuntimeCharacterDisplay(
        NCharacterSelectScreen screen,
        CharacterModel character,
        string groupId,
        string scenePath,
        IReadOnlyCollection<string> texturePaths)
    {
        var resourcePaths = new[] { scenePath }
            .Concat(texturePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        SkinService.WithRuntimeResources(
            groupId,
            resourcePaths,
            resources =>
            {
                var scene = resources[scenePath] as PackedScene ??
                            throw new InvalidOperationException($"完整运行时皮肤的选角资源不是场景：{scenePath}");
                // CZN's lobby variant switch loads this canonical path again with
                // ResourceLoader.CacheMode.Reuse.  Merely mounting a corrected .remap cannot
                // replace a scene that another skin already placed in Godot's global cache.
                // Make the coherently isolated scene own the canonical path so both this rebuild
                // and later provider-owned variant switches resolve to the selected skin.
                TakeOverCharacterSelectSceneCache(scenePath, scene);
                // Instantiate while the alias pack is mounted. PackedScene external resources
                // are often resolved at Instantiate(), not at Load(), so loading only the scene
                // object is insufficient to prevent a previous skin's skeleton/atlas binding.
                ReplaceCharacterBackground(screen, character, scene, resources);
                RefreshCharacterButtonIcon(screen, character);
                // Replay provider-owned character-select presentation before WithRuntimeResources
                // restores canonical paths. This is required for CZN's dynamic preview/background
                // scenes and keeps their two toolbar controls tied to the selected package.
                ReplaySelectedCharacterPresentation(screen, character, groupId);
                return true;
            },
            includeProviderDependencies: true);

        ModLog.Info($"已从隔离依赖路径重建完整 DLL 皮肤 {character.Id.Entry} 的选角展示。");
    }

    private static void RestoreCharacterInfoText(
        NCharacterSelectScreen screen,
        CharacterModel character)
    {
        try
        {
            var button = FindCharacterButton(screen, character);
            var title = button?.IsLocked == true
                ? new LocString("main_menu_ui", "CHARACTER_SELECT.locked.title").GetFormattedText()
                : new LocString("characters", character.CharacterSelectTitle).GetFormattedText();
            var nameLabel = CharacterNameField.GetValue(screen);
            AccessTools.Method(nameLabel?.GetType(), "SetTextAutoSize", [typeof(string)])?
                .Invoke(nameLabel, [title]);

            if (CharacterDescriptionField.GetValue(screen) is RichTextLabel description)
            {
                description.Text = button?.IsLocked == true
                    ? character.GetUnlockText().GetFormattedText()
                    : new LocString(
                        "characters",
                        character.CharacterSelectDesc).GetFormattedText();
            }

            var relic = character.StartingRelics.FirstOrDefault();
            if (relic == null)
            {
                return;
            }

            var isLocked = button?.IsLocked == true;
            if (CharacterRelicTitleField.GetValue(screen) is RichTextLabel relicTitle)
            {
                relicTitle.Text = isLocked
                    ? new LocString(
                        "main_menu_ui",
                        "CHARACTER_SELECT.lockedRelic.title").GetFormattedText()
                    : relic.Title.GetFormattedText();
            }

            if (CharacterRelicDescriptionField.GetValue(screen) is RichTextLabel relicDescription)
            {
                relicDescription.Text = isLocked
                    ? new LocString(
                        "main_menu_ui",
                        "CHARACTER_SELECT.lockedRelic.description").GetFormattedText()
                    : relic.DynamicDescription.GetFormattedText();
            }

            if (CharacterRelicIconField.GetValue(screen) is TextureRect relicIcon)
            {
                relicIcon.Texture = relic.Icon;
                relicIcon.SelfModulate = isLocked ? StsColors.ninetyPercentBlack : Colors.White;
            }

            if (CharacterRelicIconOutlineField.GetValue(screen) is TextureRect relicIconOutline)
            {
                relicIconOutline.Texture = relic.IconOutline;
                relicIconOutline.SelfModulate = isLocked
                    ? StsColors.halfTransparentWhite
                    : StsColors.halfTransparentBlack;
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn($"恢复 {character.Id.Entry} 的选角文字失败：{exception.GetBaseException().Message}");
        }
    }

    private static void ReplaySelectedCharacterPresentation(
        NCharacterSelectScreen screen,
        CharacterModel character,
        string groupId)
    {
        var providerId = SkinService.GetSelectedFullRuntimeProvider(groupId);
        if (providerId == null)
        {
            return;
        }

        var existingNodes = EnumerateNodes(screen)
            .Select(node => node.GetInstanceId())
            .ToHashSet();
        var button = FindCharacterButton(screen, character);
        if (button != null)
        {
            ManagedSkinModLoader.ReplaySelectedCharacterPresentation(
                providerId,
                screen,
                button,
                character,
                () => StabilizeProviderCharacterSelectControls(screen, existingNodes, providerId));
        }

        var animatedBackground = screen.GetNodeOrNull<Node>("AnimatedBg");
        var sceneRoot = animatedBackground?.GetChildCount() > 0
            ? animatedBackground.GetChild(0)
            : null;
        if (sceneRoot != null)
        {
            ManagedCharacterAnimationBridge.TryStartCharacterSelectLoops(sceneRoot, providerId);
        }
    }

    private static void RemoveStaleProviderCharacterSelectControls(NCharacterSelectScreen screen)
    {
        var staleRoots = EnumerateNodes(screen)
            .OfType<Control>()
            .Where(control => control.Name.ToString().EndsWith(
                "CharacterSelectOptionsPanel",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var staleRoot in staleRoots)
        {
            var parent = staleRoot.GetParent();
            if (parent != null && GodotObject.IsInstanceValid(parent))
            {
                parent.RemoveChildSafely(staleRoot);
            }

            staleRoot.QueueFreeSafely();
        }

        if (staleRoots.Length > 0)
        {
            ModLog.Info($"已清理 {staleRoots.Length} 个上一角色遗留的选角交互面板。");
        }
    }

    /// <summary>
    /// Some complete DLL skins create their own compact character-select toolbar from the
    /// SelectCharacter callback (background variant, presentation mode, and similar controls).
    /// UI replacements may clip that toolbar or draw a later full-screen layer above it. Keep
    /// newly replayed interactive roots visible and above presentation art without recognizing a
    /// particular Mod, node name, or button label.
    /// </summary>
    private static void StabilizeProviderCharacterSelectControls(
        NCharacterSelectScreen screen,
        IReadOnlySet<ulong> existingNodes,
        string providerId)
    {
        var interactiveRoots = EnumerateNodes(screen)
            .Where(node => !existingNodes.Contains(node.GetInstanceId()))
            .OfType<Control>()
            .Where(root =>
                root.Name.ToString().Contains("CharacterSelectOptionsPanel", StringComparison.OrdinalIgnoreCase) ||
                root.GetNodeOrNull<Node>("Options") != null ||
                root.GetNodeOrNull<Node>("ButtonContainer") != null)
            .ToArray();
        if (interactiveRoots.Length == 0)
        {
            return;
        }

        foreach (var root in interactiveRoots)
        {
            StabilizeProviderCharacterSelectControl(screen, root);
        }

        foreach (var root in interactiveRoots)
        {
            var parent = root.GetParent();
            var parentVisible = parent is CanvasItem parentCanvas ? parentCanvas.Visible : true;
            var ancestry = new List<string>();
            for (Node? node = root; node != null && node != screen; node = node.GetParent())
            {
                if (node is Control ancestor)
                {
                    ancestry.Add(
                        $"{ancestor.Name}:v={ancestor.Visible},clip={ancestor.ClipContents}," +
                        $"global={ancestor.GlobalPosition},size={ancestor.Size},a={ancestor.Modulate.A:0.###}");
                }
            }

            var childStates = root.GetChildren()
                .OfType<CanvasItem>()
                .Select(child => $"{child.Name}:v={child.Visible},a={child.Modulate.A:0.###}")
                .ToArray();
            ModLog.Info(
                $"选角交互面板诊断 provider={providerId} 节点={root.GetPath()} " +
                $"visible={root.Visible} parentVisible={parentVisible} " +
                $"position={root.Position} global={root.GlobalPosition} size={root.Size} z={root.ZIndex} " +
                $"alpha={root.Modulate.A:0.###} " +
                $"ancestors=[{string.Join(" | ", ancestry)}] " +
                $"children=[{string.Join(" | ", childStates)}]");
        }

        // Container layouts and third-party UI patches can run after the provider callback. Apply
        // the same normalization once more after the tree has settled; this changes no authored
        // size or position unless the whole control ended up outside the visible viewport.
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(screen))
            {
                return;
            }

            foreach (var root in interactiveRoots)
            {
                if (GodotObject.IsInstanceValid(root) && root.IsInsideTree())
                {
                    StabilizeProviderCharacterSelectControl(screen, root);
                }
            }
        }).CallDeferred();

        ModLog.Info($"已保留 {providerId} 的 {interactiveRoots.Length} 个选角交互面板。");
    }

    private static void StabilizeProviderCharacterSelectControl(
        NCharacterSelectScreen screen,
        Control control)
    {
        control.Visible = true;
        control.ZAsRelative = false;
        control.ZIndex = Math.Max(control.ZIndex, 100);
        control.MoveToFront();

        // Keep the provider-authored parent and transform. Reparenting or clamping to the
        // viewport changes the intentional CZN toolbar placement and was the source of several
        // historical "button moved" regressions. Only remove clipping on the newly-created
        // toolbar itself; its ancestors remain owned by the game/provider.
        control.ClipContents = false;

        // CZN-style toolbars are intentionally placed beside the game's bottom button strip.
        // That strip may be a clipped Control, so a panel can be fully outside its parent while
        // still reporting Visible=true. Disable only that clipping boundary and let the mutation
        // tracker restore the authored value when the provider is deselected.
        if (control.GetParent() is Control parent &&
            parent.ClipContents &&
            IsOutsideParentRect(control, parent))
        {
            parent.ClipContents = false;
            ModLog.Info(
                $"已解除选角交互面板父容器裁剪：节点={control.GetPath()} " +
                $"父容器={parent.GetPath()} global={control.GlobalPosition} " +
                $"parentGlobal={parent.GlobalPosition} parentSize={parent.Size}");
        }
    }

    private static bool IsOutsideParentRect(Control control, Control parent)
    {
        var controlPosition = control.GlobalPosition;
        var controlSize = control.Size;
        var parentPosition = parent.GlobalPosition;
        var parentSize = parent.Size;
        return controlPosition.X < parentPosition.X ||
               controlPosition.Y < parentPosition.Y ||
               controlPosition.X + controlSize.X > parentPosition.X + parentSize.X ||
               controlPosition.Y + controlSize.Y > parentPosition.Y + parentSize.Y;
    }

    private static void RebuildRuntimeProviderCharacterDisplay(
        NCharacterSelectScreen screen,
        CharacterModel character)
    {
        var scenePath = character.CharacterSelectBg;
        // 提供者 PCK 未被全局挂载，游戏 AssetCache 可能已缓存过加载失败（"Asset previously
        // failed to load"）。绕过它直接加载，成功后把结果写回缓存以治愈失效条目。
        var scene = ResourceLoader.Load<PackedScene>(
            scenePath,
            null,
            ResourceLoader.CacheMode.IgnoreDeep);
        if (scene != null)
        {
            TakeOverCharacterSelectSceneCache(scenePath, scene);
            PreloadManager.Cache.SetAsset(scenePath, scene);
        }
        else
        {
            scene = PreloadManager.Cache.GetScene(scenePath);
        }

        ReplaceCharacterBackground(screen, character, scene);

        var button = FindCharacterButton(screen, character);
        if (button != null)
        {
            button.GetNode<TextureRect>("%Icon").Texture = button.IsLocked
                ? character.CharacterSelectLockedIcon
                : character.CharacterSelectIcon;
        }

        ModLog.Info($"已由 DLL 皮肤提供器重建 {character.Id.Entry} 的选角展示。");
    }

    private static void TakeOverCharacterSelectSceneCache(string scenePath, PackedScene scene)
    {
        // Resource.take_over_path replaces the ResourceLoader cache entry itself.  The game's
        // PreloadManager cache is separate and is updated as well because base-game and skin
        // providers use both loading routes.  The next skin rebuild repeats this operation with
        // its own isolated scene, so the ownership follows the active selection instead of a
        // provider that happened to load first at startup.
        scene.TakeOverPath(scenePath);
        PreloadManager.Cache.SetAsset(scenePath, scene);
    }

    private static void ReplaceCharacterBackground(
        NCharacterSelectScreen screen,
        CharacterModel character,
        PackedScene scene,
        IReadOnlyDictionary<string, Resource>? isolatedResources = null)
    {
        var container = screen.GetNodeOrNull<Control>("AnimatedBg");
        if (container == null)
        {
            ModLog.Error("选角界面缺少 AnimatedBg 节点，无法替换角色背景。");
            return;
        }

        // Provider presentation callbacks are allowed to temporarily hide this host while their
        // own full-screen layer is selected. A normal Skin Changer rebuild always owns this host.
        container.Visible = true;

        foreach (var child in container.GetChildren())
        {
            container.RemoveChildSafely(child);
            child.QueueFreeSafely();
        }

        var background = scene.Instantiate<Control>(PackedScene.GenEditState.Disabled);
        if (isolatedResources != null)
        {
            RebindCharacterSceneResources(background, isolatedResources);
        }

        background.Name = character.Id.Entry + "_bg";
        container.AddChildSafely(background);

        if (background.IsInsideTree())
        {
            RefreshCharacterBackgroundLayout(container, background);
        }

        // NCharacterSelectScreenBg only subscribes to SizeChanged in _Ready; it does not run
        // that layout calculation for a scene added after the viewport was already sized. The
        // game therefore leaves hot-swapped backgrounds at their default scale. Replay the
        // game's own private layout method after _Ready, without imposing any skin-authored
        // position or scale on the child scene.
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(container) &&
                GodotObject.IsInstanceValid(background))
            {
                RefreshCharacterBackgroundLayout(container, background);
            }
        }).CallDeferred();
    }

    internal static void CaptureCharacterBackgroundHostLayout(NCharacterSelectScreen screen)
    {
        if (!GodotObject.IsInstanceValid(screen) ||
            CharacterBackgroundHostLayouts.TryGetValue(screen, out _))
        {
            return;
        }

        var container = screen.GetNodeOrNull<Control>("AnimatedBg");
        if (container != null)
        {
            CharacterBackgroundHostLayouts.Add(
                screen,
                CharacterBackgroundHostLayout.Capture(container));
        }
    }

    private static void RestoreCharacterBackgroundHostLayout(NCharacterSelectScreen screen)
    {
        var container = screen.GetNodeOrNull<Control>("AnimatedBg");
        if (container == null)
        {
            return;
        }

        if (!CharacterBackgroundHostLayouts.TryGetValue(screen, out var layout))
        {
            CaptureCharacterBackgroundHostLayout(screen);
            return;
        }

        if (!layout.Matches(container))
        {
            ModLog.Info(
                $"已清除选角背景容器遗留变换：" +
                $"位置={container.Position}，缩放={container.Scale}，旋转={container.Rotation:F3}。" );
        }

        layout.Apply(container);
        container.Visible = true;
    }

    private static void RebindCharacterSceneResources(
        Node background,
        IReadOnlyDictionary<string, Resource> isolatedResources)
    {
        var rebound = 0;
        foreach (var node in EnumerateNodes(background))
        {
            // SpineSprite is a native extension class, so use its stable Godot method instead of
            // depending on a concrete managed node type. The property currently contains the
            // canonical resource chosen while PackedScene was decoded; replace it before _Ready
            // initializes the skeleton.
            if (!node.HasMethod("set_skeleton_data_res"))
            {
                continue;
            }

            var current = node.Get("skeleton_data_res").AsGodotObject() as Resource;
            if (current == null ||
                string.IsNullOrWhiteSpace(current.ResourcePath) ||
                !isolatedResources.TryGetValue(current.ResourcePath, out var replacement) ||
                ReferenceEquals(current, replacement))
            {
                continue;
            }

            node.Call("set_skeleton_data_res", replacement);
            rebound++;
        }

        if (rebound > 0)
        {
            ModLog.Info($"选角场景已在进树前重新绑定 {rebound} 个隔离骨骼资源。");
        }
    }

    private static void RefreshCharacterBackgroundLayout(Control container, Control background)
    {
        if (CharacterBackgroundWindowChangeMethod == null ||
            container is not NCharacterSelectScreenBg gameBackground ||
            !GodotObject.IsInstanceValid(gameBackground))
        {
            return;
        }

        try
        {
            // The game owns aspect-ratio fitting on the shared AnimatedBg host. A provider may
            // also use NCharacterSelectScreenBg inside its scene, but the original game merely
            // adds that child and never invokes its private window callback. Calling it here a
            // second time scales some custom previews around their own pivot and moves them down
            // and right.
            CharacterBackgroundWindowChangeMethod.Invoke(gameBackground, null);
        }
        catch (Exception exception)
        {
            ModLog.Warn($"刷新选角背景的宽高比布局失败：{exception.GetBaseException().Message}");
        }

        if (GodotObject.IsInstanceValid(background))
        {
            background.QueueRedraw();
        }
    }

    private static IEnumerable<Node> EnumerateNodes(Node root)
    {
        yield return root;
        foreach (var child in root.GetChildren())
        {
            foreach (var descendant in EnumerateNodes(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RefreshCharacterButtonIcon(
        NCharacterSelectScreen screen,
        CharacterModel character)
    {
        var button = FindCharacterButton(screen, character);
        if (button == null)
        {
            return;
        }

        try
        {
            // Use the same CharacterModel property as the game. The final-result Harmony
            // patch below applies an explicit icon pack, while a code-driven full skin can
            // still provide its own icon when the setting is "follow character skin".
            button.GetNode<TextureRect>("%Icon").Texture = button.IsLocked
                ? character.CharacterSelectLockedIcon
                : character.CharacterSelectIcon;
            ModLog.Info($"已刷新 {character.Id.Entry} 的角色列表头像。");
        }
        catch (Exception exception)
        {
            ModLog.Warn(
                $"刷新 {character.Id.Entry} 的角色列表头像失败：" +
                exception.GetBaseException().Message);
        }
    }

    private static NCharacterSelectButton? FindCharacterButton(
        NCharacterSelectScreen screen,
        CharacterModel character) =>
        FindDescendant<NCharacterSelectButton>(screen, candidate =>
            candidate.Character?.Id.Entry.Equals(character.Id.Entry, StringComparison.OrdinalIgnoreCase) == true);

    private static T? FindDescendant<T>(Node root, Func<T, bool> predicate) where T : Node
    {
        foreach (var child in root.GetChildren())
        {
            if (child is T match && predicate(match))
            {
                return match;
            }

            var descendant = FindDescendant(child, predicate);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static void RebuildMonsterDisplay(
        NBestiary screen,
        NBestiaryEntry entry,
        MonsterModel monster,
        string groupId)
    {
        var visualsPath = GetMonsterVisualsPath(monster);
        var scene = SkinService.LoadRuntimeScene(groupId, visualsPath);
        PreloadManager.Cache.SetAsset(visualsPath, scene);

        // 置空选中项绕过 SelectMonster 的同项短路；SelectMonster 的 Setup 阶段会重建生物，
        // 走 MonsterModel.CreateVisuals 的接管补丁从而应用新皮肤。
        try
        {
            _refreshingMonsterDisplay = true;
            BestiarySelectedEntryField.SetValue(screen, null);
            BestiarySelectMonsterMethod.Invoke(screen, [entry]);
        }
        finally
        {
            _refreshingMonsterDisplay = false;
        }

        ModLog.Info($"已完整重建 {monster.Id.Entry} 的图鉴展示。");
    }

    internal static string GetMonsterVisualsPath(MonsterModel monster)
    {
        try
        {
            if (MonsterVisualsPathGetter.Invoke(monster, null) is string path &&
                !string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn($"无法读取 {monster.Id.Entry} 的实际 VisualsPath，将使用默认场景路径：{exception.Message}");
        }

        return CanonicalScenePath("creature_visuals/" + monster.Id.Entry.ToLowerInvariant());
    }

    internal static SkinGroup? FindGroup(string modelId, string? modelTypeName = null)
    {
        var tokens = BuildModelIdentityTokens(modelId, modelTypeName);
        return SkinService.Catalog?.Groups.FirstOrDefault(group =>
            tokens.Contains(NormalizeToken(group.Id)));
    }

    internal static bool MatchesGroupIdentity(
        string groupId,
        string modelId,
        string? modelTypeName = null) =>
        BuildModelIdentityTokens(modelId, modelTypeName).Contains(NormalizeToken(groupId));

    private static HashSet<string> BuildModelIdentityTokens(
        string modelId,
        string? modelTypeName) =>
        new[] { modelId, modelTypeName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizeToken(value!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeToken(string value) => NonAlphanumericRegex().Replace(value, string.Empty).ToLowerInvariant();

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NonAlphanumericRegex();

    internal static void ReplaceCreatedVisuals(
        string modelId,
        string visualsPath,
        ref NCreatureVisuals result) =>
        ReplaceCreatedVisuals(modelId, null, visualsPath, ref result);

    internal static void ReplaceCreatedVisuals(
        string modelId,
        string? modelTypeName,
        string visualsPath,
        ref NCreatureVisuals result)
    {
        var group = FindGroup(modelId, modelTypeName);
        if (group == null || ShouldSkipExternalRuntimeRedirect(group.Id))
        {
            return;
        }

        // Framework CombatVisual scenes are already converted by FrameworkCombatVisualPatch
        // through the same BaseLib NodeFactory contract as their original manager. Replacing the
        // result a second time here would instantiate a permitted plain Node2D as NCreatureVisuals.
        if (SkinService.TryGetSelectedFrameworkContract(group.Id, out _))
        {
            return;
        }

        try
        {
            // Instantiate while the selected player's temporary overlay is mounted. Binary
            // scenes can defer external-resource resolution until Instantiate; loading a
            // PackedScene first and instantiating it after the overlay is restored lets another
            // player's skin occupy the same canonical path in between the two operations.
            var replacement = SkinService.InstantiateRuntimeScene<NCreatureVisuals>(
                group.Id,
                visualsPath,
                () => RuntimeMonsterVisualModeBridge.ApplySelected(group.Id));
            var copied = ManagedSceneCompatibility.CopyMissingUniqueNodes(result, replacement);
            if (copied > 0)
            {
                ModLog.Info($"已为 {modelId} 的替换场景补齐 {copied} 个游戏必需节点。");
            }
            result?.QueueFree();
            result = replacement;
        }
        catch (Exception exception)
        {
            ModLog.Error($"最终应用 {modelId} 的场景皮肤失败：{exception}");
        }
    }

    /// <summary>
    /// Applies the final visual selected for one concrete creature.  Multiplayer character
    /// selections are owned by Player.NetId, not CharacterModel, so the owner-aware Creature
    /// patch must be the last writer when a per-player selection scope is active.
    /// </summary>
    internal static void ReplaceCreatedCreatureVisuals(
        Creature creature,
        ref NCreatureVisuals? result)
    {
        if (result == null || MultiplayerSkinSync.GetScopedSelections() == null)
        {
            return;
        }

        var visuals = result;
        if (creature.Player != null)
        {
            var character = creature.Player.Character;
            ReplaceCreatedVisuals(
                character.Id.Entry,
                character.GetType().Name,
                CanonicalScenePath("creature_visuals/" + character.Id.Entry.ToLowerInvariant()),
                ref visuals);
            ApplySelectedProviderVisualPostfix(
                character.Id.Entry,
                character.GetType().Name,
                character,
                ref visuals);
        }
        else if (creature.Monster != null)
        {
            var monster = creature.Monster;
            ReplaceCreatedVisuals(
                monster.Id.Entry,
                monster.GetType().Name,
                GetMonsterVisualsPath(monster),
                ref visuals);
            ApplySelectedProviderVisualPostfix(
                monster.Id.Entry,
                monster.GetType().Name,
                monster,
                ref visuals);
            MarkAndApplyMonsterScale(monster.Id.Entry, monster.GetType().Name, visuals);
        }

        result = visuals;
    }

    internal static void ApplySelectedProviderVisualPostfix(
        string modelId,
        string? modelTypeName,
        object model,
        ref NCreatureVisuals visuals)
    {
        var group = FindGroup(modelId, modelTypeName);
        if (group != null)
        {
            // A provider's replayed CreateVisuals postfix may instantiate an auxiliary scene or
            // resolve a private attachment after the main visual has been created.  For a remote
            // player that work must see the same canonical overlay as the main scene; otherwise
            // only the root model is isolated and the provider can still bind the local player's
            // skin.  Local creations keep the existing fast path.
            IDisposable? resourceScope = null;
            if (MultiplayerSkinSync.GetScopedSelection(group.Id) != null)
            {
                var scenePath = model is MonsterModel monster
                    ? GetMonsterVisualsPath(monster)
                    : CanonicalScenePath("creature_visuals/" + modelId.ToLowerInvariant());
                try
                {
                    resourceScope = SkinService.BeginRuntimeResourceScope(group.Id, scenePath);
                }
                catch (Exception exception)
                {
                    ModLog.Warn($"为联机玩家挂载 {group.Id} 的视觉后处理资源失败：{exception.Message}");
                }
            }

            try
            {
                SkinService.ApplySelectedVisualPostfix(group.Id, model, ref visuals);
            }
            finally
            {
                resourceScope?.Dispose();
            }
        }
    }

    internal static void MarkAndApplyMonsterScale(
        string modelId,
        string modelTypeName,
        NCreatureVisuals visuals)
    {
        var group = FindGroup(modelId, modelTypeName);
        if (group == null)
        {
            return;
        }

        visuals.SetMeta(MonsterScaleGroupMeta, group.Id);
        CaptureMonsterBaseScale(visuals);
        ApplyStoredMonsterScale(visuals, group.Id);
    }

    internal static void ReapplyMonsterScaleAfterGameScale(NCreatureVisuals visuals)
    {
        if (!visuals.HasMeta(MonsterScaleGroupMeta))
        {
            return;
        }

        var groupId = visuals.GetMeta(MonsterScaleGroupMeta).AsString();
        CaptureMonsterBaseScale(visuals);
        ApplyStoredMonsterScale(visuals, groupId);
    }

    internal static float GetAppliedMonsterScaleFactor(NCreatureVisuals visuals)
    {
        if (!visuals.HasMeta(MonsterScaleGroupMeta))
        {
            return 1f;
        }

        return visuals.GetMeta(MonsterAppliedScaleMeta, 1f).AsSingle();
    }

    internal static bool IsMonsterScaleManaged(NCreatureVisuals visuals) =>
        visuals.HasMeta(MonsterScaleGroupMeta);

    internal static void ApplyMonsterScalePreview(NCreatureVisuals visuals, float factor)
    {
        if (!visuals.HasMeta(MonsterScaleGroupMeta))
        {
            return;
        }

        var normalized = Mathf.Clamp(
            Mathf.Round(factor / SkinService.MonsterScaleStep) * SkinService.MonsterScaleStep,
            SkinService.MinimumMonsterScale,
            SkinService.MaximumMonsterScale);
        ApplyMonsterScaleFactor(visuals, normalized);
    }

    private static void CaptureMonsterBaseScale(NCreatureVisuals visuals)
    {
        visuals.SetMeta(MonsterBaseScaleMeta, visuals.Scale);
        visuals.SetMeta(MonsterBaseDefaultScaleMeta, visuals.DefaultScale);
    }

    private static void ApplyStoredMonsterScale(NCreatureVisuals visuals, string groupId)
    {
        ApplyMonsterScaleFactor(visuals, SkinService.GetSelectedMonsterScale(groupId));
    }

    private static void ApplyMonsterScaleFactor(NCreatureVisuals visuals, float factor)
    {
        var baseScale = visuals.GetMeta(MonsterBaseScaleMeta, visuals.Scale).AsVector2();
        var baseDefaultScale = visuals
            .GetMeta(MonsterBaseDefaultScaleMeta, visuals.DefaultScale)
            .AsSingle();
        visuals.Scale = baseScale * factor;
        visuals.DefaultScale = baseDefaultScale * factor;
        visuals.SetMeta(MonsterAppliedScaleMeta, factor);
    }

    private static void ApplyMonsterScaleToDescendants(Node root, string groupId)
    {
        if (root is NCreatureVisuals visuals &&
            visuals.GetMeta(MonsterScaleGroupMeta, string.Empty).AsString()
                .Equals(groupId, StringComparison.OrdinalIgnoreCase))
        {
            ApplyStoredMonsterScale(visuals, groupId);
        }

        foreach (var child in root.GetChildren())
        {
            ApplyMonsterScaleToDescendants(child, groupId);
        }
    }

    internal static void ReplaceCachedScene(string resourcePath, ref PackedScene result)
    {
        var groupId = SkinService.Catalog?.FindGroupIdForResourcePath(resourcePath);
        // Character-select scenes are instantiated by the game (and by a few compatibility
        // libraries) immediately after AssetCache.GetScene returns. Returning a temporary alias
        // here makes their PackedScene resolve external skeleton/layout resources after the alias
        // pack has already been restored. The deferred character-screen rebuild owns this path,
        // so leave the cache result alone and let it replace the child under the correct mount.
        if (groupId == null ||
            IsCreatureVisualScenePath(resourcePath) ||
            IsCharacterSelectBackgroundPath(resourcePath) ||
            ShouldSkipExternalRuntimeRedirect(groupId))
        {
            return;
        }

        try
        {
            result = SkinService.GetOrLoadRuntimeScene(groupId, resourcePath);
        }
        catch (Exception exception)
        {
            ModLog.Error($"最终接管场景 {resourcePath} 失败：{exception}");
        }
    }

    private static bool IsCharacterSelectBackgroundPath(string resourcePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(resourcePath);
        return fileName.StartsWith("char_select_bg_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCreatureVisualScenePath(string resourcePath) =>
        resourcePath.Contains("/creature_visuals/", StringComparison.OrdinalIgnoreCase);

    internal static void ReplaceCachedTexture(string resourcePath, ref Texture2D result)
    {
        var groupId = SkinService.Catalog?.FindGroupIdForResourcePath(resourcePath);
        if (groupId == null || ShouldSkipExternalRuntimeRedirect(groupId))
        {
            return;
        }

        try
        {
            result = SkinService.GetOrLoadRuntimeResource(groupId, resourcePath) as Texture2D ??
                     throw new InvalidOperationException($"独立皮肤资源不是贴图：{resourcePath}");
        }
        catch (Exception exception)
        {
            ModLog.Error($"最终接管贴图 {resourcePath} 失败：{exception}");
        }
    }

    internal static void ReplaceCharacterSelectTexture(
        CharacterModel character,
        bool locked,
        ref CompressedTexture2D result)
    {
        var group = FindGroup(character.Id.Entry);
        if (group == null)
        {
            return;
        }

        var characterId = character.Id.Entry.ToLowerInvariant();
        var resourcePath = CanonicalImagePath(
            "packed/character_select/char_select_" + characterId + (locked ? "_locked.png" : ".png"));
        if (SkinService.ShouldDeferCharacterIconResourceToExternalRuntime(
                group.Id,
                resourcePath))
        {
            return;
        }

        try
        {
            result = SkinService.GetOrLoadCharacterIconResource(group.Id, resourcePath) as CompressedTexture2D ??
                     throw new InvalidOperationException($"角色头像资源不是压缩贴图：{resourcePath}");
        }
        catch (Exception exception)
        {
            ModLog.Error($"最终接管角色列表贴图 {resourcePath} 失败：{exception}");
        }
    }

    internal static void ReplaceCharacterIcon(CharacterModel character, ref Control result)
    {
        var group = FindGroup(character.Id.Entry);
        if (group == null)
        {
            return;
        }

        var characterId = character.Id.Entry.ToLowerInvariant();
        var resourcePath = CanonicalScenePath("ui/character_icons/" + characterId + "_icon");
        if (SkinService.ShouldDeferCharacterIconResourceToExternalRuntime(
                group.Id,
                resourcePath))
        {
            return;
        }

        try
        {
            var replacement = SkinService.WithCharacterIconResource(
                group.Id,
                resourcePath,
                resource => (resource as PackedScene ??
                             throw new InvalidOperationException(
                                 $"角色头像资源不是场景：{resourcePath}"))
                    .Instantiate<Control>(PackedScene.GenEditState.Disabled),
                includeProviderDependencies: true);
            ManagedSceneCompatibility.CopyMissingUniqueNodes(result, replacement);
            result?.QueueFree();
            result = replacement;
        }
        catch (Exception exception)
        {
            ModLog.Error($"最终接管角色小头像 {resourcePath} 失败：{exception}");
        }
    }

    internal static void ReplaceCharacterTexture(
        CharacterModel character,
        string resourcePath,
        ref Texture2D result)
    {
        var group = FindGroup(character.Id.Entry);
        if (group == null ||
            SkinService.ShouldDeferCharacterIconResourceToExternalRuntime(
                group.Id,
                resourcePath))
        {
            return;
        }

        try
        {
            result = SkinService.GetOrLoadCharacterIconResource(group.Id, resourcePath) as Texture2D ??
                     throw new InvalidOperationException($"角色头像资源不是贴图：{resourcePath}");
        }
        catch (Exception exception)
        {
            ModLog.Error($"最终接管角色界面贴图 {resourcePath} 失败：{exception}");
        }
    }

    private static bool ShouldSkipExternalRuntimeRedirect(string groupId) =>
        MultiplayerSkinSync.GetScopedSelection(groupId) == null &&
        SkinService.IsExternalRuntimeProviderSelected(groupId);

    internal static bool RefreshMultiplayerPlayerIcons(ulong playerNetId)
    {
        try
        {
            // The lobby and the in-run HUD are not always children of the same NGame node
            // during the hand-off into a run.  Resolve the actual SceneTree root first and only
            // use NGame as a fallback; otherwise a selection packet that arrives during that
            // transition silently misses both avatar nodes.
            var root = (Engine.GetMainLoop() as SceneTree)?.Root ??
                       NGame.Instance?.GetTree().Root;
            if (root == null)
            {
                return false;
            }

            var stateCount = 0;
            var lobbyCount = 0;
            var voteCount = 0;
            var pending = new Stack<Node>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (node is NMultiplayerPlayerState state &&
                    state.Player?.NetId == playerNetId)
                {
                    if (RefreshMultiplayerPlayerStateIcon(state))
                    {
                        stateCount++;
                    }
                }
                else if (node is NRemoteLobbyPlayer lobbyPlayer &&
                         lobbyPlayer.PlayerId == playerNetId)
                {
                    if (RefreshRemoteLobbyPlayerIcon(lobbyPlayer))
                    {
                        lobbyCount++;
                    }
                }
                else if (node is NMultiplayerVoteContainer voteContainer)
                {
                    voteCount += RefreshMultiplayerVoteIcons(voteContainer, playerNetId);
                }

                foreach (var child in node.GetChildren())
                {
                    pending.Push(child);
                }
            }

            if (stateCount == 0 && lobbyCount == 0 && voteCount == 0)
            {
                // This is expected while the run scene is being rebuilt.  The _Ready patches
                // below apply the same scope when the nodes are created, so no global polling is
                // needed here.
                return false;
            }

            ModLog.Info(
                $"已刷新联机玩家 {playerNetId} 的头像：战斗栏 {stateCount}、" +
                $"选角栏 {lobbyCount}、投票栏 {voteCount}。 ");
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Warn($"刷新联机玩家 {playerNetId} 的头像失败：{exception.GetBaseException().Message}");
            return false;
        }
    }

    private static bool RefreshMultiplayerPlayerStateIcon(NMultiplayerPlayerState state)
    {
        // Prefer the game's already-resolved private field.  During a scene hand-off the
        // unique-name lookup can briefly resolve against the old owner (or return null) even
        // though NMultiplayerPlayerState has a live _characterIcon reference.
        var icon = AccessTools.Field(typeof(NMultiplayerPlayerState), "_characterIcon")
                       ?.GetValue(state) as TextureRect ??
                   state.GetNodeOrNull<TextureRect>("%CharacterIcon");
        // NMultiplayerPlayerState already owns the authoritative Player instance.  Looking it up
        // through NRun reflection here races run creation and was the reason remote HUD avatars
        // stayed on the base texture even though the selection packet had been received.
        var player = state.Player;
        var character = player?.Character;
        if (icon == null || !GodotObject.IsInstanceValid(icon) || player == null || character == null)
        {
            return false;
        }

        using var scope = MultiplayerSkinSync.BeginPlayerSelectionScope(player.NetId);
        icon.Texture = TryLoadManagedCharacterIconTexture(character) ?? character.IconTexture;
        return true;
    }

    private static Texture2D? TryLoadManagedCharacterIconTexture(CharacterModel character)
    {
        var group = FindGroup(character.Id.Entry, character.GetType().Name) ??
                    FindGroup(character.Id.Entry);
        var path = CanonicalImagePath(
            "ui/top_panel/character_icon_" + character.Id.Entry.ToLowerInvariant() + ".png");
        if (group == null ||
            SkinService.ShouldDeferCharacterIconResourceToExternalRuntime(group.Id, path))
        {
            return null;
        }

        try
        {
            var resource = SkinService.GetOrLoadCharacterIconResource(group.Id, path);
            if (resource is Texture2D texture)
            {
                return texture;
            }

            LogMissingMultiplayerIcon(group.Id, path, $"资源类型={resource.GetType().Name}");
            return null;
        }
        catch (Exception exception)
        {
            // Some skins intentionally omit a separate top-panel icon.  In that case the
            // game's current IconTexture (and any provider-specific presentation) is the
            // correct fallback; do not turn a missing optional icon into a lobby error.
            LogMissingMultiplayerIcon(group.Id, path, exception.GetBaseException().Message);
            return null;
        }
    }

    private static void LogMissingMultiplayerIcon(string groupId, string path, string detail)
    {
        var selection = MultiplayerSkinSync.GetScopedSelection(groupId);
        if (selection == null || !selection.StartsWith("__online_", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var key = groupId + "\n" + selection + "\n" + path;
        if (LoggedMissingMultiplayerIcons.Add(key))
        {
            ModLog.Warn(
                $"联机皮肤头像未能从安全资源包加载：分组={groupId}，选项={selection}，" +
                $"路径={path}，原因={detail}。 ");
        }
    }

    private static bool RefreshRemoteLobbyPlayerIcon(NRemoteLobbyPlayer node)
    {
        var character = AccessTools.Field(typeof(NRemoteLobbyPlayer), "_character")
            ?.GetValue(node) as CharacterModel;
        if (character == null)
        {
            return false;
        }

        using var scope = MultiplayerSkinSync.BeginPlayerSelectionScope(node.PlayerId);
        var icon = AccessTools.Field(typeof(NRemoteLobbyPlayer), "_characterIcon")
                       ?.GetValue(node) as TextureRect ??
                   node.GetNodeOrNull<TextureRect>("%CharacterIcon");
        if (icon == null || !GodotObject.IsInstanceValid(icon))
        {
            return false;
        }

        // Keep an explicit assignment instead of invoking RefreshVisuals.  RefreshVisuals is
        // also called by the game's lobby callbacks; invoking it here caused a synchronous
        // re-entry loop while the room was being created.
        icon.Texture = TryLoadManagedCharacterIconTexture(character) ?? character.IconTexture;
        return true;
    }

    internal static void RefreshMultiplayerVoteIcons(NMultiplayerVoteContainer container) =>
        RefreshMultiplayerVoteIcons(container, playerNetId: null);

    private static int RefreshMultiplayerVoteIcons(
        NMultiplayerVoteContainer container,
        ulong? playerNetId)
    {
        var refreshed = 0;
        try
        {
            var votes = AccessTools.Field(typeof(NMultiplayerVoteContainer), "_votes")
                ?.GetValue(container) as System.Collections.IEnumerable;
            if (votes == null)
            {
                return 0;
            }

            foreach (var vote in votes)
            {
                if (vote == null)
                {
                    continue;
                }

                var voteType = vote.GetType();
                var player = AccessTools.Field(voteType, "player")?.GetValue(vote) as Player;
                var node = AccessTools.Field(voteType, "node")?.GetValue(vote) as TextureRect;
                if (player == null || node == null || !GodotObject.IsInstanceValid(node))
                {
                    continue;
                }
                if (playerNetId.HasValue && player.NetId != playerNetId.Value)
                {
                    continue;
                }

                using var scope = MultiplayerSkinSync.BeginPlayerSelectionScope(player.NetId);
                node.Texture = TryLoadManagedCharacterIconTexture(player.Character) ??
                               player.Character.IconTexture;
                var outline = node.GetNodeOrNull<TextureRect>("Outline");
                if (outline != null && GodotObject.IsInstanceValid(outline))
                {
                    outline.Texture = player.Character.IconOutlineTexture;
                }
                refreshed++;
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("刷新联机投票头像失败：" + exception.GetBaseException().Message);
        }

        return refreshed;
    }

    internal static void ReplaceCharacterMapMarker(
        CharacterModel character,
        ref CompressedTexture2D result)
    {
        var characterId = character.Id.Entry.ToLowerInvariant();
        var resourcePath = CanonicalImagePath("packed/map/icons/map_marker_" + characterId + ".png");
        Texture2D texture = result;
        ReplaceCharacterTexture(character, resourcePath, ref texture);
        if (texture is CompressedTexture2D compressedTexture)
        {
            result = compressedTexture;
        }
    }

    private sealed record CharacterBackgroundHostLayout(
        float AnchorLeft,
        float AnchorTop,
        float AnchorRight,
        float AnchorBottom,
        float OffsetLeft,
        float OffsetTop,
        float OffsetRight,
        float OffsetBottom,
        Control.GrowDirection GrowHorizontal,
        Control.GrowDirection GrowVertical,
        Vector2 PivotOffset,
        Vector2 Scale,
        float Rotation)
    {
        internal static CharacterBackgroundHostLayout Capture(Control control) => new(
            control.AnchorLeft,
            control.AnchorTop,
            control.AnchorRight,
            control.AnchorBottom,
            control.OffsetLeft,
            control.OffsetTop,
            control.OffsetRight,
            control.OffsetBottom,
            control.GrowHorizontal,
            control.GrowVertical,
            control.PivotOffset,
            control.Scale,
            control.Rotation);

        internal bool Matches(Control control) =>
            Mathf.IsEqualApprox(control.AnchorLeft, AnchorLeft) &&
            Mathf.IsEqualApprox(control.AnchorTop, AnchorTop) &&
            Mathf.IsEqualApprox(control.AnchorRight, AnchorRight) &&
            Mathf.IsEqualApprox(control.AnchorBottom, AnchorBottom) &&
            Mathf.IsEqualApprox(control.OffsetLeft, OffsetLeft) &&
            Mathf.IsEqualApprox(control.OffsetTop, OffsetTop) &&
            Mathf.IsEqualApprox(control.OffsetRight, OffsetRight) &&
            Mathf.IsEqualApprox(control.OffsetBottom, OffsetBottom) &&
            control.GrowHorizontal == GrowHorizontal &&
            control.GrowVertical == GrowVertical &&
            control.PivotOffset.IsEqualApprox(PivotOffset) &&
            control.Scale.IsEqualApprox(Scale) &&
            Mathf.IsEqualApprox(control.Rotation, Rotation);

        internal void Apply(Control control)
        {
            control.AnchorLeft = AnchorLeft;
            control.AnchorTop = AnchorTop;
            control.AnchorRight = AnchorRight;
            control.AnchorBottom = AnchorBottom;
            control.OffsetLeft = OffsetLeft;
            control.OffsetTop = OffsetTop;
            control.OffsetRight = OffsetRight;
            control.OffsetBottom = OffsetBottom;
            control.GrowHorizontal = GrowHorizontal;
            control.GrowVertical = GrowVertical;
            control.PivotOffset = PivotOffset;
            control.Scale = Scale;
            control.Rotation = Rotation;
        }
    }
}

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.SelectCharacter))]
internal static class CharacterSelectionSkinPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(NCharacterSelectScreen __instance) =>
        ContextualSkinControls.CaptureCharacterBackgroundHostLayout(__instance);

    [HarmonyPriority(Priority.First)]
    private static void Postfix(NCharacterSelectScreen __instance, CharacterModel characterModel) =>
        ContextualSkinControls.ShowCharacter(__instance, characterModel);
}

[HarmonyPatch(typeof(NCharacterSelectScreen), "StartNewSingleplayerRun")]
internal static class SingleplayerEmbarkSkinSelectorPatch
{
    private static void Prefix(NCharacterSelectScreen __instance)
    {
        try
        {
            var groupId = ContextualSkinControls.GetLocalLobbyCharacter(__instance.Lobby).Id.Entry.ToLowerInvariant();
            if (!SkinService.ApplySelectedCharacterSkinBundleForRun(groupId, out var warnings))
            {
                ModLog.Error($"开始对局前应用皮肤包失败：{SkinService.LastError}");
            }
            foreach (var warning in warnings)
            {
                ModLog.Warn("皮肤包：" + warning);
            }
        }
        catch (Exception error)
        {
            ModLog.Error("开始单人对局前应用皮肤包失败，将继续使用当前外观：" + error);
        }
        ContextualSkinControls.HideCharacterSelector(__instance);
    }
}

[HarmonyPatch(typeof(NCharacterSelectScreen), "StartNewMultiplayerRun")]
internal static class MultiplayerEmbarkSkinSelectorPatch
{
    private static void Prefix(NCharacterSelectScreen __instance)
    {
        try
        {
            var groupId = ContextualSkinControls.GetLocalLobbyCharacter(__instance.Lobby).Id.Entry.ToLowerInvariant();
            if (!SkinService.ApplySelectedCharacterSkinBundleForRun(groupId, out var warnings))
            {
                ModLog.Error($"开始多人对局前应用皮肤包失败：{SkinService.LastError}");
            }
            foreach (var warning in warnings)
            {
                ModLog.Warn("皮肤包：" + warning);
            }
        }
        catch (Exception error)
        {
            ModLog.Error("开始多人对局前应用皮肤包失败，将继续使用当前外观：" + error);
        }
        ContextualSkinControls.HideCharacterSelector(__instance);
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
internal static class CharacterSkinBundleRunCleanupPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        try
        {
            SkinService.RestoreCharacterSkinBundleAfterRun();
        }
        catch (Exception exception)
        {
            // A cosmetic restore must never prevent the game's own run cleanup. The sidecar is
            // deliberately retained so startup recovery can finish the restore on the next boot.
            ModLog.Error("退出对局时恢复皮肤包预设失败，将在下次启动重试：" + exception);
        }
    }
}

[HarmonyPatch(typeof(NBestiary), "SelectMonster")]
internal static class BestiarySelectionSkinPatch
{
    private static void Postfix(NBestiary __instance, NBestiaryEntry entry)
    {
        // 皮肤切换触发的重建会再次进入 SelectMonster，跳过重入以避免重复刷新下拉框。
        if (!ContextualSkinControls.IsRefreshingMonsterDisplay)
        {
            ContextualSkinControls.ShowMonster(__instance, entry);
        }
    }
}

[HarmonyPatch(typeof(NBestiary), nameof(NBestiary.OnSubmenuClosed))]
internal static class BestiaryRuntimeProviderScopePatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (NRun.Instance != null)
        {
            CharacterAppearanceRuntime.FocusRuntimeProviderBehaviorsOnRunContext(
                reason: "关闭局内怪物图鉴", refreshCurrentRoom: true);
            return;
        }

        SkinService.FocusRuntimeProviderBehaviorsOnGroups(
            [],
            runEnvironmentProviderIds: [],
            reason: "关闭怪物图鉴");
    }
}

[HarmonyPatch(typeof(CharacterModel), nameof(CharacterModel.CreateVisuals))]
internal static class CharacterVisualResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CharacterModel __instance, ref NCreatureVisuals __result)
    {
        var group = ContextualSkinControls.FindGroup(
            __instance.Id.Entry,
            __instance.GetType().Name);
        if (group != null && MultiplayerSkinSync.GetScopedSelection(group.Id) != null)
        {
            // The owner-aware Creature.CreateVisuals postfix is the sole final writer for a
            // remote player.  Applying here would reduce the selection back to one value shared
            // by every instance of this CharacterModel.
            return;
        }

        ContextualSkinControls.ReplaceCreatedVisuals(
            __instance.Id.Entry,
            ContextualSkinControls.CanonicalScenePath("creature_visuals/" + __instance.Id.Entry.ToLowerInvariant()),
            ref __result);
        ContextualSkinControls.ApplySelectedProviderVisualPostfix(
            __instance.Id.Entry,
            __instance.GetType().Name,
            __instance,
            ref __result);
    }
}

[HarmonyPatch(typeof(MonsterModel), nameof(MonsterModel.CreateVisuals))]
internal static class MonsterVisualResultPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(MonsterModel __instance)
    {
        var group = ContextualSkinControls.FindGroup(
            __instance.Id.Entry,
            __instance.GetType().Name);
        if (group != null)
        {
            CharacterAppearanceRuntime.AddVisibleCombatRuntimeGroup(group.Id);
        }
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(MonsterModel __instance, ref NCreatureVisuals __result)
    {
        var group = ContextualSkinControls.FindGroup(
            __instance.Id.Entry,
            __instance.GetType().Name);
        if (group != null && MultiplayerSkinSync.GetScopedSelection(group.Id) != null)
        {
            // Pets can also be owned by a remote player.  Their companion group is resolved from
            // the owning player's complete selection transaction by the Creature-level patch.
            return;
        }

        ContextualSkinControls.ReplaceCreatedVisuals(
            __instance.Id.Entry,
            __instance.GetType().Name,
            ContextualSkinControls.GetMonsterVisualsPath(__instance),
            ref __result);
        ContextualSkinControls.ApplySelectedProviderVisualPostfix(
            __instance.Id.Entry,
            __instance.GetType().Name,
            __instance,
            ref __result);
        ContextualSkinControls.MarkAndApplyMonsterScale(
            __instance.Id.Entry,
            __instance.GetType().Name,
            __result);
    }
}

[HarmonyPatch(typeof(NCreatureVisuals), nameof(NCreatureVisuals.SetScaleAndHue))]
internal static class MonsterVisualScalePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCreatureVisuals __instance) =>
        ContextualSkinControls.ReapplyMonsterScaleAfterGameScale(__instance);
}

[HarmonyPatch(typeof(AssetCache), nameof(AssetCache.GetScene))]
internal static class CachedSceneResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(string path, ref PackedScene __result) =>
        ContextualSkinControls.ReplaceCachedScene(path, ref __result);
}

[HarmonyPatch(typeof(AssetCache), nameof(AssetCache.GetTexture2D))]
internal static class CachedTextureResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(string path, ref Texture2D __result) =>
        ContextualSkinControls.ReplaceCachedTexture(path, ref __result);
}

[HarmonyPatch(typeof(CharacterModel), nameof(CharacterModel.CharacterSelectIcon), MethodType.Getter)]
internal static class CharacterSelectIconResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CharacterModel __instance, ref CompressedTexture2D __result) =>
        ContextualSkinControls.ReplaceCharacterSelectTexture(__instance, locked: false, ref __result);
}

[HarmonyPatch(typeof(CharacterModel), nameof(CharacterModel.CharacterSelectLockedIcon), MethodType.Getter)]
internal static class CharacterSelectLockedIconResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CharacterModel __instance, ref CompressedTexture2D __result) =>
        ContextualSkinControls.ReplaceCharacterSelectTexture(__instance, locked: true, ref __result);
}

[HarmonyPatch(typeof(CharacterModel), nameof(CharacterModel.Icon), MethodType.Getter)]
internal static class CharacterIconResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CharacterModel __instance, ref Control __result) =>
        ContextualSkinControls.ReplaceCharacterIcon(__instance, ref __result);
}

[HarmonyPatch(typeof(CharacterModel), nameof(CharacterModel.IconTexture), MethodType.Getter)]
internal static class CharacterIconTextureResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CharacterModel __instance, ref Texture2D __result)
    {
        var characterId = __instance.Id.Entry.ToLowerInvariant();
        var path = ContextualSkinControls.CanonicalImagePath("ui/top_panel/character_icon_" + characterId + ".png");
        ContextualSkinControls.ReplaceCharacterTexture(__instance, path, ref __result);
    }
}

[HarmonyPatch(typeof(CharacterModel), nameof(CharacterModel.IconOutlineTexture), MethodType.Getter)]
internal static class CharacterIconOutlineTextureResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CharacterModel __instance, ref Texture2D __result)
    {
        var characterId = __instance.Id.Entry.ToLowerInvariant();
        var path = ContextualSkinControls.CanonicalImagePath("ui/top_panel/character_icon_" + characterId + "_outline.png");
        ContextualSkinControls.ReplaceCharacterTexture(__instance, path, ref __result);
    }
}

[HarmonyPatch(typeof(CharacterModel), nameof(CharacterModel.MapMarker), MethodType.Getter)]
internal static class CharacterMapMarkerResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CharacterModel __instance, ref CompressedTexture2D __result) =>
        ContextualSkinControls.ReplaceCharacterMapMarker(__instance, ref __result);
}

[HarmonyPatch(typeof(NMultiplayerPlayerState), nameof(NMultiplayerPlayerState._Ready))]
internal static class MultiplayerPlayerStateIconScopePatch
{
    private static void Prefix(NMultiplayerPlayerState __instance, out IDisposable? __state) =>
        __state = __instance.Player == null
            ? null
            : MultiplayerSkinSync.BeginPlayerSelectionScope(__instance.Player.NetId);

    private static void Postfix(
        NMultiplayerPlayerState __instance,
        IDisposable? __state)
    {
        __state?.Dispose();
        if (__instance.Player != null)
        {
            // Queue one pass after the game's own icon assignment.  The queue is drained from
            // the normal process tick, avoiding scene-tree re-entry during _Ready.
            MultiplayerSkinSync.RequestIconRefresh(__instance.Player.NetId);
        }
    }
}

[HarmonyPatch(typeof(NRemoteLobbyPlayer), nameof(NRemoteLobbyPlayer._Ready))]
internal static class RemoteLobbyPlayerIconScopePatch
{
    private static void Prefix(NRemoteLobbyPlayer __instance, out IDisposable? __state) =>
        __state = MultiplayerSkinSync.BeginPlayerSelectionScope(__instance.PlayerId);

    private static void Postfix(
        NRemoteLobbyPlayer __instance,
        IDisposable? __state)
    {
        __state?.Dispose();
        // The deferred queue is the single refresh path.  Calling back into the scene tree from
        // _Ready can re-enter NRemoteLobbyPlayer while the lobby is still adding its children.
        MultiplayerSkinSync.RequestIconRefresh(__instance.PlayerId);
    }
}

// NRemoteLobbyPlayer.RefreshVisuals is also called when the lobby receives a character update,
// after its _Ready callback has already run.  Keep the per-player selection scope around that
// call as well; otherwise the game immediately overwrites a refreshed lobby avatar with the
// unscoped character texture.
[HarmonyPatch(typeof(NRemoteLobbyPlayer), "RefreshVisuals")]
internal static class RemoteLobbyPlayerVisualRefreshScopePatch
{
    private static void Prefix(NRemoteLobbyPlayer __instance, out IDisposable? __state) =>
        __state = MultiplayerSkinSync.BeginPlayerSelectionScope(__instance.PlayerId);

    private static void Postfix(
        NRemoteLobbyPlayer __instance,
        IDisposable? __state)
    {
        __state?.Dispose();
        MultiplayerSkinSync.RequestIconRefresh(__instance.PlayerId);
    }
}

[HarmonyPatch(typeof(NMultiplayerVoteContainer), nameof(NMultiplayerVoteContainer.RefreshPlayerVotes))]
internal static class MultiplayerVoteIconRefreshPatch
{
    private static void Postfix(NMultiplayerVoteContainer __instance) =>
        ContextualSkinControls.RefreshMultiplayerVoteIcons(__instance);
}

[HarmonyPatch(typeof(NMuteInBackgroundHandler), nameof(NMuteInBackgroundHandler._Notification))]
internal static class SkinPopupBackgroundMutePatch
{
    private static bool Prefix(NMuteInBackgroundHandler __instance, int what) =>
        !ContextualSkinControls.ShouldIgnoreBackgroundMute(__instance, what);
}
