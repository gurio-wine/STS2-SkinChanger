using System.Runtime.CompilerServices;
using Godot;

namespace STS2SkinChanger.Ui;

// Use the same native text renderer as the foreground. Label and Button do not
// position/trim long centered text identically. Invisible icons retain native spacing.
internal sealed class ModThemeButtonShadow
{
    private static readonly ConditionalWeakTable<Button, ModThemeButtonShadow> Instances = new();
    private static readonly string[] States = ["normal", "hover", "pressed", "hover_pressed", "disabled"];
    private static readonly string[] IconKeys = ["icon", "checked", "unchecked", "radio_checked", "radio_unchecked",
        "checked_disabled", "unchecked_disabled", "radio_checked_disabled", "radio_unchecked_disabled",
        "checked_mirrored", "unchecked_mirrored", "checked_disabled_mirrored", "unchecked_disabled_mirrored"];
    private readonly Button _owner;
    private Button? _shadow;
    private StyleBoxEmpty? _spacing;
    private Texture2D? _arrow;

    internal static void Attach(Button button) => Instances.GetValue(button, node => new(node)).Refresh();

    private ModThemeButtonShadow(Button owner)
    {
        _owner = owner;
        owner.Draw += Refresh;
        owner.Resized += Refresh;
    }

    private static Button CreateShadow(Button owner) => owner switch
    {
        OptionButton => new OptionButton { FitToLongestItem = false },
        CheckBox => new CheckBox(),
        CheckButton => new CheckButton(),
        _ => new Button()
    };

    private void EnsureShadow()
    {
        if (_shadow != null) return;
        _shadow = CreateShadow(_owner);
        _shadow.Name = "SCThemeTextShadow";
        _shadow.ShowBehindParent = true;
        _shadow.MouseFilter = Control.MouseFilterEnum.Ignore;
        _shadow.FocusMode = Control.FocusModeEnum.None;
        _shadow.Flat = true;
        _spacing = new StyleBoxEmpty();
        // Flat suppresses backgrounds. The boxes only feed native text layout with
        // the foreground's resolved margins, including align_to_largest_stylebox.
        foreach (var state in States)
        {
            _shadow.AddThemeStyleboxOverride(state, _spacing);
            _shadow.AddThemeStyleboxOverride(state + "_mirrored", _spacing);
        }
        foreach (var state in new[] { "normal", "focus", "pressed", "hover", "hover_pressed", "disabled" })
            _shadow.AddThemeColorOverride("icon_" + state + "_color", Colors.Transparent);
        foreach (var key in new[] { "checkbox_checked_color", "checkbox_unchecked_color", "button_checked_color", "button_unchecked_color" })
            _shadow.AddThemeColorOverride(key, Colors.Transparent);
        _owner.AddChild(_shadow);
    }

