using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class CharacterModelPreviewControls
{
    private const string PanelName = "STS2CharacterModelPreview";
    internal const float GripWidth = 12;
    internal const float Gap = 1;
    internal const float ModelInset = GripWidth + Gap;

    internal static bool ShouldShow(IEnumerable<Mod> mods, bool nativeCooperationActive) =>
        !nativeCooperationActive && !mods.Any(mod => mod.state == ModLoadState.Loaded &&
            FrameworkCompatibilityLayer.IsKnownFrameworkHost(mod.manifest?.id));

    internal static bool IsEnabled => ShouldShow(ModManager.Mods, FrameworkRegistryCooperation.IsActive);

    internal static Rect2? ResolveLayout(Rect2 infoFrame, Rect2 screen)
    {
        if (!infoFrame.HasArea() || !screen.HasArea() || !infoFrame.Position.IsFinite() ||
            !infoFrame.Size.IsFinite() || !screen.Position.IsFinite() || !screen.Size.IsFinite()) return null;
        // Same narrow silhouette as the original preview, measured against the visible info
        // frame rather than InfoPanel's larger layout box. Never shrink the shared height.
        var x = infoFrame.End.X + Gap;
        var width = Math.Min(infoFrame.Size.Y * (308f / 429f) + ModelInset, screen.End.X - 24 - x);
        return width < 64 + ModelInset ? null : new Rect2(x, infoFrame.Position.Y, width, infoFrame.Size.Y);
    }

    internal static Rect2? ResolveDockArea(Rect2 infoFrame)
    {
        if (!infoFrame.HasArea() || !infoFrame.Position.IsFinite() || !infoFrame.Size.IsFinite()) return null;
        var width = GripWidth * 3 + Gap * 2;
        return new Rect2(infoFrame.Position.X - Gap - width, infoFrame.Position.Y + infoFrame.Size.Y * .1f,
            width, infoFrame.Size.Y * .8f);
    }

    internal static Rect2? ResolveDockLayout(Rect2 infoFrame) => ResolveDockArea(infoFrame) is { } area
        ? new Rect2(area.GetCenter().X - GripWidth / 2, infoFrame.Position.Y,
            infoFrame.Size.Y * (308f / 429f) + ModelInset, infoFrame.Size.Y) : null;

    internal static bool IsGripDocked(Rect2 infoFrame, Vector2 gripCenter) =>
        gripCenter.IsFinite() && ResolveDockArea(infoFrame) is { } area && area.HasPoint(gripCenter);

    internal static Rect2? ResolveDockHint(Rect2 infoFrame, bool dragging, bool visible) =>
        dragging && visible ? ResolveDockArea(infoFrame) : null;

    internal static bool ShouldLoadModel(bool docked, bool hasCharacter, bool enabled, bool visible) =>
        !docked && hasCharacter && enabled && visible;

    public static void Refresh(NCharacterSelectScreen screen, CharacterModel character)
    {
        if (!IsEnabled) { Hide(screen); return; }
        var info = screen.GetNodeOrNull<Control>("InfoPanel");
        if (info == null) return;
        var panel = info.GetNodeOrNull<CharacterModelPreviewPanel>(PanelName);
        if (panel == null)
        {
            panel = new CharacterModelPreviewPanel { Name = PanelName, Visible = false };
            panel.Initialize(screen, info);
            info.AddChild(panel);
        }
        panel.ShowCharacter(character);
    }

    public static void Hide(NCharacterSelectScreen screen) =>
        screen.GetNodeOrNull<CharacterModelPreviewPanel>($"InfoPanel/{PanelName}")?.Suspend();
}

/// <summary>A view-only host for the same complete model renderer used by the optional manager.</summary>
internal partial class CharacterModelPreviewPanel : Control
{
    private NCharacterSelectScreen _screen = null!;
    private Control _info = null!;
    private Control _frame = null!;
    private NinePatchRect _background = null!;
    private Node2D _visualContainer = null!;
    private Button _dragHandle = null!;
    private CharacterModel? _character;
    private bool _refreshQueued;
    private bool _layoutQueued;
    private bool _docked;
    private Panel _dockHint = null!;
    private StyleBoxFlat _dockHintStyle = null!;
    private Window _window = null!;
    private bool _hintDragging;

