using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class SkinWorkshopPanel
{
    private sealed record ItemVisual(Label Title, TextureRect Cover, Panel Placeholder);
    private sealed class Marquee(Control row, Control clip, Label label)
    {
        public readonly Control Row = row;
        public readonly Control Clip = clip;
        public readonly Label Label = label;
        public ulong Started;
    }
    private readonly List<Marquee> _marquees = [];
    private Label CreateMarquee(Control row, VBoxContainer labels, string text, ulong itemId)
    {
        // Control, not Container: the title's full minimum width must not stretch
        // either grid column. Clip only the title, never the interactive tags below.
        var clip = new Button { ClipContents = true, Flat = true, CustomMinimumSize = new Vector2(0, 38), SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseDefaultCursorShape = CursorShape.PointingHand };
        foreach (var state in new[] { "normal", "hover", "pressed", "disabled" }) clip.AddThemeStyleboxOverride(state, new StyleBoxEmpty());
        var focusStyle = new StyleBoxFlat(); clip.AddThemeStyleboxOverride("focus", focusStyle);
        ModThemeRuntime.Bind(clip, "title_focus", theme => ModThemeRuntime.ApplyStyle(focusStyle, ModThemeSurface.Focus, theme));
        clip.Pressed += () => OpenItem(itemId);
        labels.AddChild(clip);
        var label = Text(text, 22); clip.AddChild(label);
        void ColorTitle() => label.AddThemeColorOverride("font_color", clip.IsHovered() || clip.HasFocus() ? ModThemeRuntime.Accent : ModThemeRuntime.Text);
        clip.MouseEntered += ColorTitle; clip.MouseExited += ColorTitle;
        clip.FocusEntered += ColorTitle; clip.FocusExited += ColorTitle;
        ModThemeRuntime.Bind(label, "workshop_link", _ => ColorTitle());
        _marquees.Add(new(row, clip, label));
        ModThemeRuntime.Bind(clip, "title_height", theme => clip.CustomMinimumSize = new Vector2(0, 38 * theme.FontScale));
        return label;
    }
    private TextureRect CreateCover(HBoxContainer row, ulong itemId, out Panel placeholder)
    {
        var host = new Button { Flat = true, CustomMinimumSize = new Vector2(144, 100), MouseDefaultCursorShape = CursorShape.PointingHand, SizeFlagsVertical = SizeFlags.ShrinkBegin };
        foreach (var state in new[] { "normal", "hover", "pressed", "disabled" }) host.AddThemeStyleboxOverride(state, new StyleBoxEmpty());
        var focusStyle = new StyleBoxFlat(); host.AddThemeStyleboxOverride("focus", focusStyle);
        ModThemeRuntime.Bind(host, "cover_focus", theme => ModThemeRuntime.ApplyStyle(focusStyle, ModThemeSurface.Focus, theme));
        host.Pressed += () => OpenItem(itemId);
        row.AddChild(host);
        placeholder = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        host.AddChild(placeholder); placeholder.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var style = new StyleBoxFlat(); placeholder.AddThemeStyleboxOverride("panel", style);
        ModThemeRuntime.Bind(placeholder, "workshop_placeholder", theme =>
        {
            style.BgColor = new Color(theme.AccentColor);
            style.CornerRadiusTopLeft = style.CornerRadiusTopRight = style.CornerRadiusBottomLeft = style.CornerRadiusBottomRight = theme.CornerRadius;
        });
        var cover = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore };
        host.AddChild(cover); cover.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        return cover;
    }
    private void AnimateTitles()
    {
        if (_closed || _suspended || !IsVisibleInTree()) return;
        var mouse = GetGlobalMousePosition();
        var focus = GetViewport().GuiGetFocusOwner();
        foreach (var item in _marquees)
        {
            var overflow = Math.Max(0, item.Label.GetMinimumSize().X - item.Clip.Size.X + 6);
            var hovered = item.Row.GetGlobalRect().HasPoint(mouse) || focus != null && item.Row.IsAncestorOf(focus);
            if (!hovered || overflow <= 6)
            {
                item.Started = 0; item.Label.Position = Vector2.Zero;
                continue;
            }
            if (item.Started == 0) item.Started = Time.GetTicksMsec();
            var elapsed = (Time.GetTicksMsec() - item.Started) / 1000d;
            item.Label.Position = new Vector2(-WorkshopBrowserPolicy.MarqueeOffset(overflow, elapsed), 0);
        }
    }
    private static readonly Lazy<Shader> CoverShader = new(() => new Shader { Code = """
        shader_type canvas_item;
        render_mode unshaded;
        uniform vec2 surface_size = vec2(1.0);
        uniform float corner_radius = 0.0;
        void fragment() {
            vec2 half_size = surface_size * 0.5;
            float r = min(corner_radius, min(half_size.x, half_size.y));
            vec2 q = abs(UV * surface_size - half_size) - half_size + vec2(r);
            float d = length(max(q, vec2(0.0))) + min(max(q.x, q.y), 0.0) - r;
            COLOR.a *= 1.0 - smoothstep(-max(fwidth(d), 0.5), max(fwidth(d), 0.5), d);
        }
        """ });
    private static void RoundCover(TextureRect cover)
    {
        var material = new ShaderMaterial { Shader = CoverShader.Value };
        cover.Material = material;
        void Resize()
        {
            if (!GodotObject.IsInstanceValid(cover) || cover.Texture is not { } texture) return;
            var size = texture.GetSize();
            var factor = Math.Min(cover.Size.X / Math.Max(1, size.X), cover.Size.Y / Math.Max(1, size.Y));
            material.SetShaderParameter("surface_size", size * factor);
        }
        cover.Resized += Resize;
        ModThemeRuntime.Bind(cover, "workshop_cover", theme =>
        {
            material.SetShaderParameter("corner_radius", theme.CornerRadius);
            Resize();
        });
    }
}
