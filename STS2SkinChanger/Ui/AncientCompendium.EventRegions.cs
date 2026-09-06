using Godot;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class AncientCompendiumScreen
{
    private OptionButton _eventRegionSelector = null!;
    private string? _eventRegion;
    private HashSet<string> _eventRegionMembers = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshingEventRegions;

    private static readonly IReadOnlyDictionary<string, (string Shared, string Other)> EventRegionNames =
        new Dictionary<string, (string, string)>
        {
            ["eng"] = ("Across regions", "Other regions"),
            ["zhs"] = ("跨地区", "其它地区"),
            ["zht"] = ("跨地區", "其他地區"),
            ["deu"] = ("Regionsübergreifend", "Andere Regionen"),
            ["esp"] = ("Varias regiones", "Otras regiones"),
            ["spa"] = ("Varias regiones", "Otras regiones"),
            ["fra"] = ("Plusieurs régions", "Autres régions"),
            ["ita"] = ("Più regioni", "Altre regioni"),
            ["jpn"] = ("地域共通", "その他の地域"),
            ["kor"] = ("공통 지역", "기타 지역"),
            ["pol"] = ("Wiele regionów", "Pozostałe regiony"),
            ["ptb"] = ("Várias regiões", "Outras regiões"),
            ["rus"] = ("Разные регионы", "Прочие регионы"),
            ["tha"] = ("หลายพื้นที่", "พื้นที่อื่น"),
            ["tur"] = ("Bölgeler arası", "Diğer bölgeler")
        };

    private void BuildEventRegionSelector(VBoxContainer sidebar)
    {
        _eventRegionSelector = new OptionButton
        {
            Name = "EventRegionSelector", Visible = false, FitToLongestItem = false,
            ClipText = true, Alignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 44), SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        ContextualSkinControls.ApplyGameTheme(_eventRegionSelector);
        sidebar.AddChild(_eventRegionSelector);
        _eventRegionSelector.ItemSelected += index =>
        {
            if (_refreshingEventRegions) return;
            _eventRegion = _eventRegionSelector.GetItemMetadata(checked((int)index)).AsString();
            _selectedOther = null;
            RefreshAncients();
        };
    }

    private void RefreshEventRegions()
    {
        _eventRegionSelector.Visible = _selectedCategory == OtherCategory.Events;
        // Event controls occupy the unused lower-left, not the native text/options column.
        _skinSelector.Position = _selectedCategory == OtherCategory.Events ? new Vector2(70, 940) : new Vector2(818, 826);
        if (_selectedCategory != OtherCategory.Events) { RefreshEventPriorityHeader(); return; }
        var acts = ModelDb.Acts.ToArray();
        var groups = EventRegionPolicy.Group(EventCompendiumPreview.Events().Select(e => e.Id.Entry),
            acts.ToDictionary(a => a.Id.Entry, a => a.AllEvents.Select(e => e.Id.Entry)),
            ModelDb.AllSharedEvents.Select(e => e.Id.Entry));
        var names = EventRegionNames.GetValueOrDefault(ModLocalization.CurrentLanguage, EventRegionNames["eng"]);
        var titles = acts.ToDictionary(a => a.Id.Entry, a => a.Title.GetFormattedText());
        titles[EventRegionPolicy.Shared] = names.Shared;
        titles[EventRegionPolicy.Other] = names.Other;
        _refreshingEventRegions = true;
        try
        {
            _eventRegionSelector.Clear();
            if (_eventRegion == null || !groups.ContainsKey(_eventRegion)) _eventRegion = groups.Keys.FirstOrDefault();
            foreach (var (id, _) in groups)
            {
                var index = _eventRegionSelector.ItemCount;
                _eventRegionSelector.AddItem(titles[id]);
                _eventRegionSelector.SetItemMetadata(index, id);
                if (id == _eventRegion) _eventRegionSelector.Select(index);
            }
            _eventRegionMembers = (_eventRegion != null ? groups[_eventRegion] : [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        finally { _refreshingEventRegions = false; }
        RefreshEventPriorityHeader();
    }
}
