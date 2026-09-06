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
        panel.AddThemeStyleboxOverride("panel", style);
        Bind(panel, "panel", theme =>
        {
            ApplyStyle(style, ModThemeSurface.Panel, theme);
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
            if (child is Label or Godot.Button && child is Control control &&
                control.GetNodeOrNull<ModThemeBinding>("SCTheme_text") == null)
            {
                var original = control.GetThemeColor("font_color");
                // Non-body semantic labels (bundle gold, delete red, source colors) stay colored.
                var body = original == new Color("fff6e2") || original == Colors.White;
                TextControl(control, control.GetThemeFontSize("font_size"), preserveTextColor: !body);
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
        // Mutate our own style objects instead of replacing overrides on every change. A delete
        // confirmation's red override or other explicit per-control state must keep ownership.
        Bind(button, "button", theme =>
        {
            foreach (var (_, surface, style) in styles) ApplyStyle(style, surface, theme);
        });
        TextControl(button, fontSize);
    }

    public static void TextControl(Control control, int fontSize, bool accent = false, bool preserveTextColor = false)
    {
        var lastColors = new Dictionary<string, Color>();
        var lastSize = -1;
        Bind(control, "text", theme =>
        {
            void Color(string key, Color value)
            {
                // Keep per-entry semantic colors (yellow bundles, red destructive actions).
                var current = control.GetThemeColor(key);
                if (lastColors.TryGetValue(key, out var last) && current != last) return;
                if (current != value || !control.HasThemeColorOverride(key)) control.AddThemeColorOverride(key, value);
                lastColors[key] = value;
            }
            if (!preserveTextColor) Color("font_color", new Color(accent ? theme.AccentColor : theme.TextColor));
            Color("font_hover_color", new Color(theme.TextColor));
            Color("font_focus_color", new Color(theme.TextColor));
            Color("font_pressed_color", new Color(theme.AccentColor));
            Color("font_hover_pressed_color", new Color(theme.AccentColor));
            if (control.GetThemeColor("font_outline_color") != new Color("332f27"))
                control.AddThemeColorOverride("font_outline_color", new Color("332f27"));
            if (control.GetThemeConstant("outline_size") != theme.TextOutline)
                control.AddThemeConstantOverride("outline_size", theme.TextOutline);
            if (lastSize >= 0 && control.GetThemeFontSize("font_size") != lastSize)
                fontSize = control.GetThemeFontSize("font_size");
            lastSize = Math.Max(10, (int)MathF.Round(fontSize * theme.FontScale));
            if (control.GetThemeFontSize("font_size") != lastSize) control.AddThemeFontSizeOverride("font_size", lastSize);
        });
    }

    public static void ListButton(Button button, bool selected, int fontSize)
    {
        var normal = new StyleBoxEmpty();
        var hover = new StyleBoxFlat();
        var focus = new StyleBoxFlat();
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", focus);
        button.AddThemeStyleboxOverride("focus", focus);
        Bind(button, "row", theme =>
        {
            ApplyStyle(hover, ModThemeSurface.Hover, theme);
            hover.BgColor = Tint(theme.HoverColor, .15f);
            hover.BorderWidthLeft = hover.BorderWidthRight = hover.BorderWidthTop = hover.BorderWidthBottom = 0;
            ApplyStyle(focus, ModThemeSurface.Focus, theme);
        });
        if (ContextualSkinControls.GameFont is { } font) button.AddThemeFontOverride("font", font);
        TextControl(button, fontSize, selected);
    }

    public static void Popup(PopupMenu popup)
    {
        var panel = new StyleBoxFlat();
        var hover = new StyleBoxFlat();
        popup.AddThemeStyleboxOverride("panel", panel);
        popup.AddThemeStyleboxOverride("hover", hover);
        Bind(popup, "popup", theme =>
        {
            ApplyStyle(panel, ModThemeSurface.Panel, theme);
            ApplyStyle(hover, ModThemeSurface.Hover, theme);
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
        var styles = new[] { new StyleBoxFlat(), new StyleBoxFlat(), new StyleBoxFlat() };
        list.AddThemeStyleboxOverride("panel", styles[0]);
        list.AddThemeStyleboxOverride("hovered", styles[1]);
        list.AddThemeStyleboxOverride("selected", styles[2]);
        Bind(list, "list", theme =>
        {
            ApplyStyle(styles[0], ModThemeSurface.Panel, theme);
            ApplyStyle(styles[1], ModThemeSurface.Hover, theme);
            ApplyStyle(styles[2], ModThemeSurface.Selected, theme);
        });
        TextControl(list, 19);
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