    internal void Initialize(NCharacterSelectScreen screen, Control info)
    {
        _screen = screen;
        _info = info;
        _frame = info.GetNodeOrNull<Control>("NinePatchRect") ?? info;
        // A passive hover target for the grip; clicks still propagate and do not change skins.
        MouseFilter = MouseFilterEnum.Pass;
        ClipContents = true;
        BuildInterface();
        BuildDockHint();
        DraggableSkinControl.AttachWithHandle(screen, this, _dragHandle,
            LoadPlacement, SavePlacement, ResetPlacement, ApplyDefaultPlacement, UpdateDockedFromDrag,
            OnDragStateChanged);
        _frame.ItemRectChanged += QueueLayout;
        _info.ItemRectChanged += QueueLayout;
        _screen.Resized += QueueLayout;
        VisibilityChanged += OnVisibilityChanged;
        _window = screen.GetWindow();
        _window.FocusExited += CancelPreviewDrag;
        TreeExiting += ReleaseDockHint;
    }

    private void BuildDockHint()
    {
        _dockHint = new Panel
        {
            Name = "SCModelPreviewDockHint", Visible = false,
            MouseFilter = MouseFilterEnum.Ignore, FocusMode = FocusModeEnum.None
        };
        _dockHintStyle = new StyleBoxFlat
        {
            ContentMarginLeft = 0, ContentMarginRight = 0, ContentMarginTop = 0, ContentMarginBottom = 0,
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2
        };
        _dockHint.AddThemeStyleboxOverride("panel", _dockHintStyle);
        // A sibling shares the info frame's coordinates, stays behind the grip and cannot
        // be clipped by the moving preview or intercept its captured pointer events.
        _info.AddChild(_dockHint);
        ModThemeRuntime.Bind(_dockHint, "preview_dock", ApplyDockHintStyle);
    }

    private void ApplyDockHintStyle(ModThemeSettings theme)
    {
        _dockHintStyle.BgColor = ModThemeRuntime.Tint(theme.AccentColor, _docked ? .32f : .14f);
        _dockHintStyle.BorderColor = ModThemeRuntime.Tint(theme.AccentColor, .95f);
        _dockHintStyle.CornerRadiusTopLeft = _dockHintStyle.CornerRadiusTopRight =
            _dockHintStyle.CornerRadiusBottomLeft = _dockHintStyle.CornerRadiusBottomRight =
                Math.Min(theme.CornerRadius, 6);
    }

    private void OnDragStateChanged(bool dragging)
    {
        _hintDragging = dragging;
        RefreshDockHint();
    }

    private void RefreshDockHint()
    {
        if (!GodotObject.IsInstanceValid(_dockHint)) return;
        var area = Alive() && TryGetLayoutBounds(out var infoRect, out _)
            ? CharacterModelPreviewControls.ResolveDockHint(infoRect, _hintDragging, IsVisibleInTree()) : null;
        _dockHint.Visible = area != null;
        if (area is not { } rect) return;
        _dockHint.Position = rect.Position;
        _dockHint.Size = rect.Size;
        ApplyDockHintStyle(ModThemeRuntime.Current);
    }

    private void CancelPreviewDrag() => DraggableSkinControl.CancelDrag(this);

    private void ReleaseDockHint()
    {
        _hintDragging = false;
        if (GodotObject.IsInstanceValid(_window)) _window.FocusExited -= CancelPreviewDrag;
        if (GodotObject.IsInstanceValid(_dockHint))
        {
            _dockHint.Hide();
            _dockHint.QueueFree();
        }
    }

