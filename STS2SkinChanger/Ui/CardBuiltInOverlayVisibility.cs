namespace STS2SkinChanger.Ui;

// Kept separate from the complete card layout snapshot: ReloadOverlay runs during Reload,
// before that snapshot is captured, and can also replace the node on affliction changes.
internal sealed class CardBuiltInOverlayVisibility
{
    private object? _model;
    private object? _overlay;
    private bool _original;
    private bool? _applied;

    public bool Resolve(object model, object overlay, bool current, bool? selected)
    {
        if (!ReferenceEquals(_model, model) || !ReferenceEquals(_overlay, overlay))
        {
            _model = model;
            _overlay = overlay;
            _applied = null;
        }
        if (selected is { } visible)
        {
            if (_applied == null) _original = current;
            _applied = visible;
            return visible;
        }
        var restored = _applied == current ? _original : current;
        _applied = null;
        return restored;
    }
}
