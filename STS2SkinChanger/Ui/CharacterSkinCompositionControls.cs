using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class CharacterSkinCompositionControls
{
    private const string EntryButtonName = "STS2CharacterSkinMergeButton";
    private const string OverlayName = "STS2CharacterSkinMergeOverlay";
    private const string PanelName = "MergePanel";
    private const string MarginName = "MergeMargin";
    private const string ContentName = "MergeContent";

    private static readonly ConditionalWeakTable<NCharacterSelectScreen, EditorState> States = new();

    public static void Show(
        NCharacterSelectScreen screen,
        SkinGroup? group,
        Action refresh)
    {
        var state = States.GetValue(screen, CreateState);
        var changedGroup = !string.Equals(
            state.Group?.Id,
            group?.Id,
            StringComparison.OrdinalIgnoreCase);
        state.Group = group;
        state.Refresh = refresh;
        state.EntryButton.Visible = group != null &&
                                    SkinService.GetRawCharacterSkinOptions(group.Id).Count > 0;
        if (changedGroup && state.Overlay.Visible)
        {
            state.Overlay.Visible = false;
            ResetDraft(state);
        }
    }

    public static void Hide(NCharacterSelectScreen screen)
    {
        if (!States.TryGetValue(screen, out var state))
        {
            return;
        }

        state.EntryButton.Visible = false;
        state.Overlay.Visible = false;
    }

    private static EditorState CreateState(NCharacterSelectScreen screen)
    {
        var entryButton = new Button
        {
            Name = EntryButtonName,
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 0f,
            AnchorBottom = 0f,
            OffsetLeft = 38f,
            OffsetTop = 86f,
            OffsetRight = 218f,
            OffsetBottom = 132f,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            Visible = false,
            ZIndex = 100
        };
        ContextualSkinControls.ApplyGameTheme(entryButton);
        entryButton.AddThemeFontSizeOverride("font_size", 19);
        screen.AddChild(entryButton);

        var overlay = CreateOverlay();
        screen.AddChild(overlay);
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var state = new EditorState(screen, entryButton, overlay);
        entryButton.Pressed += () => Open(state);
        overlay.GetNode<ColorRect>("Mask").GuiInput += input =>
        {
            if (input is not InputEventMouseButton
                {
                    Pressed: true,
                    ButtonIndex: MouseButton.Left
                })
            {
                return;
            }

            state.PendingDeleteCompositionId = null;
            overlay.Visible = false;
            overlay.GetNode<ColorRect>("Mask").AcceptEvent();
        };
        ModLocalization.Bind(entryButton, () =>
        {
            entryButton.Text = ModLocalization.Get(ModText.CharacterSkinMerge);
            if (overlay.Visible)
            {
                BuildEditor(state);
            }
        });
        return state;
    }

    private static Control CreateOverlay()
    {
        var overlay = new Control
        {
            Name = OverlayName,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 2400
        };
        var mask = new ColorRect
        {
            Name = "Mask",
            Color = new Color(0f, 0f, 0f, 0.72f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        overlay.AddChild(mask);
        mask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var panel = new PanelContainer
        {
            Name = PanelName,
            MouseFilter = Control.MouseFilterEnum.Stop,
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -430f,
            OffsetTop = -330f,
            OffsetRight = 430f,
            OffsetBottom = 330f
        };
        panel.AddThemeStyleboxOverride(
            "panel",
            ContextualSkinControls.CreateStyleBox(
                new Color("241a30"),
                new Color("79547e"),
                2));
        overlay.AddChild(panel);

        var margin = new MarginContainer { Name = MarginName };
        margin.AddThemeConstantOverride("margin_left", 22);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_right", 22);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(margin);

        var content = new VBoxContainer { Name = ContentName };
        content.AddThemeConstantOverride("separation", 10);
        margin.AddChild(content);
        return overlay;
    }

    private static void Open(EditorState state)
    {
        if (state.Group == null)
        {
            return;
        }

        var selected = SkinService.Config.GetSelection(state.Group.Id);
        var selectedComposition = SkinService.GetCharacterSkinCompositions(state.Group.Id)
            .FirstOrDefault(composition => composition.Id.Equals(
                selected,
                StringComparison.OrdinalIgnoreCase));
        LoadDraft(state, selectedComposition);
        BuildEditor(state);
        state.Overlay.Visible = true;
        state.Overlay.MoveToFront();
    }

    private static void ResetDraft(EditorState state)
    {
        state.EditingCompositionId = null;
        state.DraftName = string.Empty;
        state.DraftSources.Clear();
        state.HideSources = false;
        state.PendingDeleteCompositionId = null;
        state.StatusText = string.Empty;
    }

    private static void LoadDraft(EditorState state, CharacterSkinComposition? composition)
    {
        ResetDraft(state);
        if (composition == null)
        {
            return;
        }

        state.EditingCompositionId = composition.Id;
        state.DraftName = composition.Name;
        state.DraftSources.AddRange(composition.SourceOptionIds);
        state.HideSources = composition.HideSources;
    }

    private static void BuildEditor(EditorState state)
    {
        var group = state.Group;
        if (group == null || !GodotObject.IsInstanceValid(state.Overlay))
        {
            state.Overlay.Visible = false;
            return;
        }

        var content = state.Overlay.GetNode<VBoxContainer>(
            $"{PanelName}/{MarginName}/{ContentName}");
        foreach (var child in content.GetChildren())
        {
            content.RemoveChild(child);
            child.QueueFree();
        }

        state.Rebuilding = true;
        var title = CreateLabel(
            $"{group.DisplayName} · {ModLocalization.Get(ModText.CharacterSkinMerge)}",
            27,
            new Color("efc850"));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        content.AddChild(title);

        var compositions = SkinService.GetCharacterSkinCompositions(group.Id);
        var profileRow = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(808f, 44f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        profileRow.AddThemeConstantOverride("separation", 10);
        content.AddChild(profileRow);
        var profiles = new OptionButton
        {
            CustomMinimumSize = new Vector2(650f, 42f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FitToLongestItem = false,
            ClipText = true,
            Alignment = HorizontalAlignment.Center
        };
        ContextualSkinControls.ApplyGameTheme(profiles);
        profiles.AddThemeFontSizeOverride("font_size", 19);
        profiles.AddItem(ModLocalization.Get(ModText.NewCharacterSkinMerge));
        profiles.SetItemMetadata(0, string.Empty);
        foreach (var composition in compositions)
        {
            var index = profiles.ItemCount;
            profiles.AddItem(composition.Name);
            profiles.SetItemMetadata(index, composition.Id);
        }

        var profileIndex = Enumerable.Range(0, profiles.ItemCount)
            .FirstOrDefault(index => profiles.GetItemMetadata(index).AsString().Equals(
                state.EditingCompositionId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));
        profiles.Select(profileIndex);
        profiles.ItemSelected += index =>
        {
            if (state.Rebuilding)
            {
                return;
            }

            var id = profiles.GetItemMetadata((int)index).AsString();
            LoadDraft(
                state,
                compositions.FirstOrDefault(composition => composition.Id.Equals(
                    id,
                    StringComparison.OrdinalIgnoreCase)));
            BuildEditor(state);
        };
        profileRow.AddChild(profiles);

        var saveTop = CreateButton(ModLocalization.Get(ModText.SaveCharacterSkinMerge), 138f);
        saveTop.Pressed += () => Save(state);
        profileRow.AddChild(saveTop);

        var nameEdit = new LineEdit
        {
            Text = state.DraftName,
            PlaceholderText = ModLocalization.Get(ModText.CharacterSkinMergeName),
            MaxLength = CharacterSkinCompositionPolicy.MaxNameLength,
            CustomMinimumSize = new Vector2(808f, 42f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        ApplyLineEditTheme(nameEdit);
        nameEdit.TextChanged += text => state.DraftName = text;
        content.AddChild(nameEdit);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(808f, 300f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        content.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(rows);
        BuildSourceRows(state, rows);

        var hideSources = new CheckBox
        {
            Text = ModLocalization.Get(ModText.HideMergedSkinSources),
            ButtonPressed = state.HideSources,
            CustomMinimumSize = new Vector2(808f, 38f)
        };
        ContextualSkinControls.ApplyGameTheme(hideSources);
        hideSources.AddThemeFontSizeOverride("font_size", 18);
        hideSources.Toggled += value => state.HideSources = value;
        content.AddChild(hideSources);

        var status = CreateLabel(state.StatusText, 17, new Color("efc850"));
        status.HorizontalAlignment = HorizontalAlignment.Center;
        status.CustomMinimumSize = new Vector2(808f, 25f);
        content.AddChild(status);

        var actionRow = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(808f, 44f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        actionRow.AddThemeConstantOverride("separation", 12);
        content.AddChild(actionRow);
        var delete = CreateButton(ModLocalization.Get(ModText.DeleteCharacterSkinMerge), 190f);
        delete.Visible = !string.IsNullOrWhiteSpace(state.EditingCompositionId);
        if (state.DeleteConfirmationPending)
        {
            ApplyDeleteConfirmationTheme(delete);
        }
        delete.Pressed += () => Delete(state, delete);
        actionRow.AddChild(delete);
        actionRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var close = CreateButton(ModLocalization.Get(ModText.Close), 190f);
        close.Pressed += () =>
        {
            state.PendingDeleteCompositionId = null;
            state.Overlay.Visible = false;
        };
        actionRow.AddChild(close);

        state.Rebuilding = false;
    }

    private static void BuildSourceRows(EditorState state, VBoxContainer rows)
    {
        var group = state.Group!;
        var rawOptions = SkinService.GetRawCharacterSkinOptions(group.Id);
        var rawById = rawOptions.ToDictionary(
            option => option.Id,
            StringComparer.OrdinalIgnoreCase);
        var selectedIds = state.DraftSources
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidateIds = selectedIds
            .Concat(rawOptions.Select(option => option.Id)
                .Where(optionId => !selectedIds.Contains(
                    optionId,
                    StringComparer.OrdinalIgnoreCase)))
            .ToArray();

        foreach (var optionId in candidateIds)
        {
            var enabledIndex = state.DraftSources.FindIndex(sourceId => sourceId.Equals(
                optionId,
                StringComparison.OrdinalIgnoreCase));
            var enabled = enabledIndex >= 0;
            var available = rawById.TryGetValue(optionId, out var option);
            var displayName = available
                ? ModLocalization.DisplayOptionName(option!.Name)
                : optionId + " · " + ModLocalization.Get(ModText.CharacterSkinSourceUnavailable);
            var row = new HBoxContainer
            {
                CustomMinimumSize = new Vector2(784f, 43f),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            row.AddThemeConstantOverride("separation", 8);
            rows.AddChild(row);

            var selected = new CheckBox
            {
                ButtonPressed = enabled,
                CustomMinimumSize = new Vector2(42f, 34f),
                TooltipText = displayName
            };
            ContextualSkinControls.ApplyGameTheme(selected);
            selected.Toggled += value =>
            {
                if (state.Rebuilding)
                {
                    return;
                }

                if (value)
                {
                    if (!state.DraftSources.Contains(optionId, StringComparer.OrdinalIgnoreCase))
                    {
                        state.DraftSources.Add(optionId);
                    }
                }
                else
                {
                    state.DraftSources.RemoveAll(sourceId => sourceId.Equals(
                        optionId,
                        StringComparison.OrdinalIgnoreCase));
                }

                BuildEditor(state);
            };
            row.AddChild(selected);

            var priority = CreateLabel(enabled ? (enabledIndex + 1).ToString() : string.Empty, 18);
            priority.CustomMinimumSize = new Vector2(34f, 36f);
            priority.HorizontalAlignment = HorizontalAlignment.Center;
            row.AddChild(priority);

            var name = CreateLabel(displayName, 18, available
                ? new Color("fff6e2")
                : new Color("b9adbd"));
            name.ClipText = true;
            name.TooltipText = displayName;
            name.CustomMinimumSize = new Vector2(520f, 36f);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(name);

            var up = CreateButton("↑", 52f);
            up.Disabled = !enabled || enabledIndex == 0;
            up.Pressed += () => MoveSource(state, optionId, -1);
            row.AddChild(up);
            var down = CreateButton("↓", 52f);
            down.Disabled = !enabled || enabledIndex == state.DraftSources.Count - 1;
            down.Pressed += () => MoveSource(state, optionId, 1);
            row.AddChild(down);
        }
    }

    private static void MoveSource(EditorState state, string optionId, int direction)
    {
        var index = state.DraftSources.FindIndex(sourceId => sourceId.Equals(
            optionId,
            StringComparison.OrdinalIgnoreCase));
        var target = index + direction;
        if (index < 0 || target < 0 || target >= state.DraftSources.Count)
        {
            return;
        }

        (state.DraftSources[index], state.DraftSources[target]) =
            (state.DraftSources[target], state.DraftSources[index]);
        BuildEditor(state);
    }

    private static void Save(EditorState state)
    {
        state.PendingDeleteCompositionId = null;
        if (state.Group == null)
        {
            return;
        }

        if (state.DraftSources.Count == 0)
        {
            state.StatusText = ModLocalization.Get(ModText.CharacterSkinMergeNeedsSource);
            BuildEditor(state);
            return;
        }

        if (!SkinService.SaveCharacterSkinComposition(
                state.Group.Id,
                state.EditingCompositionId,
                state.DraftName,
                state.DraftSources,
                state.HideSources,
                out var savedId))
        {
            state.StatusText = SkinService.LastError ?? string.Empty;
            BuildEditor(state);
            return;
        }

        state.EditingCompositionId = savedId;
        var saved = SkinService.GetCharacterSkinCompositions(state.Group.Id)
            .FirstOrDefault(composition => composition.Id.Equals(
                savedId,
                StringComparison.OrdinalIgnoreCase));
        LoadDraft(state, saved);
        state.Refresh?.Invoke();
        BuildEditor(state);
    }

    private static void Delete(EditorState state, Button button)
    {
        if (state.Group == null || string.IsNullOrWhiteSpace(state.EditingCompositionId))
        {
            return;
        }

        if (!state.TryConfirmDelete())
        {
            ApplyDeleteConfirmationTheme(button);
            return;
        }

        if (!SkinService.DeleteCharacterSkinComposition(
                state.Group.Id,
                state.EditingCompositionId))
        {
            state.StatusText = SkinService.LastError ?? string.Empty;
            BuildEditor(state);
            return;
        }

        ResetDraft(state);
        state.Refresh?.Invoke();
        BuildEditor(state);
    }

    private static void ApplyDeleteConfirmationTheme(Button button)
    {
        button.Text = ModLocalization.Get(ModText.ConfirmDeleteCharacterSkinMerge);
        foreach (var (state, background) in new[]
                 {
                     ("normal", "7a1f2b"), ("hover", "9a2937"), ("pressed", "5e1720")
                 })
        {
            button.AddThemeStyleboxOverride(state, ContextualSkinControls.CreateStyleBox(
                new Color(background), new Color("ff7a86"), 2));
        }
        button.AddThemeColorOverride("font_color", new Color("fff4f4"));
        button.AddThemeColorOverride("font_hover_color", new Color("fff4f4"));
        button.AddThemeColorOverride("font_pressed_color", new Color("fff4f4"));
    }

    private static Label CreateLabel(string text, int fontSize, Color? color = null)
    {
        var label = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color ?? new Color("fff6e2"));
        if (ContextualSkinControls.GameFont != null)
        {
            label.AddThemeFontOverride("font", ContextualSkinControls.GameFont);
        }

        return label;
    }

    private static Button CreateButton(string text, float width)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(width, 38f),
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        ContextualSkinControls.ApplyGameTheme(button);
        button.AddThemeFontSizeOverride("font_size", 18);
        return button;
    }

    private static void ApplyLineEditTheme(LineEdit lineEdit)
    {
        lineEdit.AddThemeFontSizeOverride("font_size", 19);
        lineEdit.AddThemeColorOverride("font_color", new Color("fff6e2"));
        lineEdit.AddThemeColorOverride("font_placeholder_color", new Color("b9adbd"));
        lineEdit.AddThemeColorOverride("caret_color", new Color("efc850"));
        lineEdit.AddThemeStyleboxOverride(
            "normal",
            ContextualSkinControls.CreateStyleBox(
                new Color("30243b"),
                new Color("79547e")));
        lineEdit.AddThemeStyleboxOverride(
            "focus",
            ContextualSkinControls.CreateStyleBox(
                new Color("30243b"),
                new Color("efc850"),
                2));
        if (ContextualSkinControls.GameFont != null)
        {
            lineEdit.AddThemeFontOverride("font", ContextualSkinControls.GameFont);
        }
    }

    private sealed class EditorState(
        NCharacterSelectScreen screen,
        Button entryButton,
        Control overlay)
    {
        public NCharacterSelectScreen Screen { get; } = screen;
        public Button EntryButton { get; } = entryButton;
        public Control Overlay { get; } = overlay;
        public SkinGroup? Group { get; set; }
        public Action? Refresh { get; set; }
        public string? EditingCompositionId { get; set; }
        public string DraftName { get; set; } = string.Empty;
        public List<string> DraftSources { get; } = [];
        public bool HideSources { get; set; }
        public string? PendingDeleteCompositionId { get; set; }
        public bool DeleteConfirmationPending =>
            !string.IsNullOrWhiteSpace(EditingCompositionId) &&
            string.Equals(PendingDeleteCompositionId, EditingCompositionId, StringComparison.OrdinalIgnoreCase);

        public bool TryConfirmDelete()
        {
            var confirmed = DeleteConfirmationPending;
            PendingDeleteCompositionId = confirmed ? null : EditingCompositionId;
            return confirmed;
        }

        public bool Rebuilding { get; set; }
        public string StatusText { get; set; } = string.Empty;
    }
}
