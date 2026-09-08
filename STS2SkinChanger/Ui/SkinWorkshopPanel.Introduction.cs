using Godot;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class SkinWorkshopPanel
{
    private sealed record HoverItem(ulong Id, Control Slot, PanelContainer Panel);
    private readonly List<HoverItem> _hoverItems = [];
    private HoverItem? _lifted;
    private readonly WorkshopHoverDwell _introDwell = new();
    private Control _intro = null!;
    private Label _introTitle = null!;
    private Label _introDescription = null!;
    private Control _introDescriptionHost = null!;
    private Label _introImageStatus = null!;
    private Label _introPage = null!;
    private TextureRect _introImage = null!;
    private Control _introImageHost = null!;
    private SceneTree? _hoverTree;
    private WorkshopCarousel<Texture2D>? _carousel;
    private readonly Dictionary<HoverItem, Tween> _returnTweens = [];
    private CancellationTokenSource? _introToken;
    private int _introGeneration;
    private int _hoverPopups;
    private bool _hoverKeyboard;
    private int _hoverChanges;
    private double _hoverTotalMs;
    private double _hoverMaxMs;

    private void InitializeHover()
    {
        CreateIntroduction();
        _hoverTree = GetTree();
        _hoverTree.ProcessFrame += UpdateHover;
    }

    private void KeepHoverBelow(OptionButton picker)
    {
        picker.GetPopup().AboutToPopup += () => { _hoverPopups++; ResetHover(); };
        picker.GetPopup().PopupHide += () => _hoverPopups = Math.Max(0, _hoverPopups - 1);
    }

    private void CreateIntroduction()
    {
        // Every descendant also ignores input: the panel must never create a
        // second hover target or intercept clicks on the list behind it.
        _intro = new Control { Visible = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 3 };
        AddChild(_intro);
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        _intro.AddChild(panel); panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); ModThemeRuntime.Panel(panel);
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        foreach (var edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 14);
        panel.AddChild(margin);
        var body = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        body.AddThemeConstantOverride("separation", 10); margin.AddChild(body);
        _introTitle = Text("", 22); _introTitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _introTitle.MaxLinesVisible = 2; _introTitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; body.AddChild(_introTitle);
        _introImageHost = new Control { CustomMinimumSize = new Vector2(0, 180), MouseFilter = MouseFilterEnum.Ignore };
        body.AddChild(_introImageHost);
        var placeholder = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        _introImageHost.AddChild(placeholder); placeholder.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var style = new StyleBoxFlat(); placeholder.AddThemeStyleboxOverride("panel", style);
        ModThemeRuntime.Bind(placeholder, "intro_placeholder", theme =>
        {
            style.BgColor = new Color(new Color(theme.AccentColor), .15f);
            style.CornerRadiusTopLeft = style.CornerRadiusTopRight = style.CornerRadiusBottomLeft = style.CornerRadiusBottomRight = theme.CornerRadius;
        });
        _introImage = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore };
        _introImageHost.AddChild(_introImage); _introImage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _introImageStatus = Text("…", 18); _introImageStatus.HorizontalAlignment = HorizontalAlignment.Center;
        _introImageStatus.VerticalAlignment = VerticalAlignment.Center;
        _introImageStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _introImageHost.AddChild(_introImageStatus); _introImageStatus.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _introPage = Text("", 16); _introPage.HorizontalAlignment = HorizontalAlignment.Center;
        body.AddChild(_introPage);
        // Excerpt height belongs to the available viewport, not the full description's
        // minimum size. Otherwise a long BBCode description pushes the panel off-screen.
        _introDescriptionHost = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        body.AddChild(_introDescriptionHost);
        _introDescription = Text("…", 18); _introDescription.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _introDescription.MaxLinesVisible = 7; _introDescription.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _introDescription.ClipText = true;
        _introDescriptionHost.AddChild(_introDescription); _introDescription.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    private void AttachItemClick(PanelContainer panel, WorkshopItemBinding binding)
    {
        panel.MouseFilter = MouseFilterEnum.Stop;
        panel.MouseDefaultCursorShape = CursorShape.PointingHand;
        panel.FocusMode = FocusModeEnum.All;
        var armed = false;
        var ticket = default(WorkshopItemTicket);
        var pressedAt = Vector2.Zero;
        panel.MouseExited += () => armed = false;
        panel.GuiInput += ev =>
        {
            if (_closed || _suspended || _hoverPopups > 0) { armed = false; return; }
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel &&
                panel.TopLevel)
            {
                // Top-level controls stop native input bubbling. Forward only
                // wheel scrolling; action-button clicks must not be forwarded.
                _listScroll.ScrollVertical += (int)(48 * Math.Max(1, wheel.Factor)) * (wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1);
                panel.AcceptEvent();
                CancelIntroduction();
            }
            else if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left } click)
            {
                panel.AcceptEvent();
                if (click.Pressed)
                {
                    armed = true; ticket = binding.Capture(); pressedAt = panel.GetGlobalMousePosition(); panel.GrabFocus();
                }
                else
                {
                    var open = armed && binding.Matches(ticket) && panel.GetGlobalRect().HasPoint(panel.GetGlobalMousePosition()) &&
                        pressedAt.DistanceTo(panel.GetGlobalMousePosition()) <= 8;
                    armed = false;
                    if (open) OpenItem(binding.Id);
                }
            }
            else if (ev is InputEventMouseMotion && pressedAt.DistanceTo(panel.GetGlobalMousePosition()) > 8) armed = false;
            else if (panel.HasFocus() && ev.IsActionPressed("ui_accept") && !ev.IsEcho())
            { panel.AcceptEvent(); OpenItem(binding.Id); }
        };
    }

    private HoverItem? HoveredItem()
    {
        var mouse = GetGlobalMousePosition();
        var focus = GetViewport().GuiGetFocusOwner();
        var clip = _listScroll.GetGlobalRect();
        return _hoverItems.FirstOrDefault(item => item.Slot.GetGlobalRect().Intersects(clip) &&
            (_hoverKeyboard ? focus != null && (focus == item.Panel || item.Panel.IsAncestorOf(focus)) :
                clip.Grow(8).HasPoint(mouse) && (item.Slot.GetGlobalRect().HasPoint(mouse) ||
                    item == _lifted && item.Panel.GetGlobalRect().HasPoint(mouse))));
    }

    private void UpdateHover()
    {
        if (_hoverPopups > 0 || _closed || _suspended || !IsVisibleInTree()) return;
        foreach (var item in _returnTweens.Keys) PositionLift(item);
        var candidate = HoveredItem();
        // Keep the pressed control's canvas/input-root stable until release.
        // Leaving it still hides the introduction immediately.
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            if (!WorkshopHoverPolicy.CanDisplay(candidate?.Id ?? 0, _lifted?.Id ?? 0, false)) CancelIntroduction();
            return;
        }
        if (candidate != _lifted) Lift(candidate);
        if (_lifted is not { } active) return;
        PositionLift(active);
        if (_introDwell.Observe(active.Id, Time.GetTicksMsec() / 1000d) && _introToken == null)
            ShowIntroduction(active);
        if (_intro.Visible)
        {
            PositionIntroduction();
            if (SkinWorkshopService.CachedDetails(active.Id) is { } details) _introTitle.Text = details.Title;
            _carousel?.Tick(!InstantHover);
        }
    }

    private static bool InstantHover => SaveManager.Instance?.PrefsSave?.FastMode == FastModeType.Instant;

    private void Lift(HoverItem? item)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lifted is { } previous) BeginReturn(previous);
        CancelIntroduction();
        _lifted = item;
        if (item == null) { RecordHoverTiming(started); return; }
        if (_returnTweens.Remove(item, out var returning)) returning.Kill();
        // Detach only the canvas transform/clipping, never the node subtree.
        // Reparenting reruns every theme binding and backdrop's TreeEntered work,
        // invalidates text/layout and can stall each hover switch synchronously.
        item.Panel.TopLevel = true;
        item.Panel.ZIndex = 2;
        item.Panel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        item.Panel.Scale = Vector2.One * 1.025f;
        PositionLift(item);
        item.Panel.PivotOffset = item.Panel.Size * .5f;
        // Preserve one themed surface/border. A displaced StyleBox shadow drawn
        // above the backdrop makes the enlarged item look like two stacked cards.
        RecordHoverTiming(started);
    }

    private void ShowIntroduction(HoverItem item)
    {
        // Delay the actual work, not just visibility: cached text still needs
        // shaping/layout and cached images can complete synchronously.
        _introToken = new();
        _introTitle.Text = SkinWorkshopService.CachedDetails(item.Id)?.Title ?? "…";
        _introDescription.Text = "…"; _introImage.Texture = null; _introImageStatus.Text = "…"; _introImageStatus.Show();
        _introPage.Hide();
        _intro.Show(); PositionIntroduction();
        _ = LoadIntroduction(item.Id, _introGeneration, _introToken.Token);
    }

    private void BeginReturn(HoverItem item)
    {
        if (InstantHover) { RestoreItem(item); return; }
        if (_returnTweens.Remove(item, out var previous)) previous.Kill();
        item.Panel.ZIndex = 1;
        var tween = item.Panel.CreateTween().SetIgnoreTimeScale().SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        _returnTweens[item] = tween;
        tween.TweenProperty(item.Panel, "scale", Vector2.One, .12);
        tween.Finished += () =>
        {
            if (_returnTweens.TryGetValue(item, out var current) && current == tween)
            {
                _returnTweens.Remove(item);
                if (item != _lifted) RestoreItem(item);
            }
        };
    }

    private void PositionLift(HoverItem item)
    {
        item.Panel.Size = item.Slot.Size;
        item.Panel.PivotOffset = item.Panel.Size * .5f;
        var amount = Math.Clamp((item.Panel.Scale.X - 1) / .025f, 0, 1);
        var position = item.Slot.GlobalPosition - new Vector2(0, 3 * amount);
        // A top-level CanvasItem's coordinates are in its canvas, like the slot's
        // global position; viewport stretching remains the CanvasLayer's job.
        item.Panel.Position = position;
    }

    private void RestoreLift()
    {
        foreach (var (item, tween) in _returnTweens) { tween.Kill(); RestoreItem(item); }
        _returnTweens.Clear();
        if (_lifted is { } old) RestoreItem(old);
        _lifted = null;
    }

    private void RestoreItem(HoverItem old)
    {
        if (IsInsideTree() && !IsQueuedForDeletion() &&
            GodotObject.IsInstanceValid(_layer) && !_layer.IsQueuedForDeletion() &&
            GodotObject.IsInstanceValid(old.Panel) && GodotObject.IsInstanceValid(old.Slot) &&
            !old.Panel.IsQueuedForDeletion() && !old.Slot.IsQueuedForDeletion())
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            old.Panel.Scale = Vector2.One;
            old.Panel.TopLevel = false;
            old.Panel.ZIndex = 0;
            old.Panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            RecordHoverTiming(started);
        }
    }

    private void RecordHoverTiming(long started)
    {
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _hoverChanges++; _hoverTotalMs += ms; _hoverMaxMs = Math.Max(_hoverMaxMs, ms);
    }

    private void LogHoverTiming()
    {
        if (_hoverChanges == 0) return;
        // One summary on close, no disk logging in the mouse/frame hot path.
        ModLog.Info($"工坊悬停节点切换：{_hoverChanges} 次，平均 {_hoverTotalMs / _hoverChanges:F2}ms，最高 {_hoverMaxMs:F2}ms；简介延迟 1 秒。");
        _hoverChanges = 0; _hoverTotalMs = _hoverMaxMs = 0;
    }

    private void PositionIntroduction()
    {
        if (_lifted == null) return;
        var font = ModThemeRuntime.Current.FontScale;
        var rect = WorkshopHoverPolicy.PlaceIntroduction(_lifted.Panel.GetGlobalRect().Grow(6),
            new Vector2(Math.Clamp(Size.X * .27f, 300, 450), Math.Min(Size.Y - 24, 480 * font)), new Rect2(Vector2.Zero, Size));
        _intro.Position = GetGlobalTransform().AffineInverse() * rect.Position; _intro.Size = rect.Size;
        _introImageHost.CustomMinimumSize = new Vector2(0, Math.Min(rect.Size.X * .56f, rect.Size.Y * .4f));
        var lineHeight = _introDescription.GetThemeFont("font").GetHeight(_introDescription.GetThemeFontSize("font_size")) +
            _introDescription.GetThemeConstant("line_spacing");
        _introDescription.MaxLinesVisible = Math.Clamp((int)(_introDescriptionHost.Size.Y / Math.Max(1, lineHeight)), 1, 7);
    }

    private async Task LoadIntroduction(ulong id, int generation, CancellationToken token)
    {
        try
        {
            var detail = await SkinWorkshopService.Introduction(id, token);
            if (!IntroductionCurrent(generation, token)) return;
            _introDescription.Text = !detail.Available ? WorkshopDetailsText.Get(WorkshopDetailsTextKey.LoadFailed) :
                detail.Description.Length > 0 ? detail.Description : WorkshopDetailsText.Get(WorkshopDetailsTextKey.NoDescription);
            _introPage.Visible = detail.Images.Length > 1;
            if (detail.Images.Length == 0) _introImageStatus.Text = WorkshopDetailsText.Get(WorkshopDetailsTextKey.ImageUnavailable);
            else
            {
                _carousel = new(WorkshopCoverCache.GetPreview, (texture, index, count) =>
                {
                    if (!IntroductionCurrent(generation, token)) return;
                    _introImage.Texture = texture;
                    _introPage.Text = $"{index + 1} / {count}";
                    if (texture != null) { _introImageStatus.Hide(); RoundCover(_introImage); }
                    else _introImageStatus.Text = WorkshopDetailsText.Get(WorkshopDetailsTextKey.ImageUnavailable);
                }, () => Time.GetTicksMsec() / 1000d);
                _carousel.Start(detail.Images, token);
                _carousel.Tick(!InstantHover);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ModLog.Info("工坊简介显示失败：" + ex.Message); }
    }

    private bool IntroductionCurrent(int generation, CancellationToken token) =>
        !token.IsCancellationRequested && !_closed && !_suspended && _hoverPopups == 0 &&
        generation == _introGeneration && GodotObject.IsInstanceValid(_intro) &&
        WorkshopHoverPolicy.CanDisplay(HoveredItem()?.Id ?? 0, _lifted?.Id ?? 0, false);

    private void CancelIntroduction()
    {
        _introDwell.Clear();
        _introGeneration++;
        _carousel?.Clear(); _carousel = null;
        _introToken?.Cancel(); _introToken?.Dispose(); _introToken = null;
        if (GodotObject.IsInstanceValid(_intro)) { _intro.Hide(); _introImage.Texture = null; }
    }

    private void ResetHover()
    {
        RestoreLift(); CancelIntroduction();
    }
}
