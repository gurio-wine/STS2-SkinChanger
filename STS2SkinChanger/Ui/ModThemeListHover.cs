using Godot;

namespace STS2SkinChanger.Ui;

internal static class ModThemeListHover
{
    private const float HoverScale = 1.04f;

    internal static void AddScrollList(ScrollContainer scroll, VBoxContainer list)
    {
        var padding = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(padding);
        padding.AddChild(list);
        void Refresh()
        {
            var theme = ModThemeRuntime.Current;
            var height = list.GetChildren().OfType<Button>().Select(b => b.GetCombinedMinimumSize().Y).DefaultIfEmpty(58).Max();
            var decoration = theme.TextOutline + (theme.TextShadowEnabled
                ? Math.Max(Math.Abs(theme.TextShadowOffsetX), Math.Abs(theme.TextShadowOffsetY)) + theme.TextShadowSize : 0) + 2;
            var inset = ScrollPadding(new Vector2(scroll.Size.X, height), decoration * HoverScale);
            foreach (var (side, value) in new[] { ("left", inset.X), ("right", inset.X), ("top", inset.Y), ("bottom", inset.Y) })
                if (padding.GetThemeConstant("margin_" + side) != (int)value)
                    padding.AddThemeConstantOverride("margin_" + side, (int)value);
        }
        scroll.Resized += Refresh;
        list.MinimumSizeChanged += Refresh;
        ModThemeRuntime.Bind(padding, "hover_padding", _ => Refresh());
    }

    internal static Vector2 ScrollPadding(Vector2 size, float decoration) => new(
        MathF.Ceiling((size.X * (HoverScale - 1) + decoration * 2) / (2 * HoverScale)),
        MathF.Ceiling(size.Y * (HoverScale - 1) / 2 + decoration));

    internal static void Attach(Button button)
    {
        const string marker = "sc_theme_list_hover";
        if (button.HasMeta(marker)) return;
        button.SetMeta(marker, true);
        Tween? tween = null;
        void Resize() => button.PivotOffset = button.Size * .5f;
        void Change(bool hovered)
        {
            tween?.Kill();
            Resize();
            if (!button.IsInsideTree()) return;
            tween = button.CreateTween().SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
            tween.TweenProperty(button, "scale", Vector2.One * (hovered ? HoverScale : 1f), .14);
        }
        void Reset()
        {
            tween?.Kill();
            tween = null;
            button.Scale = Vector2.One;
        }
        button.MouseEntered += () => Change(true);
        button.MouseExited += () => Change(false);
        button.VisibilityChanged += () => { if (!button.IsVisibleInTree()) Reset(); };
        button.TreeExiting += Reset;
        button.Resized += Resize;
        Resize();
    }
}
