using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.Bestiary;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static partial class ContextualSkinControls
{
    private const string SelectorName = "STS2SkinSelector";
    private const string MultiplayerSkinLoadingToggleName = "MultiplayerSkinLoadingToggle";
    private const string MultiplayerSkinLoadingStatusName = "MultiplayerSkinLoadingStatus";
    private const string DropdownName = "SkinDropdown";
    private const string MonsterScaleSliderName = "MonsterScaleSlider";
    private const string MonsterScaleValueName = "MonsterScaleValue";
    private const string MonsterScaleLabelName = "MonsterScaleLabel";
    private const string MonsterScaleResetName = "MonsterScaleReset";
    private const string CharacterRefreshGenerationMeta = "sts2_skin_character_refresh_generation";
    private const string GroupMeta = "sts2_skin_group";
    private const string UpdatingMeta = "sts2_skin_updating";
    private const string MonsterScaleGroupMeta = "sts2_skin_monster_scale_group";
    private const string MonsterBaseScaleMeta = "sts2_skin_monster_base_scale";
    private const string MonsterBaseDefaultScaleMeta = "sts2_skin_monster_base_default_scale";
    private const string MonsterAppliedScaleMeta = "sts2_skin_monster_applied_scale";
    private static readonly Dictionary<ulong, Action> RefreshActions = [];
    private static bool _refreshingMonsterDisplay;
    [ThreadStatic]
    private static bool _refreshingRemoteLobbyVisuals;
    private static Font? _gameFont;
    private static WeakReference<NCharacterSelectScreen>? _multiplayerCharacterSelectScreen;
    private static ulong _lastMultiplayerStatusRefreshMsec;

    internal static bool IsRefreshingMonsterDisplay => _refreshingMonsterDisplay;

    internal static bool IsRefreshingRemoteLobbyVisuals => _refreshingRemoteLobbyVisuals;

    internal static Font? GameFont =>
        _gameFont ??= ResourceLoader.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres");

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
        var selector = EnsureCharacterSelector(screen);
        var group = FindGroup(character.Id.Entry);
        if (group != null && !IsMultiplayerCharacterSelect(screen))
        {
            SkinService.FocusRuntimeProviderBehaviorsOnCharacters([group.Id]);
        }
        RegisterRefresh(selector, group == null ? null : () => RebuildCharacterDisplay(screen, character, group.Id));
        Populate(selector, group);
        RefreshMultiplayerSkinLoadingToggle(screen);
        RefreshMultiplayerSkinLoadingStatus(force: true);
        if (group != null)
        {
            // 游戏的 SelectCharacter 每次点击都会清空 AnimatedBg 并重新实例化原版背景，
            // 所以这里必须每次重建；资源已缓存在 SkinService，重建不会再次写盘或加载。
            ScheduleCharacterRefresh(screen, character, group.Id);
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

        var existing = infoPanel.GetNodeOrNull<HBoxContainer>(SelectorName);
        if (existing != null)
        {
            EnsureMultiplayerSkinLoadingToggle(screen, infoPanel);
            return existing;
        }

        var selector = BuildSelector();
        selector.AnchorLeft = 0.5f;
        selector.AnchorTop = 0;
        selector.AnchorRight = 0.5f;
        selector.AnchorBottom = 0;
        selector.OffsetLeft = -122;
        selector.OffsetTop = -80;
        selector.OffsetRight = 122;
        selector.OffsetBottom = -36;
        infoPanel.AddChild(selector);
        EnsureMultiplayerSkinLoadingToggle(screen, infoPanel);
        ModLocalization.Bind(selector, () => RefreshLocalizedText(selector));
        return selector;
    }

    private static void EnsureMultiplayerSkinLoadingToggle(
        NCharacterSelectScreen screen,
        Control infoPanel)
    {
        var toggle = infoPanel.GetNodeOrNull<CheckButton>(MultiplayerSkinLoadingToggleName);
        var status = infoPanel.GetNodeOrNull<Label>(MultiplayerSkinLoadingStatusName);
        if (!IsMultiplayerCharacterSelect(screen))
        {
            if (toggle != null)
            {
                toggle.Visible = false;
            }
            if (status != null)
            {
                status.Visible = false;
            }
            return;
        }

        _multiplayerCharacterSelectScreen = new WeakReference<NCharacterSelectScreen>(screen);

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
            toggle.SetPressedNoSignal(SkinService.ShouldLoadOtherPlayersCustomSkins());
            toggle.Toggled += SkinService.SetLoadOtherPlayersCustomSkins;
            infoPanel.AddChild(toggle);
            ModLocalization.Bind(toggle, () =>
            {
                toggle.Text = ModLocalization.Get(ModText.LoadOtherPlayersCustomSkins);
                toggle.SetPressedNoSignal(SkinService.ShouldLoadOtherPlayersCustomSkins());
            });
        }

        if (status == null)
        {
            status = new Label
            {
                Name = MultiplayerSkinLoadingStatusName,
                AnchorLeft = 0.5f,
                AnchorTop = 0f,
                AnchorRight = 0.5f,
                AnchorBottom = 0f,
                OffsetLeft = -280f,
                OffsetTop = -166f,
                OffsetRight = 280f,
                OffsetBottom = -126f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                Visible = false
            };
            status.AddThemeColorOverride("font_color", new Color("f0c951"));
            status.AddThemeColorOverride("font_outline_color", new Color("332f27"));
            status.AddThemeConstantOverride("outline_size", 3);
            status.AddThemeFontSizeOverride("font_size", 17);
            if (GameFont != null)
            {
                status.AddThemeFontOverride("font", GameFont);
            }
            infoPanel.AddChild(status);
        }

        toggle.Visible = true;
        RefreshMultiplayerSkinLoadingStatus(force: true);
    }

    private static void RefreshMultiplayerSkinLoadingToggle(NCharacterSelectScreen screen)
    {
        var toggle = screen.GetNodeOrNull<CheckButton>(
            $"InfoPanel/{MultiplayerSkinLoadingToggleName}");
        if (toggle != null)
        {
            toggle.Visible = IsMultiplayerCharacterSelect(screen);
            toggle.SetPressedNoSignal(SkinService.ShouldLoadOtherPlayersCustomSkins());
        }
    }

    internal static void RefreshMultiplayerSkinLoadingStatus(bool force = false)
    {
        var now = Time.GetTicksMsec();
        if (!force && now - _lastMultiplayerStatusRefreshMsec < 100)
        {
            return;
        }
        _lastMultiplayerStatusRefreshMsec = now;

        if (_multiplayerCharacterSelectScreen == null ||
            !_multiplayerCharacterSelectScreen.TryGetTarget(out var screen) ||
            !GodotObject.IsInstanceValid(screen))
        {
            return;
        }

        var status = screen.GetNodeOrNull<Label>(
            $"InfoPanel/{MultiplayerSkinLoadingStatusName}");
        if (status == null)
        {
            return;
        }

        var progress = OnlineSkinCache.GetProgress();
        status.Visible = IsMultiplayerCharacterSelect(screen) && progress.IsVisible;
        if (!status.Visible)
        {
            status.Text = string.Empty;
            status.TooltipText = string.Empty;
            return;
        }

        status.Text = ModLocalization.FormatOnlineSkinCacheProgress(progress);
        status.TooltipText = progress.Detail;
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
        selector.TreeExited += () => RefreshActions.Remove(selector.GetInstanceId());
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
        var selector = screen.GetNodeOrNull<Control>($"InfoPanel/{SelectorName}");
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
        if (group == null || group.Options.Count == 0)
        {
            selector.Visible = false;
            dropdown.Clear();
            return;
        }

        selector.SetMeta(UpdatingMeta, true);
        selector.SetMeta(GroupMeta, group.Id);
        dropdown.Clear();
        if (HasMonsterPriorityContext(selector))
        {
            dropdown.AddItem(ModLocalization.Get(ModText.FollowCategory));
            dropdown.SetItemMetadata(0, SkinService.InheritMonsterSelectionId);
        }

        var defaultIndex = dropdown.ItemCount;
        dropdown.AddItem(ModLocalization.Get(ModText.GameDefault));
        dropdown.SetItemMetadata(defaultIndex, SkinCatalog.BaseOptionId);
        foreach (var option in group.Options)
        {
            var index = dropdown.ItemCount;
            dropdown.AddItem(ModLocalization.DisplayOptionName(option.Name));
            dropdown.SetItemMetadata(index, option.Id);
        }

        var selected = HasMonsterPriorityContext(selector)
            ? SkinService.GetMonsterOverrideSelection(group.Id)
            : SkinService.Config.GetSelection(group.Id);
        var selectedIndex = Enumerable.Range(0, dropdown.ItemCount)
            .FirstOrDefault(index => dropdown.GetItemMetadata(index).AsString()
                .Equals(selected, StringComparison.OrdinalIgnoreCase));
        dropdown.Select(selectedIndex);
        PopulateMonsterScale(selector, group.Id);
        selector.SetMeta(UpdatingMeta, false);
        selector.Visible = true;
        RefreshMonsterPriorityButton(selector);
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
            return;
        }

        selector.SetMeta(UpdatingMeta, true);
        PopulateMonsterScale(selector, groupId);
        selector.SetMeta(UpdatingMeta, false);

        if (RefreshActions.TryGetValue(selector.GetInstanceId(), out var refresh))
        {
            Callable.From(() => RunRefresh(refresh)).CallDeferred();
        }
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
        RestoreCharacterInfoText(screen, character);

        if (ShouldSkipExternalRuntimeRedirect(groupId))
        {
            RebuildRuntimeProviderCharacterDisplay(screen, character);
            ReplaySelectedCharacterPresentation(screen, character, groupId);
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
            ReplaySelectedCharacterPresentation(screen, character, groupId);
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
                // The scene must be instantiated before WithRuntimeResources restores canonical
                // dependency paths; otherwise a skeleton/animation resource can come from the
                // previous character skin even though the PackedScene itself loaded correctly.
                ReplaceCharacterBackground(screen, character, scene, resources);
                RefreshCharacterButtonIcon(screen, character, characterSelectTextures, resources);
                return true;
            },
            includeProviderDependencies: true);
        ReplaySelectedCharacterPresentation(screen, character, groupId);
        ModLog.Info($"已完整重建 {character.Id.Entry} 的选角展示。");
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
                // Instantiate while the alias pack is mounted. PackedScene external resources
                // are often resolved at Instantiate(), not at Load(), so loading only the scene
                // object is insufficient to prevent a previous skin's skeleton/atlas binding.
                ReplaceCharacterBackground(screen, character, scene, resources);
                RefreshCharacterButtonIcon(screen, character, texturePaths, resources);
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

        var button = FindCharacterButton(screen, character);
        if (button != null)
        {
            ManagedSkinModLoader.ReplaySelectedCharacterPresentation(
                providerId,
                screen,
                button,
                character);
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
        var baselineSpineAnchors = CaptureSpineAnchors(container);

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
            ScheduleSpineAnchorCorrection(background, baselineSpineAnchors);
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
                ScheduleSpineAnchorCorrection(background, baselineSpineAnchors);
            }
        }).CallDeferred();
    }

    private static void ScheduleSpineAnchorCorrection(
        Node selectedRoot,
        IReadOnlyDictionary<string, SpineAnchor> baselineAnchors)
    {
        CorrectSpineAnchors(selectedRoot, baselineAnchors);
        foreach (var node in EnumerateNodes(selectedRoot).Where(node =>
                     node.GetClass().ToString().Equals("SpineSprite", StringComparison.Ordinal)))
        {
            try
            {
                selectedRoot.RunWhenSpineReady(
                    new MegaSprite(node),
                    _ =>
                    {
                        if (GodotObject.IsInstanceValid(selectedRoot))
                        {
                            CorrectSpineAnchors(selectedRoot, baselineAnchors);
                        }
                    });
            }
            catch
            {
                // A provider may use a non-Spine visual in one of its variants. The correction is
                // best effort and must never prevent the character preview from appearing.
            }
        }
    }

    private static IReadOnlyDictionary<string, SpineAnchor> CaptureSpineAnchors(Node root)
    {
        var anchors = new Dictionary<string, SpineAnchor>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in EnumerateNodes(root).Where(node =>
                     node.GetClass().ToString().Equals("SpineSprite", StringComparison.Ordinal)))
        {
            if (node is not Node2D node2D || !TryGetSpineBounds(node, out var bounds))
            {
                continue;
            }

            var key = root.GetPathTo(node).ToString();
            anchors[key] = new SpineAnchor(node2D.ToGlobal(bounds.GetCenter()), bounds.Size);
        }

        return anchors;
    }

    private static void CorrectSpineAnchors(
        Node selectedRoot,
        IReadOnlyDictionary<string, SpineAnchor> baselineAnchors)
    {
        if (baselineAnchors.Count == 0)
        {
            return;
        }

        foreach (var node in EnumerateNodes(selectedRoot).Where(node =>
                     node.GetClass().ToString().Equals("SpineSprite", StringComparison.Ordinal)))
        {
            if (node is not Node2D node2D ||
                !baselineAnchors.TryGetValue(selectedRoot.GetPathTo(node).ToString(), out var baseline) ||
                !TryGetSpineBounds(node, out var bounds))
            {
                continue;
            }

            var delta = baseline.GlobalCenter - node2D.ToGlobal(bounds.GetCenter());
            // A small difference is normal between animation frames. Avoid accumulating tiny
            // corrections while keeping genuinely different skeleton origins aligned.
            if (delta.Length() < 8f || delta.Length() > 1600f)
            {
                continue;
            }

            node2D.GlobalPosition += delta;
            ModLog.Info(
                $"已校正选角 Spine 视觉锚点 {node.Name}：" +
                $"偏移=({delta.X:F0}, {delta.Y:F0})，" +
                $"原尺寸=({baseline.Size.X:F0}, {baseline.Size.Y:F0})，" +
                $"当前尺寸=({bounds.Size.X:F0}, {bounds.Size.Y:F0})。" );
        }
    }

    private static bool TryGetSpineBounds(Node node, out Rect2 bounds)
    {
        bounds = default;
        try
        {
            var skeleton = new MegaSprite(node).GetSkeleton();
            if (skeleton == null)
            {
                return false;
            }

            bounds = skeleton.GetBounds();
            return bounds.Size.X > 1f && bounds.Size.Y > 1f;
        }
        catch
        {
            return false;
        }
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
        if (CharacterBackgroundWindowChangeMethod == null)
        {
            return;
        }

        foreach (var node in EnumerateNodes(container)
                     .Concat(EnumerateNodes(background))
                     .DistinctBy(node => node.GetInstanceId()))
        {
            if (node is not NCharacterSelectScreenBg gameBackground ||
                !GodotObject.IsInstanceValid(gameBackground))
            {
                continue;
            }

            try
            {
                CharacterBackgroundWindowChangeMethod.Invoke(gameBackground, null);
            }
            catch (Exception exception)
            {
                ModLog.Warn($"刷新选角背景的宽高比布局失败：{exception.GetBaseException().Message}");
            }
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
        CharacterModel character,
        IReadOnlyCollection<string> texturePaths,
        IReadOnlyDictionary<string, Resource> resources)
    {
        var button = FindCharacterButton(screen, character);
        if (button == null)
        {
            return;
        }

        var iconPath = texturePaths.FirstOrDefault(path =>
            path.Contains("/packed/character_select/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(button.IsLocked ? "_locked.png" : $"_{character.Id.Entry.ToLowerInvariant()}.png",
                StringComparison.OrdinalIgnoreCase));
        if (iconPath == null || !resources.TryGetValue(iconPath, out var resource) || resource is not Texture2D texture)
        {
            return;
        }

        button.GetNode<TextureRect>("%Icon").Texture = texture;
        ModLog.Info($"已刷新 {character.Id.Entry} 的角色列表头像。");
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

        try
        {
            var scene = SkinService.GetOrLoadRuntimeScene(group.Id, visualsPath);
            RuntimeMonsterVisualModeBridge.ApplySelected(group.Id);
            var replacement = scene.Instantiate<NCreatureVisuals>(PackedScene.GenEditState.Disabled);
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

    internal static void ApplySelectedProviderVisualPostfix(
        string modelId,
        string? modelTypeName,
        object model,
        ref NCreatureVisuals visuals)
    {
        var group = FindGroup(modelId, modelTypeName);
        if (group != null)
        {
            SkinService.ApplySelectedVisualPostfix(group.Id, model, ref visuals);
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
        if (group == null || ShouldSkipExternalRuntimeRedirect(group.Id))
        {
            return;
        }

        var characterId = character.Id.Entry.ToLowerInvariant();
        var resourcePath = CanonicalImagePath(
            "packed/character_select/char_select_" + characterId + (locked ? "_locked.png" : ".png"));
        try
        {
            result = SkinService.GetOrLoadRuntimeResource(group.Id, resourcePath) as CompressedTexture2D ??
                     throw new InvalidOperationException($"独立皮肤资源不是压缩贴图：{resourcePath}");
        }
        catch (Exception exception)
        {
            ModLog.Error($"最终接管角色列表贴图 {resourcePath} 失败：{exception}");
        }
    }

    internal static void ReplaceCharacterIcon(CharacterModel character, ref Control result)
    {
        var group = FindGroup(character.Id.Entry);
        if (group == null || ShouldSkipExternalRuntimeRedirect(group.Id))
        {
            return;
        }

        var characterId = character.Id.Entry.ToLowerInvariant();
        var resourcePath = CanonicalScenePath("ui/character_icons/" + characterId + "_icon");
        try
        {
            var replacement = SkinService.GetOrLoadRuntimeScene(group.Id, resourcePath)
                .Instantiate<Control>(PackedScene.GenEditState.Disabled);
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
        if (group == null || ShouldSkipExternalRuntimeRedirect(group.Id))
        {
            return;
        }

        try
        {
            result = SkinService.GetOrLoadRuntimeResource(group.Id, resourcePath) as Texture2D ??
                     throw new InvalidOperationException($"独立皮肤资源不是贴图：{resourcePath}");
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
        icon.Texture = character.IconTexture;
        return true;
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
        // The game owns this node's complete visual update (name, title, icon and ready state).
        // Calling the same private method under the per-player scope is important: assigning
        // only TextureRect.Texture leaves the icon getter's cached/base resource in place when
        // the lobby node was created before the remote skin package became available.
        var refreshVisuals = AccessTools.Method(typeof(NRemoteLobbyPlayer), "RefreshVisuals");
        try
        {
            // RefreshVisuals is Harmony-patched below.  Its fallback Postfix normally queues a
            // second icon pass when no per-player scope exists (for example for the local host).
            // Mark this intentional call so that Postfix cannot synchronously call back into this
            // method and recurse until the main thread appears frozen while creating a room.
            _refreshingRemoteLobbyVisuals = true;
            refreshVisuals?.Invoke(node, null);
        }
        catch (Exception exception)
        {
            ModLog.Warn("调用游戏的远程选角外观刷新失败，将使用头像贴图兜底：" +
                        exception.GetBaseException().Message);
        }
        finally
        {
            _refreshingRemoteLobbyVisuals = false;
        }

        var icon = AccessTools.Field(typeof(NRemoteLobbyPlayer), "_characterIcon")
                       ?.GetValue(node) as TextureRect ??
                   node.GetNodeOrNull<TextureRect>("%CharacterIcon");
        if (icon == null || !GodotObject.IsInstanceValid(icon))
        {
            return false;
        }

        // Keep an explicit assignment as a fallback for game versions where the private method
        // was renamed or where the node has not completed _Ready yet.
        icon.Texture = character.IconTexture;
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
                node.Texture = player.Character.IconTexture;
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

    private sealed record SpineAnchor(Vector2 GlobalCenter, Vector2 Size);
}

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.SelectCharacter))]
internal static class CharacterSelectionSkinPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Postfix(NCharacterSelectScreen __instance, CharacterModel characterModel) =>
        ContextualSkinControls.ShowCharacter(__instance, characterModel);
}

[HarmonyPatch(typeof(NCharacterSelectScreen), "StartNewSingleplayerRun")]
internal static class SingleplayerEmbarkSkinSelectorPatch
{
    private static void Prefix(NCharacterSelectScreen __instance) =>
        ContextualSkinControls.HideCharacterSelector(__instance);
}

[HarmonyPatch(typeof(NCharacterSelectScreen), "StartNewMultiplayerRun")]
internal static class MultiplayerEmbarkSkinSelectorPatch
{
    private static void Prefix(NCharacterSelectScreen __instance) =>
        ContextualSkinControls.HideCharacterSelector(__instance);
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

[HarmonyPatch(typeof(CharacterModel), nameof(CharacterModel.CreateVisuals))]
internal static class CharacterVisualResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CharacterModel __instance, ref NCreatureVisuals __result)
    {
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
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(MonsterModel __instance, ref NCreatureVisuals __result)
    {
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
            // If the packet arrived while _Ready was constructing the node, retry after the
            // game's own icon assignment instead of leaving the base texture cached forever.
            // Queue one extra pass even when a scope existed: the selected provider may only be
            // mounted after the game's _Ready callback returns.
            if (__state == null)
            {
                ContextualSkinControls.RefreshMultiplayerPlayerIcons(__instance.Player.NetId);
            }
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
        if (__state == null && !ContextualSkinControls.IsRefreshingRemoteLobbyVisuals)
        {
            ContextualSkinControls.RefreshMultiplayerPlayerIcons(__instance.PlayerId);
        }
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
        if (__state == null)
        {
            ContextualSkinControls.RefreshMultiplayerPlayerIcons(__instance.PlayerId);
        }
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
