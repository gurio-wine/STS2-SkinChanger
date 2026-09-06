using System.Runtime.CompilerServices;
using Godot;

namespace STS2SkinChanger.Ui;

// Explicitly owned background below native borders/text. Each blurred layer samples the
// already rendered lower layers, so a selection cannot erase the panel beneath it.
internal sealed class ModThemeBackdrop
{
    private static readonly ConditionalWeakTable<Control, ModThemeBackdrop> Instances = new();
    private static readonly Lazy<Shader> FlatShader = new(() => new Shader { Code = Header + Mask + "void fragment() { COLOR = tint * vec4(1.0, 1.0, 1.0, coverage(UV)) * COLOR; }" });
    private static readonly Lazy<Shader> BlurShader = new(() => new Shader { Code = Header + """
        uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_linear;
        uniform float blur_radius = 0.0;
        """ + Mask + """
        void fragment() {
            vec2 d = SCREEN_PIXEL_SIZE * blur_radius;
            vec3 b = texture(screen_texture, SCREEN_UV).rgb * 0.25;
            b += (texture(screen_texture, SCREEN_UV + vec2(d.x, 0)).rgb + texture(screen_texture, SCREEN_UV - vec2(d.x, 0)).rgb
                + texture(screen_texture, SCREEN_UV + vec2(0, d.y)).rgb + texture(screen_texture, SCREEN_UV - vec2(0, d.y)).rgb) * 0.125;
            b += (texture(screen_texture, SCREEN_UV + d).rgb + texture(screen_texture, SCREEN_UV - d).rgb
                + texture(screen_texture, SCREEN_UV + vec2(d.x, -d.y)).rgb + texture(screen_texture, SCREEN_UV + vec2(-d.x, d.y)).rgb) * 0.0625;
            COLOR = vec4(mix(b, tint.rgb, tint.a), coverage(UV)) * COLOR;
        }
        """ });
    private const string Header = """
        shader_type canvas_item;
        render_mode unshaded;
        uniform vec4 tint : source_color;
        uniform vec2 surface_size = vec2(1.0);
        uniform float corner_radius = 0.0;
        """;
    private const string Mask = """
        float coverage(vec2 uv) {
            vec2 half_size = surface_size * 0.5;
            float r = min(corner_radius, min(half_size.x, half_size.y));
            vec2 q = abs(uv * surface_size - half_size) - half_size + vec2(r);
            float signed_distance = length(max(q, vec2(0.0))) + min(max(q.x, q.y), 0.0) - r;
            float edge = max(fwidth(signed_distance), 0.5);
            return 1.0 - smoothstep(-edge, edge, signed_distance);
        }
        """;

    private readonly Control _owner;
    private readonly BackBufferCopy _copy;
    private readonly ColorRect _fill;
    private readonly ShaderMaterial _material = new();
    private (Color Tint, float Blur, int Radius, bool Visible)? _last;
    private float _padding;

    private ModThemeBackdrop(Control owner)
    {
        _owner = owner;
        _material.Shader = FlatShader.Value;
        // Node2D wrapper excludes decorations from Container layout/minimum-size calculations.
        _copy = new BackBufferCopy { Name = "SCThemeSurface", ShowBehindParent = true, CopyMode = BackBufferCopy.CopyModeEnum.Disabled };
        _fill = new ColorRect { Color = Colors.White, Material = _material,
            MouseFilter = Control.MouseFilterEnum.Ignore, FocusMode = Control.FocusModeEnum.None };
        _copy.AddChild(_fill);
        owner.AddChild(_copy);
        owner.MoveChild(_copy, 0);
        owner.Resized += Resize;
        owner.TreeEntered += Resize;
        Resize();
    }

    internal static ModThemeBackdrop For(Control owner) => Instances.GetValue(owner, node => new(node));

    internal void Update(Color tint, float blur, int radius, bool visible = true)
    {
        var next = (tint, blur, radius, visible);
        if (_last == next) return;
        _last = next;
        _material.Shader = blur > 0 ? BlurShader.Value : FlatShader.Value;
        _material.SetShaderParameter("tint", tint);
        _material.SetShaderParameter("corner_radius", radius);
        // Fixed nine taps, no per-button full-viewport mipmap generation.
        var pixelRadius = MathF.Pow(2, blur) - 1;
        if (blur > 0) _material.SetShaderParameter("blur_radius", pixelRadius);
        _padding = pixelRadius + 2;
        _copy.CopyMode = visible && blur > 0 ? BackBufferCopy.CopyModeEnum.Rect : BackBufferCopy.CopyModeEnum.Disabled;
        _copy.Visible = visible && (blur > 0 || tint.A > 0);
        Resize();
    }

    private void Resize()
    {
        if (!GodotObject.IsInstanceValid(_owner) || _owner.IsQueuedForDeletion()) return;
        _fill.Size = _owner.Size;
        _material.SetShaderParameter("surface_size", _owner.Size);
        var scale = _owner.IsInsideTree() ? _owner.GetGlobalTransformWithCanvas().Scale.Abs() : Vector2.One;
        var minScale = Math.Max(.01f, Math.Min(scale.X, scale.Y));
        // Sampling offsets are physical pixels; the copy rect is in local UI coordinates.
        _copy.Rect = new Rect2(Vector2.Zero, _owner.Size).Grow(_padding / minScale);
    }
}
