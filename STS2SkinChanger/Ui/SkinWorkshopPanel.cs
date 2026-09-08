using Godot;
using STS2SkinChanger.Core;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Ui;

internal partial class SkinWorkshopPanel : Control
{
    private const int PageSize = 8;
    private bool _suspended;
    private static readonly string[] Kinds = ["", "character", "cards", "monster", "ancient", "merchant", "companion", "event"];
    private string _kind = "";
    private string _target = "";
    private int _page;
    private int _generation;
    private Window? _window;
    private CancellationTokenSource? _pageToken;
    private Action? _refresh;
    private Node _origin = null!;
    private bool _originConnected;
    private CanvasLayer _layer = null!;
    private GridContainer _rows = null!;
    private Label _pageLabel = null!;
    private Button _previous = null!;
    private Button _next = null!;
    private ScrollContainer _listScroll = null!;
    private readonly Dictionary<ulong, Label> _metrics = [];
    private readonly List<(ulong Id, Label Status, Button Action, Button Cancel)> _actions = [];

    internal static void Show(Node origin, string kind, string target, Action refresh, string region = "")
    {
        var root = origin.GetTree()?.Root;
        if (root == null || root.GetNodeOrNull("SCSkinWorkshopLayer") != null) return;
        var layer = new CanvasLayer { Name = "SCSkinWorkshopLayer", Layer = 100, ProcessMode = ProcessModeEnum.Always };
        var panel = new SkinWorkshopPanel { _kind = kind, _target = target, _region = region, _origin = origin, _refresh = refresh, _layer = layer };
        try
        {
            root.AddChild(layer);
            layer.AddChild(panel);
            panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            // Like ModThemeEditor, this DLL has no generated Godot virtual-method bridge.
            // Adding a plain Control to the tree does not dispatch our _Ready override.
            panel.Initialize();
            ModLog.Info($"已打开皮肤工坊：类型={kind}，对象={target}，地区={panel._region}。");
        }
        catch (Exception ex)
        {
            panel.Cleanup();
            if (layer.GetParent() is { } parent) parent.RemoveChild(layer);
            layer.QueueFree();
            ModLog.Error($"打开皮肤工坊失败：{ex}");
        }
    }

