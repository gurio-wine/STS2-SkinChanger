namespace STS2SkinChanger.Core;

internal sealed class WorkshopNoticeQueue
{
    private readonly Dictionary<ulong, WorkshopLoadReason> _pending = [];
    private readonly HashSet<ulong> _seen = [];
    public int Count => _pending.Count;
    public void Enqueue(ulong id, WorkshopLoadReason reason)
    {
        if (reason != WorkshopLoadReason.None && _seen.Add(id)) _pending[id] = reason;
    }
    public void BeginAttempt(ulong id) { _pending.Remove(id); _seen.Remove(id); }
    public void Acknowledge(ulong id) => _pending.Remove(id);
    public KeyValuePair<ulong, WorkshopLoadReason>? Peek() => _pending.Count == 0 ? null : _pending.First();
}