    private void BuildInterface()
    {
        _background = new NinePatchRect { Name = "NinePatchRect", MouseFilter = MouseFilterEnum.Ignore };
        if (_frame is NinePatchRect source)
        {
            // Share the game's existing fuzzy frame texture, never depend on the optional PCK.
            // Only the backdrop inherits its dark tint; the model stays untinted.
            _background.Texture = source.Texture;
            _background.Modulate = source.Modulate * source.SelfModulate;
            _background.PatchMarginLeft = source.PatchMarginLeft;
            _background.PatchMarginRight = source.PatchMarginRight;
            _background.PatchMarginTop = source.PatchMarginTop;
            _background.PatchMarginBottom = source.PatchMarginBottom;
        }
        AddChild(_background);
        _background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _background.OffsetLeft = CharacterModelPreviewControls.ModelInset;
        _visualContainer = new Node2D { Name = "VisualContainer" };
        AddChild(_visualContainer);
        _dragHandle = new Button
        {
            Name = "SkinChangerDragHandle",
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.Move,
            AnchorTop = .1f,
            AnchorBottom = .9f,
            // Match the backdrop opacity without tinting hover feedback or the model.
            SelfModulate = new Color(1, 1, 1, _background.Modulate.A * _background.SelfModulate.A),
            OffsetRight = CharacterModelPreviewControls.GripWidth
        };
        foreach (var (state, color) in new[] { ("normal", "000000"), ("hover", "161616"),
                     ("pressed", "303030"), ("focus", "161616"), ("disabled", "000000") })
            _dragHandle.AddThemeStyleboxOverride(state, new StyleBoxFlat
            {
                BgColor = new Color(color),
                ContentMarginLeft = 0, ContentMarginRight = 0, ContentMarginTop = 0, ContentMarginBottom = 0,
                CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4
            });
        AddChild(_dragHandle);
    }

    internal void ShowCharacter(CharacterModel character)
    {
        _character = character;
        // Layout also establishes visibility when the parent has not finished opening yet.
        // A parked preview still needs its grip positioned, but never a model queued.
        QueueLayout();
        QueueRefresh();
    }

    internal void Suspend()
    {
        _character = null;
        Visible = false;
        ClearModel();
    }

    private void QueueRefresh()
    {
        if (_refreshQueued || !Alive() || !CanLoadModel(requireVisible: false)) return;
        _refreshQueued = true;
        Callable.From(() =>
        {
            if (!Alive()) return;
            try { RefreshModel(); }
            catch (Exception exception) { ModLog.Warn("SC 模型预览刷新失败：" + exception.GetBaseException().Message); }
            finally { _refreshQueued = false; }
        }).CallDeferred();
    }

    private void RefreshModel()
    {
        if (_character == null || !CharacterModelPreviewControls.IsEnabled) { Suspend(); return; }
        if (!CanLoadModel()) return;
        ApplyLayout();
        if (!IsVisibleInTree() || !CanLoadModel()) return;
        // Do not leave the previous character on screen when the new provider cannot load.
        ClearModel();
        FrameworkModelPreview.Refresh(this, _character);
    }

    private bool CanLoadModel(bool requireVisible = true) => CharacterModelPreviewControls.ShouldLoadModel(
        _docked, _character != null, CharacterModelPreviewControls.IsEnabled,
        !requireVisible || _info.IsVisibleInTree());

    private void SetDocked(bool docked)
    {
        if (_docked == docked) return;
        _docked = docked;
        // Keep the same host geometry during a drag so the grip cannot jump out from under
        // the cursor. Only the grip remains hittable while parked over the info panel.
        _background.Visible = _visualContainer.Visible = !docked;
        MouseFilter = docked ? MouseFilterEnum.Ignore : MouseFilterEnum.Pass;
        if (docked) ClearModel();
        else QueueRefresh();
    }

    private void UpdateDockedFromDrag()
    {
        if (!TryGetLayoutBounds(out var infoRect, out _)) return;
        var gripCenter = _info.GetGlobalTransformWithCanvas().AffineInverse() *
                         (_dragHandle.GetGlobalTransformWithCanvas() * (_dragHandle.Size / 2));
        SetDocked(CharacterModelPreviewControls.IsGripDocked(infoRect, gripCenter));
        RefreshDockHint();
    }

