using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class AncientCompendiumScreen
{
    private HBoxContainer _eventPriorityHeader = null!;
    private Label _eventPriorityRegionName = null!;
    private Button _eventPriorityButton = null!;
    private Control _eventPriorityOverlay = null!;
    private VBoxContainer _eventPriorityContent = null!;
    private bool _eventPriorityPending;

    private void BuildEventPriorityControls()
    {
        _eventPriorityHeader = new HBoxContainer
        {
            Name = "EventSkinPriorityHeader", Position = new Vector2(70, 884),
            Visible = false, ZIndex = 10, MouseFilter = MouseFilterEnum.Ignore
        };
        _eventPriorityHeader.AddThemeConstantOverride("separation", 14);
        AddChild(_eventPriorityHeader);
        _eventPriorityRegionName = new Label { VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
        ModThemeRuntime.TextControl(_eventPriorityRegionName, 23, accent: true);
        _eventPriorityHeader.AddChild(_eventPriorityRegionName);
        _eventPriorityButton = new Button { CustomMinimumSize = new Vector2(190, 44) };
        ContextualSkinControls.ApplyGameTheme(_eventPriorityButton);
        _eventPriorityHeader.AddChild(_eventPriorityButton);

        _eventPriorityOverlay = new Control
        {
            Name = "EventSkinPriorityOverlay", Visible = false, ZIndex = 2000, MouseFilter = MouseFilterEnum.Stop
        };
        AddChild(_eventPriorityOverlay);
        _eventPriorityOverlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var mask = new ColorRect { Color = new Color(0, 0, 0, .68f), MouseFilter = MouseFilterEnum.Stop };
        _eventPriorityOverlay.AddChild(mask);
        mask.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        mask.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
            _eventPriorityOverlay.Hide();
            mask.AcceptEvent();
        };
        var panel = new PanelContainer
        {
            AnchorLeft = .5f, AnchorRight = .5f, AnchorTop = .5f, AnchorBottom = .5f,
            OffsetLeft = -360, OffsetRight = 360, OffsetTop = -260, OffsetBottom = 260,
            MouseFilter = MouseFilterEnum.Stop
        };
        ModThemeRuntime.Panel(panel);
        _eventPriorityOverlay.AddChild(panel);
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 20);
        panel.AddChild(margin);
        _eventPriorityContent = new VBoxContainer();
        _eventPriorityContent.AddThemeConstantOverride("separation", 10);
        margin.AddChild(_eventPriorityContent);
        _eventPriorityButton.Pressed += () =>
        {
            BuildEventPriorityList();
            _eventPriorityOverlay.Show();
            _eventPriorityOverlay.MoveToFront();
        };
        _eventPriorityOverlay.VisibilityChanged += () => _sidebarDrawer?.Refresh();
        ModLocalization.Bind(_eventPriorityOverlay, () =>
        {
            RefreshEventPriorityHeader();
            if (_eventPriorityOverlay.Visible) BuildEventPriorityList();
        });
    }

    private void RefreshEventPriorityHeader()
    {
        if (_eventPriorityHeader == null) return;
        _eventPriorityHeader.Visible = _selectedCategory == OtherCategory.Events && _eventRegion != null &&
            SkinService.GetEventPriorityOptions(_eventRegion).Count > 0;
        _eventPriorityButton.Text = ModLocalization.Get(ModText.MonsterSkinPriority);
        _eventPriorityRegionName.Text = _eventRegionSelector.Selected < 0 ? "" :
            _eventRegionSelector.GetItemText(_eventRegionSelector.Selected);
        if (!_eventPriorityHeader.Visible) _eventPriorityOverlay.Hide();
    }

    private void BuildEventPriorityList()
    {
        var region = _eventRegion;
        if (region == null) return;
        var scroll = ScrollListRebuild.Begin(_eventPriorityContent, region);
        var title = new Label
        {
            Text = _eventPriorityRegionName.Text + " · " + ModLocalization.Get(ModText.MonsterSkinPriority),
            HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore
        };
        ModThemeRuntime.TextControl(title, 25, accent: true);
        _eventPriorityContent.AddChild(title);
        scroll.CustomMinimumSize = new Vector2(670, 350);
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        ScrollListRebuild.PlaceAfterHeader(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(rows);
        var options = SkinService.GetEventPriorityOptions(region);
        foreach (var option in options)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(650, 42) };
            row.AddThemeConstantOverride("separation", 8);
            rows.AddChild(row);
            var enabled = new CheckBox
            {
                Text = ModLocalization.Get(ModText.EnabledForCategory), ButtonPressed = option.Enabled,
                CustomMinimumSize = new Vector2(100, 36)
            };
            ContextualSkinControls.ApplyGameTheme(enabled);
            enabled.AddThemeFontSizeOverride("font_size", 17);
            enabled.Toggled += value => QueueEventPriorityChange(region,
                () => SkinService.SetEventPriorityOptionEnabled(region, option.OptionId, value));
            row.AddChild(enabled);
            var name = new Label
            {
                Text = ModLocalization.DisplayOptionName(option.Name), ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(260, 36),
                VerticalAlignment = VerticalAlignment.Center
            };
            ModThemeRuntime.TextControl(name, 18);
            row.AddChild(name);
            var coverage = new Label
            {
                Text = $"{option.Coverage}/{option.TotalEvents}", CustomMinimumSize = new Vector2(66, 36),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            ModThemeRuntime.TextControl(coverage, 16);
            row.AddChild(coverage);
            foreach (var direction in new[] { -1, 1 })
            {
                var arrow = new Button
                {
                    Text = direction < 0 ? "↑" : "↓", CustomMinimumSize = new Vector2(42, 34),
                    Disabled = direction < 0 ? option == options[0] : option == options[^1]
                };
                ContextualSkinControls.ApplyGameTheme(arrow);
                arrow.Pressed += () => QueueEventPriorityChange(region,
                    () => SkinService.MoveEventPriority(region, option.OptionId, direction));
                row.AddChild(arrow);
            }
        }
        var close = new Button
        {
            Text = ModLocalization.Get(ModText.Close), CustomMinimumSize = new Vector2(180, 42),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter
        };
        ContextualSkinControls.ApplyGameTheme(close);
        close.Pressed += () => _eventPriorityOverlay.Hide();
        var footer = new HBoxContainer();
        footer.AddChild(close);
        footer.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        footer.AddChild(SkinWorkshopEntry.CreatePriorityButton(this, "event", "", BuildEventPriorityList, region));
        _eventPriorityContent.AddChild(footer);
    }

    private void QueueEventPriorityChange(string region, Func<bool> apply)
    {
        if (_eventPriorityPending) return;
        _eventPriorityPending = true;
        Callable.From(() =>
        {
            try
            {
                if (!IsInsideTree() || IsQueuedForDeletion() || _eventRegion != region) return;
                if (!apply()) ModLog.Error("事件优先级修改失败：" + SkinService.LastError);
                RefreshAncients();
                BuildEventPriorityList();
            }
            finally { _eventPriorityPending = false; }
        }).CallDeferred();
    }
}
