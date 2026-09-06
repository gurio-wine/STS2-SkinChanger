using Godot;

namespace STS2SkinChanger.Ui;

// A single screen snapshot is shared by the panel and the selected rows. No per-row copies,
// full-screen blur passes, timers or CPU image processing; only the visible rectangles draw.
internal sealed class CompendiumBackdrop
{
    private const string SelectionNode = "CompendiumSelectionBackdrop";
    private readonly ShaderMaterial _panel;
    private readonly ShaderMaterial _selection;

    public CompendiumBackdrop()
    {
        var shader = new Shader
        {
            Code = """
                shader_type canvas_item;
                render_mode unshaded;
                uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_linear_mipmap;
                uniform float blur_lod = 2.5;
                uniform vec4 tint : source_color;
                void fragment() {
                    vec3 blurred = textureLod(screen_texture, SCREEN_UV, blur_lod).rgb;
                    COLOR = vec4(mix(blurred, tint.rgb, tint.a), 1.0) * COLOR;
                }
                """
        };
        _panel = new ShaderMaterial { Shader = shader };
        _selection = new ShaderMaterial { Shader = shader };
    }

    public void AddPanel(Control parent)
    {
        // Explicitly refresh once after the preview is drawn. Other UI mods may already have
        // read SCREEN_TEXTURE earlier in this canvas; relying on that cached copy is unsafe.
        parent.AddChild(new BackBufferCopy { Name = "CompendiumBackdropCopy", CopyMode = BackBufferCopy.CopyModeEnum.Viewport });
        parent.AddChild(Create("CompendiumPanelBackdrop", _panel));
        ModThemeRuntime.Bind(parent, "backdrop", theme =>
        {
            _panel.SetShaderParameter("blur_lod", theme.PanelBlur);
            _panel.SetShaderParameter("tint", ModThemeRuntime.Tint(theme.PanelColor, theme.PanelOpacity));
            _selection.SetShaderParameter("blur_lod", theme.SelectionBlur);
            _selection.SetShaderParameter("tint", ModThemeRuntime.Tint(theme.SelectionColor, theme.SelectionOpacity));
        });
    }

    public void SetSelected(Button button, bool selected)
    {
        var background = button.GetNodeOrNull<ColorRect>(SelectionNode);
        if (background == null && selected)
        {
            background = Create(SelectionNode, _selection);
            background.ShowBehindParent = true;
            button.AddChild(background);
        }
        if (background != null) background.Visible = selected;
    }

    private static ColorRect Create(string name, ShaderMaterial material)
    {
        var background = new ColorRect
        {
            Name = name, Color = Colors.White, Material = material,
            MouseFilter = Control.MouseFilterEnum.Ignore, FocusMode = Control.FocusModeEnum.None
        };
        background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return background;
    }
}
