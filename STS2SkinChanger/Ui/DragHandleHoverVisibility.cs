using Godot;
using Godot.NativeInterop;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

/// <summary>Reveal a grip over its whole control group, without changing layout or hit testing.</summary>
internal partial class DragHandleHoverVisibility : Node
{
    private const string BindingName = "SkinChangerDragHandleHover";
    private Control _target = null!;
    private Button _handle = null!;
    private Func<bool> _isDragging = null!;
    private Color _shownColor;
    private Window? _window;
    private bool _refreshQueued;

    // Plain class-library builds have no Godot script source generator. Register the native
    // callback names explicitly; Node's existing dispatcher then invokes our C# overrides.
    protected override bool HasGodotClassMethod(in godot_string_name method) =>
        method == Node.MethodName._Ready || method == Node.MethodName._Input ||
        method == Node.MethodName._ExitTree || base.HasGodotClassMethod(method);

    internal static void Attach(Control target, Button handle, Func<bool> isDragging)
    {
        if (target.GetNodeOrNull<DragHandleHoverVisibility>(BindingName) != null) return;
        var binding = new DragHandleHoverVisibility
        {
            Name = BindingName,
            _target = target,
            _handle = handle,
            _isDragging = isDragging,
            _shownColor = handle.SelfModulate
        };
        // Keep the handle in the container and hittable. Visible=false would shift the button
        // under the pointer and make moving from the button onto its grip flicker.
        binding.ApplyShown(false);
        target.AddChild(binding);
    }

    public override void _Ready()
    {
        _window = _target.GetWindow();
        _target.MouseEntered += QueueRefresh;
        _target.MouseExited += QueueRefresh;
        _target.ItemRectChanged += QueueRefresh;
        _target.VisibilityChanged += OnVisibilityChanged;
        _handle.MouseEntered += QueueRefresh;
        _handle.MouseExited += QueueRefresh;
        _handle.GuiInput += OnHandleInput;
        _window.FocusEntered += QueueRefresh;
        _window.FocusExited += OnWindowUnavailable;
        _window.MouseEntered += QueueRefresh;
        _window.MouseExited += OnWindowUnavailable;
        OnVisibilityChanged();
        ModLog.Info("拖拽柄悬停事件已接入：" + _target.Name);
    }

    public override void _Input(InputEvent input)
    {
        // Observe, never consume, mouse events. Defer until GUI hover and drag capture settle.
        // This also sees crossings between child buttons that stop GUI event propagation.
        if (input is InputEventMouse) QueueRefresh();
    }

    private void OnHandleInput(InputEvent input)
    {
        if (input is InputEventMouse) QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_refreshQueued || !IsInsideTree() || IsQueuedForDeletion()) return;
        _refreshQueued = true;
        Callable.From(() =>
        {
            _refreshQueued = false;
            if (!GodotObject.IsInstanceValid(this) || !IsInsideTree() || IsQueuedForDeletion()) return;
            Refresh();
        }).CallDeferred();
    }

    private void Refresh()
    {
        if (!GodotObject.IsInstanceValid(_target) || !GodotObject.IsInstanceValid(_handle)) return;
        var hovered = _target.GetViewport().GuiGetHoveredControl();
        // The deepest hovered child may be the main button, its label, or the grip itself.
        // Unlike rect-only polling this excludes an unrelated modal covering the same pixels.
        var over = hovered != null && (hovered == _target || _target.IsAncestorOf(hovered));
        ApplyShown(ShouldReveal(_target.IsVisibleInTree(),
            GodotObject.IsInstanceValid(_window) && _window!.HasFocus(), _isDragging(),
            Input.IsMouseButtonPressed(MouseButton.Left), over));
    }

    internal static bool ShouldReveal(bool targetVisible, bool windowFocused, bool dragging,
        bool leftPressed, bool pointerOverTarget) =>
        targetVisible && windowFocused && (pointerOverTarget || (dragging && leftPressed));

    private void ApplyShown(bool shown)
    {
        if (!GodotObject.IsInstanceValid(_handle)) return;
        var color = _shownColor;
        if (!shown) color.A = 0;
        if (_handle.SelfModulate != color) _handle.SelfModulate = color;
    }

    private void OnVisibilityChanged()
    {
        var visible = _target.IsVisibleInTree();
        SetProcessInput(visible);
        if (visible) QueueRefresh();
        else ApplyShown(false);
    }

    private void OnWindowUnavailable() => ApplyShown(false);

    public override void _ExitTree()
    {
        SetProcessInput(false);
        ApplyShown(false);
        _refreshQueued = false;
        if (GodotObject.IsInstanceValid(_target))
        {
            _target.MouseEntered -= QueueRefresh;
            _target.MouseExited -= QueueRefresh;
            _target.ItemRectChanged -= QueueRefresh;
            _target.VisibilityChanged -= OnVisibilityChanged;
        }
        if (GodotObject.IsInstanceValid(_handle))
        {
            _handle.MouseEntered -= QueueRefresh;
            _handle.MouseExited -= QueueRefresh;
            _handle.GuiInput -= OnHandleInput;
        }
        if (GodotObject.IsInstanceValid(_window))
        {
            _window!.FocusEntered -= QueueRefresh;
            _window.FocusExited -= OnWindowUnavailable;
            _window.MouseEntered -= QueueRefresh;
            _window.MouseExited -= OnWindowUnavailable;
        }
        // Inspect/selection screens may be cached and reinserted instead of reconstructed.
        RequestReady();
    }
}
