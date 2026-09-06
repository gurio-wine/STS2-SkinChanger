using Godot;

namespace STS2SkinChanger.Ui;

internal static class ModThemeListHover
{
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
            tween.TweenProperty(button, "scale", Vector2.One * (hovered ? 1.04f : 1f), .14);
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
