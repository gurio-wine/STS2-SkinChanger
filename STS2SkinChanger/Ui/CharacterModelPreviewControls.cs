using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class CharacterModelPreviewControls
{
    private const string PanelName = "STS2CharacterModelPreview";

    internal static bool ShouldShow(IEnumerable<Mod> mods, bool nativeCooperationActive) =>
        !nativeCooperationActive && !mods.Any(mod => mod.state == ModLoadState.Loaded &&
            FrameworkCompatibilityLayer.IsKnownFrameworkHost(mod.manifest?.id));

    internal static bool IsEnabled => ShouldShow(ModManager.Mods, FrameworkRegistryCooperation.IsActive);

    internal static Rect2? ResolveLayout(Rect2 infoFrame, Rect2 screen)
    {
        if (!infoFrame.HasArea() || !screen.HasArea() || !infoFrame.Position.IsFinite() ||
            !infoFrame.Size.IsFinite() || !screen.Position.IsFinite() || !screen.Size.IsFinite()) return null;
        // Same narrow silhouette as the original preview, measured against the visible info
        // frame rather than InfoPanel's larger layout box. Never shrink the shared height.
        var x = infoFrame.End.X + 24;
        var width = Math.Min(infoFrame.Size.Y * (308f / 429f), screen.End.X - 24 - x);
        return width < 64 ? null : new Rect2(x, infoFrame.Position.Y, width, infoFrame.Size.Y);
    }

    public static void Refresh(NCharacterSelectScreen screen, CharacterModel character)
    {
        if (!IsEnabled) { Hide(screen); return; }
        var info = screen.GetNodeOrNull<Control>("InfoPanel");
        if (info == null) return;
        var panel = info.GetNodeOrNull<CharacterModelPreviewPanel>(PanelName);
        if (panel == null)
        {
            panel = new CharacterModelPreviewPanel { Name = PanelName, Visible = false };
            panel.Initialize(screen, info);
            info.AddChild(panel);
        }
        panel.ShowCharacter(character);
    }

    public static void Hide(NCharacterSelectScreen screen) =>
        screen.GetNodeOrNull<CharacterModelPreviewPanel>($"InfoPanel/{PanelName}")?.Suspend();
}

/// <summary>A view-only host for the same complete model renderer used by the optional manager.</summary>
internal partial class CharacterModelPreviewPanel : Control
{
    private NCharacterSelectScreen _screen = null!;
    private Control _info = null!;
    private Control _frame = null!;
    private NinePatchRect _background = null!;
    private Node2D _visualContainer = null!;
    private Label _name = null!;
    private CharacterModel? _character;
    private bool _refreshQueued;
    private bool _layoutQueued;

    internal void Initialize(NCharacterSelectScreen screen, Control info)
    {
        _screen = screen;
        _info = info;
        _frame = info.GetNodeOrNull<Control>("NinePatchRect") ?? info;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        BuildInterface();
        _frame.ItemRectChanged += QueueLayout;
        _info.ItemRectChanged += QueueLayout;
        _screen.Resized += QueueLayout;
        VisibilityChanged += OnVisibilityChanged;
        ModLocalization.Bind(this, RefreshLabel);
    }

