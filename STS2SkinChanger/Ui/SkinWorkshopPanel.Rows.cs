using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class SkinWorkshopPanel
{
    private sealed class RowView(WorkshopItemBinding binding, Control slot, PanelContainer panel)
    {
        public readonly WorkshopItemBinding Binding = binding;
        public readonly Control Slot = slot;
        public readonly PanelContainer Panel = panel;
        public Label Title = null!, Status = null!, Metric = null!;
        public TextureRect Cover = null!;
        public Panel Placeholder = null!;
        public Button Action = null!, Cancel = null!;
        public HFlowContainer Tags = null!;
        public ulong WorkshopId;
        public Action? PrimaryAction, SecondaryAction;
        public readonly List<TagView> TagSlots = [];
        public ItemVisual Snapshot() => new(Binding, Binding.Capture(), Title, Cover, Placeholder);
    }

    private sealed class TagView
    {
        public Button Button = null!;
        public Action? Select;
    }

    private WorkshopPagePool<RowView>? _pageRows;
    private int _pageBuilds;
    private double _pageBuildTotalMs, _pageBuildMaxMs;

    private Button BoundButton(WorkshopItemBinding binding, string title, Action select)
    {
        var pressed = default(WorkshopItemTicket);
        var button = Button(title, () => { if (binding.Matches(pressed)) select(); });
        button.ButtonDown += () => pressed = binding.Capture();
        return button;
    }

    private RowView CreateRow(WorkshopItemBinding binding)
    {
        var slot = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(0, 182) };
        _rows.AddChild(slot);
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        slot.AddChild(panel); panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); ModThemeRuntime.Panel(panel);
        panel.MinimumSizeChanged += () => slot.CustomMinimumSize = new Vector2(0, Math.Max(182, panel.GetCombinedMinimumSize().Y));
        var view = new RowView(binding, slot, panel);
        AttachItemClick(panel, binding, () => view.WorkshopId);
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore }; panel.AddChild(margin);
        foreach (var edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 12);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 158), MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 8); margin.AddChild(box);
        var top = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; top.AddThemeConstantOverride("separation", 14); box.AddChild(top);
        view.Cover = CreateCover(top, out view.Placeholder);
        var labels = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore }; top.AddChild(labels);
        view.Title = CreateMarquee(panel, labels, "…");
        view.Tags = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore }; labels.AddChild(view.Tags);
        var controls = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; box.AddChild(controls);
        view.Status = Text("", 16); view.Status.SizeFlagsHorizontal = SizeFlags.ExpandFill; view.Status.ClipText = true;
        view.Status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; controls.AddChild(view.Status);
        view.Metric = Text("", 16); view.Metric.ClipText = true; view.Metric.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        view.Metric.SizeFlagsHorizontal = SizeFlags.ExpandFill; view.Metric.HorizontalAlignment = HorizontalAlignment.Right; controls.AddChild(view.Metric);
        view.Action = BoundButton(binding, "", () => view.PrimaryAction?.Invoke());
        view.Action.CustomMinimumSize = new Vector2(100, 38); controls.AddChild(view.Action);
        view.Cancel = BoundButton(binding, "", () => view.SecondaryAction?.Invoke());
        view.Cancel.CustomMinimumSize = new Vector2(120, 38); controls.AddChild(view.Cancel);
        return view;
    }

    private void PrepareRows(IReadOnlyList<ulong> bindingIds)
    {
        _pageRows ??= new(PageSize, CreateRow);
        _pageRows.Bind(bindingIds);
        _actions.Clear(); _loadTags.Clear(); _metrics.Clear(); _hoverItems.Clear();
        foreach (var marquee in _marquees) { marquee.Started = 0; marquee.Label.Position = Vector2.Zero; }
        for (var index = 0; index < _pageRows.Slots.Count; index++)
        {
            var view = _pageRows.Slots[index].View;
            view.Slot.Visible = index < bindingIds.Count;
            view.WorkshopId = 0; view.PrimaryAction = view.SecondaryAction = null;
            view.Cover.Texture = null; view.Placeholder.Show();
            view.Action.Hide(); view.Cancel.Hide();
            view.Action.Disabled = view.Cancel.Disabled = true;
            view.Action.Text = view.Cancel.Text = "";
            view.Action.TooltipText = view.Cancel.TooltipText = "";
            view.Status.Text = view.Status.TooltipText = view.Panel.TooltipText = "";
            view.Tags.Show(); view.Metric.Show();
            foreach (var tag in view.TagSlots) { tag.Select = null; tag.Button.Hide(); }
            if (view.Action.HasMeta("sc_restart_accent") && view.Action.GetMeta("sc_restart_accent").AsBool())
            {
                ModThemeRuntime.TextControl(view.Action, 20);
                view.Action.SetMeta("sc_restart_accent", false);
            }
        }
    }

    private Dictionary<ulong, ItemVisual> BindRows(WorkshopCatalogItem[] visible)
    {
        PrepareRows(visible.Select(item => item.Id).ToArray());
        var snapshots = new Dictionary<ulong, ItemVisual>();
        for (var index = 0; index < visible.Length; index++)
        {
            var view = _pageRows!.Slots[index].View;
            var item = visible[index];
            view.WorkshopId = item.Id;
            view.PrimaryAction = () => PerformPrimary(item.Id);
            view.SecondaryAction = () => _ = Unsubscribe(item.Id);
            view.Cancel.Text = WorkshopBrowserText.Get(WorkshopBrowserTextKey.Unsubscribe);
            view.Title.Text = SkinWorkshopService.CachedDetails(item.Id)?.Title ?? "…";
            view.Metric.Text = WorkshopSortPolicy.Metric(SkinWorkshopService.CachedDetails(item.Id), _sort, ModLocalization.CurrentLanguage);
            BindTags(view, item);
            _hoverItems.Add(new(item.Id, view.Slot, view.Panel));
            _actions.Add((item.Id, view.Status, view.Action, view.Cancel));
            _metrics[item.Id] = view.Metric;
            snapshots[item.Id] = view.Snapshot();
        }
        return snapshots;
    }

    private void RecordPageBuild(long started)
    {
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _pageBuilds++; _pageBuildTotalMs += ms; _pageBuildMaxMs = Math.Max(_pageBuildMaxMs, ms);
    }

    private void LogPageBuildTiming()
    {
        if (_pageBuilds == 0) return;
        ModLog.Info($"工坊翻页/筛选：{_pageBuilds} 次，平均 {_pageBuildTotalMs / _pageBuilds:F2}ms，最高 {_pageBuildMaxMs:F2}ms；复用 {_pageRows?.Slots.Count ?? 0} 个物品槽位。");
        _pageBuilds = 0; _pageBuildTotalMs = _pageBuildMaxMs = 0;
    }
}
