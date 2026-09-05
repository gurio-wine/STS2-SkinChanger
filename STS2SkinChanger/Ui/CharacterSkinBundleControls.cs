using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class CharacterSkinBundleControls
{
    private static readonly ConditionalWeakTable<NCharacterSelectScreen, EditorState> States = new();

    internal static void ShowForCharacter(NCharacterSelectScreen screen, string groupId, string displayName, Action refresh)
    {
        var state = States.GetValue(screen, CreateState);
        if (!state.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase))
        {
            state.Overlay.Hide();
        }
        state.GroupId = groupId;
        state.DisplayName = displayName;
        state.Refresh = refresh;
        state.Entry.Show();
    }

    internal static void Hide(NCharacterSelectScreen screen)
    {
        if (States.TryGetValue(screen, out var state))
        {
            state.Entry.Hide();
            state.Overlay.Hide();
        }
    }

    private static EditorState CreateState(NCharacterSelectScreen screen)
    {
        var entry = new HBoxContainer
        {
            Name = "STS2CharacterSkinBundleControl",
            ZIndex = 100,
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false
        };
        var button = CreateButton(ModText.CharacterSkinBundle);
        button.Name = "STS2CharacterSkinBundleButton";
        button.CustomMinimumSize = new Vector2(180f, 46f);
        entry.AddChild(button);
        screen.AddChild(entry);
        DraggableSkinControl.Attach(screen, entry,
            SkinService.GetCharacterSkinBundlePosition, SkinService.SetCharacterSkinBundlePosition,
            SkinService.ResetCharacterSkinBundlePosition, () =>
                DraggableSkinControl.ApplyDefaultPosition(
                    screen, entry, DraggableControlPlacementPolicy.CharacterBundleDefault));

        var overlay = new Control
        {
            Name = "STS2CharacterSkinBundleOverlay",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 2400
        };
        screen.AddChild(overlay);
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var mask = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.72f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        overlay.AddChild(mask);
        mask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = new PanelContainer
        {
            Name = "BundlePanel",
            MouseFilter = Control.MouseFilterEnum.Stop,
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 0.1f,
            AnchorBottom = 0.9f,
            OffsetLeft = -430f,
            OffsetRight = 430f,
            OffsetTop = 0f,
            OffsetBottom = 0f,
            ClipContents = true
        };
        panel.AddThemeStyleboxOverride("panel", ContextualSkinControls.CreateStyleBox(
            new Color("241a30"), new Color("79547e"), 2));
        overlay.AddChild(panel);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 22);
        margin.AddThemeConstantOverride("margin_right", 22);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(margin);
        var content = new VBoxContainer
        {
            Name = "BundleContent",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        content.AddThemeConstantOverride("separation", 10);
        margin.AddChild(content);
        var state = new EditorState(screen, entry, overlay, content);
        button.Pressed += () => Open(state);
        mask.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                overlay.Hide();
                mask.AcceptEvent();
            }
        };
        ModLocalization.Bind(button, () =>
        {
            button.Text = ModLocalization.Get(ModText.CharacterSkinBundle);
            if (overlay.Visible)
            {
                BuildEditor(state);
            }
        });
        return state;
    }

    private static void Open(EditorState state)
    {
        // Catalogue initialization can migrate old presets and save. Finish it before the
        // bundle transaction starts; category dropdowns only enumerate existing preset names.
        SkinService.InitializeCardGroupsAfterModels();
        state.CardCategories = SkinService.GetCardPresetCategories();
        state.MonsterCategories = SkinService.GetMonsterPresetCategories();
        var active = SkinService.Config.ActiveCharacterSkinBundles.GetValueOrDefault(state.GroupId);
        var bundles = SkinService.GetCharacterSkinBundles(state.GroupId);
        LoadDraft(state, bundles.FirstOrDefault(bundle => bundle.Name.Equals(active,
            StringComparison.OrdinalIgnoreCase)) ?? bundles.FirstOrDefault());
        BuildEditor(state);
        state.Overlay.Show();
        state.Overlay.MoveToFront();
    }

    private static void LoadDraft(EditorState state, CharacterSkinBundle? bundle)
    {
        state.EditingName = bundle?.Name;
        state.Draft = bundle == null ? new CharacterSkinBundle
        {
            CharacterGroupId = state.GroupId,
            CharacterOptionId = SkinService.Config.GetSelection(state.GroupId)
        } : CharacterSkinBundlePolicy.Clone(bundle);
        if (bundle == null) BundlePresetPolicy.InitializeDraft(state.Draft,
            state.CardCategories.Select(category => category.Id), state.MonsterCategories.Select(category => category.Id));
        state.Dirty = bundle == null;
        state.PendingDelete = false;
        state.Status = string.Empty;
    }

    private static void BuildEditor(EditorState state)
    {
        state.CardCategories = SkinService.GetCardPresetCategories();
        state.MonsterCategories = SkinService.GetMonsterPresetCategories();
        state.RefreshPresetNames.Clear();
        var scroll = ScrollListRebuild.Begin(state.Content, state.GroupId + ":" + state.Draft.Id);
        var title = CreateLabel(state.DisplayName + " · " + ModLocalization.Get(ModText.CharacterSkinBundle), 27);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.AddThemeColorOverride("font_color", new Color("efc850"));
        state.Content.AddChild(title);

        var profileRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        profileRow.AddThemeConstantOverride("separation", 10);
        state.Content.AddChild(profileRow);
        var profiles = CreateOptions();
        profiles.Name = "BundleProfiles";
        profiles.AddItem(ModLocalization.Get(ModText.NewBundle));
        var bundles = SkinService.GetCharacterSkinBundles(state.GroupId);
        for (var i = 0; i < bundles.Count; i++)
        {
            profiles.AddItem(bundles[i].Name);
            if (bundles[i].Name.Equals(state.EditingName, StringComparison.OrdinalIgnoreCase))
            {
                profiles.Select(i + 1);
            }
        }
        profiles.ItemSelected += index =>
        {
            LoadDraft(state, index == 0 ? null : bundles[(int)index - 1]);
            BuildEditor(state);
        };
        profileRow.AddChild(profiles);
        var save = CreateButton(ModText.SaveCharacterSkinMerge);
        save.Name = "SaveBundle";
        save.CustomMinimumSize = new Vector2(138f, 42f);
        save.Pressed += () => Save(state);
        profileRow.AddChild(save);

        var name = new LineEdit
        {
            Name = "BundleName",
            Text = state.Draft.Name,
            MaxLength = SkinService.CardSkinPresetNameMaxLength,
            PlaceholderText = ModLocalization.Get(ModText.BundleName),
            Editable = true,
            CustomMinimumSize = new Vector2(0f, 42f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        CharacterSkinCompositionControls.ApplyLineEditTheme(name);
        name.AddThemeFontSizeOverride("font_size", 20);
        name.TextChanged += value =>
        {
            state.Draft.Name = value;
            MarkDirty(state);
            foreach (var update in state.RefreshPresetNames) update();
        };
        state.Content.AddChild(name);

        var skinRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        skinRow.AddThemeConstantOverride("separation", 12);
        state.Content.AddChild(skinRow);
        var skinLabel = CreateLabel(ModLocalization.Get(ModText.BundleCharacterSkin), 19);
        skinLabel.CustomMinimumSize = new Vector2(250f, 42f);
        skinRow.AddChild(skinLabel);
        var skins = CreateOptions();
        skins.Name = "BundleCharacterSkin";
        var options = SkinService.GetCharacterSkinBundleSourceOptions(state.GroupId)
            .Select(option => (option.Id, Name: ModLocalization.DisplayOptionName(option.Name))).ToList();
        options.Insert(0, (SkinCatalog.BaseOptionId, ModLocalization.Get(ModText.GameDefault)));
        var selected = options.FindIndex(option => option.Id.Equals(state.Draft.CharacterOptionId, StringComparison.OrdinalIgnoreCase));
        if (selected < 0)
        {
            var hidden = SkinService.Catalog?.Groups.FirstOrDefault(group =>
                    group.Id.Equals(state.GroupId, StringComparison.OrdinalIgnoreCase))?.Options.FirstOrDefault(option =>
                    option.Id.Equals(state.Draft.CharacterOptionId, StringComparison.OrdinalIgnoreCase));
            options.Add((state.Draft.CharacterOptionId, hidden != null ? ModLocalization.DisplayOptionName(hidden.Name) :
                ModLocalization.Get(ModText.CharacterSkinSourceUnavailable) + " · " + state.Draft.CharacterOptionId));
            selected = options.Count - 1;
        }
        foreach (var option in options)
        {
            skins.AddItem(option.Name);
        }
        skins.Select(selected);
        skins.ItemSelected += index => { state.Draft.CharacterOptionId = options[(int)index].Id; MarkDirty(state); };
        skinRow.AddChild(skins);

        scroll.CustomMinimumSize = Vector2.Zero;
        scroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
        ScrollListRebuild.PlaceAfterHeader(scroll);
        var fields = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin
        };
        fields.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(fields);
        AddPresetSection(state, fields, ModText.BundleCardPresets, state.CardCategories, state.Draft.CardPresetNames);
        AddPresetSection(state, fields, ModText.BundleMonsterPresets, state.MonsterCategories, state.Draft.MonsterPresetNames);

        var hideSource = new CheckBox
        {
            Text = ModLocalization.BundleHideSource,
            ButtonPressed = state.Draft.HideSources,
            CustomMinimumSize = new Vector2(0f, 38f)
        };
        ContextualSkinControls.ApplyGameTheme(hideSource);
        hideSource.AddThemeFontSizeOverride("font_size", 18);
        hideSource.Toggled += value => { state.Draft.HideSources = value; MarkDirty(state); };
        state.Content.AddChild(hideSource);
        var status = CreateLabel(state.Status, 17);
        status.Name = "BundleStatus";
        status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        status.HorizontalAlignment = HorizontalAlignment.Center;
        status.CustomMinimumSize = new Vector2(0f, 25f);
        status.AddThemeColorOverride("font_color", new Color("efc850"));
        state.Content.AddChild(status);
        state.StatusLabel = status;

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 12);
        state.Content.AddChild(actions);
        var delete = CreateButton(state.PendingDelete ? ModText.ConfirmDeleteCharacterSkinMerge : ModText.DeleteCharacterSkinMerge);
        delete.Name = "DeleteBundle";
        delete.Visible = state.EditingName != null;
        delete.CustomMinimumSize = new Vector2(190f, 42f);
        if (state.PendingDelete)
        {
            ApplyDeleteWarning(delete);
        }
        delete.Pressed += () => Delete(state);
        actions.AddChild(delete);
        state.DeleteButton = delete;
        actions.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var close = CreateButton(ModText.Close);
        close.CustomMinimumSize = new Vector2(190f, 42f);
        close.Pressed += () => state.Overlay.Hide();
        actions.AddChild(close);
    }

    private static void AddPresetSection(EditorState state, VBoxContainer fields, ModText title,
        IReadOnlyList<SkinPresetCategory> categories, Dictionary<string, string> references)
    {
        if (categories.Count == 0) return;
        fields.AddChild(new Control { CustomMinimumSize = new Vector2(0f, 8f), MouseFilter = Control.MouseFilterEnum.Ignore });
        fields.AddChild(CreateLabel(ModLocalization.Get(title), 21));
        // Unavailable categories remain saved, but must not occupy the editor.
        foreach (var category in categories)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var label = CreateLabel(category.DisplayName, 19);
            label.CustomMinimumSize = new Vector2(250f, 40f);
            label.ClipText = true;
            label.TooltipText = category.DisplayName;
            row.AddChild(label);
            var picker = CreateOptions();
            picker.Name = "BundlePreset";
            picker.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            var ownKey = BundlePresetPolicy.PresetKey(state.Draft);
            var names = new[] { ownKey }.Concat(category.PresetNames.Where(BundlePresetPolicy.IsOwned))
                .Concat(category.PresetNames.Where(key => !BundlePresetPolicy.IsOwned(key)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var current = BundlePresetPolicy.ResolveReference(state.Draft, references.GetValueOrDefault(category.Id), names);
            references[category.Id] = current;
            var currentIndex = names.FindIndex(value => value.Equals(current, StringComparison.OrdinalIgnoreCase));
            string Display(string key) => key == ownKey
                ? SkinService.GetBundlePresetDisplayName(state.Draft) : SkinService.GetPresetDisplayName(key);
            for (var i = 0; i < names.Count; i++)
            {
                picker.AddItem(Display(names[i]));
                picker.SetItemMetadata(i, names[i]);
            }
            picker.Select(currentIndex);
            PresetChoiceColoring.Attach(picker);
            state.RefreshPresetNames.Add(() => picker.SetItemText(0, Display(ownKey)));
            picker.ItemSelected += index =>
            {
                references[category.Id] = names[(int)index];
                MarkDirty(state);
            };
            row.AddChild(picker);
            fields.AddChild(row);
        }
    }

    private static void MarkDirty(EditorState state)
    {
        state.Dirty = true;
        state.PendingDelete = false;
        state.StatusLabel.Text = string.Empty;
        state.Status = string.Empty;
        state.DeleteButton.Text = ModLocalization.Get(ModText.DeleteCharacterSkinMerge);
        ContextualSkinControls.ApplyGameTheme(state.DeleteButton);
    }

    private static void Save(EditorState state)
    {
        var ok = state.EditingName == null
            ? SkinService.CreateCharacterSkinBundle(state.Draft)
            : SkinService.OverwriteCharacterSkinBundle(state.EditingName, state.Draft);
        if (ok)
        {
            LoadDraft(state, SkinService.GetCharacterSkinBundles(state.GroupId).First(bundle =>
                bundle.Name.Equals(state.Draft.Name.Trim(), StringComparison.OrdinalIgnoreCase)));
        }
        state.Status = ok ? ModLocalization.Get(ModText.BundleSaved) : SkinService.LastError ?? string.Empty;
        state.PendingDelete = false;
        BuildEditor(state);
        if (ok)
        {
            state.Refresh?.Invoke();
        }
    }

    private static void Delete(EditorState state)
    {
        if (state.EditingName == null)
        {
            return;
        }
        if (!state.PendingDelete)
        {
            state.PendingDelete = true;
            state.DeleteButton.Text = ModLocalization.Get(ModText.ConfirmDeleteCharacterSkinMerge);
            ApplyDeleteWarning(state.DeleteButton);
            return;
        }
        if (SkinService.DeleteCharacterSkinBundle(state.GroupId, state.EditingName))
        {
            LoadDraft(state, null);
        }
        else
        {
            state.Status = SkinService.LastError ?? string.Empty;
        }
        state.PendingDelete = false;
        BuildEditor(state);
        if (state.EditingName == null)
        {
            state.Refresh?.Invoke();
        }
    }

    private static Label CreateLabel(string text, int size)
    {
        var label = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        label.AddThemeColorOverride("font_color", new Color("fff6e2"));
        if (ContextualSkinControls.GameFont is { } font)
        {
            label.AddThemeFontOverride("font", font);
        }
        label.AddThemeFontSizeOverride("font_size", size);
        return label;
    }

    private static Button CreateButton(ModText text)
    {
        var button = new Button
        {
            Text = ModLocalization.Get(text),
            CustomMinimumSize = new Vector2(100f, 42f),
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        ContextualSkinControls.ApplyGameTheme(button);
        button.AddThemeFontSizeOverride("font_size", 19);
        return button;
    }

    private static OptionButton CreateOptions()
    {
        var picker = new OptionButton
        {
            CustomMinimumSize = new Vector2(0f, 42f),
            FitToLongestItem = false,
            ClipText = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Alignment = HorizontalAlignment.Center
        };
        ContextualSkinControls.ApplyGameTheme(picker);
        picker.AddThemeFontSizeOverride("font_size", 19);
        return picker;
    }

    private static void ApplyDeleteWarning(Button button)
    {
        foreach (var style in new[] { "normal", "hover", "pressed", "focus" })
        {
            button.AddThemeStyleboxOverride(style, ContextualSkinControls.CreateStyleBox(
                new Color("852f3b"), new Color("f28b83"), 2));
        }
    }

    private sealed class EditorState(
        NCharacterSelectScreen screen,
        HBoxContainer entry,
        Control overlay,
        VBoxContainer content)
    {
        public NCharacterSelectScreen Screen { get; } = screen;
        public HBoxContainer Entry { get; } = entry;
        public Control Overlay { get; } = overlay;
        public VBoxContainer Content { get; } = content;
        public string GroupId = string.Empty;
        public string DisplayName = string.Empty;
        public Action? Refresh;
        public CharacterSkinBundle Draft = new();
        public string? EditingName;
        public string Status = string.Empty;
        public bool Dirty;
        public bool PendingDelete;
        public Button DeleteButton = null!;
        public Label StatusLabel = null!;
        public IReadOnlyList<SkinPresetCategory> CardCategories = [];
        public IReadOnlyList<SkinPresetCategory> MonsterCategories = [];
        public List<Action> RefreshPresetNames = [];
    }
}
