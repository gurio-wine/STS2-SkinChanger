using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class AncientCompendiumScreen
{
    private HBoxContainer _ancientPriorityHeader = null!;
    private Button _ancientPriorityButton = null!;
    private Control _ancientPriorityOverlay = null!;
    private VBoxContainer _ancientPriorityContent = null!;
    private bool _ancientPriorityPending;

    private void BuildAncientPriorityControls()
    {
        _ancientPriorityHeader = new HBoxContainer
        {
            Name = "AncientSkinPriorityHeader",
            Position = new Vector2(70, 840),
            Visible = false,
            ZIndex = 10,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _ancientPriorityHeader.AddThemeConstantOverride("separation", 14);
        AddChild(_ancientPriorityHeader);

        _ancientPriorityButton = new Button { CustomMinimumSize = new Vector2(190, 44) };
        ContextualSkinControls.ApplyGameTheme(_ancientPriorityButton);
        _ancientPriorityButton.Text = ModLocalization.Get(ModText.MonsterSkinPriority);
        _ancientPriorityHeader.AddChild(_ancientPriorityButton);

        _ancientPriorityOverlay = new Control
        {
            Name = "AncientSkinPriorityOverlay",
            Visible = false,
            ZIndex = 2000,
            MouseFilter = MouseFilterEnum.Stop
        };
        AddChild(_ancientPriorityOverlay);
        _ancientPriorityOverlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var mask = new ColorRect { Color = new Color(0, 0, 0, .68f), MouseFilter = MouseFilterEnum.Stop };
        _ancientPriorityOverlay.AddChild(mask);
        mask.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        mask.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
            _ancientPriorityOverlay.Hide();
            mask.AcceptEvent();
        };

        var panel = new PanelContainer
        {
            AnchorLeft = .5f, AnchorRight = .5f, AnchorTop = .5f, AnchorBottom = .5f,
            OffsetLeft = -360, OffsetRight = 360, OffsetTop = -260, OffsetBottom = 260,
            MouseFilter = MouseFilterEnum.Stop
        };
        ModThemeRuntime.Panel(panel);
        _ancientPriorityOverlay.AddChild(panel);

        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, 20);
        panel.AddChild(margin);

        _ancientPriorityContent = new VBoxContainer();
        _ancientPriorityContent.AddThemeConstantOverride("separation", 10);
        margin.AddChild(_ancientPriorityContent);

        _ancientPriorityButton.Pressed += () =>
        {
            BuildAncientPriorityList();
            _ancientPriorityOverlay.Show();
            _ancientPriorityOverlay.MoveToFront();
        };

        _ancientPriorityOverlay.VisibilityChanged += () => _sidebarDrawer?.Refresh();
        ModLocalization.Bind(_ancientPriorityOverlay, () =>
        {
            RefreshAncientPriorityHeader();
            if (_ancientPriorityOverlay.Visible) BuildAncientPriorityList();
        });
    }

    private void RefreshAncientPriorityHeader()
    {
        if (_ancientPriorityHeader == null) return;
        var hasOptions = SkinService.GetAncientPriorityOptions().Count > 0;
        _ancientPriorityHeader.Visible = _selectedCategory == OtherCategory.Ancients && hasOptions;
        _ancientPriorityButton.Text = ModLocalization.Get(ModText.MonsterSkinPriority);
        if (!_ancientPriorityHeader.Visible) _ancientPriorityOverlay.Hide();
    }

    private void BuildAncientPriorityList()
    {
        var scroll = ScrollListRebuild.Begin(_ancientPriorityContent, "ancients");
        var title = new Label
        {
            Text = ModLocalization.Get(ModText.OtherCategoryAncients) + " · " + ModLocalization.Get(ModText.MonsterSkinPriority),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        ModThemeRuntime.TextControl(title, 25, accent: true);
        _ancientPriorityContent.AddChild(title);

        scroll.CustomMinimumSize = new Vector2(670, 350);
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        ScrollListRebuild.PlaceAfterHeader(scroll);

        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(rows);

        var options = SkinService.GetAncientPriorityOptions();
        foreach (var option in options)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(650, 42) };
            row.AddThemeConstantOverride("separation", 8);
            rows.AddChild(row);

            var enabled = new CheckBox
            {
                Text = ModLocalization.Get(ModText.EnabledForCategory),
                ButtonPressed = option.Enabled,
                CustomMinimumSize = new Vector2(100, 36)
            };
            ContextualSkinControls.ApplyGameTheme(enabled);
            enabled.AddThemeFontSizeOverride("font_size", 17);
            enabled.Toggled += value => QueueAncientPriorityChange(
                () => SkinService.SetAncientPriorityOptionEnabled(option.OptionId, value));
            row.AddChild(enabled);

            var name = new Label
            {
                Text = ModLocalization.DisplayOptionName(option.Name),
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(260, 36),
                VerticalAlignment = VerticalAlignment.Center
            };
            ModThemeRuntime.TextControl(name, 18);
            row.AddChild(name);

            var coverage = new Label
            {
                Text = $"{option.Coverage}/{option.TotalEvents}",
                CustomMinimumSize = new Vector2(66, 36),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            ModThemeRuntime.TextControl(coverage, 16);
            row.AddChild(coverage);

            foreach (var direction in new[] { -1, 1 })
            {
                var arrow = new Button
                {
                    Text = direction < 0 ? "↑" : "↓",
                    CustomMinimumSize = new Vector2(42, 34),
                    Disabled = direction < 0 ? option == options[0] : option == options[^1]
                };
                ContextualSkinControls.ApplyGameTheme(arrow);
                arrow.Pressed += () => QueueAncientPriorityChange(
                    () => SkinService.MoveAncientPriority(option.OptionId, direction));
                row.AddChild(arrow);
            }
        }

        var close = new Button
        {
            Text = ModLocalization.Get(ModText.Close),
            CustomMinimumSize = new Vector2(180, 42),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter
        };
        ContextualSkinControls.ApplyGameTheme(close);
        close.Pressed += () => _ancientPriorityOverlay.Hide();

        var footer = new HBoxContainer();
        footer.AddChild(close);
        footer.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        footer.AddChild(SkinWorkshopEntry.CreatePriorityButton(this, "ancient", "", BuildAncientPriorityList));
        _ancientPriorityContent.AddChild(footer);
    }

    private void QueueAncientPriorityChange(Func<bool> apply)
    {
        if (_ancientPriorityPending) return;
        _ancientPriorityPending = true;
        Callable.From(() =>
        {
            try
            {
                if (!IsInsideTree() || IsQueuedForDeletion()) return;
                if (!apply()) ModLog.Error("先古优先级修改失败：" + SkinService.LastError);

                if (_selectedAncient != null)
                {
                    var ancient = _selectedAncient;
                    RebuildPreview(ancient);
                    PopulateSkinDropdown(AncientCompendiumEntry.FindGroup(ancient.Id.Entry));
                }
                BuildAncientPriorityList();
            }
            finally { _ancientPriorityPending = false; }
        }).CallDeferred();
    }
}