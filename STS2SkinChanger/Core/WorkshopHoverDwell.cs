namespace STS2SkinChanger.Core;

// Monotonic wall-clock dwell: no timers, downloads or scene work while scanning
// the list. Cached introductions obey the same delay as first-time requests.
internal sealed class WorkshopHoverDwell
{
    private ulong _item;
    private double _since;

    public bool Observe(ulong item, double now)
    {
        if (item == 0) { Clear(); return false; }
        if (item != _item) { _item = item; _since = now; return false; }
        return now - _since >= 1;
    }

    public void Clear() { _item = 0; _since = 0; }
}