    private void Initialize()
    {
        MouseFilter = MouseFilterEnum.Stop;
        ProcessMode = ProcessModeEnum.Always;
        TreeExiting += Cleanup;
        _window = GetWindow();
        _window.WindowInput += HandleInput;
        var timer = new Godot.Timer { WaitTime = .5, Autostart = true, ProcessMode = ProcessModeEnum.Always };
        timer.Timeout += Poll;
        AddChild(timer);
        var marqueeTimer = new Godot.Timer { WaitTime = .05, Autostart = true, ProcessMode = ProcessModeEnum.Always };
        marqueeTimer.Timeout += AnimateTitles;
        AddChild(marqueeTimer);
        var mask = new ColorRect { Color = new Color(0, 0, 0, .48f), MouseFilter = MouseFilterEnum.Stop };
        AddChild(mask);
        mask.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        mask.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { mask.AcceptEvent(); Close(); }
        };
        var panel = new PanelContainer { AnchorLeft = .06f, AnchorRight = .94f, AnchorTop = .015f, AnchorBottom = .985f, MouseFilter = MouseFilterEnum.Stop };
        AddChild(panel);
        ModThemeRuntime.Panel(panel);
        var margin = new MarginContainer();
        foreach (var edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 16);
        panel.AddChild(margin);
        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 10);
        margin.AddChild(content);
        var heading = new HBoxContainer(); heading.AddThemeConstantOverride("separation", 18); content.AddChild(heading);
        heading.AddChild(Text(WorkshopText.EntryLabel, 27, true));
        var submit = Button(WorkshopCommunityText.Get(WorkshopCommunityTextKey.SubmitMod), () => { });
        submit.Pressed += () => OpenSubmission(submit); heading.AddChild(submit);
        BuildSort(heading);
        BuildFilters(content);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _listScroll = scroll;
        content.AddChild(scroll);
        _rows = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("h_separation", 18);
        _rows.AddThemeConstantOverride("v_separation", 12);
        scroll.AddChild(_rows);
        var footer = new HBoxContainer(); content.AddChild(footer);
        footer.AddChild(Button(ModLocalization.Get(ModText.Close), Close));
        footer.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _previous = Button(WorkshopText.Get(WorkshopTextKey.Previous), () => { _page--; scroll.ScrollVertical = 0; Rebuild(); });
        footer.AddChild(_previous);
        _pageLabel = Text(""); _pageLabel.CustomMinimumSize = new Vector2(85, 0); _pageLabel.HorizontalAlignment = HorizontalAlignment.Center;
        footer.AddChild(_pageLabel);
        _next = Button(WorkshopText.Get(WorkshopTextKey.Next), () => { _page++; scroll.ScrollVertical = 0; Rebuild(); });
        footer.AddChild(_next);
        InitializeHover();
        _origin.TreeExited += Close;
        _originConnected = true;
        Rebuild();
    }

    private void Rebuild()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        ResetHover();
        _pageToken?.Cancel(); _pageToken?.Dispose(); _pageToken = new();
        var token = _pageToken.Token;
        var generation = ++_generation;
        _subscriptions.Refresh(SkinWorkshopService.Catalog.Select(item => item.Id), SkinWorkshopService.IsSubscribed);
        var filtered = FilteredItems();
        _filteredIds = filtered.Select(item => item.Id).ToArray();
        var pages = Math.Max(1, (filtered.Length + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);
        _previous.Disabled = _page == 0; _next.Disabled = _page + 1 >= pages;
        _pageLabel.Text = $"{_page + 1} / {pages}";
        if (_emptyLabel == null) { _emptyLabel = Text(EmptyText); _rows.AddChild(_emptyLabel); }
        _emptyLabel.Text = EmptyText;
        _emptyLabel.Visible = filtered.Length == 0;
        var visible = filtered.Skip(_page * PageSize).Take(PageSize).ToArray();
        var rows = BindRows(visible);
        UpdateVisibleActions();
        _ = LoadDetails(visible.Select(i => i.Id).ToArray(), rows, generation, token);
        RecordPageBuild(started);
    }

    private async Task LoadDetails(ulong[] ids, Dictionary<ulong, ItemVisual> rows, int generation, CancellationToken token)
    {
        try
        {
            var details = await SkinWorkshopService.Query(ids, token);
            if (token.IsCancellationRequested || generation != _generation || !GodotObject.IsInstanceValid(this)) return;
            var covers = new List<Task>();
            foreach (var (id, detail) in details)
            {
                if (!rows.TryGetValue(id, out var row) || !row.Binding.Matches(row.Ticket)) continue;
                row.Title.Text = detail.Title;
                if (_metrics.TryGetValue(id, out var metric)) metric.Text = WorkshopSortPolicy.Metric(detail, _sort, ModLocalization.CurrentLanguage);
                covers.Add(LoadCover(detail.PreviewUrl, row, generation, token));
            }
            await Task.WhenAll(covers);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ModLog.Info("工坊列表详情加载失败：" + ex.Message); }
    }

    private async Task LoadCover(string url, ItemVisual row, int generation, CancellationToken token)
    {
        var texture = await WorkshopCoverCache.Get(url, token);
        if (texture == null || token.IsCancellationRequested || generation != _generation ||
            !row.Binding.Matches(row.Ticket) || !GodotObject.IsInstanceValid(row.Cover)) return;
        row.Cover.Texture = texture;
        row.Placeholder.Hide();
        RoundCover(row.Cover);
    }

    private async Task Subscribe(ulong id)
    {
        if (_closed || SkinWorkshopService.DownloadState(id)?.Busy == true) return;
        _subscriptions.BeginAction(id);
        try { await SkinWorkshopService.Subscribe(id); }
        finally
        {
            _subscriptions.EndAction(id);
            if (GodotObject.IsInstanceValid(this) && IsInsideTree()) Poll();
        }
    }
    private async Task Unsubscribe(ulong id)
    {
        if (_closed || SkinWorkshopService.DownloadState(id)?.Busy == true) return;
        _actionErrors.Remove(id);
        _subscriptions.BeginAction(id);
        try { await SkinWorkshopService.Unsubscribe(id); }
        finally
        {
            _subscriptions.EndAction(id);
            if (GodotObject.IsInstanceValid(this) && IsInsideTree()) Poll();
        }
    }
    private void Poll()
    {
        if (_closed) return;
        UpdateVisibleActions();
        PollFilters();
    }
    private void UpdateVisibleActions()
    {
        if (_closed) return;
        // The browser's CanvasLayer sits above the native UI. Yield to ALL native
        // modals, including another Mod's subscription notice, not only our own.
        _suspended = MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer.Instance?.OpenModal != null;
        if (_suspended) ResetHover();
        _layer.Visible = !_suspended;
        foreach (var (id, label, action, cancel) in _actions)
        {
            try
            {
                var state = SkinWorkshopService.DownloadState(id);
                var active = SkinWorkshopService.IsActive(id);
                var subscribed = SkinWorkshopService.IsSubscribed(id);
                var knownRestart = SkinWorkshopService.Catalog.First(i => i.Id == id).RestartRequired;
                var primary = WorkshopItemActions.Primary(subscribed, active, SkinWorkshopService.IsInstalled(id), knownRestart, state?.State, state?.Busy == true);
                action.Visible = primary != null;
                action.Disabled = _restartPending || state?.Busy == true;
                action.Text = primary is { } key ? WorkshopText.Get(key) : "";
                action.TooltipText = primary == WorkshopTextKey.Restart && state is { Reason: not WorkshopLoadReason.None } ? WorkshopNoticeText.Reason(state.Reason) :
                    !active && knownRestart ? WorkshopNoticeText.Get(WorkshopNoticeKey.KnownRestart) : "";
                cancel.Visible = subscribed; cancel.Disabled = _restartPending || state?.Busy == true;
                cancel.TooltipText = WorkshopBrowserText.Get(WorkshopBrowserTextKey.Retained);
                var restart = primary == WorkshopTextKey.Restart;
                label.Text = WorkshopItemActions.Status(state?.State) is { } statusKey ? WorkshopText.Get(statusKey) : "";
                if (!action.HasMeta("sc_restart_accent") || action.GetMeta("sc_restart_accent").AsBool() != restart)
                {
                    ModThemeRuntime.TextControl(action, 20, restart);
                    action.SetMeta("sc_restart_accent", restart);
                }
                label.TooltipText = state is { Reason: not WorkshopLoadReason.None } ? WorkshopNoticeText.Reason(state.Reason) : state?.Error ?? "";
                if (state?.Unsubscribed == true) { label.Text = WorkshopCommunityText.Get(WorkshopCommunityTextKey.Removed); label.TooltipText = label.Text; }
                if (state?.Removing == true) label.Text = WorkshopBrowserText.Get(WorkshopBrowserTextKey.Unsubscribing);
                if (state is { Busy: false, Error.Length: > 0 }) { label.Text = WorkshopText.Get(WorkshopTextKey.Failed); label.TooltipText = state.Error; }
                if (state is { Busy: true, Removing: false, State: WorkshopTextKey.Waiting } && SkinWorkshopService.Progress(id) is { } progress)
                    label.Text = WorkshopText.Get(WorkshopTextKey.Download) + $" {progress:F0}%";
                if (_actionErrors.TryGetValue(id, out var error)) { label.Text = WorkshopText.Get(WorkshopTextKey.Failed); label.TooltipText = error; }
            }
            catch { action.Disabled = true; cancel.Disabled = true; label.Text = WorkshopText.Get(WorkshopTextKey.Offline); }
        }
    }
    private void HandleInput(InputEvent ev)
    {
        if (ev is InputEventMouse) _hoverKeyboard = false;
        else if (ev is InputEventKey { Pressed: true } or InputEventJoypadButton { Pressed: true }) _hoverKeyboard = true;
        // Do not wait for the animation timer to hide the old introduction on exit.
        if (ev is InputEventMouseMotion) UpdateHover();
        if (!_closed && !_suspended && ev.IsActionPressed("ui_cancel") && !ev.IsEcho())
        {
            GetViewport().SetInputAsHandled();
            Close();
        }
    }
    private bool _closed;
    private void Close()
    {
        if (_closed) return; _closed = true;
        ResetHover();
        DisconnectOrigin();
        _pageToken?.Cancel();
        _layer.Hide();
        _layer.QueueFree();
        if (GodotObject.IsInstanceValid(_origin) && _origin.IsInsideTree()) _refresh?.Invoke();
    }
    private void Cleanup()
    {
        _closed = true;
        ResetHover();
        LogHoverTiming();
        LogPageBuildTiming();
        if (GodotObject.IsInstanceValid(_hoverTree)) _hoverTree!.ProcessFrame -= UpdateHover;
        _hoverTree = null;
        if (GodotObject.IsInstanceValid(_window)) _window!.WindowInput -= HandleInput;
        _window = null;
        DisconnectOrigin();
        _pageToken?.Cancel(); _pageToken?.Dispose();
        _pageToken = null;
        _marquees.Clear();
    }
    private void DisconnectOrigin()
    {
        if (!_originConnected) return;
        _originConnected = false;
        if (GodotObject.IsInstanceValid(_origin)) _origin.TreeExited -= Close;
    }
    private static Label Text(string text, int size = 20, bool accent = false)
    {
        var label = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
        if (ContextualSkinControls.GameFont is { } font) label.AddThemeFontOverride("font", font);
        ModThemeRuntime.TextControl(label, size, accent); return label;
    }
    private static Button Button(string text, Action pressed)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(130, 42), MouseDefaultCursorShape = CursorShape.PointingHand };
        ContextualSkinControls.ApplyGameTheme(button); button.Pressed += pressed; return button;
    }
}

