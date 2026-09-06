using System.Runtime.CompilerServices;
using Godot;

namespace STS2SkinChanger.Ui;

// PopupMenu renders into a separate viewport. Draw its blur in the host viewport,
// immediately below embedded windows, so the sample contains the game but never menu text.
internal sealed class ModThemeDropdownBackdrop
{
    private const int EmbeddedWindowCanvasLayer = 1024;
    private static readonly ConditionalWeakTable<PopupMenu, ModThemeDropdownBackdrop> Instances = new();
    private readonly PopupMenu _popup;
    private Viewport? _host;
    private CanvasLayer? _layer;
    private Control? _surface;
    private ModThemeBackdrop? _backdrop;
    private bool _following;

    internal static void Attach(PopupMenu popup) => Instances.GetValue(popup, node => new(node));

    private ModThemeDropdownBackdrop(PopupMenu popup)
    {
        _popup = popup;
        // Native signals also work for mod DLLs without Godot's virtual-method generators.
        popup.TreeEntered += Refresh;
        popup.VisibilityChanged += Refresh;
        popup.SizeChanged += Refresh;
        popup.PopupHide += Release;
        popup.TreeExiting += Release;
        ModThemeRuntime.Bind(popup, "dropdown_blur", _ => Refresh());
    }

    private void Refresh()
    {
        if (!_popup.IsInsideTree() || !_popup.Visible || ModThemeRuntime.Current.DropdownBlur <= 0)
        {
            Release();
            return;
        }
        var host = FindHost();
        if (host == null) { Release(); return; }
        if (_host != host) Release();
        _host = host;
        // Register geometry first, before ModThemeBackdrop registers its own copy update.
        // This keeps the sampled rect and popup at the same position even during resizing.
        StartFollowing();
        EnsureSurface();
        SyncGeometry();
    }

    private Viewport? FindHost()
    {
        if (!_popup.IsEmbedded()) return _popup.GetParent()?.GetWindow();
        // Mirrors Window::get_embedder, which is not exposed in the managed Godot API.
        for (var viewport = _popup.GetParent()?.GetViewport(); viewport != null;
             viewport = viewport.GetParent()?.GetViewport())
            if (viewport.GuiEmbedSubwindows) return viewport;
        return null;
    }

    private void StartFollowing()
    {
        if (_following) return;
        _following = true;
        RenderingServer.FramePreDraw += SyncGeometry;
    }

    private void EnsureSurface()
    {
        if (GodotObject.IsInstanceValid(_layer)) return;
        _layer = new CanvasLayer
        {
            Name = "SCThemeDropdownBackdrop_" + _popup.GetInstanceId(),
            Layer = EmbeddedWindowCanvasLayer - 1
        };
        _host!.AddChild(_layer);
        _surface = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None,
            Visible = false
        };
        _layer.AddChild(_surface);
        _backdrop = ModThemeBackdrop.For(_surface);
    }

    private void SyncGeometry()
    {
        if (!GodotObject.IsInstanceValid(_popup) || !_popup.IsInsideTree() || !_popup.Visible ||
            !GodotObject.IsInstanceValid(_host) || ModThemeRuntime.Current.DropdownBlur <= 0)
        {
            Release();
            return;
        }
        if (!GodotObject.IsInstanceValid(_surface) || !GodotObject.IsInstanceValid(_layer)) return;
        var embedded = _popup.IsEmbedded();
        var hostTransform = _host!.GetFinalTransform();
        if (!embedded && Math.Abs(hostTransform.Determinant()) < .000001f)
        {
            _surface!.Visible = false;
            return;
        }
        var (position, transform) = Placement(embedded, _popup.Position,
            _host is Window window ? window.Position : Vector2.Zero, hostTransform);
        if (_layer!.Transform != transform) _layer.Transform = transform;
        if (_surface!.Position != position) _surface.Position = position;
        if (_surface.Size != (Vector2)_popup.Size) _surface.Size = _popup.Size;
        var theme = ModThemeRuntime.Current;
        var contentSize = _popup.GetVisibleRect().Size;
        var contentScale = contentSize.X > 0 ? _popup.Size.X / contentSize.X : 1f;
        // Tint/opacity/borders remain in the native menu (or its colored ItemList), once.
        _backdrop!.Update(Colors.Transparent, theme.DropdownBlur,
            (int)MathF.Round(theme.DropdownCornerRadius * contentScale));
        _surface.Visible = true;
    }

    internal static (Vector2 Position, Transform2D Transform) Placement(bool embedded,
        Vector2 popupPosition, Vector2 hostWindowPosition, Transform2D hostTransform) =>
        embedded ? (popupPosition, Transform2D.Identity) :
            (popupPosition - hostWindowPosition, hostTransform.AffineInverse());

    private void Release()
    {
        if (_following) RenderingServer.FramePreDraw -= SyncGeometry;
        _following = false;
        if (GodotObject.IsInstanceValid(_layer))
        {
            _layer!.Visible = false;
            _layer.QueueFree();
        }
        _layer = null;
        _surface = null;
        _backdrop = null;
        _host = null;
    }
}
