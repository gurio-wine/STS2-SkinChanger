using Godot;

namespace STS2SkinChanger.Ui;

// Native Button has no text-shadow theme property. Render only the shadow with a native
// Label, between our background and the original Button drawing. Text, icons, clicks,
// focus and accessibility remain entirely on the real button.
internal static class ModThemeButtonShadow
{
    internal static void Attach(Button button)
    {
        const string name = "SCThemeTextShadow";
        if (button.GetNodeOrNull<Label>(name) is { } existing)
        {
            UpdateTheme(existing);
            return;
        }
        var shadow = new Label
        {
            Name = name, ShowBehindParent = true,
            MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center,
            LabelSettings = new LabelSettings { FontColor = Colors.Transparent, OutlineColor = Colors.Transparent, OutlineSize = 0 }
        };
        button.AddChild(shadow);
        void Refresh()
        {
            if (!GodotObject.IsInstanceValid(button) || button.IsQueuedForDeletion()) return;
            var theme = ModThemeRuntime.Current;
            var shown = theme.TextShadowEnabled && theme.TextShadowOpacity > 0 && !string.IsNullOrEmpty(button.Text);
            if (shadow.Visible != shown) shadow.Visible = shown;
            if (!shown) return;
            var text = button.Text;
            if (shadow.Text != text) shadow.Text = text;
            shadow.HorizontalAlignment = button.Alignment;
            shadow.TextDirection = button.TextDirection;
            shadow.Language = button.Language;
            shadow.ClipText = button.ClipText;
            shadow.TextOverrunBehavior = button.TextOverrunBehavior;
            var settings = shadow.LabelSettings;
            var font = button.GetThemeFont("font");
            var size = button.GetThemeFontSize("font_size");
            if (settings.Font != font) settings.Font = font;
            if (settings.FontSize != size) settings.FontSize = size;
            var styleName = button.GetDrawMode() switch
            {
                BaseButton.DrawMode.Disabled => "disabled", BaseButton.DrawMode.Pressed => "pressed",
                BaseButton.DrawMode.HoverPressed => "hover_pressed", BaseButton.DrawMode.Hover => "hover", _ => "normal"
            };
            var style = button.GetThemeStylebox(styleName);
            float left = style.GetContentMargin(Side.Left), right = style.GetContentMargin(Side.Right);
            float top = style.GetContentMargin(Side.Top), bottom = style.GetContentMargin(Side.Bottom);
            var separation = button.GetThemeConstant("h_separation");
            var icon = button.Icon;
            if (icon != null)
            {
                if (button.IconAlignment == HorizontalAlignment.Left) left += icon.GetWidth() + separation;
                else if (button.IconAlignment == HorizontalAlignment.Right) right += icon.GetWidth() + separation;
            }
            if (button is OptionButton)
            {
                var arrow = button.GetThemeIcon("arrow");
                if (arrow != null) right += arrow.GetWidth() + separation;
            }
            else if (button is CheckBox)
                left += button.GetThemeIcon("checked").GetWidth() + separation;
            else if (button is CheckButton)
                right += button.GetThemeIcon("checked").GetWidth() + separation;
            var position = new Vector2(left, top);
            var area = new Vector2(Math.Max(0, button.Size.X - left - right), Math.Max(0, button.Size.Y - top - bottom));
            if (shadow.Position != position) shadow.Position = position;
            if (shadow.Size != area) shadow.Size = area;
        }
        UpdateTheme(shadow);
        button.Draw += Refresh;
        button.Resized += Refresh;
        Refresh();
    }

    private static void UpdateTheme(Label shadow)
    {
        var theme = ModThemeRuntime.Current;
        var settings = shadow.LabelSettings;
        var color = ModThemeRuntime.Tint(theme.TextShadowColor, theme.TextShadowEnabled ? theme.TextShadowOpacity : 0);
        var offset = new Vector2(theme.TextShadowOffsetX, theme.TextShadowOffsetY);
        if (settings.ShadowColor != color) settings.ShadowColor = color;
        if (settings.ShadowOffset != offset) settings.ShadowOffset = offset;
        if (settings.ShadowSize != theme.TextShadowSize) settings.ShadowSize = theme.TextShadowSize;
        shadow.Visible = theme.TextShadowEnabled && theme.TextShadowOpacity > 0;
    }
}
