using Godot;
using STS2SkinChanger.Core;

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
        const string key = "sc_theme_selected";
        var first = !button.HasMeta(key);
        button.SetMeta(key, selected);
        var background = ModThemeBackdrop.For(button);
        void Refresh()
        {
            if (!GodotObject.IsInstanceValid(button) || button.IsQueuedForDeletion()) return;
            var theme = ModThemeRuntime.Current;
            var (tint, blur, visible) = Appearance(theme, button.GetMeta(key).AsBool(), button.IsHovered(), button.Disabled);
            background.Update(tint, blur, theme.CornerRadius, visible);
        }
        if (first) button.Draw += Refresh;
        ModThemeRuntime.Bind(button, "selection", _ => Refresh());
    }

    internal static (Color Tint, float Blur, bool Visible) Appearance(ModThemeSettings theme, bool selected, bool hovered, bool disabled)
    {
        hovered &= !disabled;
        return (ModThemeRuntime.ButtonTint(theme, hovered, selected, disabled),
            selected ? hovered ? theme.SelectionHoverBlur : theme.SelectionBlur : theme.ButtonBlur,
            selected || hovered);
    }
}