    private void BuildInterface()
    {
        _background = new NinePatchRect { Name = "NinePatchRect", MouseFilter = MouseFilterEnum.Ignore };
        if (_frame is NinePatchRect source)
        {
            // Share the game's existing fuzzy frame texture, never depend on the optional PCK.
            // Only the backdrop inherits its dark tint; the model and label stay untinted.
            _background.Texture = source.Texture;
            _background.Modulate = source.Modulate * source.SelfModulate;
            _background.PatchMarginLeft = source.PatchMarginLeft;
            _background.PatchMarginRight = source.PatchMarginRight;
            _background.PatchMarginTop = source.PatchMarginTop;
            _background.PatchMarginBottom = source.PatchMarginBottom;
        }
        AddChild(_background);
        _background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _visualContainer = new Node2D { Name = "VisualContainer" };
        AddChild(_visualContainer);
        // Keep the original preview's name footer, without either navigation button.
        var footer = new HBoxContainer { Name = "HBoxContainer", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(footer);
        footer.AnchorTop = footer.AnchorBottom = footer.AnchorRight = 1;
        footer.OffsetLeft = 12;
        footer.OffsetRight = -12;
        footer.OffsetTop = -44;
        footer.OffsetBottom = -12;
        _name = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        if (ContextualSkinControls.GameFont is { } font) _name.AddThemeFontOverride("font", font);
        _name.AddThemeFontSizeOverride("font_size", 19);
        footer.AddChild(_name);
    }

    internal void ShowCharacter(CharacterModel character)
    {
        _character = character;
        QueueRefresh();
    }

    internal void Suspend()
    {
        _character = null;
        Visible = false;
        ClearModel();
    }

    private void QueueRefresh()
    {
        if (_refreshQueued || !Alive()) return;
        _refreshQueued = true;
        Callable.From(() =>
        {
            if (!Alive()) return;
            try { RefreshModel(); }
            catch (Exception exception) { ModLog.Warn("SC 模型预览刷新失败：" + exception.GetBaseException().Message); }
            finally { _refreshQueued = false; }
        }).CallDeferred();
    }

    private void RefreshModel()
    {
        if (_character == null || !CharacterModelPreviewControls.IsEnabled) { Suspend(); return; }
        if (!_info.IsVisibleInTree()) return;
        ApplyLayout();
        if (!IsVisibleInTree()) return;
        // Do not leave the previous character on screen when the new provider cannot load.
        ClearModel();
        RefreshLabel();
        FrameworkModelPreview.Refresh(this, _character);
    }

    private void RefreshLabel()
    {
        if (_character == null) return;
        var group = ContextualSkinControls.FindGroup(_character.Id.Entry, _character.GetType().Name);
        _name.Text = group == null ? _character.Title.GetFormattedText() :
            ContextualSkinControls.GetFrameworkSelectionName(_screen, group.Id, null) ?? group.DisplayName;
    }

    private void QueueLayout()
    {
        if (_layoutQueued || !Alive()) return;
        _layoutQueued = true;
        Callable.From(() =>
        {
            _layoutQueued = false;
            if (!Alive()) return;
            ApplyLayout();
        }).CallDeferred();
    }

    private void ApplyLayout()
    {
        if (!GodotObject.IsInstanceValid(_frame)) return;
        var inverse = _info.GetGlobalTransformWithCanvas().AffineInverse();
        var infoRect = (inverse * _frame.GetGlobalTransformWithCanvas()) * new Rect2(Vector2.Zero, _frame.Size);
        var screenRect = (inverse * _screen.GetGlobalTransformWithCanvas()) * new Rect2(Vector2.Zero, _screen.Size);
        var area = CharacterModelPreviewControls.ResolveLayout(infoRect, screenRect);
        Visible = area != null && _character != null && CharacterModelPreviewControls.IsEnabled;
        if (area is not { } rect || !Visible) return;
        var resized = !Size.IsEqualApprox(rect.Size);
        Position = rect.Position;
        Size = rect.Size;
        if (resized && _visualContainer.GetNodeOrNull<FrameworkPreviewSurface>("PreviewSprite") is { } surface)
            surface.RefreshLayout();
    }

    private void OnVisibilityChanged()
    {
        if (!IsVisibleInTree()) ClearModel();
        else if (_character != null) QueueRefresh();
    }

    private void ClearModel()
    {
        if (!GodotObject.IsInstanceValid(_visualContainer)) return;
        foreach (var model in _visualContainer.GetChildren())
        {
            _visualContainer.RemoveChild(model);
            model.QueueFree();
        }
    }

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(_frame)) _frame.ItemRectChanged -= QueueLayout;
        if (GodotObject.IsInstanceValid(_info)) _info.ItemRectChanged -= QueueLayout;
        if (GodotObject.IsInstanceValid(_screen)) _screen.Resized -= QueueLayout;
        VisibilityChanged -= OnVisibilityChanged;
    }

    private bool Alive() => GodotObject.IsInstanceValid(this) && IsInsideTree() && !IsQueuedForDeletion();
}
