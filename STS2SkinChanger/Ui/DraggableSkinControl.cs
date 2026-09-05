using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// Same interaction as the single-card selector: a separate grip, left-drag, right-reset.
// Normalized positions belong to the screen, never to a moving character/info-panel model.
internal partial class DraggableSkinControl : Node
{
    private const string BindingName = "SkinChangerDragBinding";
    private Control _screen = null!;
    private Control _target = null!;
    private Button _handle = null!;
    private Action _defaultPlacement = null!;
    private Func<(float X, float Y)?> _loadPosition = null!;
    private Action<float, float> _savePosition = null!;
    private Action _resetPosition = null!;
    private bool _dragging;
    private bool _placing;
    private Vector2 _dragOffset;

    internal static void Attach(
        Control screen, HBoxContainer target, bool mergeButton, Action defaultPlacement)
        => Attach(screen, target,
            () => SkinService.GetCharacterSkinControlPosition(mergeButton),
            (x, y) => SkinService.SetCharacterSkinControlPosition(mergeButton, x, y),
            () => SkinService.ResetCharacterSkinControlPosition(mergeButton), defaultPlacement);

    internal static void Attach(
        Control screen, HBoxContainer target, Func<(float X, float Y)?> loadPosition,
        Action<float, float> savePosition, Action resetPosition, Action defaultPlacement)
        => AttachCore(screen, target, null, loadPosition, savePosition, resetPosition, defaultPlacement);

    internal static void AttachWithHandle(
        Control screen, Control target, Button handle, Func<(float X, float Y)?> loadPosition,
        Action<float, float> savePosition, Action resetPosition, Action defaultPlacement)
        => AttachCore(screen, target, handle, loadPosition, savePosition, resetPosition, defaultPlacement);

    private static void AttachCore(
        Control screen, Control target, Button? handle, Func<(float X, float Y)?> loadPosition,
        Action<float, float> savePosition, Action resetPosition, Action defaultPlacement)
    {
        var binding = target.GetNodeOrNull<DraggableSkinControl>(BindingName);
        if (binding != null)
        {
            binding.RestorePosition();
            return;
        }

        binding = new DraggableSkinControl
        {
            Name = BindingName,
            _screen = screen,
            _target = target,
            _handle = handle!,
            _loadPosition = loadPosition,
            _savePosition = savePosition,
            _resetPosition = resetPosition,
            _defaultPlacement = defaultPlacement
        };
        target.AddChild(binding);
        binding.Initialize();
    }

    private void Initialize()
    {
        if (_handle == null)
        {
            _handle = new Button
            {
                Name = "SkinChangerDragHandle",
                Text = "⋮",
                CustomMinimumSize = new Vector2(24f, 44f),
                FocusMode = Control.FocusModeEnum.None,
                MouseFilter = Control.MouseFilterEnum.Stop,
                MouseDefaultCursorShape = Control.CursorShape.Move
            };
            ContextualSkinControls.ApplyGameTheme(_handle);
            _handle.AddThemeFontSizeOverride("font_size", 22);
            _target.AddThemeConstantOverride("separation", 4);
            _target.AddChild(_handle);
            _target.MoveChild(_handle, 0);
        }
        _handle.GuiInput += HandleInput;
        _target.VisibilityChanged += CancelHiddenDrag;
        _target.Resized += ClampAfterLayout;
        _screen.Resized += RestorePosition;
        ModLocalization.Bind(_handle, () =>
            _handle.TooltipText = ModLocalization.Get(ModText.CardSkinSelectorDragHint));
        RestorePosition();
        Callable.From(RestorePosition).CallDeferred();
    }

    private void HandleInput(InputEvent input)
    {
        if (input is InputEventMouseButton mouse)
        {
            if (mouse.ButtonIndex == MouseButton.Right)
            {
                if (mouse.Pressed)
                {
                    _dragging = false;
                    _resetPosition();
                    RestorePosition();
                }
                _handle.AcceptEvent();
                return;
            }
            if (mouse.ButtonIndex != MouseButton.Left)
            {
                return;
            }
            if (mouse.Pressed)
            {
                var center = _screen.GetGlobalTransformWithCanvas().AffineInverse() *
                             (_target.GetGlobalTransformWithCanvas() * (_target.Size / 2f));
                ApplyPosition(center.X / Math.Max(1f, _screen.Size.X),
                    center.Y / Math.Max(1f, _screen.Size.Y));
                _dragging = true;
                _dragOffset = _screen.GetLocalMousePosition() - center;
                _handle.GrabClickFocus();
            }
            else if (_dragging)
            {
                _dragging = false;
                var position = MoveToMouse();
                _savePosition(position.X, position.Y);
            }
            _handle.AcceptEvent();
        }
        else if (input is InputEventMouseMotion && _dragging)
        {
            if (!Input.IsMouseButtonPressed(MouseButton.Left) || !_screen.GetWindow().HasFocus())
            {
                _dragging = false;
                RestorePosition();
                return;
            }
            MoveToMouse();
            _handle.AcceptEvent();
        }
    }

