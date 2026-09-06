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
    private readonly Node2D _surface;
    private readonly BackBufferCopy _copy;
    private readonly ColorRect _fill;
    private readonly ShaderMaterial _material = new();
    private (Color Tint, float Blur, int Radius, bool Visible)? _last;
    private float _padding;
    private bool _trackingCopy;

    private ModThemeBackdrop(Control owner)
    {
        _owner = owner;
        _material.Shader = FlatShader.Value;
        // Node2D wrapper excludes decorations from Container layout/minimum-size calculations.
        _surface = new Node2D { Name = "SCThemeSurface", ShowBehindParent = true };
        _copy = new BackBufferCopy { CopyMode = BackBufferCopy.CopyModeEnum.Disabled };
        _fill = new ColorRect { Color = Colors.White, Material = _material,
            MouseFilter = Control.MouseFilterEnum.Ignore, FocusMode = Control.FocusModeEnum.None };
        // A parent's TreeEntered precedes its children. Off-tree factories need the surface's
        // own native entry signal as well, including cached panels re-entering the tree.
        _surface.TreeEntered += TrackCopy;
        _surface.TreeExiting += StopTrackingCopy;
        _surface.VisibilityChanged += TrackCopy;
        // Copy and fill are ordered siblings: only the copy uses viewport coordinates.
        // Transforming their common parent would also displace the actual button background.
        _surface.AddChild(_copy);
        _surface.AddChild(_fill);
        owner.AddChild(_surface);
        owner.MoveChild(_surface, 0);
        owner.Resized += Resize;
        owner.TreeEntered += Resize;
        owner.TreeExiting += StopTrackingCopy;
        owner.VisibilityChanged += TrackCopy;
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
        _surface.Visible = visible && (blur > 0 || tint.A > 0);
        Resize();
    }

    private void Resize()
    {
        if (!GodotObject.IsInstanceValid(_owner) || _owner.IsQueuedForDeletion()) return;
        _fill.Size = _owner.Size;
        _material.SetShaderParameter("surface_size", _owner.Size);
        TrackCopy();
    }

    private void TrackCopy()
    {
        if (!_owner.IsInsideTree() || !_surface.IsVisibleInTree() || _last is not { Blur: > 0, Visible: true })
        {
            StopTrackingCopy();
            return;
        }
        if (!_trackingCopy)
        {
            _trackingCopy = true;
            RenderingServer.FramePreDraw += SyncCopy;
        }
        SyncCopy();
    }

    private void StopTrackingCopy()
    {
        if (_trackingCopy) RenderingServer.FramePreDraw -= SyncCopy;
        _trackingCopy = false;
        if (GodotObject.IsInstanceValid(_copy)) _copy.CopyMode = BackBufferCopy.CopyModeEnum.Disabled;
    }

    private void SyncCopy()
    {
        if (!GodotObject.IsInstanceValid(_owner) || _owner.IsQueuedForDeletion() || !_owner.IsInsideTree()) return;
        var transform = _owner.GetViewportTransform() * _owner.GetGlobalTransform();
        // Hidden-by-scale animations can briefly have a singular transform.
        if (Math.Abs(transform.Determinant()) < .000001f || _owner.Size.X <= 0 || _owner.Size.Y <= 0)
        {
            _copy.CopyMode = BackBufferCopy.CopyModeEnum.Disabled;
            return;
        }
        var (rect, copyTransform) = CopyGeometry(_owner.Size, transform, _padding);
        if (_copy.Transform != copyTransform) _copy.Transform = copyTransform;
        if (_copy.Rect != rect) _copy.Rect = rect;
        if (_copy.CopyMode != BackBufferCopy.CopyModeEnum.Rect) _copy.CopyMode = BackBufferCopy.CopyModeEnum.Rect;
    }

    internal static (Rect2 Rect, Transform2D Transform) CopyGeometry(Vector2 size, Transform2D toViewport, float padding)
    {
        // Godot 4.5's RD/GLES renderer consumes the copy rect in render-target pixels,
        // not the transformed screen_rect prepared by canvas culling. Make both identical:
        // copy with an identity final transform and a screen-space, outward-rounded rect.
        var bounds = toViewport * new Rect2(Vector2.Zero, size);
        var start = bounds.Position.Floor();
        var end = bounds.End.Ceil();
        return (new Rect2(start, end - start).Grow(padding), toViewport.AffineInverse());
    }
}
