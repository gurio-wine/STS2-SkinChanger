namespace STS2SkinChanger.Core;

// UI-session state only. Subscription is a Steam record, not installed/loaded files.
internal sealed class WorkshopSubscriptionFilter
{
    public static readonly string[] Options = ["subscribed", "unsubscribed", "code_errors"];
    public string Value { get; private set; } = "unsubscribed";
    public bool Unavailable { get; private set; }
    private readonly Dictionary<ulong, bool> _known = [];
    private readonly HashSet<ulong> _pending = [];

    public void Select(string value)
    {
        if (value.Length != 0 && !Options.Contains(value)) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
        _pending.Clear();
    }
    public void Refresh(IEnumerable<ulong> ids, Func<ulong, bool> read)
    {
        Unavailable = false;
        foreach (var id in ids)
        {
            try { _known[id] = read(id); }
            catch { Unavailable = true; } // Keep last known state; never invent "not subscribed".
        }
    }
    public void BeginAction(ulong id) => _pending.Add(id);
    public void EndAction(ulong id) => _pending.Remove(id);
    public bool Matches(ulong id) => Value!="code_errors" && (Value.Length == 0 || _pending.Contains(id) ||
        _known.TryGetValue(id, out var subscribed) && (Value == "subscribed" ? subscribed : !subscribed));
    public IEnumerable<WorkshopCatalogItem> Filter(IEnumerable<WorkshopCatalogItem> items) => items.Where(item => Matches(item.Id));
    public static string Name(string value) => value switch
    {
        "subscribed" => WorkshopCommunityText.Get(WorkshopCommunityTextKey.Subscribed),
        "unsubscribed" => WorkshopCommunityText.Get(WorkshopCommunityTextKey.NotSubscribed),
        "code_errors" => WorkshopCodeErrorText.Get(WorkshopCodeUi.Filter),
        _ => WorkshopText.Get(WorkshopTextKey.All)
    };
}
