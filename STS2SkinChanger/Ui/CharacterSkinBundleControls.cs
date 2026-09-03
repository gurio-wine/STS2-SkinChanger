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
            {
                entry.AnchorLeft = entry.AnchorRight = entry.AnchorTop = entry.AnchorBottom = 0f;
                entry.OffsetLeft = 38f;
                entry.OffsetTop = 142f;
                entry.OffsetRight = 246f;
                entry.OffsetBottom = 188f;
            });

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
            AnchorTop = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -440f,
            OffsetRight = 440f,
            OffsetTop = -350f,
            OffsetBottom = 350f
        };
        panel.AddThemeStyleboxOverride("panel", ContextualSkinControls.CreateStyleBox(
            new Color("241a30"), new Color("79547e"), 2));
        overlay.AddChild(panel);
        var margin = new MarginContainer();
        foreach (var edge in new[] { "left", "right", "top", "bottom" })
        {
            margin.AddThemeConstantOverride("margin_" + edge, 20);
        }
        panel.AddChild(margin);
        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 12);
        margin.AddChild(content);
        var state = new EditorState(screen, entry, overlay, content);
        button.Pressed += () => Open(state);
        mask.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                if (!state.Busy)
                {
                    overlay.Hide();
                }
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
        state.Dirty = bundle == null;
        state.PendingDelete = false;
        state.Status = string.Empty;
    }

    private static void BuildEditor(EditorState state)
    {
        foreach (var child in state.Content.GetChildren())
        {
            state.Content.RemoveChild(child);
            child.QueueFree();
        }
        var title = CreateLabel(state.DisplayName + " · " + ModLocalization.Get(ModText.CharacterSkinBundle), 25);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.AddThemeColorOverride("font_color", new Color("efc850"));
        state.Content.AddChild(title);

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
        profiles.Disabled = state.Busy;
        profiles.ItemSelected += index =>
        {
            LoadDraft(state, index == 0 ? null : bundles[(int)index - 1]);
            BuildEditor(state);
        };
        state.Content.AddChild(profiles);

        var name = new LineEdit
        {
            Name = "BundleName",
            Text = state.Draft.Name,
            MaxLength = SkinService.CardSkinPresetNameMaxLength,
            PlaceholderText = ModLocalization.Get(ModText.BundleName),
            Editable = !state.Busy,
            CustomMinimumSize = new Vector2(0f, 42f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        CharacterSkinCompositionControls.ApplyLineEditTheme(name);
        name.AddThemeFontSizeOverride("font_size", 20);
        name.TextChanged += value => { state.Draft.Name = value; MarkDirty(state); };
        state.Content.AddChild(name);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0f, 360f),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        state.Content.AddChild(scroll);
        var fields = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        fields.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(fields);
        fields.AddChild(CreateLabel(ModLocalization.Get(ModText.BundleCharacterSkin), 21));
        var skins = CreateOptions();
        skins.Name = "BundleCharacterSkin";
        var options = SkinService.GetCharacterSkinOptions(state.GroupId)
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
        skins.Disabled = state.Busy;
        skins.ItemSelected += index => { state.Draft.CharacterOptionId = options[(int)index].Id; MarkDirty(state); };
        fields.AddChild(skins);
        AddPresetSection(state, fields, ModText.BundleCardPresets, state.CardCategories, state.Draft.CardPresetNames);
        AddPresetSection(state, fields, ModText.BundleMonsterPresets, state.MonsterCategories, state.Draft.MonsterPresetNames);

        var hint = CreateLabel(ModLocalization.Get(ModText.BundleReferenceHint), 16);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.AddThemeColorOverride("font_color", new Color("bbb4c0"));
        state.Content.AddChild(hint);
        var status = CreateLabel(state.Status, 17);
        status.Name = "BundleStatus";
        status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        status.AddThemeColorOverride("font_color", new Color("efc850"));
        state.Content.AddChild(status);
        state.StatusLabel = status;

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 12);
        state.Content.AddChild(actions);
        var delete = CreateButton(state.PendingDelete ? ModText.ConfirmDeleteCharacterSkinMerge : ModText.DeleteCharacterSkinMerge);
        delete.Name = "DeleteBundle";
        delete.Disabled = state.EditingName == null || state.Busy;
        if (state.PendingDelete)
        {
            ApplyDeleteWarning(delete);
        }
        delete.Pressed += () => Delete(state);
        actions.AddChild(delete);
        state.DeleteButton = delete;
        actions.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var save = CreateButton(ModText.SaveCharacterSkinMerge);
        save.Name = "SaveBundle";
        save.Disabled = state.Busy;
        save.Pressed += () => Save(state);
        actions.AddChild(save);
        var apply = CreateButton(ModText.BundleApply);
        apply.Name = "ApplyBundle";
        apply.Disabled = state.Busy || state.EditingName == null || state.Dirty;
        apply.TooltipText = state.Dirty ? ModLocalization.Get(ModText.BundleUnsaved) : string.Empty;
        apply.Pressed += () => Apply(state);
        actions.AddChild(apply);
        state.ApplyButton = apply;
        var close = CreateButton(ModText.Close);
        close.Disabled = state.Busy;
        close.Pressed += () => state.Overlay.Hide();
        actions.AddChild(close);
    }

    private static void AddPresetSection(EditorState state, VBoxContainer fields, ModText title,
        IReadOnlyList<SkinPresetCategory> categories, Dictionary<string, string> references)
    {
        fields.AddChild(new Control { CustomMinimumSize = new Vector2(0f, 8f), MouseFilter = Control.MouseFilterEnum.Ignore });
        fields.AddChild(CreateLabel(ModLocalization.Get(title), 21));
        // Keep references to unavailable categories visible/editable instead of silently
        // dropping them when a Mod has been temporarily disabled.
        var rows = categories.Concat(references.Keys.Where(id => categories.All(category =>
                !category.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Select(id => new SkinPresetCategory(id, id, [])));
        foreach (var category in rows)
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
            picker.AddItem(ModLocalization.Get(ModText.BundleUnchanged));
            var names = category.PresetNames.ToList();
            var current = references.GetValueOrDefault(category.Id);
            var currentIndex = names.FindIndex(value => value.Equals(current, StringComparison.OrdinalIgnoreCase));
            for (var i = 0; i < names.Count; i++)
            {
                picker.AddItem(names[i]);
            }
            if (current != null && currentIndex < 0)
            {
                picker.AddItem(current + " · " + ModLocalization.Get(ModText.CharacterSkinSourceUnavailable));
                names.Add(current);
                currentIndex = names.Count - 1;
            }
            picker.Select(currentIndex + 1);
            picker.Disabled = state.Busy;
            picker.ItemSelected += index =>
            {
                if (index == 0)
                {
                    references.Remove(category.Id);
                }
                else
                {
                    references[category.Id] = names[(int)index - 1];
                }
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
        state.ApplyButton.Disabled = true;
        state.ApplyButton.TooltipText = ModLocalization.Get(ModText.BundleUnsaved);
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
    }

    private static void Apply(EditorState state)
    {
        if (state.EditingName == null || state.Dirty || state.Busy)
        {
            return;
        }
        var groupId = state.GroupId;
        var name = state.EditingName;
        state.Busy = true;
        state.Status = ModLocalization.Get(ModText.BundleApplying);
        state.PendingDelete = false;
        BuildEditor(state);
        Callable.From(() =>
        {
            try
            {
                if (!GodotObject.IsInstanceValid(state.Overlay) || !state.Overlay.Visible || state.GroupId != groupId)
                {
                    return;
                }
                var ok = SkinService.ApplyCharacterSkinBundle(groupId, name, out var warnings);
                state.Status = ok ? string.Join("\n", new[] { ModLocalization.Get(ModText.BundleApplied) }.Concat(warnings)) :
                    ModLocalization.Get(ModText.AppearanceFailed) + " " + SkinService.LastError;
                // Same character preview/portrait refresh as changing a normal skin. Also
                // rebuild after rollback, because mounted resources may have been recreated.
                state.Refresh?.Invoke();
            }
            catch (Exception error)
            {
                ModLog.Error("刷新皮肤包界面失败：" + error);
                state.Status = ModLocalization.Get(ModText.AppearanceFailed) + " " + error.Message;
            }
            finally
            {
                state.Busy = false;
                if (GodotObject.IsInstanceValid(state.Overlay))
                {
                    BuildEditor(state);
                }
            }
        }).CallDeferred();
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

    private sealed class EditorState(NCharacterSelectScreen screen, HBoxContainer entry, Control overlay, VBoxContainer content)
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
        public bool Busy;
        public bool PendingDelete;
        public Button ApplyButton = null!;
        public Button DeleteButton = null!;
        public Label StatusLabel = null!;
        public IReadOnlyList<SkinPresetCategory> CardCategories = [];
        public IReadOnlyList<SkinPresetCategory> MonsterCategories = [];
    }
}
