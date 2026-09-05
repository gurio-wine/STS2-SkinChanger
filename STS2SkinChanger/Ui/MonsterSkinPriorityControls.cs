using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Bestiary;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static partial class ContextualSkinControls
{
    private const string MonsterPriorityHeaderName = "MonsterSkinPriorityHeader";
    private const string MonsterPriorityRegionLabelName = "MonsterSkinPriorityRegionLabel";
    private const string MonsterPriorityButtonName = "MonsterSkinPriorityButton";
    private const string MonsterPriorityOverlayName = "MonsterSkinPriorityOverlay";
    private const string MonsterPriorityPanelName = "PriorityPanel";
    private const string MonsterPriorityMarginName = "PriorityMargin";
    private const string MonsterPriorityContentName = "PriorityContent";
    private const string MonsterPresetButtonName = "MonsterPresetButton";
    private const string MonsterPresetOverlayName = "MonsterPresetOverlay";
    private const string MonsterPresetPanelName = "PresetPanel";
    private const string MonsterPresetMarginName = "PresetMargin";
    private const string MonsterPresetContentName = "PresetContent";
    private const string MonsterCategoryMeta = "sts2_skin_monster_category";
    private const string MonsterCategoryNameMeta = "sts2_skin_monster_category_name";

    private static readonly Color[] MonsterPriorityColors =
    [
        new("df6688"), new("58b9d1"), new("e1a95f"), new("78c891"),
        new("ad83d4"), new("e47d67"), new("71a3e3"), new("c4c85f")
    ];
    private static readonly Dictionary<string, IReadOnlyList<string>> MonsterCategoryGroupCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static void AttachMonsterPriorityControls(NBestiary screen, HBoxContainer selector)
    {
        var header = new Control
        {
            Name = MonsterPriorityHeaderName,
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            OffsetLeft = -330,
            OffsetTop = 72,
            OffsetRight = 330,
            OffsetBottom = 118,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
            ZIndex = 10
        };
        var regionLabel = new Label
        {
            Name = MonsterPriorityRegionLabelName,
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            OffsetLeft = -295,
            OffsetRight = -109,
            OffsetBottom = 42,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        regionLabel.AddThemeFontSizeOverride("font_size", 23);
        regionLabel.AddThemeColorOverride("font_color", new Color("efc850"));
        regionLabel.AddThemeColorOverride("font_outline_color", new Color("332f27"));
        regionLabel.AddThemeConstantOverride("outline_size", 4);
        if (GameFont != null)
        {
            regionLabel.AddThemeFontOverride("font", GameFont);
        }
        header.AddChild(regionLabel);

        var button = new Button
        {
            Name = MonsterPriorityButtonName,
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            OffsetLeft = -95,
            OffsetRight = 95,
            OffsetBottom = 42,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        ApplyCompactButtonTheme(button);
        header.AddChild(button);

        var presetButton = new Button
        {
            Name = MonsterPresetButtonName,
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            OffsetLeft = 110,
            OffsetRight = 270,
            OffsetBottom = 42,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            Text = ModLocalization.Get(ModText.CardPresets)
        };
        ApplyCompactButtonTheme(presetButton);
        header.AddChild(presetButton);
        screen.AddChild(header);

        var overlay = CreateMonsterPriorityOverlay();
        screen.AddChild(overlay);
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.Pressed += () => OpenMonsterPriorityOverlay(screen, selector, overlay);
        var presetOverlay = CreateMonsterPresetOverlay();
        screen.AddChild(presetOverlay);
        presetOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        presetButton.Pressed += () => OpenMonsterPresetOverlay(screen, selector, presetOverlay);
        ModLocalization.Bind(overlay, () =>
        {
            if (overlay.Visible)
            {
                BuildMonsterPriorityOverlay(screen, selector, overlay);
            }
            presetButton.Text = ModLocalization.Get(ModText.CardPresets);
            if (presetOverlay.Visible)
            {
                BuildMonsterPresetOverlay(screen, selector, presetOverlay);
            }
        });
    }

    private static MonsterSkinCategory? ResolveMonsterSkinCategory(NBestiaryEntry entry)
    {
        var encounter = entry.Entry.encounterModel;
        if (encounter == null)
        {
            return null;
        }

        foreach (var act in ModelDb.Acts)
        {
            if (act.AllEncounters.Any(candidate => candidate.Id.Equals(encounter.Id)))
            {
                return BuildMonsterSkinCategory(
                    "act:" + act.Id.Entry.ToLowerInvariant(),
                    act.Title.GetFormattedText(),
                    act.AllMonsters);
            }
        }

        var eventEncounters = GetEventEncounters().ToArray();
        if (eventEncounters.Any(candidate => candidate.Id.Equals(encounter.Id)))
        {
            return BuildMonsterSkinCategory(
                "events",
                new LocString("bestiary", "EVENTS.title").GetFormattedText(),
                eventEncounters.SelectMany(candidate => candidate.AllPossibleMonsters));
        }

        return null;
    }

    private static IEnumerable<EncounterModel> GetEventEncounters() =>
        typeof(ModelDb).GetProperty("EventEncounters")?.GetValue(null) as IEnumerable<EncounterModel> ?? [];

    private static MonsterSkinCategory BuildMonsterSkinCategory(
        string id,
        string displayName,
        IEnumerable<MonsterModel> monsters)
    {
        if (MonsterCategoryGroupCache.TryGetValue(id, out var cachedGroupIds))
        {
            return new MonsterSkinCategory(id, displayName, cachedGroupIds);
        }

        var groupIds = monsters
            .Select(monster => FindGroup(monster.Id.Entry, monster.GetType().Name))
            .Where(group => group is { Options.Count: > 0 })
            .Select(group => group!.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        MonsterCategoryGroupCache[id] = groupIds;
        return new MonsterSkinCategory(id, displayName, groupIds);
    }

    private static void SetMonsterPriorityContext(
        HBoxContainer selector,
        MonsterSkinCategory? category)
    {
        if (category == null || category.GroupIds.Count == 0 ||
            !SkinService.RegisterMonsterSkinCategory(category.Id, category.GroupIds))
        {
            selector.RemoveMeta(MonsterCategoryMeta);
            selector.RemoveMeta(MonsterCategoryNameMeta);
            RefreshMonsterPriorityButton(selector);
            return;
        }

        selector.SetMeta(MonsterCategoryMeta, category.Id);
        selector.SetMeta(MonsterCategoryNameMeta, category.DisplayName);
        RefreshMonsterPriorityButton(selector);
    }

    private static bool HasMonsterPriorityContext(HBoxContainer selector) =>
        !string.IsNullOrWhiteSpace(selector.GetMeta(MonsterCategoryMeta, string.Empty).AsString());

    private static void RefreshMonsterPriorityButton(HBoxContainer selector)
    {
        var screen = selector.GetParent() as NBestiary;
        var header = screen?.GetNodeOrNull<Control>(MonsterPriorityHeaderName);
        var button = header?.GetNodeOrNull<Button>(MonsterPriorityButtonName);
        var regionLabel = header?.GetNodeOrNull<Label>(MonsterPriorityRegionLabelName);
        var presetButton = header?.GetNodeOrNull<Button>(MonsterPresetButtonName);
        if (header == null || button == null || presetButton == null || regionLabel == null)
        {
            return;
        }

        var categoryId = selector.GetMeta(MonsterCategoryMeta, string.Empty).AsString();
        var options = string.IsNullOrWhiteSpace(categoryId)
            ? []
            : SkinService.GetMonsterPriorityOptions(categoryId);
        header.Visible = options.Count > 0;
        regionLabel.Text = selector.GetMeta(MonsterCategoryNameMeta, string.Empty).AsString();
        button.Text = ModLocalization.Get(ModText.MonsterSkinPriority);
        button.TooltipText = string.Empty;
        presetButton.Text = ModLocalization.Get(ModText.CardPresets);
    }

    private static Control CreateMonsterPriorityOverlay()
    {
        var overlay = new Control
        {
            Name = MonsterPriorityOverlayName,
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
            Name = MonsterPriorityPanelName,
            MouseFilter = Control.MouseFilterEnum.Stop,
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -360,
            OffsetTop = -245,
            OffsetRight = 360,
            OffsetBottom = 245
        };
        panel.AddThemeStyleboxOverride(
            "panel",
            CreateStyleBox(new Color("241a30"), new Color("79547e"), 2));
        overlay.AddChild(panel);
        var margin = new MarginContainer { Name = MonsterPriorityMarginName };
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(margin);
        var content = new VBoxContainer { Name = MonsterPriorityContentName };
        content.AddThemeConstantOverride("separation", 10);
        margin.AddChild(content);
        return overlay;
    }

    private static void OpenMonsterPriorityOverlay(
        NBestiary screen,
        HBoxContainer selector,
        Control overlay)
    {
        BuildMonsterPriorityOverlay(screen, selector, overlay);
        overlay.Visible = true;
        overlay.MoveToFront();
    }

    private static void BuildMonsterPriorityOverlay(
        NBestiary screen,
        HBoxContainer selector,
        Control overlay)
    {
        var categoryId = selector.GetMeta(MonsterCategoryMeta, string.Empty).AsString();
        var categoryName = selector.GetMeta(MonsterCategoryNameMeta, string.Empty).AsString();
        var content = overlay.GetNode<VBoxContainer>(
            $"{MonsterPriorityPanelName}/{MonsterPriorityMarginName}/{MonsterPriorityContentName}");
        var scroll = ScrollListRebuild.Begin(content, categoryId);

        var options = SkinService.GetMonsterPriorityOptions(categoryId);
        if (string.IsNullOrWhiteSpace(categoryId) || options.Count == 0)
        {
            overlay.Visible = false;
            return;
        }

        var title = new Label
        {
            Text = categoryName + " · " + ModLocalization.Get(ModText.MonsterSkinPriority),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        title.AddThemeFontSizeOverride("font_size", 25);
        title.AddThemeColorOverride("font_color", new Color("efc850"));
        if (GameFont != null)
        {
            title.AddThemeFontOverride("font", GameFont);
        }
        content.AddChild(title);

        scroll.CustomMinimumSize = new Vector2(670, 350);
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        ScrollListRebuild.PlaceAfterHeader(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(rows);

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
                Color = MonsterPriorityColors[option.ColorIndex % MonsterPriorityColors.Length],
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
            ApplyGameTheme(enabled);
            enabled.AddThemeFontSizeOverride("font_size", 17);
            enabled.Toggled += value => QueueMonsterPriorityChange(
                screen,
                selector,
                overlay,
                categoryId,
                () => SkinService.SetMonsterPriorityOptionEnabled(
                    categoryId,
                    option.OptionId,
                    value));
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
                Text = $"{option.Coverage}/{option.TotalMonsters}",
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
            ApplyGameTheme(up);
            up.AddThemeFontSizeOverride("font_size", 18);
            up.Pressed += () => QueueMonsterPriorityChange(
                screen,
                selector,
                overlay,
                categoryId,
                () => SkinService.MoveMonsterPriority(categoryId, option.OptionId, -1));
            row.AddChild(up);

            var down = new Button
            {
                Text = "↓",
                Disabled = option == options[^1],
                CustomMinimumSize = new Vector2(42, 34)
            };
            ApplyGameTheme(down);
            down.AddThemeFontSizeOverride("font_size", 18);
            down.Pressed += () => QueueMonsterPriorityChange(
                screen,
                selector,
                overlay,
                categoryId,
                () => SkinService.MoveMonsterPriority(categoryId, option.OptionId, 1));
            row.AddChild(down);
        }

        var close = new Button
        {
            Text = ModLocalization.Get(ModText.Close),
            CustomMinimumSize = new Vector2(180, 42),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter
        };
        ApplyGameTheme(close);
        close.AddThemeFontSizeOverride("font_size", 19);
        close.Pressed += () => overlay.Visible = false;
        content.AddChild(close);
    }

    private static Control CreateMonsterPresetOverlay()
    {
        var overlay = new Control
        {
            Name = MonsterPresetOverlayName,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 2001
        };
        var mask = new ColorRect
        {
            Name = "Mask",
            Color = new Color(0f, 0f, 0f, 0.68f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        mask.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                overlay.Visible = false;
                mask.AcceptEvent();
            }
        };
        overlay.AddChild(mask);
        mask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = new PanelContainer
        {
            Name = MonsterPresetPanelName,
            MouseFilter = Control.MouseFilterEnum.Stop,
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -470,
            OffsetTop = -275,
            OffsetRight = 470,
            OffsetBottom = 275
        };
        panel.AddThemeStyleboxOverride(
            "panel", CreateStyleBox(new Color("241a30"), new Color("79547e"), 2));
        overlay.AddChild(panel);
        var margin = new MarginContainer { Name = MonsterPresetMarginName };
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(margin);
        var content = new VBoxContainer { Name = MonsterPresetContentName };
        content.AddThemeConstantOverride("separation", 10);
        margin.AddChild(content);
        return overlay;
    }

    private static void OpenMonsterPresetOverlay(
        NBestiary screen, HBoxContainer selector, Control overlay)
    {
        BuildMonsterPresetOverlay(screen, selector, overlay);
        overlay.Visible = true;
        overlay.MoveToFront();
    }

    private static void BuildMonsterPresetOverlay(
        NBestiary screen, HBoxContainer selector, Control overlay)
    {
        var categoryId = selector.GetMeta(MonsterCategoryMeta, string.Empty).AsString();
        var categoryName = selector.GetMeta(MonsterCategoryNameMeta, string.Empty).AsString();
        var content = overlay.GetNode<VBoxContainer>(
            $"{MonsterPresetPanelName}/{MonsterPresetMarginName}/{MonsterPresetContentName}");
        var scroll = ScrollListRebuild.Begin(content, categoryId);
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            overlay.Visible = false;
            return;
        }

        var title = new Label
        {
            Text = categoryName + " · " + ModLocalization.Get(ModText.CardPresets),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        title.AddThemeFontSizeOverride("font_size", 25);
        title.AddThemeColorOverride("font_color", new Color("efc850"));
        content.AddChild(title);

        var createRow = new HBoxContainer { CustomMinimumSize = new Vector2(890, 44) };
        createRow.AddThemeConstantOverride("separation", 10);
        content.AddChild(createRow);
        var newName = new LineEdit
        {
            PlaceholderText = ModLocalization.Get(ModText.CardPresetName),
            MaxLength = SkinService.MonsterSkinPresetNameMaxLength,
            CustomMinimumSize = new Vector2(600, 40),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        newName.AddThemeFontSizeOverride("font_size", 18);
        createRow.AddChild(newName);
        var save = CreateMonsterPresetActionButton(ModLocalization.Get(ModText.SaveCurrentPreset), 220);
        save.Pressed += () => QueueMonsterPresetChange(
            screen, selector, overlay,
            () => SkinService.CreateMonsterSkinPreset(categoryId, newName.Text),
            refreshMonsters: false);
        createRow.AddChild(save);

        scroll.CustomMinimumSize = new Vector2(890, 360);
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        ScrollListRebuild.PlaceAfterHeader(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 7);
        scroll.AddChild(rows);
        var presets = SkinService.GetMonsterSkinPresets(categoryId);
        if (presets.Count == 0)
        {
            var empty = new Label
            {
                Text = ModLocalization.Get(ModText.NoMonsterPresets),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CustomMinimumSize = new Vector2(860, 100),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            empty.AddThemeFontSizeOverride("font_size", 19);
            empty.AddThemeColorOverride("font_color", new Color("b9adbd"));
            rows.AddChild(empty);
        }

        foreach (var preset in presets)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(870, 46) };
            row.AddThemeConstantOverride("separation", 8);
            rows.AddChild(row);
            var active = new Label
            {
                Text = preset.Active ? "●" : string.Empty,
                CustomMinimumSize = new Vector2(24, 38),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            active.AddThemeColorOverride("font_color", new Color("efc850"));
            row.AddChild(active);
            var name = new LineEdit
            {
                Text = preset.DisplayName,
                Editable = !preset.IsBundlePreset,
                MaxLength = preset.IsBundlePreset ? 0 : SkinService.MonsterSkinPresetNameMaxLength,
                CustomMinimumSize = new Vector2(310, 38),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            if (preset.IsBundlePreset)
            {
                name.AddThemeColorOverride("font_uneditable_color", new Color("efc850"));
            }
            row.AddChild(name);
            var apply = CreateMonsterPresetActionButton(
                preset.Active ? ModLocalization.Get(ModText.ActiveCardPreset) : ModLocalization.Get(ModText.ApplyCardPreset), 112);
            apply.Disabled = preset.Active;
            apply.Pressed += () => QueueMonsterPresetChange(
                screen, selector, overlay,
                () => SkinService.ApplyMonsterSkinPreset(categoryId, preset.Name), true);
            row.AddChild(apply);
            var overwrite = CreateMonsterPresetActionButton(ModLocalization.Get(ModText.OverwriteCardPreset), 100);
            overwrite.Pressed += () => QueueMonsterPresetChange(
                screen, selector, overlay,
                () => SkinService.OverwriteMonsterSkinPreset(categoryId, preset.Name), false);
            row.AddChild(overwrite);
            var rename = CreateMonsterPresetActionButton(ModLocalization.Get(ModText.RenameCardPreset), 100);
            rename.Pressed += () => QueueMonsterPresetChange(
                screen, selector, overlay,
                () => SkinService.RenameMonsterSkinPreset(categoryId, preset.Name, name.Text), false);
            row.AddChild(rename);
            rename.Disabled = preset.IsBundlePreset;
            var delete = CreateMonsterPresetActionButton(ModLocalization.Get(ModText.DeleteCardPreset), 112);
            var deleteArmed = false;
            delete.Pressed += () =>
            {
                if (!deleteArmed)
                {
                    deleteArmed = true;
                    delete.Text = ModLocalization.Get(ModText.ConfirmDeleteCardPreset);
                    delete.AddThemeColorOverride("font_color", new Color("ef6670"));
                    return;
                }
                QueueMonsterPresetChange(
                    screen, selector, overlay,
                    () => SkinService.DeleteMonsterSkinPreset(categoryId, preset.Name), false);
            };
            row.AddChild(delete);
            delete.Disabled = preset.IsBundlePreset;
        }
        var close = CreateMonsterPresetActionButton(ModLocalization.Get(ModText.Close), 180);
        close.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        close.Pressed += () => overlay.Visible = false;
        content.AddChild(close);
    }

    private static Button CreateMonsterPresetActionButton(string text, float width)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(width, 38) };
        ApplyGameTheme(button);
        button.AddThemeFontSizeOverride("font_size", 17);
        return button;
    }

    private static void QueueMonsterPresetChange(
        NBestiary screen,
        HBoxContainer selector,
        Control overlay,
        Func<bool> change,
        bool refreshMonsters)
    {
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(screen) || !change())
            {
                ModLog.Error($"调整怪物皮肤预设失败：{SkinService.LastError}");
                return;
            }
            BuildMonsterPresetOverlay(screen, selector, overlay);
            RefreshMonsterPriorityButton(selector);
            if (refreshMonsters)
            {
                var categoryId = selector.GetMeta(MonsterCategoryMeta, string.Empty).AsString();
                var groups = MonsterCategoryGroupCache.GetValueOrDefault(categoryId) ?? [];
                if (NRun.Instance != null)
                {
                    CharacterAppearanceRuntime.RefreshRunMonsterSelection(groups, "局内怪物预设");
                }
                RefreshBestiaryMonsterNames(screen);
                var groupId = selector.GetMeta(GroupMeta, string.Empty).AsString();
                var group = string.IsNullOrWhiteSpace(groupId) ? null : FindGroup(groupId);
                if (group != null)
                {
                    Populate(selector, group);
                    if (RefreshActions.TryGetValue(selector.GetInstanceId(), out var refresh))
                    {
                        RunRefresh(refresh);
                    }
                }
            }
        }).CallDeferred();
    }

    private static void QueueMonsterPriorityChange(
        NBestiary screen,
        HBoxContainer selector,
        Control overlay,
        string categoryId,
        Func<bool> change)
    {
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(screen) || !change())
            {
                ModLog.Error($"调整怪物皮肤优先级失败：{SkinService.LastError}");
                return;
            }

            var groupId = selector.GetMeta(GroupMeta, string.Empty).AsString();
            var group = string.IsNullOrWhiteSpace(groupId) ? null : FindGroup(groupId);
            if (group != null)
            {
                Populate(selector, group);
            }

            var categoryGroups = MonsterCategoryGroupCache.GetValueOrDefault(categoryId) ?? [];
            if (NRun.Instance != null)
            {
                CharacterAppearanceRuntime.RefreshRunMonsterSelection(
                    categoryGroups,
                    "局内怪物地区优先级");
            }
            else
            {
                SkinService.FocusRuntimeProviderBehaviorsOnGroups(
                    categoryGroups,
                    runEnvironmentProviderIds: [],
                    reason: "怪物图鉴名称刷新");
            }
            RefreshBestiaryMonsterNames(screen);

            BuildMonsterPriorityOverlay(screen, selector, overlay);
            RefreshMonsterPriorityButton(selector);
            if (RefreshActions.TryGetValue(selector.GetInstanceId(), out var refresh))
            {
                RunRefresh(refresh);
            }

            if (group != null)
            {
                if (NRun.Instance != null)
                {
                    CharacterAppearanceRuntime.FocusRuntimeProviderBehaviorsOnRunContext(
                        [group.Id],
                        "局内怪物图鉴", refreshCurrentRoom: true);
                }
                else
                {
                    SkinService.FocusRuntimeProviderBehaviorsOnGroups(
                        [group.Id],
                        runEnvironmentProviderIds: [],
                        reason: "怪物图鉴");
                }
            }
        }).CallDeferred();
    }

    private static void RefreshBestiaryMonsterNames(NBestiary screen)
    {
        if (!GodotObject.IsInstanceValid(screen))
        {
            return;
        }

        try
        {
            var list = screen.GetNodeOrNull<VBoxContainer>("%BestiaryList");
            if (list != null)
            {
                foreach (var entry in list.GetChildren().OfType<NBestiaryEntry>()
                             .Where(entry => entry.IsDiscovered))
                {
                    ManagedSkinModLoader.RestoreAllNodeReadyBehaviors(entry);
                    var label = entry.GetNodeOrNull<RichTextLabel>("%Label");
                    if (label != null)
                    {
                        label.Text = entry.Entry.GetEntryTitle();
                    }

                    var monster = entry.Entry.monsterModel;
                    var group = monster == null
                        ? null
                        : FindGroup(monster.Id.Entry, monster.GetType().Name);
                    var providerId = group == null
                        ? null
                        : SkinService.GetSelectedRuntimeProvider(group.Id);
                    if (providerId != null)
                    {
                        ManagedSkinModLoader.ReplaySelectedNodeReadyBehavior(providerId, entry);
                    }
                }
            }

            var selectedEntry = AccessTools.Field(typeof(NBestiary), "_selectedEntry")?
                .GetValue(screen) as NBestiaryEntry;
            if (selectedEntry is { IsDiscovered: true })
            {
                var selectedName = screen.GetNodeOrNull<RichTextLabel>("%MonsterName");
                if (selectedName != null)
                {
                    selectedName.Text = selectedEntry.Entry.GetEntryTitle();
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn(
                "刷新怪物图鉴名称失败：" +
                exception.GetBaseException().Message);
        }
    }

    internal static void ScheduleInitialBestiaryMonsterNameRefresh(NBestiary screen) =>
        Callable.From(() => RefreshBestiaryMonsterNames(screen)).CallDeferred();

    private sealed record MonsterSkinCategory(
        string Id,
        string DisplayName,
        IReadOnlyList<string> GroupIds);
}

[HarmonyPatch(typeof(NBestiary), nameof(NBestiary.OnSubmenuOpened))]
internal static class BestiaryInitialSkinNamePatch
{
    [HarmonyPostfix]
    private static void Postfix(NBestiary __instance) =>
        ContextualSkinControls.ScheduleInitialBestiaryMonsterNameRefresh(__instance);
}
