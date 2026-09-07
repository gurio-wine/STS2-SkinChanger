using Godot;
using STS2SkinChanger.Core;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Ui;

internal partial class SkinWorkshopPanel : Control
{
    private const int PageSize = 8;
    private static SkinWorkshopPanel? _current;
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
    private CanvasLayer _layer = null!;
    private GridContainer _rows = null!;
    private Label _pageLabel = null!;
    private Button _previous = null!;
    private Button _next = null!;
    private readonly List<(ulong Id, Label Status, Button Action, Button Cancel)> _actions = [];
    private readonly List<Texture2D> _textures = [];

    internal static void Show(Node origin, string kind, string target, Action refresh)
    {
        var root = origin.GetTree()?.Root;
        if (root == null || root.GetNodeOrNull("SCSkinWorkshopLayer") != null) return;
        var layer = new CanvasLayer { Name = "SCSkinWorkshopLayer", Layer = 100, ProcessMode = ProcessModeEnum.Always };
        var panel = new SkinWorkshopPanel { _kind = kind, _target = target, _origin = origin, _refresh = refresh, _layer = layer };
        try
        {
            _current = panel;
            root.AddChild(layer);
            layer.AddChild(panel);
            panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            // Like ModThemeEditor, this DLL has no generated Godot virtual-method bridge.
            // Adding a plain Control to the tree does not dispatch our _Ready override.
            panel.Initialize();
            ModLog.Info($"已打开皮肤工坊：类型={kind}，对象={target}。");
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
        var panel = new PanelContainer { AnchorLeft = .06f, AnchorRight = .94f, AnchorTop = .06f, AnchorBottom = .94f, MouseFilter = MouseFilterEnum.Stop };
        AddChild(panel);
        ModThemeRuntime.Panel(panel);
        var margin = new MarginContainer();
        foreach (var edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 20);
        panel.AddChild(margin);
        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 12);
        margin.AddChild(content);
        content.AddChild(Text(WorkshopText.EntryLabel, 27, true));
        BuildFilters(content);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        content.AddChild(scroll);
        _rows = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("h_separation", 18);
        _rows.AddThemeConstantOverride("v_separation", 18);
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
        _origin.TreeExited += Close;
        Rebuild();
    }

