using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Bestiary;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static partial class ContextualSkinControls
{
    private const string SelectorName = "STS2SkinSelector";
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
    // These are the values authored by the game's character_select_screen.tscn.  A skin
    // provider can legitimately change the background child, but it must not leave its parent
    // container's transform behind when another skin is selected.
    private const float CharacterBackgroundOffsetLeft = -388f;
    private const float CharacterBackgroundOffsetTop = -80f;
    private const float CharacterBackgroundOffsetRight = 252f;
    private const float CharacterBackgroundOffsetBottom = 40f;
    private static readonly Vector2 CharacterBackgroundPivot = new(1280f, 600f);
    private static readonly Dictionary<ulong, Action> RefreshActions = [];
    private static bool _refreshingMonsterDisplay;
    private static Font? _gameFont;

    internal static bool IsRefreshingMonsterDisplay => _refreshingMonsterDisplay;

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
        RegisterRefresh(selector, group == null ? null : () => RebuildCharacterDisplay(screen, character, group.Id));
        Populate(selector, group);
        if (group != null)
        {
            // 游戏的 SelectCharacter 每次点击都会清空 AnimatedBg 并重新实例化原版背景，
            // 所以这里必须每次重建；资源已缓存在 SkinService，重建不会再次写盘或加载。
            ScheduleCharacterRefresh(screen, character, group.Id);
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
        ModLocalization.Bind(selector, () => RefreshLocalizedText(selector));
        return selector;
    }

    private static HBoxContainer EnsureMonsterSelector(NBestiary screen)
    {
        var existing = screen.GetNodeOrNull<HBoxContainer>(SelectorName);
        if (existing != null)
        {
            return existing;
        }

        var selector = BuildSelector();
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

    internal static void HideCharacterSelector(NCharacterSelectScreen screen)
    {
        var selector = screen.GetNodeOrNull<Control>($"InfoPanel/{SelectorName}");
        if (selector != null)
        {
            selector.Visible = false;
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
        dropdown.AddItem(ModLocalization.Get(ModText.GameDefault));
        dropdown.SetItemMetadata(0, SkinCatalog.BaseOptionId);
        foreach (var option in group.Options)
        {
            var index = dropdown.ItemCount;
            dropdown.AddItem(ModLocalization.DisplayOptionName(option.Name));
            dropdown.SetItemMetadata(index, option.Id);
        }

        var selected = SkinService.Config.GetSelection(group.Id);
        var selectedIndex = Enumerable.Range(0, dropdown.ItemCount)
            .FirstOrDefault(index => dropdown.GetItemMetadata(index).AsString() == selected);
        dropdown.Select(selectedIndex);
        PopulateMonsterScale(selector, group.Id);
        selector.SetMeta(UpdatingMeta, false);
        selector.Visible = true;
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
        if (!SkinService.ApplySelection(groupId, optionId))
        {
            ModLog.Error($"界面切换失败：{SkinService.LastError}");
            var current = SkinService.Config.GetSelection(groupId);
            var currentIndex = Enumerable.Range(0, dropdown.ItemCount)
                .FirstOrDefault(item => dropdown.GetItemMetadata(item).AsString() == current);
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
        RestoreCharacterInfoText(screen, character);

        if (SkinService.IsExternalRuntimeProviderSelected(groupId))
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

        // A complete DLL skin is mounted at its original game-facing paths while it is selected.
        // Loading it through the per-resource alias layer changes the path context used by
        // exported scenes and lets a later AssetCache factory instantiate it after that layer has
        // been restored. Keep this branch on the canonical path for every full runtime provider;
        // it is intentionally not tied to a particular mod.
        if (SkinService.GetSelectedFullRuntimeProvider(groupId) != null)
        {
            RebuildMountedFullRuntimeCharacterDisplay(screen, character, characterSelectPath);
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
                ReplaceCharacterBackground(screen, character, scene);
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
        string scenePath)
    {
        var scene = ResourceLoader.Load<PackedScene>(
            scenePath,
            null,
            ResourceLoader.CacheMode.IgnoreDeep) ??
            PreloadManager.Cache.GetScene(scenePath);
        PreloadManager.Cache.SetAsset(scenePath, scene);
        ReplaceCharacterBackground(screen, character, scene);

        var button = FindCharacterButton(screen, character);
        if (button != null)
        {
            button.GetNode<TextureRect>("%Icon").Texture = button.IsLocked
                ? character.CharacterSelectLockedIcon
                : character.CharacterSelectIcon;
        }

        ModLog.Info($"已从原始游戏路径重建完整 DLL 皮肤 {character.Id.Entry} 的选角展示。");
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
        PackedScene scene)
    {
        var container = screen.GetNodeOrNull<Control>("AnimatedBg");
        if (container == null)
        {
            ModLog.Error("选角界面缺少 AnimatedBg 节点，无法替换角色背景。");
            return;
        }

        RestoreCharacterBackgroundContainerLayout(container);
        foreach (var child in container.GetChildren())
        {
            container.RemoveChildSafely(child);
            child.QueueFreeSafely();
        }

        var background = scene.Instantiate<Control>(PackedScene.GenEditState.Disabled);
        background.Name = character.Id.Entry + "_bg";
        container.AddChildSafely(background);
        RestoreCharacterBackgroundContainerLayout(container);

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
                // A provider may run a deferred presentation callback during _Ready.  Restore
                // the parent once more immediately before the game's aspect-ratio calculation
                // so a callback cannot move the whole background for the next skin.
                RestoreCharacterBackgroundContainerLayout(container);
                RefreshCharacterBackgroundLayout(container, background);
            }
        }).CallDeferred();
    }

    private static void RestoreCharacterBackgroundContainerLayout(Control container)
    {
        if (!GodotObject.IsInstanceValid(container))
        {
            return;
        }

        container.AnchorLeft = 0f;
        container.AnchorTop = 0f;
        container.AnchorRight = 1f;
        container.AnchorBottom = 1f;
        container.OffsetLeft = CharacterBackgroundOffsetLeft;
        container.OffsetTop = CharacterBackgroundOffsetTop;
        container.OffsetRight = CharacterBackgroundOffsetRight;
        container.OffsetBottom = CharacterBackgroundOffsetBottom;
        container.GrowHorizontal = Control.GrowDirection.Both;
        container.GrowVertical = Control.GrowDirection.Both;
        container.PivotOffset = CharacterBackgroundPivot;
        container.Rotation = 0f;
        container.Visible = true;

        // _Ready only connects SizeChanged and does not calculate the initial scale.  The
        // game's private callback is invoked below; 1.1 is its 16:9 fallback if reflection is
        // unavailable on a future game build.
        container.Scale = Vector2.One * 1.1f;
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
        var tokens = new[] { modelId, modelTypeName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizeToken(value!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return SkinService.Catalog?.Groups.FirstOrDefault(group =>
            tokens.Contains(NormalizeToken(group.Id)));
    }

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
        if (group == null || SkinService.IsExternalRuntimeProviderSelected(group.Id))
        {
            return;
        }

        try
        {
            var scene = SkinService.GetOrLoadRuntimeScene(group.Id, visualsPath);
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
            SkinService.IsExternalRuntimeProviderSelected(groupId))
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
        if (groupId == null || SkinService.IsExternalRuntimeProviderSelected(groupId))
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
        if (group == null || SkinService.IsExternalRuntimeProviderSelected(group.Id))
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
        if (group == null || SkinService.IsExternalRuntimeProviderSelected(group.Id))
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
        if (group == null || SkinService.IsExternalRuntimeProviderSelected(group.Id))
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

[HarmonyPatch(typeof(NMuteInBackgroundHandler), nameof(NMuteInBackgroundHandler._Notification))]
internal static class SkinPopupBackgroundMutePatch
{
    private static bool Prefix(NMuteInBackgroundHandler __instance, int what) =>
        !ContextualSkinControls.ShouldIgnoreBackgroundMute(__instance, what);
}
