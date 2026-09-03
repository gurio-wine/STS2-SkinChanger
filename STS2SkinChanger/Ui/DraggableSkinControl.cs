using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// Same interaction as the single-card selector: a separate grip, left-drag, right-reset.
// Normalized positions belong to the screen, never to a moving character/info-panel model.
internal partial class DraggableSkinControl : Node
{
    private const string BindingName = "SkinChangerDragBinding";
    private Control _screen = null!;
    private HBoxContainer _target = null!;
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
    {
        var size = _target.Size.Max(_target.GetCombinedMinimumSize());
        var position = DraggableControlPlacementPolicy.ClampNormalized(
            x, y, _screen.Size.X, _screen.Size.Y, size.X, size.Y);
        _placing = true;
        try
        {
            _target.AnchorLeft = _target.AnchorRight = position.X;
            _target.AnchorTop = _target.AnchorBottom = position.Y;
            _target.OffsetLeft = -size.X / 2f;
            _target.OffsetTop = -size.Y / 2f;
            _target.OffsetRight = size.X / 2f;
            _target.OffsetBottom = size.Y / 2f;
        }
        finally
        {
            _placing = false;
        }
        return position;
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
