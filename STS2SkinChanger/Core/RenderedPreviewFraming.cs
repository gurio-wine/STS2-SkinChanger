using Godot;

namespace STS2SkinChanger.Core;

/// <summary>A bounded, per-preview camera calibration driven by rendered pixels, not skin IDs.</summary>
internal sealed class RenderedPreviewFraming
{
    public Transform2D CanvasTransform { get; private set; }
    public bool Complete { get; private set; }
    public bool HasContent => VisibleBounds != null;
    public int Samples { get; private set; }
    public Rect2? VisibleBounds { get; private set; }
    private readonly Rect2 _canvas;
    private int _validSamples;

    public RenderedPreviewFraming(Rect2 initialBounds, Vector2I resolution)
    {
        _canvas = new Rect2(Vector2.Zero, new Vector2(Math.Max(32, resolution.X), Math.Max(32, resolution.Y)));
        if (!Valid(initialBounds)) initialBounds = new Rect2(-200, -400, 400, 400);
        // A coarse enclosure only starts the capture. It must never be the final authority
        // over the model's apparent size, and it is never applied to the actual model nodes.
        SetCamera(initialBounds.Grow(Math.Max(initialBounds.Size.X, initialBounds.Size.Y) / 8), _canvas);
    }

    public bool Observe(Rect2? renderedPixels)
    {
        if (Complete) return false;
        Samples++;
        if (renderedPixels is { } pixels && Valid(pixels))
        {
            if (pixels.Position.X <= 1 || pixels.Position.Y <= 1 ||
                pixels.End.X >= _canvas.End.X - 1 || pixels.End.Y >= _canvas.End.Y - 1)
            {
                // Never zoom into a crop. Widen the existing view and ask the renderer again.
                var view = CanvasTransform.AffineInverse() * _canvas;
                SetCamera(view.Grow(Math.Max(view.Size.X, view.Size.Y) / 2), _canvas);
                _validSamples = 0;
            }
            else
            {
                VisibleBounds = CanvasTransform.AffineInverse() * pixels;
                SetCamera(VisibleBounds.Value, _canvas.Grow(-8));
                _validSamples++;
            }
        }
        Complete = _validSamples >= 2 || Samples >= 6;
        return !Complete;
    }

    public void Cancel() => Complete = true;

    private void SetCamera(Rect2 bounds, Rect2 area)
    {
        if (FrameworkModelPreview.FitBounds(bounds, area) is not { } fit) return;
        CanvasTransform = new Transform2D(new Vector2(fit.Scale, 0), new Vector2(0, fit.Scale), fit.Position);
    }

    private static bool Valid(Rect2 rect) => rect.Position.IsFinite() && rect.Size.IsFinite() && rect.HasArea();
}
