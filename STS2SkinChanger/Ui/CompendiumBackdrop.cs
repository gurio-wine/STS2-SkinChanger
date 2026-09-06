using Godot;

namespace STS2SkinChanger.Ui;

internal sealed class CompendiumBackdrop
{
    public void AddPanel(Control parent)
    {
        var background = ModThemeBackdrop.For(parent);
        ModThemeRuntime.Bind(parent, "backdrop", theme => background.Update(
            ModThemeRuntime.Tint(theme.PanelColor, theme.PanelOpacity), theme.PanelBlur, theme.CornerRadius));
    }

    public void SetSelected(Button button, bool selected)
    {
        var background = ModThemeBackdrop.For(button);
        ModThemeRuntime.Bind(button, "selection", theme => background.Update(
            ModThemeRuntime.Tint(theme.SelectionColor, theme.SelectionOpacity), theme.SelectionBlur, theme.CornerRadius, selected));
    }
}
