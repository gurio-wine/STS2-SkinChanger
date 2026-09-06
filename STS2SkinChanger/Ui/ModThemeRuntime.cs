using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal enum ModThemeSurface { Panel, Button, Hover, Selected, Disabled, Focus }

// Explicit opt-in only: never walk the game's tree or mutate a cached native Theme resource.
internal static class ModThemeRuntime
{
    public static string Path => System.IO.Path.Combine(OS.GetUserDataDir(), "skin_changer_theme.json");
    private static readonly Lazy<ModThemeSession> Editing = new(() => new(ModThemeStore.Load(Path)));
    public static ModThemeSession Session => Editing.Value;
    public static ModThemeSettings Current => Session.Current;
    public static Color Text => new(Current.TextColor);
    public static Color Accent => new(Current.AccentColor);
    public static Color Tint(string color, float alpha) => new(new Color(color), alpha);

    public static void Bind(Node owner, string key, Action<ModThemeSettings> refresh)
    {
        var name = "SCTheme_" + key;
        var binding = owner.GetNodeOrNull<ModThemeBinding>(name);
        if (binding == null)
        {
            binding = new ModThemeBinding { Name = name, Refresh = refresh };
            owner.AddChild(binding);
        }
        else binding.Refresh = refresh;
        binding.Apply();
    }

    public static void ApplyStyle(StyleBoxFlat style, ModThemeSurface surface, ModThemeSettings theme)
    {
        style.BgColor = surface switch
        {
            ModThemeSurface.Panel => Tint(theme.PanelColor, theme.PanelOpacity),
            ModThemeSurface.Hover => Tint(theme.HoverColor, theme.ButtonOpacity),
            ModThemeSurface.Selected => Tint(theme.SelectionColor, theme.SelectionOpacity),
            ModThemeSurface.Disabled => Tint(theme.ButtonColor, theme.ButtonOpacity * .4f),
            ModThemeSurface.Focus => Colors.Transparent,
            _ => Tint(theme.ButtonColor, theme.ButtonOpacity)
        };
        style.BorderColor = new Color(surface is ModThemeSurface.Selected or ModThemeSurface.Focus
            ? theme.AccentColor : theme.BorderColor);
        var width = theme.BorderWidth;
        style.BorderWidthLeft = style.BorderWidthRight = style.BorderWidthTop = style.BorderWidthBottom = width;
        style.CornerRadiusTopLeft = style.CornerRadiusTopRight = style.CornerRadiusBottomLeft = style.CornerRadiusBottomRight = theme.CornerRadius;
        style.ContentMarginLeft = style.ContentMarginRight = 12;
    }

