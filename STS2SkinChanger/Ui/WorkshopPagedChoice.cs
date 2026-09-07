using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal sealed class WorkshopPagedChoice
{
    public OptionButton Picker { get; } = new() { FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(215, 42), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private string[] _ids = [];
    private string _value = "";
    private int _page;
    private readonly Func<string, string> _name;
    private readonly Action<string> _changed;
    public WorkshopPagedChoice(Func<string, string> name, Action<string> changed)
    {
        _name = name; _changed = changed;
        ContextualSkinControls.ApplyGameTheme(Picker);
        Picker.ItemSelected += index =>
        {
            var id = Picker.GetItemMetadata((int)index).AsString();
            if (id is "__previous_page__" or "__next_page__")
            {
                _page += id == "__previous_page__" ? -1 : 1;
                Populate();
                Callable.From(() => { if (GodotObject.IsInstanceValid(Picker) && Picker.IsInsideTree() && Picker.IsVisibleInTree()) Picker.ShowPopup(); }).CallDeferred();
                return;
            }
            _value = id; _changed(id);
        };
    }
    public void SetOptions(IEnumerable<string> ids, string value)
    {
        _ids = ids.Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Preserve a context entry with no currently curated matches: never silently broaden.
        if (value.Length > 0 && !_ids.Contains(value, StringComparer.OrdinalIgnoreCase)) _ids = [.. _ids, value];
        _value = value;
        _page = Math.Max(0, Array.FindIndex(_ids, id => id.Equals(value, StringComparison.OrdinalIgnoreCase))) / WorkshopBrowserPolicy.TargetPageSize;
        Populate();
    }
    private void Populate()
    {
        Picker.Clear();
        void Add(string text, string id)
        {
            var i = Picker.ItemCount; Picker.AddItem(text); Picker.SetItemMetadata(i, id);
            Picker.GetPopup().SetItemTooltip(i, text);
            if (id == _value) Picker.Select(i);
        }
        Add(WorkshopText.Get(WorkshopTextKey.All), "");
        foreach (var id in WorkshopBrowserPolicy.Page(_ids, _page)) Add(_name(id), id);
        var pages = Math.Max(1, (_ids.Length + 9) / 10);
        if (_page > 0) Add("‹ " + WorkshopText.Get(WorkshopTextKey.Previous) + $"  {_page}/{pages}", "__previous_page__");
        if (_page + 1 < pages) Add(WorkshopText.Get(WorkshopTextKey.Next) + $"  {_page + 2}/{pages} ›", "__next_page__");
        Picker.Text = _value.Length == 0 ? WorkshopText.Get(WorkshopTextKey.All) : _name(_value);
        Picker.TooltipText = Picker.Text;
    }
}
