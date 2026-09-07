using Godot;
using STS2SkinChanger.Core;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Ui;

internal partial class SkinWorkshopPanel : Control
{
    private const int PageSize = 8;
    private static readonly string[] Kinds = ["", "character", "cards", "monster", "ancient", "merchant", "companion", "event"];
    private string _kind = "";
    private string _target = "";
    private int _page;
    private int _generation;
    private double _pollTime;
    private CancellationTokenSource? _pageToken;
    private Action? _refresh;
    private Node _origin = null!;
    private CanvasLayer _layer = null!;
    private VBoxContainer _rows = null!;
    private Label _pageLabel = null!;
    private Button _previous = null!;
    private Button _next = null!;
    private readonly List<(ulong Id, Label Status, Button Action)> _actions = [];
    private readonly List<Texture2D> _textures = [];

    internal static void Show(Node origin, string kind, string target, Action refresh)
    {
        var root = origin.GetTree()?.Root;
        if (root == null || root.GetNodeOrNull("SCSkinWorkshopLayer") != null) return;
        var layer = new CanvasLayer { Name = "SCSkinWorkshopLayer", Layer = 100 };
        var panel = new SkinWorkshopPanel { _kind = kind, _target = target, _origin = origin, _refresh = refresh, _layer = layer };
        root.AddChild(layer);
        layer.AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        ProcessMode = ProcessModeEnum.Always;
        var mask = new ColorRect { Color = new Color(0, 0, 0, .48f), MouseFilter = MouseFilterEnum.Stop };
        AddChild(mask);
        mask.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        mask.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { mask.AcceptEvent(); Close(); }
        };
        var panel = new PanelContainer { AnchorLeft = .14f, AnchorRight = .86f, AnchorTop = .06f, AnchorBottom = .94f, MouseFilter = MouseFilterEnum.Stop };
        AddChild(panel);
        ModThemeRuntime.Panel(panel);
        var margin = new MarginContainer();
        foreach (var edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 20);
        panel.AddChild(margin);
        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 12);
        margin.AddChild(content);
        content.AddChild(Text(WorkshopText.Get(WorkshopTextKey.Title), 27, true));
        var filters = new HBoxContainer();
        content.AddChild(filters);
        var type = new OptionButton { CustomMinimumSize = new Vector2(190, 42) };
        foreach (var kind in Kinds) type.AddItem(WorkshopText.Kind(kind));
        ContextualSkinControls.ApplyGameTheme(type);
        type.Select(Math.Max(0, Array.IndexOf(Kinds, _kind)));
        filters.AddChild(type);
        var target = new OptionButton { CustomMinimumSize = new Vector2(230, 42) };
        ContextualSkinControls.ApplyGameTheme(target);
        filters.AddChild(target);
        void PopulateTargets()
        {
            target.Clear(); target.AddItem(WorkshopText.Get(WorkshopTextKey.All)); target.SetItemMetadata(0, "");
            foreach (var id in SkinWorkshopService.Catalog.SelectMany(item => item.Targets).Where(t => _kind.Length == 0 || t.Kind == _kind)
                         .Select(t => t.Target).Distinct().Order(StringComparer.OrdinalIgnoreCase))
            {
                var i = target.ItemCount;
                target.AddItem(TargetName(id)); target.SetItemMetadata(i, id);
            }
            var index = Enumerable.Range(0, target.ItemCount).FirstOrDefault(i => target.GetItemMetadata(i).AsString() == _target);
            target.Select(index);
            // Keep a missing context filter: don't silently broaden an empty target to all skins.
            if (index == 0 && _target.Length > 0)
            { target.AddItem(TargetName(_target)); target.SetItemMetadata(target.ItemCount - 1, _target); target.Select(target.ItemCount - 1); }
        }
        PopulateTargets();
        type.ItemSelected += index => { _kind = Kinds[(int)index]; _target = ""; _page = 0; PopulateTargets(); Rebuild(); };
        target.ItemSelected += index => { _target = target.GetItemMetadata((int)index).AsString(); _page = 0; Rebuild(); };
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        content.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_rows);
        var footer = new HBoxContainer(); content.AddChild(footer);
        _previous = Button(WorkshopText.Get(WorkshopTextKey.Previous), () => { _page--; scroll.ScrollVertical = 0; Rebuild(); });
        footer.AddChild(_previous);
        _pageLabel = Text(""); _pageLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill; _pageLabel.HorizontalAlignment = HorizontalAlignment.Center;
        footer.AddChild(_pageLabel);
        _next = Button(WorkshopText.Get(WorkshopTextKey.Next), () => { _page++; scroll.ScrollVertical = 0; Rebuild(); });
        footer.AddChild(_next);
        footer.AddChild(Button(ModLocalization.Get(ModText.Close), Close));
        _origin.TreeExited += Close;
        Rebuild();
    }

    private void Rebuild()
    {
        _pageToken?.Cancel(); _pageToken?.Dispose(); _pageToken = new();
        var token = _pageToken.Token;
        var generation = ++_generation;
        foreach (var child in _rows.GetChildren()) { _rows.RemoveChild(child); child.QueueFree(); }
        foreach (var texture in _textures) texture.Dispose(); _textures.Clear(); _actions.Clear();
        var filtered = WorkshopCatalogPolicy.Filter(SkinWorkshopService.Catalog, _kind, _target).ToArray();
        var pages = Math.Max(1, (filtered.Length + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);
        _previous.Disabled = _page == 0; _next.Disabled = _page + 1 >= pages;
        _pageLabel.Text = $"{_page + 1} / {pages}";
        if (filtered.Length == 0) _rows.AddChild(Text(WorkshopText.Get(WorkshopTextKey.Empty)));
        var visible = filtered.Skip(_page * PageSize).Take(PageSize).ToArray();
        var rows = new Dictionary<ulong, (Label Title, TextureRect Cover)>();
        foreach (var item in visible)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 110) }; row.AddThemeConstantOverride("separation", 14); _rows.AddChild(row);
            var cover = new TextureRect { CustomMinimumSize = new Vector2(144, 100), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
            row.AddChild(cover);
            var labels = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; row.AddChild(labels);
            var title = Text("#" + item.Id, 22); title.ClipText = true; title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; labels.AddChild(title);
            var tags = Text(string.Join(" · ", item.Targets.Select(t => WorkshopText.Kind(t.Kind)).Distinct()) +
                " · " + string.Join(" / ", item.Targets.Where(t => t.Kind == "character").Select(t => TargetName(t.Target)).Distinct()), 16);
            tags.ClipText = true; tags.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; labels.AddChild(tags);
            var status = Text("", 17); labels.AddChild(status);
            var action = Button(WorkshopText.Get(WorkshopTextKey.Subscribe), () => _ = Subscribe(item.Id));
            action.SizeFlagsVertical = SizeFlags.ShrinkCenter; row.AddChild(action);
            _actions.Add((item.Id, status, action)); rows[item.Id] = (title, cover);
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
    }

    private async Task Subscribe(ulong id)
    {
        await SkinWorkshopService.Subscribe(id);
        if (GodotObject.IsInstanceValid(this) && IsInsideTree()) Poll();
    }
    public override void _Process(double delta)
    {
        _pollTime += delta;
        if (_pollTime < .5) return;
        _pollTime = 0; Poll();
    }
    private void Poll()
    {
        foreach (var (id, label, action) in _actions)
        {
            try
            {
                var state = SkinWorkshopService.DownloadState(id);
                var active = SkinWorkshopService.IsActive(id);
                action.Disabled = state?.Busy == true || active || state?.State == WorkshopTextKey.Restart;
                action.Text = WorkshopText.Get(active ? WorkshopTextKey.Ready : state?.State == WorkshopTextKey.Restart ? WorkshopTextKey.Restart :
                    SkinWorkshopService.IsSubscribed(id) ? WorkshopTextKey.Download : WorkshopTextKey.Subscribe);
                label.Text = state == null ? "" : WorkshopText.Get(state.State);
                label.TooltipText = state?.Error ?? "";
                if (state is { Busy: true, State: WorkshopTextKey.Waiting } && SkinWorkshopService.Progress(id) is { } progress)
                    label.Text = WorkshopText.Get(WorkshopTextKey.Download) + $" {progress:F0}%";
            }
            catch { action.Disabled = true; label.Text = WorkshopText.Get(WorkshopTextKey.Offline); }
        }
    }
    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev.IsActionPressed("ui_cancel")) { GetViewport().SetInputAsHandled(); Close(); }
    }
    private bool _closed;
    private void Close()
    {
        if (_closed) return; _closed = true;
        if (GodotObject.IsInstanceValid(_origin)) _origin.TreeExited -= Close;
        _pageToken?.Cancel();
        _layer.QueueFree();
        if (GodotObject.IsInstanceValid(_origin) && _origin.IsInsideTree()) _refresh?.Invoke();
    }
    public override void _ExitTree()
    {
        _closed = true;
        if (GodotObject.IsInstanceValid(_origin)) _origin.TreeExited -= Close;
        _pageToken?.Cancel(); _pageToken?.Dispose();
        foreach (var texture in _textures) texture.Dispose(); _textures.Clear();
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
    internal static string TargetName(string id) => SkinService.Catalog?.Groups.FirstOrDefault(g => g.Id == id)?.DisplayName ??
        SkinService.Catalog?.CardGroups.FirstOrDefault(g => g.Id == id)?.DisplayName ?? id;
}

internal static class SkinWorkshopEntry
{
    internal static void Append(OptionButton dropdown)
    {
        var i = dropdown.ItemCount;
        dropdown.AddItem(WorkshopText.Get(WorkshopTextKey.Title)); dropdown.SetItemMetadata(i, WorkshopCatalogPolicy.CommandId);
    }
    internal static bool Open(string optionId, Node origin, string groupId, Action refresh, bool cards = false, string? kind = null)
    {
        if (WorkshopCatalogPolicy.IsSkinChoice(optionId)) return false;
        refresh();
        SkinWorkshopPanel.Show(origin, kind ?? (cards ? "cards" : SkinService.Catalog?.WorkshopKind(groupId) ?? "character"), groupId, refresh);
        return true;
    }
}