    private (float X, float Y)? LoadPlacement()
    {
        SetDocked(SkinService.IsCharacterModelPreviewDocked());
        // Park beside the live info frame even after a resolution/UI-layout change.
        return _docked ? null : SkinService.GetCharacterModelPreviewPosition();
    }

    private void SavePlacement(float x, float y)
    {
        SkinService.SetCharacterModelPreviewPlacement(x, y, _docked);
        DraggableSkinControl.RefreshPlacement(this);
    }

    private void ResetPlacement()
    {
        SkinService.ResetCharacterModelPreviewPosition();
        SetDocked(false);
    }

    private void QueueLayout()
    {
        if (_layoutQueued || !Alive()) return;
        _layoutQueued = true;
        Callable.From(() =>
        {
            _layoutQueued = false;
            if (!Alive()) return;
            ApplyLayout();
        }).CallDeferred();
    }

    private void ApplyLayout()
    {
        if (!GodotObject.IsInstanceValid(_frame)) return;
        var area = GetDefaultLayout();
        Visible = area != null && _character != null && CharacterModelPreviewControls.IsEnabled;
        RefreshDockHint();
        if (area is not { } rect || !Visible) return;
        var resized = !Size.IsEqualApprox(rect.Size);
        Size = rect.Size;
        DraggableSkinControl.RefreshPlacement(this);
        if (resized && _visualContainer.GetNodeOrNull<FrameworkPreviewSurface>("PreviewSprite") is { } surface)
            surface.RefreshLayout();
    }

    private Rect2? GetDefaultLayout()
    {
        if (!TryGetLayoutBounds(out var infoRect, out var screenRect)) return null;
        var expanded = CharacterModelPreviewControls.ResolveLayout(infoRect, screenRect);
        if (!_docked) return expanded;
        if (CharacterModelPreviewControls.ResolveDockLayout(infoRect) is not { } dock) return null;
        // Preserve the expanded host size across docking, including narrow-screen layouts.
        // Its background/input surface is disabled, so it cannot cover the info controls.
        return new Rect2(dock.Position, expanded?.Size ?? dock.Size);
    }

    private bool TryGetLayoutBounds(out Rect2 infoRect, out Rect2 screenRect)
    {
        infoRect = screenRect = default;
        var infoTransform = _info.GetGlobalTransformWithCanvas();
        if (!infoTransform.IsFinite() || Mathf.IsZeroApprox(infoTransform.Determinant())) return false;
        var inverse = infoTransform.AffineInverse();
        infoRect = (inverse * _frame.GetGlobalTransformWithCanvas()) * new Rect2(Vector2.Zero, _frame.Size);
        screenRect = (inverse * _screen.GetGlobalTransformWithCanvas()) * new Rect2(Vector2.Zero, _screen.Size);
        return true;
    }

    private void ApplyDefaultPlacement()
    {
        if (!Alive() || !GodotObject.IsInstanceValid(_frame) || GetDefaultLayout() is not { } rect) return;
        AnchorLeft = AnchorRight = AnchorTop = AnchorBottom = 0;
        Position = rect.Position;
    }

    private void OnVisibilityChanged()
    {
        RefreshDockHint();
        if (!IsVisibleInTree()) ClearModel();
        else if (_character != null) QueueRefresh();
    }

    private void ClearModel()
    {
        if (!GodotObject.IsInstanceValid(_visualContainer)) return;
        foreach (var model in _visualContainer.GetChildren())
        {
            if (model is FrameworkPreviewSurface surface) surface.StopCapture();
            _visualContainer.RemoveChild(model);
            model.QueueFree();
        }
    }

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(_frame)) _frame.ItemRectChanged -= QueueLayout;
        if (GodotObject.IsInstanceValid(_info)) _info.ItemRectChanged -= QueueLayout;
        if (GodotObject.IsInstanceValid(_screen)) _screen.Resized -= QueueLayout;
        VisibilityChanged -= OnVisibilityChanged;
    }

    private bool Alive() => GodotObject.IsInstanceValid(this) && IsInsideTree() && !IsQueuedForDeletion();
}
