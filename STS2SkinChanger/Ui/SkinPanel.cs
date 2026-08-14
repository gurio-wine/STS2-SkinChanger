using Godot;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal sealed class SkinPanel : CanvasLayer
{
    private sealed record PendingAnimation(Node Node, string AnimationName, int Retries);

    private static SkinPanel? _instance;
    private readonly List<PendingAnimation> _pendingAnimations = [];
    private PanelContainer? _panel;
    private VBoxContainer? _rows;
    private Label? _status;

    public static void EnsureInstalled(SceneTree tree)
    {
        if (_instance != null && IsInstanceValid(_instance))
        {
            return;
        }

        _instance = new SkinPanel { Name = "STS2SkinPanel", Layer = 200 };
        tree.Root.AddChild(_instance);
    }

    public static void NotifyServiceReady()
    {
        if (_instance != null && IsInstanceValid(_instance))
        {
            _instance.RebuildRows();
        }
    }

    public static void QueueAnimationRestore(Node node, string animationName)
    {
        if (_instance != null && IsInstanceValid(_instance))
        {
            _instance._pendingAnimations.Add(new PendingAnimation(node, animationName, 30));
        }
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        BuildUi();
        RebuildRows();
        SetProcess(true);
        SetProcessUnhandledInput(true);
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.F8 })
        {
            return;
        }

        _panel!.Visible = !_panel.Visible;
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        for (var i = _pendingAnimations.Count - 1; i >= 0; i--)
        {
            var pending = _pendingAnimations[i];
            if (!IsInstanceValid(pending.Node) || pending.Retries <= 0)
            {
                _pendingAnimations.RemoveAt(i);
                continue;
            }

            try
            {
                var mega = new MegaSprite(Variant.From(pending.Node));
                var state = mega.TryGetAnimationState();
                if (state != null && mega.HasAnimation(pending.AnimationName))
                {
                    state.SetAnimation(pending.AnimationName);
                    _pendingAnimations.RemoveAt(i);
                    continue;
                }
            }
            catch
            {
                // Spine 在换资源后的几帧内可能尚未重建，继续重试。
            }

            _pendingAnimations[i] = pending with { Retries = pending.Retries - 1 };
        }
    }

    private void BuildUi()
    {
        var backdrop = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorRight = 1,
            AnchorBottom = 1
        };
        AddChild(backdrop);

        _panel = new PanelContainer
        {
            Name = "Panel",
            Visible = false,
            AnchorLeft = 0.64f,
            AnchorTop = 0.06f,
            AnchorRight = 0.98f,
            AnchorBottom = 0.94f,
            OffsetLeft = 0,
            OffsetTop = 0,
            OffsetRight = 0,
            OffsetBottom = 0,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        backdrop.AddChild(_panel);

        var theme = new Theme();
        var font = LoadCjkFont();
        if (font != null)
        {
            theme.DefaultFont = font;
        }
        theme.DefaultFontSize = 22;
        _panel.Theme = theme;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        _panel.AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 14);
        margin.AddChild(column);

        var header = new HBoxContainer();
        column.AddChild(header);
        var title = new Label
        {
            Text = "皮肤切换器",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        title.AddThemeFontSizeOverride("font_size", 30);
        header.AddChild(title);
        var close = new Button { Text = "关闭" };
        close.Pressed += () => _panel.Visible = false;
        header.AddChild(close);

        var hint = new Label
        {
            Text = "选择后立即生效 · F8 显示/隐藏",
            Modulate = new Color(0.78f, 0.82f, 0.86f)
        };
        column.AddChild(hint);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        column.AddChild(scroll);
        _rows = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _rows.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(_rows);

        _status = new Label
        {
            Text = "正在扫描皮肤……",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        column.AddChild(_status);
    }

    private void RebuildRows()
    {
        if (_rows == null || _status == null)
        {
            return;
        }

        foreach (var child in _rows.GetChildren())
        {
            child.QueueFree();
        }

        var catalog = SkinService.Catalog;
        if (catalog == null)
        {
            _status.Text = SkinService.LastError ?? "正在扫描皮肤……";
            return;
        }

        foreach (var group in catalog.Groups)
        {
            _rows.AddChild(BuildGroupRow(group));
        }

        _status.Text = catalog.Groups.Count == 0
            ? "没有发现可管理的角色或怪物皮肤。"
            : $"已发现 {catalog.Groups.Count} 个外观组。";
    }

    private Control BuildGroupRow(SkinGroup group)
    {
        var row = new HBoxContainer();
        var label = new Label
        {
            Text = group.DisplayName,
            CustomMinimumSize = new Vector2(190, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        row.AddChild(label);

        var dropdown = new OptionButton
        {
            CustomMinimumSize = new Vector2(260, 48),
            FitToLongestItem = false
        };
        dropdown.AddItem("游戏默认");
        foreach (var option in group.Options)
        {
            dropdown.AddItem(option.Name);
        }

        var selectedId = SkinService.Config.GetSelection(group.Id);
        var selectedIndex = selectedId == SkinCatalog.BaseOptionId
            ? 0
            : group.Options.FindIndex(option => option.Id == selectedId) + 1;
        dropdown.Select(Math.Max(0, selectedIndex));
        dropdown.ItemSelected += index =>
        {
            var optionId = index == 0 ? SkinCatalog.BaseOptionId : group.Options[checked((int)index - 1)].Id;
            _status!.Text = $"正在切换 {group.DisplayName}……";
            Callable.From(() =>
            {
                var success = SkinService.ApplySelection(group.Id, optionId);
                _status.Text = success
                    ? $"{group.DisplayName} 已切换。"
                    : $"切换失败：{SkinService.LastError}";
            }).CallDeferred();
        };
        row.AddChild(dropdown);
        return row;
    }

    private static Font? LoadCjkFont()
    {
        foreach (var path in new[]
                 {
                     "res://fonts/zhs/NotoSansMonoCJKsc-Regular.otf",
                     "res://fonts/jpn/NotoSansCJKjp-Regular.otf"
                 })
        {
            var font = ResourceLoader.Load<Font>(path);
            if (font != null)
            {
                return font;
            }
        }

        return null;
    }
}