    public static void Panel(PanelContainer panel)
    {
        var style = new StyleBoxFlat();
        var background = ModThemeBackdrop.For(panel);
        panel.AddThemeStyleboxOverride("panel", style);
        Bind(panel, "panel", theme =>
        {
            ApplyStyle(style, ModThemeSurface.Panel, theme);
            style.DrawCenter = false;
            background.Update(Tint(theme.PanelColor, theme.PanelOpacity), theme.PanelBlur, theme.CornerRadius);
            BindPanelText(panel);
        });
        // Panel factories finish adding their owned labels after attaching the panel itself.
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(panel) && !panel.IsQueuedForDeletion()) BindPanelText(panel);
        }).CallDeferred();
    }

    private static void BindPanelText(Node owner)
    {
        // Only inside explicitly registered SC settings panels, once on creation / edit.
        // No global node-added hook, game scene traversal or per-frame discovery.
        foreach (var child in owner.GetChildren())
        {
            if (child.Name.ToString().StartsWith("SCTheme", StringComparison.Ordinal)) continue;
            if (child is LineEdit input && input.GetNodeOrNull<ModThemeBinding>("SCTheme_input") == null)
                Input(input, input.HasMeta("sc_theme_base_font_size")
                    ? input.GetMeta("sc_theme_base_font_size").AsInt32() : input.GetThemeFontSize("font_size"),
                    input.GetThemeColor("font_color") == Accent);
            if (child is Label or Godot.Button && child is Control control &&
                control.GetNodeOrNull<ModThemeBinding>("SCTheme_text") == null)
            {
                var original = control.GetThemeColor("font_color");
                // Gold was the old accent, not a fixed semantic color. Red errors and
                // per-provider source colors remain independent of the theme.
                var body = original == new Color("fff6e2") || original == Colors.White;
                var accent = original == new Color("efc850");
                TextControl(control, control.GetThemeFontSize("font_size"), accent, preserveTextColor: !body && !accent);
            }
            if (child is not ModThemeBinding) BindPanelText(child);
        }
    }

    public static void Button(Button button, int fontSize)
    {
        var styles = new (string Name, ModThemeSurface Surface, StyleBoxFlat Style)[]
        {
            ("normal", ModThemeSurface.Button, new()), ("hover", ModThemeSurface.Hover, new()),
            ("pressed", ModThemeSurface.Selected, new()), ("hover_pressed", ModThemeSurface.Hover, new()),
            ("focus", ModThemeSurface.Focus, new()), ("disabled", ModThemeSurface.Disabled, new())
        };
        foreach (var (name, _, style) in styles) button.AddThemeStyleboxOverride(name, style);
        var background = ModThemeBackdrop.For(button);
        void RefreshBackground()
        {
            if (!GodotObject.IsInstanceValid(button) || button.IsQueuedForDeletion()) return;
            var theme = Current;
            var mode = button.GetDrawMode();
            var pressed = mode is BaseButton.DrawMode.Pressed or BaseButton.DrawMode.HoverPressed;
            background.Update(ButtonTint(theme, button.IsHovered(), pressed, button.Disabled),
                theme.ButtonBlur, theme.CornerRadius);
        }
        // Button.Draw is a native signal. It catches disabled/toggle states too, without
        // a frame poll; the surface caches identical values to avoid a redraw loop.
        if (!button.HasMeta("sc_theme_button_draw"))
        {
            button.SetMeta("sc_theme_button_draw", true);
            button.Draw += RefreshBackground;
        }
        // Mutate our own style objects instead of replacing overrides on every change. A delete
        // confirmation's red override or other explicit per-control state must keep ownership.
        Bind(button, "button", theme =>
        {
            foreach (var (_, surface, style) in styles) { ApplyStyle(style, surface, theme); style.DrawCenter = false; }
            RefreshBackground();
        });
        TextControl(button, fontSize);
    }

    internal static Color ButtonTint(ModThemeSettings theme, bool hovered, bool pressed, bool disabled) =>
        Tint(pressed ? hovered && !disabled ? theme.SelectionHoverColor : theme.SelectionColor :
                hovered && !disabled ? theme.HoverColor : theme.ButtonColor,
            (pressed ? hovered && !disabled ? theme.SelectionHoverOpacity : theme.SelectionOpacity : theme.ButtonOpacity) * (disabled ? .4f : 1));

    public static void Input(LineEdit input, int fontSize, bool accent = false)
    {
        var background = ModThemeBackdrop.For(input);
        var styles = new (string Name, ModThemeSurface Surface, StyleBoxFlat Style)[]
        {
            ("normal", ModThemeSurface.Button, new()), ("read_only", ModThemeSurface.Disabled, new()),
            ("focus", ModThemeSurface.Focus, new())
        };
        foreach (var (name, _, style) in styles) input.AddThemeStyleboxOverride(name, style);
        void RefreshBackground()
        {
            if (!GodotObject.IsInstanceValid(input) || input.IsQueuedForDeletion()) return;
            var theme = Current;
            background.Update(ButtonTint(theme, false, false, !input.Editable), theme.ButtonBlur, theme.CornerRadius);
        }
        if (!input.HasMeta("sc_theme_input_draw"))
        {
            input.SetMeta("sc_theme_input_draw", true);
            input.Draw += RefreshBackground;
        }
        Bind(input, "input", theme =>
        {
            foreach (var (_, surface, style) in styles) { ApplyStyle(style, surface, theme); style.DrawCenter = false; }
            input.AddThemeColorOverride("font_placeholder_color", new Color(theme.TextColor));
            input.AddThemeColorOverride("selection_color", Tint(theme.SelectionColor, theme.SelectionOpacity));
            input.AddThemeColorOverride("font_selected_color", new Color(accent ? theme.AccentColor : theme.TextColor));
            RefreshBackground();
        });
        if (ContextualSkinControls.GameFont is { } font) input.AddThemeFontOverride("font", font);
        TextControl(input, fontSize, accent);
    }

    public static void TextControl(Control control, int fontSize, bool accent = false, bool preserveTextColor = false)
    {
        control.SetMeta("sc_theme_base_font_size", fontSize);
        var lastColors = new Dictionary<string, Color>();
        var lastSize = -1;
        Bind(control, "text", theme =>
        {
            void Color(string key, Color value)
            {
                // Keep explicit semantic overrides such as red destructive actions.
                var current = control.GetThemeColor(key);
                if (lastColors.TryGetValue(key, out var last) && current != last) return;
                if (current != value || !control.HasThemeColorOverride(key)) control.AddThemeColorOverride(key, value);
                lastColors[key] = value;
            }
            if (!preserveTextColor) Color("font_color", new Color(accent ? theme.AccentColor : theme.TextColor));
            Color("font_hover_color", new Color(accent ? theme.AccentColor : theme.TextColor));
            Color("font_focus_color", new Color(accent ? theme.AccentColor : theme.TextColor));
            Color("font_pressed_color", new Color(theme.AccentColor));
            Color("font_hover_pressed_color", new Color(theme.AccentColor));
            if (control is LineEdit)
            {
                Color("font_uneditable_color", new Color(accent ? theme.AccentColor : theme.TextColor));
                Color("caret_color", new Color(theme.AccentColor));
            }
            if (control.GetThemeColor("font_outline_color") != new Color("332f27"))
                control.AddThemeColorOverride("font_outline_color", new Color("332f27"));
            if (control.GetThemeConstant("outline_size") != theme.TextOutline)
                control.AddThemeConstantOverride("outline_size", theme.TextOutline);
            if (lastSize >= 0 && control.GetThemeFontSize("font_size") != lastSize)
                fontSize = control.GetThemeFontSize("font_size");
            lastSize = Math.Max(10, (int)MathF.Round(fontSize * theme.FontScale));
            if (control.GetThemeFontSize("font_size") != lastSize) control.AddThemeFontSizeOverride("font_size", lastSize);
            ApplyTextShadow(control, theme);
        });
    }

    public static void AccentText(Control control) => TextControl(control,
        control.HasMeta("sc_theme_base_font_size") ? control.GetMeta("sc_theme_base_font_size").AsInt32() : control.GetThemeFontSize("font_size"), accent: true);

    internal static void ApplyTextShadow(Control control, ModThemeSettings theme)
    {
        control.AddThemeColorOverride("font_shadow_color", Tint(theme.TextShadowColor, theme.TextShadowEnabled ? theme.TextShadowOpacity : 0));
        control.AddThemeConstantOverride("shadow_offset_x", theme.TextShadowOffsetX);
        control.AddThemeConstantOverride("shadow_offset_y", theme.TextShadowOffsetY);
        control.AddThemeConstantOverride("shadow_outline_size", theme.TextShadowSize);
        if (control is Godot.Button button) ModThemeButtonShadow.Attach(button);
    }

    public static void ListButton(Button button, bool selected, int fontSize)
    {
        var normal = new StyleBoxEmpty();
        var focus = new StyleBoxFlat();
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", normal);
        button.AddThemeStyleboxOverride("hover_pressed", normal);
        button.AddThemeStyleboxOverride("disabled", normal);
        button.AddThemeStyleboxOverride("pressed", focus);
        button.AddThemeStyleboxOverride("focus", focus);
        Bind(button, "row", theme =>
        {
            ApplyStyle(focus, ModThemeSurface.Focus, theme);
            focus.DrawCenter = false;
        });
        if (ContextualSkinControls.GameFont is { } font) button.AddThemeFontOverride("font", font);
        TextControl(button, fontSize, selected);
    }

    public static void Popup(PopupMenu popup)
    {
        ModThemeDropdownBackdrop.Attach(popup);
        var panel = new StyleBoxFlat();
        var hover = new StyleBoxFlat();
        popup.AddThemeStyleboxOverride("panel", panel);
        popup.AddThemeStyleboxOverride("hover", hover);
        Bind(popup, "popup", theme =>
        {
            ApplyDropdownStyle(panel, theme);
            ApplyDropdownStyle(hover, theme, hovered: true);
            popup.AddThemeColorOverride("font_color", new Color(theme.TextColor));
            popup.AddThemeColorOverride("font_hover_color", new Color(theme.TextColor));
            popup.AddThemeColorOverride("font_separator_color", new Color(theme.AccentColor));
            popup.AddThemeColorOverride("font_outline_color", new Color("332f27"));
            popup.AddThemeConstantOverride("outline_size", theme.TextOutline);
            popup.AddThemeFontSizeOverride("font_size", (int)MathF.Round(22 * theme.FontScale));
        });
    }

    public static void ItemList(ItemList list)
    {
        var styles = new[] { new StyleBoxFlat(), new StyleBoxFlat(), new StyleBoxFlat(), new StyleBoxFlat() };
        list.AddThemeStyleboxOverride("panel", styles[0]);
        list.AddThemeStyleboxOverride("hovered", styles[1]);
        list.AddThemeStyleboxOverride("selected", styles[2]);
        list.AddThemeStyleboxOverride("selected_focus", styles[2]);
        list.AddThemeStyleboxOverride("hovered_selected", styles[3]);
        list.AddThemeStyleboxOverride("hovered_selected_focus", styles[3]);
        var focus = new StyleBoxFlat();
        list.AddThemeStyleboxOverride("focus", focus);
        Bind(list, "list", theme =>
        {
            // PopupMenu is its own viewport. Sampling its screen texture blurs the native
            // menu under the custom list, not the scene, and duplicates text/opacity.
            ApplyDropdownStyle(styles[0], theme);
            ApplyDropdownStyle(styles[1], theme, hovered: true);
            ApplyDropdownStyle(styles[2], theme, selected: true);
            ApplyDropdownStyle(styles[3], theme, hovered: true, selected: true);
            ApplyDropdownStyle(focus, theme);
            focus.DrawCenter = false;
            list.AddThemeColorOverride("font_hovered_color", new Color(theme.TextColor));
            list.AddThemeColorOverride("font_selected_color", new Color(theme.TextColor));
            list.AddThemeColorOverride("font_hovered_selected_color", new Color(theme.TextColor));
        });
        AttachDropdownListHost(list);
        TextControl(list, 19);
    }

    internal static Color DropdownTint(ModThemeSettings theme, bool hovered, bool selected) =>
        selected ? hovered ? Tint(theme.DropdownSelectionHoverColor, theme.DropdownSelectionHoverOpacity) :
            Tint(theme.DropdownSelectionColor, theme.DropdownSelectionOpacity) :
        hovered ? Tint(theme.DropdownHoverColor, theme.DropdownHoverOpacity) : Tint(theme.DropdownColor, theme.DropdownOpacity);

    private static void ApplyDropdownStyle(StyleBoxFlat style, ModThemeSettings theme, bool hovered = false, bool selected = false)
    {
        style.BgColor = DropdownTint(theme, hovered, selected);
        style.BorderColor = new Color(theme.DropdownBorderColor);
        style.BorderWidthLeft = style.BorderWidthRight = style.BorderWidthTop = style.BorderWidthBottom = theme.DropdownBorderWidth;
        style.CornerRadiusTopLeft = style.CornerRadiusTopRight = style.CornerRadiusBottomLeft = style.CornerRadiusBottomRight = theme.DropdownCornerRadius;
        style.ContentMarginLeft = style.ContentMarginRight = 12;
    }

    private static void AttachDropdownListHost(ItemList list)
    {
        if (list.HasMeta("sc_theme_list_host")) return;
        list.SetMeta("sc_theme_list_host", true);
        PanelContainer? nativePanel = null;
        var original = Colors.White;
        void Restore()
        {
            if (GodotObject.IsInstanceValid(nativePanel)) nativePanel!.Modulate = original;
            nativePanel = null;
        }
        void Refresh()
        {
            if (list.GetParent() is not PopupMenu popup) return;
            // Godot's internal PanelContainer owns the native menu drawing; leave its
            // layout and input alive, but let the colored ItemList draw exactly once.
            if (nativePanel == null)
            {
                nativePanel = popup.GetChildren(includeInternal: true).OfType<PanelContainer>().FirstOrDefault();
                if (nativePanel == null) return;
                original = nativePanel.Modulate;
            }
            if (GodotObject.IsInstanceValid(nativePanel))
                nativePanel.Modulate = list.Visible ? new Color(original, 0) : original;
        }
        list.TreeEntered += Refresh;
        list.VisibilityChanged += Refresh;
        list.TreeExiting += Restore;
        Refresh();
    }
}

internal partial class ModThemeBinding : Node
{
    internal Action<ModThemeSettings> Refresh = _ => { };
    private bool _connected;

    public ModThemeBinding()
    {
        // Runtime-created mod nodes have no generated Godot virtual dispatch. Native signals
        // also cover controls built off-tree, reparented later, or removed and re-entered.
        TreeEntered += Connect;
        TreeExiting += Disconnect;
    }

    private void Connect()
    {
        if (_connected) return;
        _connected = true;
        ModThemeRuntime.Session.Changed += Apply;
        Apply();
    }

    private void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
        ModThemeRuntime.Session.Changed -= Apply;
    }

    internal void Apply()
    {
        if (!GodotObject.IsInstanceValid(this) || IsQueuedForDeletion() || GetParent()?.IsQueuedForDeletion() == true) return;
        try { Refresh(ModThemeRuntime.Current); }
        catch (Exception e) { ModLog.Warn("刷新 Mod 主题失败：" + e.GetBaseException().Message); }
    }
}
