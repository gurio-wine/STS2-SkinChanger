namespace STS2SkinChanger.Core;

internal readonly record struct WorkshopItemTicket(ulong Id, long Revision);

// A row keeps its nodes but not its previous identity. Every rebind invalidates
// outstanding images and pressed-button actions, even when returning to the same ID.
internal sealed class WorkshopItemBinding
{
    public ulong Id { get; private set; }
    private long _revision;
    internal void Bind(ulong id) { Id = id; _revision++; }
    public WorkshopItemTicket Capture() => new(Id, _revision);
    public bool Matches(WorkshopItemTicket ticket) => Id != 0 && ticket == Capture();
}

internal sealed class WorkshopPagePool<T>(int capacity, Func<WorkshopItemBinding, T> create)
{
    private readonly List<(WorkshopItemBinding Binding, T View)> _slots = [];
    public IReadOnlyList<(WorkshopItemBinding Binding, T View)> Slots => _slots;

    public void Bind(IReadOnlyList<ulong> ids)
    {
        if (ids.Count > capacity || ids.Any(id => id == 0)) throw new ArgumentOutOfRangeException(nameof(ids));
        while (_slots.Count < ids.Count)
        {
            var binding = new WorkshopItemBinding();
            _slots.Add((binding, create(binding)));
        }
        for (var i = 0; i < _slots.Count; i++) _slots[i].Binding.Bind(i < ids.Count ? ids[i] : 0);
    }
}