internal static class SkinWorkshopEntry
{
    internal static void Append(OptionButton dropdown, bool characterPopup = false)
    {
        var i = dropdown.ItemCount;
        dropdown.AddItem(WorkshopText.EntryLabel); dropdown.SetItemMetadata(i, WorkshopCatalogPolicy.CommandId);
        for (var index = 0; index < dropdown.ItemCount; index++)
            if (dropdown.GetItemMetadata(index).AsString() == SkinCatalog.BaseOptionId)
                dropdown.SetItemText(index, ModLocalization.Get(ModText.GameDefault));
        if (!characterPopup)
        {
            PresetChoiceColoring.Attach(dropdown, SkinOptionStylePolicy.IsAccented, colorSelection: false);
            RefreshSelectionColor(dropdown);
        }
    }

    internal static void RefreshSelectionColor(OptionButton dropdown)
    {
        // Programmatic Select does not emit ItemSelected. Call after repopulating too,
        // including the lazy one-item card selector, without loading its skin options.
        if (!dropdown.HasMeta("sc_skin_choice_color"))
        {
            dropdown.SetMeta("sc_skin_choice_color", true);
            dropdown.ItemSelected += _ => RefreshSelectionColor(dropdown);
        }
        ModThemeRuntime.Bind(dropdown, "skin_choice_color", theme =>
        {
            var accented = dropdown.Selected >= 0 && dropdown.Selected < dropdown.ItemCount &&
                SkinOptionStylePolicy.IsAccented(dropdown.GetItemMetadata(dropdown.Selected).AsString());
            var color = new Color(accented ? theme.AccentColor : theme.TextColor);
            foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
                dropdown.AddThemeColorOverride(state, color);
        });
    }

    internal static Button CreatePriorityButton(Node origin, string kind, string target, Action refresh, string region = "")
    {
        var button = new Button
        {
            Text = WorkshopText.EntryLabel, CustomMinimumSize = new Vector2(180, 42),
            FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        ContextualSkinControls.ApplyGameTheme(button);
        ModThemeRuntime.AccentText(button);
        button.Pressed += () => Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(origin) && origin.IsInsideTree())
                SkinWorkshopPanel.Show(origin, kind, target, refresh, region);
        }).CallDeferred();
        return button;
    }
    internal static bool Open(string optionId, Node origin, string groupId, Action refresh, bool cards = false, string? kind = null)
    {
        if (WorkshopCatalogPolicy.IsSkinChoice(optionId)) return false;
        refresh();
        // Finish the dropdown's click/hide dispatch before adding the modal input mask.
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(origin) || !origin.IsInsideTree()) return;
            SkinWorkshopPanel.Show(origin, kind ?? (cards ? "cards" : SkinService.Catalog?.WorkshopKind(groupId) ?? "character"), groupId, refresh);
        }).CallDeferred();
        return true;
    }
}
