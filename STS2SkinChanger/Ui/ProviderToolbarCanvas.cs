using Godot;

namespace STS2SkinChanger.Ui;

/// <summary>
/// A toolbar is allowed outside a clipped character strip; its sibling character buttons
/// are not. Detach only its canvas, retaining author lookups, callbacks and cleanup ownership.
/// </summary>
internal partial class ProviderToolbarCanvas : Node
{
    private const string BindingName = "SkinChangerToolbarCanvas";
    private readonly ToolbarCanvasPlacement _placement = new();
    private readonly ToolbarCanvasTint _tint = new();
    private Control _control = null!;
    private bool _updating;

    internal static void Bind(Control control)
    {
        if (control.GetNodeOrNull<ProviderToolbarCanvas>(BindingName) is { } existing)
        {
            existing.UpdateCanvas();
            return;
        }
        if (control.TopLevel) return; // Already owned by the author.
        var bounds = control.GetGlobalRect();
        var clipped = false;
        for (var parent = control.GetParent() as CanvasItem; parent != null; parent = parent.GetParent() as CanvasItem)
        {
            if (parent is Control { ClipContents: true } clip && !clip.GetGlobalRect().Encloses(bounds))
                clipped = true;
            if (parent.TopLevel) break;
        }
        if (!clipped) return;

        var binding = new ProviderToolbarCanvas { Name = BindingName, _control = control };
        control.AddChild(binding);
        binding.UpdateCanvas();
    }

    public override void _EnterTree() => _control.ItemRectChanged += UpdateCanvas;

    public override void _Process(double delta) => UpdateCanvas();

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(_control)) _control.ItemRectChanged -= UpdateCanvas;
    }

    private void UpdateCanvas()
    {
        if (_updating || !GodotObject.IsInstanceValid(_control) || _control.IsQueuedForDeletion()) return;
        _updating = true;
        try
        {
            var parent = _control.GetParent() as CanvasItem;
            var (position, scale, rotation) = _placement.Resolve(_control.Position, _control.Scale,
                _control.Rotation, _control.PivotOffset, parent?.GetGlobalTransform() ?? Transform2D.Identity);
            var inheritedTint = Colors.White;
            for (var ancestor = parent; ancestor != null; ancestor = ancestor.GetParent() as CanvasItem)
            {
                inheritedTint *= ancestor.Modulate;
                if (ancestor.TopLevel) break;
            }
            var tint = _tint.Resolve(_control.Modulate, inheritedTint);
            _control.TopLevel = true;
            if (!_control.Scale.IsEqualApprox(scale)) _control.Scale = scale;
            if (!Mathf.IsEqualApprox(_control.Rotation, rotation)) _control.Rotation = rotation;
            if (!_control.Position.IsEqualApprox(position)) _control.Position = position;
            if (!_control.Modulate.IsEqualApprox(tint)) _control.Modulate = tint;
        }
        finally { _updating = false; }
    }
}

// Preserve authored local coordinates while the canvas is top-level. Later author layout
// writes replace the corresponding local component; our own last output never compounds.
internal sealed class ToolbarCanvasPlacement
{
    private bool _initialized;
    private Vector2 _localPosition, _localScale, _appliedPosition, _appliedScale;
    private float _localRotation, _appliedRotation;

    internal (Vector2 Position, Vector2 Scale, float Rotation) Resolve(
        Vector2 position, Vector2 scale, float rotation, Vector2 pivot, Transform2D parent)
    {
        if (!_initialized || !position.IsEqualApprox(_appliedPosition)) _localPosition = position;
        if (!_initialized || !scale.IsEqualApprox(_appliedScale)) _localScale = scale;
        if (!_initialized || !Mathf.IsEqualApprox(rotation, _appliedRotation)) _localRotation = rotation;
        var local = new Transform2D(_localRotation, _localScale, 0, Vector2.Zero);
        local.Origin = _localPosition + pivot - local.BasisXform(pivot);
        var global = parent * local;
        _appliedPosition = global.Origin - pivot + global.BasisXform(pivot);
        _appliedScale = global.Scale;
        _appliedRotation = global.Rotation;
        _initialized = true;
        return (_appliedPosition, _appliedScale, _appliedRotation);
    }
}

internal sealed class ToolbarCanvasTint
{
    private Color? _applied;
    private Color _local;
    internal Color Resolve(Color current, Color inherited)
    {
        if (_applied is not { } applied || !current.IsEqualApprox(applied)) _local = current;
        var result = _local * inherited;
        _applied = result;
        return result;
    }
}