    private NormalizedControlPosition MoveToMouse()
    {
        var center = _screen.GetLocalMousePosition() - _dragOffset;
        return ApplyPosition(center.X / Math.Max(1f, _screen.Size.X),
            center.Y / Math.Max(1f, _screen.Size.Y));
    }

    private NormalizedControlPosition ApplyPosition(float x, float y)
        => ApplyPosition(_screen, _target, x, y, ref _placing);

    internal static void ApplyDefaultPosition(
        Control screen,
        HBoxContainer target,
        NormalizedControlPosition position)
    {
        var placing = false;
        ApplyPosition(screen, target, position.X, position.Y, ref placing);
    }

    private static NormalizedControlPosition ApplyPosition(
        Control screen,
        Control target,
        float x,
        float y,
        ref bool placing)
    {
        var size = target.Size.Max(target.GetCombinedMinimumSize());
        var position = DraggableControlPlacementPolicy.ClampNormalized(
            x, y, screen.Size.X, screen.Size.Y, size.X, size.Y);
        placing = true;
        try
        {
            if (target.GetParent() is CanvasItem parent && parent != screen)
            {
                // A preview can stay under InfoPanel (and inherit its visibility/fade), while
                // saved drag coordinates still belong to the entire screen, like other grips.
                var screenTransform = screen.GetGlobalTransformWithCanvas();
                if (!screenTransform.IsFinite() || Mathf.IsZeroApprox(screenTransform.Determinant())) return position;
                var parentToScreen = screenTransform.AffineInverse() *
                                     parent.GetGlobalTransformWithCanvas();
                if (ResolveNestedPlacement(size, screen.Size, parentToScreen, new(x, y)) is not { } placement)
                    return position;
                var (topLeft, normalized) = placement;
                target.AnchorLeft = target.AnchorRight = target.AnchorTop = target.AnchorBottom = 0;
                target.OffsetLeft = topLeft.X;
                target.OffsetTop = topLeft.Y;
                target.OffsetRight = topLeft.X + size.X;
                target.OffsetBottom = topLeft.Y + size.Y;
                return new NormalizedControlPosition(normalized.X, normalized.Y);
            }
            target.AnchorLeft = target.AnchorRight = position.X;
            target.AnchorTop = target.AnchorBottom = position.Y;
            target.OffsetLeft = -size.X / 2f;
            target.OffsetTop = -size.Y / 2f;
            target.OffsetRight = size.X / 2f;
            target.OffsetBottom = size.Y / 2f;
        }
        finally
        {
            placing = false;
        }
        return position;
    }

    internal static (Vector2 TopLeft, Vector2 Normalized)? ResolveNestedPlacement(
        Vector2 size, Vector2 screenSize, Transform2D parentToScreen, Vector2 desired)
    {
        if (!parentToScreen.IsFinite() || Mathf.IsZeroApprox(parentToScreen.Determinant()) ||
            !size.IsFinite() || !screenSize.IsFinite() || screenSize.X <= 0 || screenSize.Y <= 0) return null;
        var screenBounds = parentToScreen * new Rect2(Vector2.Zero, size);
        var position = DraggableControlPlacementPolicy.ClampNormalized(desired.X, desired.Y,
            screenSize.X, screenSize.Y, screenBounds.Size.X, screenBounds.Size.Y);
        var normalized = new Vector2(position.X, position.Y);
        var center = parentToScreen.AffineInverse() * (normalized * screenSize);
        return (center - size / 2, normalized);
    }

    internal static void RefreshPlacement(Control target)
    {
        if (target.GetNodeOrNull<DraggableSkinControl>(BindingName) is { _dragging: false } binding)
            binding.RestorePosition();
    }

    private void RestorePosition()
    {
        if (!GodotObject.IsInstanceValid(_target))
        {
            if (GodotObject.IsInstanceValid(_screen))
            {
                _screen.Resized -= RestorePosition;
            }
            return;
        }
        _dragging = false;
        if (_loadPosition() is { } position)
        {
            ApplyPosition(position.X, position.Y);
        }
        else
        {
            _defaultPlacement();
        }
    }

    private void CancelHiddenDrag()
    {
        if (!_target.IsVisibleInTree())
        {
            _dragging = false;
        }
    }

    private void ClampAfterLayout()
    {
        if (!_placing && !_dragging &&
            _loadPosition() is { } position)
        {
            ApplyPosition(position.X, position.Y);
        }
    }

    public override void _ExitTree()
    {
        _dragging = false;
        if (GodotObject.IsInstanceValid(_screen))
        {
            _screen.Resized -= RestorePosition;
        }
    }
}
