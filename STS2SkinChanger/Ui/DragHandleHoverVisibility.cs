using Godot;
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
    private bool _connected;
    private readonly List<Control> _hoverTargets = new();
    private bool _pointerSeen;
    private int _diagnosticsLeft;
    private string? _lastDiagnostic;

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
        // These native signals do not depend on Godot recognizing virtual methods from a
        // dynamically loaded mod DLL. Subscribe before AddChild for the not-yet-in-tree preview.
        binding.TreeEntered += binding.Initialize;
        binding.TreeExiting += binding.Disconnect;
        target.AddChild(binding);
        binding.Initialize();
    }

    private void Initialize()
    {
        if (_connected || !IsInsideTree()) return;
        _connected = true;
        _pointerSeen = false;
        _diagnosticsLeft = 6;
        _lastDiagnostic = null;
        _window = _target.GetWindow();
        WatchHoverTarget(_target);
        foreach (var child in _target.GetChildren().OfType<Control>()) WatchHoverTarget(child);
        _target.ItemRectChanged += QueueRefresh;
        _target.VisibilityChanged += OnVisibilityChanged;
        _window.FocusEntered += QueueRefresh;
        _window.FocusExited += OnWindowUnavailable;
        _window.MouseEntered += QueueRefresh;
        _window.MouseExited += OnWindowUnavailable;
        OnVisibilityChanged();
        ModLog.Info("拖拽柄悬停事件已接入：" + _target.Name);
    }

    private void WatchHoverTarget(Control target)
    {
        _hoverTargets.Add(target);
        target.MouseEntered += OnPointerActivity;
        target.MouseExited += OnPointerActivity;
        target.GuiInput += OnPointerInput;
    }

    private void OnPointerActivity()
    {
        _pointerSeen = true;
        QueueRefresh();
    }

    private void OnPointerInput(InputEvent input)
    {
        // Defer until GUI hover and existing drag handlers have finished this event.
        if (input is InputEventMouse) OnPointerActivity();
    }

    private void QueueRefresh()
    {
        if (_refreshQueued || !IsInsideTree() || IsQueuedForDeletion()) return;
        _refreshQueued = true;
        Callable.From(() =>
        {
            _refreshQueued = false;
            if (!GodotObject.IsInstanceValid(this) || !_connected || !IsInsideTree() || IsQueuedForDeletion()) return;
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
        var visible = _target.IsVisibleInTree();
        var focused = GodotObject.IsInstanceValid(_window) && _window!.HasFocus();
        var dragging = _isDragging();
        ApplyShown(ShouldReveal(visible, focused, dragging, Input.IsMouseButtonPressed(MouseButton.Left), over));
        if (_pointerSeen && _diagnosticsLeft > 0)
        {
            var status = $"hover={hovered?.Name}, visible={visible}, focus={focused}, over={over}, drag={dragging}, alpha={_handle.SelfModulate.A}";
            if (status != _lastDiagnostic)
            {
                _lastDiagnostic = status;
                _diagnosticsLeft--;
                ModLog.Info($"拖拽柄悬停状态 {_target.Name}：{status}");
            }
        }
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
        if (visible) QueueRefresh();
        else ApplyShown(false);
    }

    private void OnWindowUnavailable() => ApplyShown(false);

    private void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
        ApplyShown(false);
        _refreshQueued = false;
        if (GodotObject.IsInstanceValid(_target))
        {
            _target.ItemRectChanged -= QueueRefresh;
            _target.VisibilityChanged -= OnVisibilityChanged;
        }
        foreach (var target in _hoverTargets)
        {
            if (!GodotObject.IsInstanceValid(target)) continue;
            target.MouseEntered -= OnPointerActivity;
            target.MouseExited -= OnPointerActivity;
            target.GuiInput -= OnPointerInput;
        }
        _hoverTargets.Clear();
        if (GodotObject.IsInstanceValid(_window))
        {
            _window!.FocusEntered -= QueueRefresh;
            _window.FocusExited -= OnWindowUnavailable;
            _window.MouseEntered -= QueueRefresh;
            _window.MouseExited -= OnWindowUnavailable;
        }
        _window = null;
    }
}
