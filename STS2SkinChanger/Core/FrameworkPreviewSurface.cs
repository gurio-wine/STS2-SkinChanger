using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace STS2SkinChanger.Core;

/// <summary>One complete live model, one isolated render target, and a finite capture lifetime.</summary>
internal partial class FrameworkPreviewSurface : Node2D
{
    private Control _selector = null!;
    private Node2D _container = null!;
    private FrameworkPreviewCreature _owner = null!;
    private SubViewport _viewport = null!;
    private Sprite2D _display = null!;
    private string _groupId = "";
    private RenderedPreviewFraming? _framing;
    private bool _subscribed;
    private bool _started;
    private bool _presentNextFrame;
    private bool _finished;
    private bool _exited;
    private bool _playRequested = true;

    internal void Initialize(Control selector, Node2D container, FrameworkPreviewCreature owner, string groupId)
    {
        _selector = selector;
        _container = container;
        _owner = owner;
        _groupId = groupId;
        var parentViewport = selector.GetViewport();
        _viewport = new SubViewport
        {
            Name = "ModelViewport",
            Size = new Vector2I(512, 512),
            World2D = new World2D(),
            Disable3D = true,
            TransparentBg = true,
            GuiDisableInput = true,
            CanvasItemDefaultTextureFilter = parentViewport.CanvasItemDefaultTextureFilter,
            CanvasItemDefaultTextureRepeat = parentViewport.CanvasItemDefaultTextureRepeat,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled
        };
        _viewport.AddChild(owner);
        AddChild(_viewport);
        _display = new Sprite2D { Name = "ModelImage", Centered = false, Visible = false, Texture = _viewport.GetTexture() };
        AddChild(_display);
        VisibilityChanged += UpdateVisibility;
    }

    internal void BeginCapture()
    {
        if (_exited || _started) return;
        // Wait for the selected model's real Spine readiness, not a guessed load duration.
        if (_owner.Visuals.SpineBody is { } body)
            this.RunWhenSpineReady(body, _ => StartCapture());
        else StartCapture();
    }

    private void StartCapture()
    {
        if (_exited || _started || !Alive(this)) return;
        _started = true;
        FrameworkModelPreview.StartAnimations(_owner, _groupId, playEntry: false);
        UpdateVisibility();
    }

    internal void PlayEntry()
    {
        if (_exited) return;
        if (!_finished) _playRequested = true;
        else FrameworkModelPreview.StartAnimations(_owner, _groupId);
    }

    internal void RefreshLayout()
    {
        if (_exited || !_started || !Alive(this)) return;
        // A window/UI resize needs a new camera fit, not another skin load or model instance.
        _framing?.Cancel();
        _framing = null;
        _presentNextFrame = false;
        _finished = false;
        _display.Visible = false;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (_exited || !_started || !Alive(this)) return;
        if (!IsVisibleInTree())
        {
            Unsubscribe();
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            _viewport.ProcessMode = ProcessModeEnum.Disabled;
            return;
        }
        _viewport.ProcessMode = ProcessModeEnum.Inherit;
        if (_finished)
        {
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible;
            return;
        }
        if (!_subscribed)
        {
            RenderingServer.FramePostDraw += OnFrameDrawn;
            _subscribed = true;
        }
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    private void OnFrameDrawn()
    {
        if (_exited || !Alive(this) || !Alive(_selector) || !Alive(_container) || !Alive(_owner))
        {
            CancelCapture();
            return;
        }
        if (!IsVisibleInTree()) { UpdateVisibility(); return; }
        try
        {
            if (_presentNextFrame)
            {
                Present();
                return;
            }
            if (_framing == null)
            {
                // The first completed frame settles both the native UI layout and the model's
                // animation/world vertices. Raw geometry seeds the camera; it does not decide fit.
                LayoutSurface();
                var seed = FrameworkModelPreview.MeasureCaptureSeed(_owner.Visuals);
                _framing = new RenderedPreviewFraming(seed, _viewport.Size);
            }
            else
            {
                // This is the renderer's final composite: bone/attachment alpha, nested sprites,
                // clipping and shaders have already been applied. Never touch those resources.
                using var image = _viewport.GetTexture().GetImage();
                Rect2? pixels = image == null || image.IsEmpty() ? null : image.GetUsedRect();
                _presentNextFrame = !_framing.Observe(pixels);
            }
            _viewport.CanvasTransform = _framing.CanvasTransform;
            // Even the last camera change must actually render before showing the result.
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        }
        catch (Exception exception)
        {
            ModLog.Warn($"统一小预览取景失败 {_groupId}，保留当前相机：{exception.GetBaseException().Message}");
            _framing?.Cancel();
            Present();
        }
    }

    private void LayoutSurface()
    {
        var panel = _selector.GetNodeOrNull<Control>("NinePatchRect") ?? _selector;
        var inverse = _container.GlobalTransform.AffineInverse();
        var panelRect = (inverse * panel.GetGlobalTransform()) * new Rect2(Vector2.Zero, panel.Size);
        var footer = _selector.GetNodeOrNull<Control>("HBoxContainer");
        Rect2? footerRect = footer == null ? null :
            (inverse * footer.GetGlobalTransform()) * new Rect2(Vector2.Zero, footer.Size);
        var area = FrameworkModelPreview.PreviewArea(panelRect, footerRect);
        if (!area.HasArea() || !area.Position.IsFinite() || !area.Size.IsFinite())
            throw new InvalidOperationException("小模型预览区域尚未就绪。");
        var screenTransform = _container.GetScreenTransform();
        var physical = area.Size * new Vector2(screenTransform.X.Length(), screenTransform.Y.Length());
        if (!physical.IsFinite() || physical.X <= 0 || physical.Y <= 0) physical = area.Size;
        physical *= Math.Min(1, 1024 / Math.Max(physical.X, physical.Y));
        var resolution = new Vector2I(Math.Clamp((int)Math.Ceiling(physical.X), 32, 1024),
            Math.Clamp((int)Math.Ceiling(physical.Y), 32, 1024));
        _viewport.Size = resolution;
        _display.Position = area.Position;
        _display.Scale = area.Size / (Vector2)resolution;
    }

    private void Present()
    {
        Unsubscribe();
        _finished = true;
        _display.Visible = true;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible;
        if (_framing?.HasContent == true)
            ModLog.Info($"统一小预览实际取景：{_groupId}；可见={_framing.VisibleBounds}；" +
                        $"采样={_framing.Samples}；画布={_viewport.Size}；相机比例={_framing.CanvasTransform.X.X:0.###}。");
        else ModLog.Warn($"统一小预览未取得有效像素 {_groupId}，已停止采样并保留初始相机。");
        if (_playRequested) { _playRequested = false; FrameworkModelPreview.StartAnimations(_owner, _groupId); }
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        RenderingServer.FramePostDraw -= OnFrameDrawn;
        _subscribed = false;
    }

    private void CancelCapture()
    {
        Unsubscribe();
        _framing?.Cancel();
        _exited = true;
        if (GodotObject.IsInstanceValid(_viewport))
        {
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            _viewport.ProcessMode = ProcessModeEnum.Disabled;
        }
    }

    public override void _ExitTree()
    {
        CancelCapture();
        VisibilityChanged -= UpdateVisibility;
    }

    private static bool Alive(Node node) => GodotObject.IsInstanceValid(node) &&
        !node.IsQueuedForDeletion() && node.IsInsideTree();
}