    private void Refresh()
    {
        if (!GodotObject.IsInstanceValid(_owner) || _owner.IsQueuedForDeletion()) return;
        var theme = ModThemeRuntime.Current;
        var shown = theme.TextShadowEnabled && theme.TextShadowOpacity > 0 && !string.IsNullOrEmpty(_owner.Text);
        if (!shown) { if (_shadow != null) _shadow.Visible = false; return; }
        EnsureShadow();
        var shadow = _shadow!;
        shadow.Visible = true;
        if (shadow.Text != _owner.Text) shadow.Text = _owner.Text;
        if (shadow.Alignment != _owner.Alignment) shadow.Alignment = _owner.Alignment;
        if (shadow.LayoutDirection != _owner.LayoutDirection) shadow.LayoutDirection = _owner.LayoutDirection;
        if (shadow.TextDirection != _owner.TextDirection) shadow.TextDirection = _owner.TextDirection;
        if (shadow.Language != _owner.Language) shadow.Language = _owner.Language;
        if (shadow.ClipText != _owner.ClipText) shadow.ClipText = _owner.ClipText;
        if (shadow.TextOverrunBehavior != _owner.TextOverrunBehavior) shadow.TextOverrunBehavior = _owner.TextOverrunBehavior;
        if (shadow.AutowrapMode != _owner.AutowrapMode) shadow.AutowrapMode = _owner.AutowrapMode;
        if (shadow.AutowrapTrimFlags != _owner.AutowrapTrimFlags) shadow.AutowrapTrimFlags = _owner.AutowrapTrimFlags;
        if (shadow.Icon != _owner.Icon) shadow.Icon = _owner.Icon;
        if (shadow.IconAlignment != _owner.IconAlignment) shadow.IconAlignment = _owner.IconAlignment;
        if (shadow.VerticalIconAlignment != _owner.VerticalIconAlignment) shadow.VerticalIconAlignment = _owner.VerticalIconAlignment;
        if (shadow.ExpandIcon != _owner.ExpandIcon) shadow.ExpandIcon = _owner.ExpandIcon;
        var font = _owner.GetThemeFont("font");
        if (shadow.GetThemeFont("font") != font || !shadow.HasThemeFontOverride("font")) shadow.AddThemeFontOverride("font", font);
        var fontSize = _owner.GetThemeFontSize("font_size");
        if (shadow.GetThemeFontSize("font_size") != fontSize || !shadow.HasThemeFontSizeOverride("font_size"))
            shadow.AddThemeFontSizeOverride("font_size", fontSize);
        foreach (var key in new[] { "h_separation", "icon_max_width", "line_spacing", "check_v_offset", "arrow_margin" })
            Constant(key, _owner.GetThemeConstant(key));
        foreach (var key in IconKeys)
            if (_owner.HasThemeIcon(key) && (!shadow.HasThemeIconOverride(key) || shadow.GetThemeIcon(key) != _owner.GetThemeIcon(key)))
                shadow.AddThemeIconOverride(key, _owner.GetThemeIcon(key));
        if (_owner is OptionButton && _owner.HasThemeIcon("arrow"))
        {
            var size = _owner.GetThemeIcon("arrow").GetSize();
            if (_arrow == null || _arrow.GetSize() != size)
            {
                _arrow = new GradientTexture2D { Width = Math.Max(1, (int)size.X), Height = Math.Max(1, (int)size.Y),
                    Gradient = new Gradient { Colors = [Colors.Transparent, Colors.Transparent] } };
                shadow.AddThemeIconOverride("arrow", _arrow);
            }
        }
        var color = ModThemeRuntime.Tint(theme.TextShadowColor, theme.TextShadowOpacity);
        foreach (var key in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_disabled_color", "font_focus_color", "font_outline_color" })
            if (!shadow.HasThemeColorOverride(key) || shadow.GetThemeColor(key) != color) shadow.AddThemeColorOverride(key, color);
        Constant("outline_size", theme.TextShadowSize);
        SyncMargins();
        var position = new Vector2(theme.TextShadowOffsetX, theme.TextShadowOffsetY);
        if (shadow.Position != position) shadow.Position = position;
        if (shadow.Size != _owner.Size) shadow.Size = _owner.Size;
    }

    private void Constant(string key, int value)
    {
        if (!_shadow!.HasThemeConstantOverride(key) || _shadow.GetThemeConstant(key) != value)
            _shadow.AddThemeConstantOverride(key, value);
    }

    private void SyncMargins()
    {
        var current = _owner.GetDrawMode() switch
        {
            BaseButton.DrawMode.Disabled => "disabled", BaseButton.DrawMode.HoverPressed => "hover_pressed",
            BaseButton.DrawMode.Pressed => "pressed", BaseButton.DrawMode.Hover => "hover", _ => "normal"
        };
        var largest = _owner.GetThemeConstant("align_to_largest_stylebox") != 0;
        foreach (var side in new[] { Side.Left, Side.Top, Side.Right, Side.Bottom })
        {
            var margin = 0f;
            foreach (var state in largest ? States : new[] { current })
            {
                var key = _owner.IsLayoutRtl() && _owner.HasThemeStylebox(state + "_mirrored") ? state + "_mirrored" : state;
                margin = Math.Max(margin, _owner.GetThemeStylebox(key).GetMargin(side));
            }
            if (_spacing!.GetContentMargin(side) != margin) _spacing.SetContentMargin(side, margin);
        }
    }
}