    private void Rebuild()
    {
        _pageToken?.Cancel(); _pageToken?.Dispose(); _pageToken = new();
        var token = _pageToken.Token;
        var generation = ++_generation;
        foreach (var child in _rows.GetChildren()) { _rows.RemoveChild(child); child.QueueFree(); }
        foreach (var texture in _textures) texture.Dispose(); _textures.Clear(); _actions.Clear(); _marquees.Clear();
        var filtered = WorkshopBrowserPolicy.Filter(SkinWorkshopService.Catalog, _kind, _target, RegionMembers).ToArray();
        var pages = Math.Max(1, (filtered.Length + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);
        _previous.Disabled = _page == 0; _next.Disabled = _page + 1 >= pages;
        _pageLabel.Text = $"{_page + 1} / {pages}";
        if (filtered.Length == 0) _rows.AddChild(Text(WorkshopText.Get(WorkshopTextKey.Empty)));
        var visible = filtered.Skip(_page * PageSize).Take(PageSize).ToArray();
        var rows = new Dictionary<ulong, (Label Title, TextureRect Cover)>();
        foreach (var item in visible)
        {
            var itemBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 180) };
            itemBox.AddThemeConstantOverride("separation", 8); _rows.AddChild(itemBox);
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 14); itemBox.AddChild(row);
            var cover = new TextureRect { CustomMinimumSize = new Vector2(144, 100), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
            row.AddChild(cover);
            var labels = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; row.AddChild(labels);
            var title = CreateMarquee(itemBox, labels, "#" + item.Id);
            var tags = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; labels.AddChild(tags); AddTags(tags, item);
            var controls = new HBoxContainer(); itemBox.AddChild(controls);
            var status = Text("", 16); status.SizeFlagsHorizontal = SizeFlags.ExpandFill; status.ClipText = true; status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; controls.AddChild(status);
            var action = Button(WorkshopText.Get(WorkshopTextKey.Subscribe), () => _ = Subscribe(item.Id));
            action.CustomMinimumSize = new Vector2(100, 38); controls.AddChild(action);
            var cancel = Button(WorkshopBrowserText.Get(WorkshopBrowserTextKey.Unsubscribe), () => _ = Unsubscribe(item.Id));
            cancel.CustomMinimumSize = new Vector2(120, 38); controls.AddChild(cancel);
            _actions.Add((item.Id, status, action, cancel)); rows[item.Id] = (title, cover);
        }
        Poll();
        _ = LoadDetails(visible.Select(i => i.Id).ToArray(), rows, generation, token);
    }

    private async Task LoadDetails(ulong[] ids, Dictionary<ulong, (Label Title, TextureRect Cover)> rows, int generation, CancellationToken token)
    {
        try
        {
            var details = await SkinWorkshopService.Query(ids, token);
            if (token.IsCancellationRequested || generation != _generation || !GodotObject.IsInstanceValid(this)) return;
            var covers = new List<Task>();
            foreach (var (id, detail) in details)
            {
                var row = rows[id]; row.Title.Text = detail.Title;
                covers.Add(LoadCover(detail.PreviewUrl, row.Cover, generation, token));
            }
            await Task.WhenAll(covers);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ModLog.Info("工坊列表详情加载失败：" + ex.Message); }
    }

    private async Task LoadCover(string url, TextureRect cover, int generation, CancellationToken token)
    {
        var bytes = await SkinWorkshopService.Cover(url, token);
        if (bytes == null || token.IsCancellationRequested || generation != _generation || !GodotObject.IsInstanceValid(cover)) return;
        using var image = new Image();
        var error = bytes[0] == 137 ? image.LoadPngFromBuffer(bytes) : image.LoadJpgFromBuffer(bytes);
        if (error != Error.Ok || image.GetWidth() > 4096 || image.GetHeight() > 4096) return;
        var factor = Math.Min(1d, Math.Min(144d / image.GetWidth(), 100d / image.GetHeight()));
        image.Resize(Math.Max(1, (int)(image.GetWidth() * factor)), Math.Max(1, (int)(image.GetHeight() * factor)), Image.Interpolation.Lanczos);
        var texture = ImageTexture.CreateFromImage(image); _textures.Add(texture); cover.Texture = texture;
        RoundCover(cover);
    }

    private async Task Subscribe(ulong id)
    {
        await SkinWorkshopService.Subscribe(id);
        if (GodotObject.IsInstanceValid(this) && IsInsideTree()) Poll();
    }
    private async Task Unsubscribe(ulong id)
    {
        await SkinWorkshopService.Unsubscribe(id);
        if (GodotObject.IsInstanceValid(this) && IsInsideTree()) Poll();
    }
    private void Poll()
    {
        if (_closed) return;
        // The browser's CanvasLayer sits above the native UI. Yield to ALL native
        // modals, including another Mod's subscription notice, not only our own.
        _suspended = MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer.Instance?.OpenModal != null;
        _layer.Visible = !_suspended;
        foreach (var (id, label, action, cancel) in _actions)
        {
            try
            {
                var state = SkinWorkshopService.DownloadState(id);
                var active = SkinWorkshopService.IsActive(id);
                var subscribed = SkinWorkshopService.IsSubscribed(id);
                action.Visible = !subscribed || !active && state?.State != WorkshopTextKey.Restart;
                action.Disabled = state?.Busy == true || subscribed && (active || state?.State == WorkshopTextKey.Restart);
                action.Text = WorkshopText.Get(subscribed ? WorkshopTextKey.Download : WorkshopTextKey.Subscribe);
                cancel.Visible = subscribed; cancel.Disabled = state?.Busy == true;
                cancel.TooltipText = WorkshopBrowserText.Get(WorkshopBrowserTextKey.Retained);
                var restart = !active && (state?.State == WorkshopTextKey.Restart ||
                    state == null && SkinWorkshopService.Catalog.First(i => i.Id == id).RestartRequired);
                label.Text = state == null ? active ? WorkshopText.Get(WorkshopTextKey.Ready) :
                    WorkshopNoticeText.Get(restart ? WorkshopNoticeKey.KnownRestart : WorkshopNoticeKey.CheckAfterDownload) : WorkshopText.Get(state.State);
                if (!label.HasMeta("sc_restart_accent") || label.GetMeta("sc_restart_accent").AsBool() != restart)
                {
                    ModThemeRuntime.TextControl(label, 17, restart);
                    label.SetMeta("sc_restart_accent", restart);
                }
                label.TooltipText = state is { Reason: not WorkshopLoadReason.None } ? WorkshopNoticeText.Reason(state.Reason) : state?.Error ?? "";
                if (state?.Unsubscribed == true) { label.Text = WorkshopBrowserText.Get(WorkshopBrowserTextKey.Unsubscribed); label.TooltipText = WorkshopBrowserText.Get(WorkshopBrowserTextKey.Retained); }
                if (state?.Removing == true) label.Text = WorkshopBrowserText.Get(WorkshopBrowserTextKey.Unsubscribing);
                if (state is { Busy: false, Error.Length: > 0 }) { label.Text = WorkshopText.Get(WorkshopTextKey.Failed); label.TooltipText = state.Error; }
                if (state is { Busy: true, Removing: false, State: WorkshopTextKey.Waiting } && SkinWorkshopService.Progress(id) is { } progress)
                    label.Text = WorkshopText.Get(WorkshopTextKey.Download) + $" {progress:F0}%";
            }
            catch { action.Disabled = true; cancel.Disabled = true; label.Text = WorkshopText.Get(WorkshopTextKey.Offline); }
        }
    }
    private void HandleInput(InputEvent ev)
    {
        if (!_closed && !_suspended && ev.IsActionPressed("ui_cancel") && !ev.IsEcho())
        {
            GetViewport().SetInputAsHandled();
            Close();
        }
    }
    private bool _closed;
    internal static void SuspendForNotice(bool suspend)
    {
        if (!GodotObject.IsInstanceValid(_current) || _current!._closed) return;
        _current._suspended = suspend;
        _current._layer.Visible = !suspend;
    }
    private void Close()
    {
        if (_closed) return; _closed = true;
        if (GodotObject.IsInstanceValid(_origin)) _origin.TreeExited -= Close;
        _pageToken?.Cancel();
        _layer.Hide();
        _layer.QueueFree();
        if (GodotObject.IsInstanceValid(_origin) && _origin.IsInsideTree()) _refresh?.Invoke();
    }
    private void Cleanup()
    {
        if (ReferenceEquals(_current, this)) _current = null;
        _closed = true;
        if (GodotObject.IsInstanceValid(_window)) _window!.WindowInput -= HandleInput;
        _window = null;
        if (GodotObject.IsInstanceValid(_origin)) _origin.TreeExited -= Close;
        _pageToken?.Cancel(); _pageToken?.Dispose();
        _pageToken = null;
        foreach (var texture in _textures) texture.Dispose(); _textures.Clear();
        _marquees.Clear();
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
            PresetChoiceColoring.Attach(dropdown, id => id == SkinCatalog.BaseOptionId || !WorkshopCatalogPolicy.IsSkinChoice(id), colorSelection: false);
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
