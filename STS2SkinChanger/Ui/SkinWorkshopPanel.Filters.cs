using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class SkinWorkshopPanel
{
    private WorkshopTargetNames _names = null!;
    private string _region = "";
    private WorkshopPagedChoice _typePicker = null!;
    private WorkshopPagedChoice _regionPicker = null!;
    private WorkshopPagedChoice _targetPicker = null!;
    private IReadOnlySet<string>? RegionMembers => _region.Length == 0 ? null : _names.Regions(_kind).GetValueOrDefault(_region) ?? new HashSet<string>();
    private void BuildFilters(VBoxContainer content)
    {
        if (_kind == "companion" && _target == "osty") { _kind = "character"; _target = "necrobinder"; }
        _names = WorkshopTargetNames.Build();
        var filters = new HBoxContainer(); filters.AddThemeConstantOverride("separation", 12); content.AddChild(filters);
        _typePicker = new(WorkshopText.Kind, id => SetFilter(id, ""));
        _regionPicker = new(id => _names.RegionNames.GetValueOrDefault(id, id), id =>
        { _region = id; _target = ""; RefreshFilters(); _page = 0; Rebuild(); });
        _targetPicker = new(id => _names.Name(_kind, id), id => { _target = id; _page = 0; Rebuild(); });
        foreach (var picker in new[] { _typePicker, _regionPicker, _targetPicker }) filters.AddChild(picker.Picker);
        InferRegion(); RefreshFilters();
    }
    private void SetFilter(string kind, string target)
    {
        _kind = kind; _target = target; _region = ""; _page = 0;
        InferRegion(); RefreshFilters(); Rebuild();
    }
    private void InferRegion()
    {
        if (_target.Length > 0 && WorkshopBrowserPolicy.HasRegions(_kind))
            _region = _names.Regions(_kind).FirstOrDefault(pair => pair.Value.Contains(_target)).Key ?? "";
    }
    private void RefreshFilters()
    {
        _typePicker.SetOptions(Kinds, _kind);
        _regionPicker.Picker.Visible = WorkshopBrowserPolicy.HasRegions(_kind);
        _regionPicker.SetOptions(_names.Regions(_kind).Keys, _region);
        _targetPicker.Picker.Visible = _kind.Length > 0;
        var ids = SkinWorkshopService.Catalog.SelectMany(i => i.Targets).Where(t => t.Kind == _kind && WorkshopBrowserPolicy.VisibleTarget(t))
            .Select(t => t.Target).Where(id => RegionMembers == null || RegionMembers.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => _names.Name(_kind, id), StringComparer.CurrentCulture);
        _targetPicker.SetOptions(ids, _target);
    }
    private void AddTags(HFlowContainer flow, WorkshopCatalogItem item)
    {
        void Tag(string title, string kind, string target)
        {
            var button = Button(title, () => SetFilter(kind, target));
            button.CustomMinimumSize = new Vector2(0, 30);
            button.ClipText = true;
            button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            button.TooltipText = title;
            button.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            ModThemeRuntime.TextControl(button, 15);
            ModThemeRuntime.Bind(button, "tag_width", theme => button.CustomMinimumSize = new Vector2(
                Math.Clamp(button.GetThemeFont("font").GetStringSize(title, fontSize: (int)(15 * theme.FontScale)).X + 24, 48, 190 * theme.FontScale), 30 * theme.FontScale));
            flow.AddChild(button);
        }
        var targets = item.Targets.Where(WorkshopBrowserPolicy.VisibleTarget).ToArray();
        foreach (var kind in targets.Select(t => t.Kind).Distinct()) Tag(WorkshopText.Kind(kind), kind, "");
        foreach (var target in targets.Where(t => t.Kind == "character" || t.Kind == _kind && t.Target == _target).Distinct())
            Tag(_names.Name(target.Kind, target.Target), target.Kind, target.Target);
    }
}
