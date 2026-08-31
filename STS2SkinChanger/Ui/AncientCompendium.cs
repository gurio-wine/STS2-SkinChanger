using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class AncientCompendiumEntry
{
    private const string ButtonName = "STS2AncientCompendiumButton";
    private const string ScreenName = "STS2AncientCompendium";
    private static readonly System.Reflection.FieldInfo StackField = AccessTools.Field(typeof(NSubmenu), "_stack");
    private static NCompendiumBottomButton? _entryButton;

    public static void Attach(NCompendiumSubmenu compendium)
    {
        if (compendium.GetNodeOrNull<NCompendiumBottomButton>(
                $"MarginContainer/VBoxContainer/BottomRow/{ButtonName}") != null)
        {
            return;
        }

        var scenePath = SceneHelper.GetScenePath("screens/main_menu/compendium_bottom_button");
        var button = PreloadManager.Cache.GetScene(scenePath)
            .Instantiate<NCompendiumBottomButton>(PackedScene.GenEditState.Disabled);
        button.Name = ButtonName;
        button.FocusMode = Control.FocusModeEnum.All;

        var bottomRow = compendium.GetNodeOrNull<HBoxContainer>("MarginContainer/VBoxContainer/BottomRow");
        if (bottomRow == null)
        {
            ModLog.Error("图鉴底部缺少按钮行节点，先古图鉴入口未添加。");
            return;
        }

        bottomRow.AddChild(button);
        var statistics = compendium.GetNodeOrNull<NCompendiumBottomButton>("%StatisticsButton");
        var runHistory = compendium.GetNodeOrNull<NCompendiumBottomButton>("%RunHistoryButton");
        if (statistics != null && runHistory != null)
        {
            bottomRow.MoveChild(button, statistics.GetIndex() + 1);
        }

        var buttonLabel = button.GetNodeOrNull<MegaLabel>("Label");
        buttonLabel?.SetTextAutoSize(ModLocalization.Get(ModText.AncientCompendium));
        if (buttonLabel != null)
        {
            ModLocalization.Bind(button, () =>
                buttonLabel.SetTextAutoSize(ModLocalization.Get(ModText.AncientCompendium)));
        }
        var icon = button.GetNodeOrNull<TextureRect>("Icon");
        if (icon != null)
        {
            icon.OffsetLeft = 70;
            icon.OffsetTop = 22;
            icon.OffsetRight = -70;
            icon.OffsetBottom = -62;
            icon.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            var firstAncient = GetAncients().FirstOrDefault();
            if (firstAncient != null)
            {
                icon.Texture = firstAncient.MapIcon;
            }
        }

        _entryButton = button;

        button.Connect(
            NClickableControl.SignalName.Released,
            Callable.From((Action<NButton>)(_ => Open(compendium))));

        if (statistics == null || runHistory == null)
        {
            return;
        }

        var bestiary = compendium.GetNodeOrNull<NShortSubmenuButton>("%BestiaryButton");
        statistics.FocusNeighborRight = button.GetPath();
        button.FocusNeighborLeft = statistics.GetPath();
        button.FocusNeighborRight = runHistory.GetPath();
        button.FocusNeighborTop = bestiary?.GetPath() ?? button.GetPath();
        button.FocusNeighborBottom = button.GetPath();
        runHistory.FocusNeighborLeft = button.GetPath();
        if (bestiary != null)
        {
            bestiary.FocusNeighborBottom = button.GetPath();
        }
    }

    private static void Open(NCompendiumSubmenu compendium)
    {
        if (StackField.GetValue(compendium) is not NSubmenuStack stack)
        {
            ModLog.Error("无法取得图鉴菜单栈，先古图鉴未打开。");
            return;
        }

        var gallery = stack.GetNodeOrNull<AncientCompendiumScreen>(ScreenName);
        if (gallery == null)
        {
            gallery = new AncientCompendiumScreen
            {
                Name = ScreenName,
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            stack.AddChild(gallery);
        }

        stack.Push(gallery);
    }

    internal static AncientEventModel[] GetAncients() => ModelDb.AllAncients
        .Where(ancient => ResourceLoader.Exists(GetScenePath(ancient)))
        .DistinctBy(ancient => ancient.Id)
        .OrderBy(ancient => GetTitle(ancient), StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    internal static string GetScenePath(AncientEventModel ancient) =>
        "res://scenes/events/background_scenes/" + ancient.Id.Entry.ToLowerInvariant() + ".tscn";

    internal static string GetTitle(AncientEventModel ancient)
    {
        try
        {
            return ancient.Title.GetFormattedText();
        }
        catch
        {
            return ancient.Id.Entry.Replace('_', ' ').CapitalizeWords();
        }
    }

    internal static SkinGroup? FindGroup(string modelId)
    {
        var catalog = SkinService.Catalog;
        if (catalog == null)
        {
            return null;
        }

        var token = NormalizeToken(modelId);
        return catalog.Groups.FirstOrDefault(candidate => NormalizeToken(candidate.Id) == token);
    }

    internal static void ReplaceAncientIcon(
        AncientEventModel ancient,
        string resourcePath,
        ref Texture2D result)
    {
        var group = FindGroup(ancient.Id.Entry);
        if (group == null)
        {
            return;
        }

        try
        {
            // An external full-scene illustration does not imply ownership of the Ancient's
            // map/run-history icons. Resolve the requested icon normally: a PCK-backed skin that
            // actually supplies it wins, while an image-only provider falls back to the game icon.
            result = SkinService.GetOrLoadRuntimeResource(group.Id, resourcePath) as Texture2D ??
                     throw new InvalidOperationException($"独立先古皮肤资源不是贴图：{resourcePath}");
        }
        catch (Exception exception)
        {
            ModLog.Error($"最终接管先古头像 {resourcePath} 失败：{exception}");
        }
    }

    internal static void RefreshCompendiumEntryIcon(Node context)
    {
        // 优先用缓存的按钮引用，避免每次换肤都全树查找。
        var button = _entryButton;
        if (!GodotObject.IsInstanceValid(button))
        {
            button = context.GetTree().Root.FindChild(ButtonName, recursive: true, owned: false)
                as NCompendiumBottomButton;
            _entryButton = button;
        }

        var firstAncient = GetAncients().FirstOrDefault();
        if (button != null && firstAncient != null)
        {
            button.GetNode<TextureRect>("Icon").Texture = firstAncient.MapIcon;
        }
    }

    private static string NormalizeToken(string value) =>
        Regex.Replace(value, "[^a-zA-Z0-9]", string.Empty).ToLowerInvariant();
}

internal partial class AncientCompendiumScreen : NSubmenu
{
    private readonly Dictionary<AncientEventModel, Button> _entryButtons = [];
    private VBoxContainer _entryList = null!;
    private Label _nameLabel = null!;
    private Label _epithetLabel = null!;
    private Label _headingLabel = null!;
    private HBoxContainer _skinSelector = null!;
    private OptionButton _skinDropdown = null!;
    private SubViewport _previewViewport = null!;
    private SubViewportContainer _previewContainer = null!;
    private AncientEventModel? _selectedAncient;
    private bool _updatingDropdown;

    protected override Control? InitialFocusedControl => _entryButtons.Values.FirstOrDefault();

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        BuildUi();
        ConnectSignals();
    }

    public override void OnSubmenuOpened()
    {
        base.OnSubmenuOpened();
        _previewViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        RefreshAncients();
    }

    public override void OnSubmenuClosed()
    {
        // Dynamic Ancient providers can register scene-tree-wide input hooks. Merely hiding the
        // compendium leaves their preview nodes alive, so those hooks keep receiving input after
        // the player returns to the compendium or enters a run. Release the preview scene at the
        // same lifecycle boundary as the submenu itself; reopening rebuilds the selected preview.
        ClearPreview();
        _previewViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        base.OnSubmenuClosed();
    }

    private void BuildUi()
    {
        _previewContainer = new SubViewportContainer
        {
            AnchorLeft = 0,
            AnchorTop = 0,
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetLeft = 0,
            OffsetTop = 0,
            OffsetRight = 0,
            OffsetBottom = 0,
            Stretch = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_previewContainer);

        _previewViewport = new SubViewport
        {
            Size = new Vector2I(1920, 1080),
            TransparentBg = false,
            GuiDisableInput = true,
            // 子菜单打开时切为 Always 以持续播放 Spine/AnimationPlayer；关闭时禁用。
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled
        };
        _previewContainer.AddChild(_previewViewport);

        _nameLabel = BuildLabel(48, new Color("efc850"));
        _nameLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _nameLabel.AnchorLeft = 0;
        _nameLabel.AnchorRight = 0;
        _nameLabel.OffsetLeft = 82;
        _nameLabel.OffsetTop = 900;
        _nameLabel.OffsetRight = 750;
        _nameLabel.OffsetBottom = 958;
        AddChild(_nameLabel);

        _epithetLabel = BuildLabel(24, new Color("87ceeB"));
        _epithetLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _epithetLabel.AnchorLeft = 0;
        _epithetLabel.AnchorRight = 0;
        _epithetLabel.OffsetLeft = 86;
        _epithetLabel.OffsetTop = 958;
        _epithetLabel.OffsetRight = 750;
        _epithetLabel.OffsetBottom = 998;
        AddChild(_epithetLabel);

        _skinSelector = new HBoxContainer
        {
            AnchorLeft = 0,
            AnchorTop = 0,
            AnchorRight = 0,
            AnchorBottom = 0,
            OffsetLeft = 818,
            OffsetTop = 826,
            OffsetRight = 1102,
            OffsetBottom = 874,
            Visible = false,
            ZIndex = 10
        };
        AddChild(_skinSelector);

        _skinDropdown = new OptionButton
        {
            CustomMinimumSize = new Vector2(284, 48),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FitToLongestItem = false,
            ClipText = true,
            Alignment = HorizontalAlignment.Center
        };
        ContextualSkinControls.ApplyGameTheme(_skinDropdown);
        _skinDropdown.ItemSelected += index => OnSkinSelected(checked((int)index));
        _skinSelector.AddChild(_skinDropdown);

        var sidebar = new MarginContainer
        {
            AnchorLeft = 1,
            AnchorTop = 0,
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetLeft = -380,
            OffsetTop = 0,
            OffsetRight = 0,
            OffsetBottom = 0
        };
        AddChild(sidebar);

        sidebar.AddThemeConstantOverride("margin_left", 34);
        sidebar.AddThemeConstantOverride("margin_top", 58);
        sidebar.AddThemeConstantOverride("margin_right", 34);
        sidebar.AddThemeConstantOverride("margin_bottom", 90);

        var sidebarContent = new VBoxContainer();
        sidebarContent.AddThemeConstantOverride("separation", 22);
        sidebar.AddChild(sidebarContent);

        _headingLabel = BuildLabel(30, new Color("efc850"));
        _headingLabel.Text = ModLocalization.Get(ModText.AncientCompendium);
        _headingLabel.CustomMinimumSize = new Vector2(0, 54);
        sidebarContent.AddChild(_headingLabel);

        var divider = new HSeparator();
        divider.AddThemeConstantOverride("separation", 12);
        sidebarContent.AddChild(divider);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        sidebarContent.AddChild(scroll);

        _entryList = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(312, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _entryList.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(_entryList);

        var backButton = PreloadManager.Cache
            .GetScene(SceneHelper.GetScenePath("ui/back_button"))
            .Instantiate<NBackButton>(PackedScene.GenEditState.Disabled);
        backButton.Name = "BackButton";
        AddChild(backButton);
        ModLocalization.Bind(this, RefreshLocalizedText);
    }

    private void RefreshLocalizedText()
    {
        _headingLabel.Text = ModLocalization.Get(ModText.AncientCompendium);
        if (_selectedAncient == null && _entryButtons.Count == 0)
        {
            _nameLabel.Text = ModLocalization.Get(ModText.NoAncientsAvailable);
        }

        var groupId = _skinDropdown.GetMeta("sts2_skin_group", string.Empty).AsString();
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            PopulateSkinDropdown(SkinService.Catalog?.Groups.FirstOrDefault(group =>
                group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private void RefreshAncients()
    {
        foreach (var child in _entryList.GetChildren())
        {
            _entryList.RemoveChild(child);
            child.QueueFree();
        }

        _entryButtons.Clear();
        var ancients = AncientCompendiumEntry.GetAncients();
        foreach (var ancient in ancients)
        {
            var button = new Button
            {
                Text = AncientCompendiumEntry.GetTitle(ancient),
                CustomMinimumSize = new Vector2(312, 58),
                FocusMode = FocusModeEnum.All,
                Alignment = HorizontalAlignment.Center
            };
            ApplyEntryTheme(button, selected: false);
            button.Pressed += () => SelectAncient(ancient);
            _entryList.AddChild(button);
            _entryButtons[ancient] = button;
        }

        if (ancients.Length == 0)
        {
            _nameLabel.Text = ModLocalization.Get(ModText.NoAncientsAvailable);
            _epithetLabel.Text = string.Empty;
            _skinSelector.Visible = false;
            ClearPreview();
            return;
        }

        var selection = _selectedAncient == null
            ? ancients[0]
            : ancients.FirstOrDefault(candidate => candidate.Id == _selectedAncient.Id) ?? ancients[0];
        SelectAncient(selection);
    }

    private void SelectAncient(AncientEventModel ancient)
    {
        _selectedAncient = ancient;
        _nameLabel.Text = AncientCompendiumEntry.GetTitle(ancient);
        try
        {
            _epithetLabel.Text = ancient.Epithet.GetFormattedText();
        }
        catch
        {
            _epithetLabel.Text = string.Empty;
        }

        foreach (var pair in _entryButtons)
        {
            ApplyEntryTheme(pair.Value, pair.Key.Id == ancient.Id);
        }

        PopulateSkinDropdown(AncientCompendiumEntry.FindGroup(ancient.Id.Entry));
        RebuildPreview(ancient);
    }

    private void PopulateSkinDropdown(SkinGroup? group)
    {
        _updatingDropdown = true;
        _skinDropdown.Clear();
        if (group == null || group.Options.Count == 0)
        {
            _skinSelector.Visible = false;
            _updatingDropdown = false;
            return;
        }

        _skinDropdown.AddItem(ModLocalization.Get(ModText.GameDefault));
        _skinDropdown.SetItemMetadata(0, SkinCatalog.BaseOptionId);
        foreach (var option in group.Options)
        {
            var index = _skinDropdown.ItemCount;
            _skinDropdown.AddItem(ModLocalization.DisplayOptionName(option.Name));
            _skinDropdown.SetItemMetadata(index, option.Id);
        }

        var current = SkinService.Config.GetSelection(group.Id);
        var selectedIndex = Enumerable.Range(0, _skinDropdown.ItemCount)
            .FirstOrDefault(index => _skinDropdown.GetItemMetadata(index).AsString()
                .Equals(current, StringComparison.OrdinalIgnoreCase));
        _skinDropdown.Select(selectedIndex);
        _skinDropdown.SetMeta("sts2_skin_group", group.Id);
        _updatingDropdown = false;
        _skinSelector.Visible = true;
    }

    private void OnSkinSelected(int index)
    {
        if (_updatingDropdown || _selectedAncient == null)
        {
            return;
        }

        var groupId = _skinDropdown.GetMeta("sts2_skin_group", string.Empty).AsString();
        var optionId = _skinDropdown.GetItemMetadata(index).AsString();
        if (!SkinService.ApplySelection(groupId, optionId))
        {
            ModLog.Error($"先古皮肤切换失败：{SkinService.LastError}");
            PopulateSkinDropdown(AncientCompendiumEntry.FindGroup(_selectedAncient.Id.Entry));
            return;
        }

        AncientCompendiumEntry.RefreshCompendiumEntryIcon(this);
        var ancient = _selectedAncient;
        Callable.From(() => RebuildPreview(ancient)).CallDeferred();
    }

    private void RebuildPreview(AncientEventModel ancient)
    {
        try
        {
            ClearPreview();
            var scenePath = AncientCompendiumEntry.GetScenePath(ancient);
            var group = AncientCompendiumEntry.FindGroup(ancient.Id.Entry);
            var interactive = group != null &&
                              SkinService.IsInteractiveRuntimeProviderSelected(group.Id);
            _previewContainer.MouseFilter = interactive
                ? MouseFilterEnum.Pass
                : MouseFilterEnum.Ignore;
            _previewViewport.GuiDisableInput = !interactive;
            PackedScene scene;
            if (group != null && SkinService.IsExternalRuntimeProviderSelected(group.Id))
            {
                scene = BuildSelectedRuntimeImageScene(group.Id);
            }
            else if (group != null)
            {
                scene = SkinService.LoadRuntimeScene(group.Id, scenePath);
            }
            else
            {
                scene = ResourceLoader.Load<PackedScene>(scenePath, null, ResourceLoader.CacheMode.IgnoreDeep)
                        ?? throw new InvalidOperationException($"无法加载先古场景：{scenePath}");
            }

            var preview = scene.Instantiate<Control>(PackedScene.GenEditState.Disabled);
            preview.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            var previewHost = new Control
            {
                Name = "PreviewHost",
                MouseFilter = MouseFilterEnum.Pass
            };
            previewHost.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _previewViewport.AddChild(previewHost);
            previewHost.AddChild(preview);
            ManagedAncientLayeredImage.TryApply(group?.Id, preview);
            ManagedAncientSceneAnimation.TryStart(group?.Id, preview);
            ModLog.Info($"先古图鉴已展示 {ancient.Id.Entry}。");
        }
        catch (Exception exception)
        {
            ModLog.Error($"先古图鉴预览 {ancient.Id.Entry} 失败：{exception}");
        }
    }

    internal static PackedScene BuildSelectedRuntimeImageScene(string groupId)
    {
        var root = new Control { Name = "RuntimeAncientBackground" };
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        ManagedAncientStaticBackground.Mark(root);
        var image = new TextureRect
        {
            Name = "Image",
            Texture = SkinService.GetSelectedRuntimeImageTexture(groupId),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        image.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddChild(image);
        image.Owner = root;

        var scene = new PackedScene();
        var error = scene.Pack(root);
        root.Free();
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"无法创建先古图片场景：{error}");
        }

        return scene;
    }

    private void ClearPreview()
    {
        _previewContainer.MouseFilter = MouseFilterEnum.Ignore;
        _previewViewport.GuiDisableInput = true;
        foreach (var child in _previewViewport.GetChildren())
        {
            // Some provider input routers inspect CanvasItem.Visible rather than
            // IsVisibleInTree(). Hide and stop the subtree before detaching it so there is no
            // one-frame window in which an old interactive preview can still consume input.
            if (child is CanvasItem canvasItem)
            {
                canvasItem.Visible = false;
            }

            child.ProcessMode = ProcessModeEnum.Disabled;
            _previewViewport.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static Label BuildLabel(int fontSize, Color color)
    {
        var label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color("332f27"));
        label.AddThemeConstantOverride("outline_size", fontSize >= 34 ? 10 : 5);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        var font = ContextualSkinControls.GameFont;
        if (font != null)
        {
            label.AddThemeFontOverride("font", font);
        }

        return label;
    }

    private static void ApplyEntryTheme(Button button, bool selected)
    {
        var ivory = new Color("fff6e2");
        var gold = new Color("efc850");
        button.AddThemeColorOverride("font_color", selected ? gold : ivory);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", gold);
        button.AddThemeFontSizeOverride("font_size", 24);
        var font = ContextualSkinControls.GameFont;
        if (font != null)
        {
            button.AddThemeFontOverride("font", font);
        }

        button.AddThemeStyleboxOverride(
            "normal",
            ContextualSkinControls.CreateStyleBox(
                selected ? new Color("45104eb8") : new Color("00000000"),
                selected ? gold : new Color("00000000"),
                selected ? 3 : 0));
        button.AddThemeStyleboxOverride(
            "hover",
            ContextualSkinControls.CreateStyleBox(new Color("3c627eaa"), new Color("afcdde"), 2));
        button.AddThemeStyleboxOverride(
            "pressed",
            ContextualSkinControls.CreateStyleBox(new Color("45104e"), gold, 2));
        button.AddThemeStyleboxOverride(
            "focus",
            ContextualSkinControls.CreateStyleBox(new Color("2a465faa"), gold, 3));
    }

}

internal static class ManagedAncientLayeredImage
{
    private const string CoverNodeName = "SkinChangerAncientBackgroundCover";
    private const string CharacterNodeName = "SkinChangerAncientCharacter";
    private const string CharacterMaskShaderCode = """
        shader_type canvas_item;
        uniform sampler2D mask_texture;

        void fragment() {
            vec4 base_color = texture(TEXTURE, UV);
            vec4 mask_color = texture(mask_texture, UV);
            float mask_value = mask_color.a * dot(mask_color.rgb, vec3(0.299, 0.587, 0.114));
            COLOR = vec4(base_color.rgb, base_color.a * mask_value);
        }
        """;

    public static void TryApply(string? groupId, Node sceneRoot)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return;
        }

        try
        {
            var layers = SkinService.GetSelectedAncientLayeredImageTextures(groupId);
            if (layers == null)
            {
                return;
            }

            var placeholderMarker = groupId + "_placeholder";
            var target = DescendantsAndSelf(sceneRoot)
                .OfType<TextureRect>()
                .FirstOrDefault(textureRect =>
                    textureRect.Texture?.ResourcePath.Contains(
                        placeholderMarker,
                        StringComparison.OrdinalIgnoreCase) == true);
            if (target?.GetParent() is not Node parent)
            {
                ModLog.Warn($"先古图层皮肤 {groupId} 找不到原场景占位图。");
                return;
            }

            RemoveOldLayer(parent, CoverNodeName);
            RemoveOldLayer(parent, CharacterNodeName);

            var insertIndex = target.GetIndex() + 1;
            if (layers.BackgroundCover != null)
            {
                var cover = DuplicateLayer(target, CoverNodeName, layers.BackgroundCover);
                cover.Material = null;
                parent.AddChild(cover);
                parent.MoveChild(cover, insertIndex++);
            }

            var character = DuplicateLayer(target, CharacterNodeName, layers.Character);
            if (layers.Mask != null)
            {
                var shader = new Shader { Code = CharacterMaskShaderCode };
                var material = new ShaderMaterial { Shader = shader };
                material.SetShaderParameter("mask_texture", layers.Mask);
                character.Material = material;
            }
            else
            {
                character.Material = null;
            }

            parent.AddChild(character);
            parent.MoveChild(character, insertIndex);
            ModLog.Info($"已应用 {groupId} 的代码型先古图层皮肤。");
        }
        catch (Exception exception)
        {
            ModLog.Warn($"应用 {groupId} 的先古图层皮肤失败：{exception.Message}");
        }
    }

    private static TextureRect DuplicateLayer(
        TextureRect source,
        string name,
        Texture2D texture)
    {
        var layer = source.Duplicate() as TextureRect ??
                    throw new InvalidOperationException("无法复制先古场景占位图节点。");
        layer.Name = name;
        layer.Texture = texture;
        return layer;
    }

    private static void RemoveOldLayer(Node parent, string name)
    {
        var existing = parent.GetNodeOrNull<Node>(name);
        existing?.Free();
    }

    private static IEnumerable<Node> DescendantsAndSelf(Node root)
    {
        yield return root;
        foreach (Node child in root.GetChildren())
        {
            foreach (var descendant in DescendantsAndSelf(child))
            {
                yield return descendant;
            }
        }
    }
}

internal static class ManagedAncientSceneAnimation
{
    public static void TryStart(string? groupId, Node sceneRoot)
    {
        if (string.IsNullOrWhiteSpace(groupId) ||
            !SkinService.IsManagedResourceOptionSelected(groupId))
        {
            return;
        }

        var spineNode = sceneRoot.GetNodeOrNull<Node>("SpineSprite") ??
                        sceneRoot.FindChild("SpineSprite", recursive: true, owned: false);
        if (spineNode == null)
        {
            return;
        }

        try
        {
            var sprite = new MegaSprite(spineNode);
            sceneRoot.RunWhenSpineReady(sprite, animationState =>
                StartDefaultAnimation(groupId, sprite, animationState));
        }
        catch (Exception exception)
        {
            ModLog.Warn($"准备 {groupId} 的先古 Spine 动画失败：{exception.Message}");
        }
    }

    private static void StartDefaultAnimation(
        string groupId,
        MegaSprite sprite,
        MegaAnimationState animationState)
    {
        try
        {
            var animationNames = sprite.GetSkeleton()?.GetData()?.GetAnimationNames();
            if (animationNames == null || animationNames.Count == 0)
            {
                return;
            }

            // 两个支持版本都提供这个值类型入口，并由各自版本负责
            // MegaTrackEntry 的正确释放方式。
            var currentName = animationState.GetCurrentAnimationName(0);
            if (!string.IsNullOrWhiteSpace(currentName) &&
                animationNames.Any(name =>
                    name.Equals(currentName, StringComparison.OrdinalIgnoreCase)) &&
                !currentName.Equals("Dummy", StringComparison.OrdinalIgnoreCase) &&
                !currentName.StartsWith("Touch_", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var intro = FindAnimation(animationNames, "Intro");
            var idle = FindAnimation(animationNames, "Idle");
            if (intro != null)
            {
                SetAnimationCompat(animationState, intro, loop: false);
                if (idle != null)
                {
                    AddAnimationCompat(animationState, idle, delay: 0f, loop: true);
                }

                ModLog.Info($"已启动 {groupId} 的先古 Spine 动画：{intro}" +
                            (idle == null ? string.Empty : $" -> {idle}"));
                return;
            }

            var loopAnimation = idle ??
                                FindAnimation(animationNames, "animation") ??
                                FindAnimation(animationNames, "default") ??
                                animationNames.FirstOrDefault(name =>
                                    !name.Equals("Dummy", StringComparison.OrdinalIgnoreCase) &&
                                    !name.StartsWith("Touch_", StringComparison.OrdinalIgnoreCase));
            if (loopAnimation == null)
            {
                return;
            }

            SetAnimationCompat(animationState, loopAnimation, loop: true);
            ModLog.Info($"已启动 {groupId} 的先古 Spine 循环动画：{loopAnimation}");
        }
        catch (Exception exception)
        {
            ModLog.Warn($"启动 {groupId} 的先古 Spine 动画失败：{exception.Message}");
        }
    }

    private static void SetAnimationCompat(
        MegaAnimationState animationState,
        string animationName,
        bool loop)
    {
        // SetAnimation 在 0.107.1 返回 MegaTrackEntry，到 0.111.0 改为 void。
        // 两版底层 Spine 方法签名不变，直接调用它可避免发布 DLL 绑定某一版。
        using var result = animationState.BoundObject.Call(
            "set_animation", animationName, loop, 0);
    }

    private static void AddAnimationCompat(
        MegaAnimationState animationState,
        string animationName,
        float delay,
        bool loop)
    {
        // AddAnimation 也发生了相同的返回类型变化。
        using var result = animationState.BoundObject.Call(
            "add_animation", animationName, delay, loop, 0);
    }

    private static string? FindAnimation(
        IReadOnlyList<string> animationNames,
        string expectedName) =>
        animationNames.FirstOrDefault(name =>
            name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
}

internal static class AncientRuntimeAppearance
{
    private static readonly System.Reflection.FieldInfo AncientEventField =
        AccessTools.Field(typeof(NAncientEventLayout), "_ancientEvent");

    internal static bool TryGetCurrent(
        out AncientEventModel ancient,
        out NAncientEventLayout layout,
        out SkinGroup group)
    {
        ancient = null!;
        layout = null!;
        group = null!;
        if (NEventRoom.Instance?.Layout is not NAncientEventLayout currentLayout ||
            !TryGet(currentLayout, out var currentAncient, out var currentGroup))
        {
            return false;
        }

        ancient = currentAncient;
        layout = currentLayout;
        group = currentGroup;
        return true;
    }

    internal static bool TryGet(
        NAncientEventLayout layout,
        out AncientEventModel ancient,
        out SkinGroup group)
    {
        ancient = null!;
        group = null!;
        if (AncientEventField.GetValue(layout) is not AncientEventModel currentAncient)
        {
            return false;
        }

        var currentGroup = AncientCompendiumEntry.FindGroup(currentAncient.Id.Entry);
        if (currentGroup == null)
        {
            return false;
        }

        ancient = currentAncient;
        group = currentGroup;
        return true;
    }

    internal static Control? GetBackgroundTarget(NAncientEventLayout layout) =>
        layout.GetNodeOrNull<Control>("%AncientBgContainer");

    internal static bool TryRefresh(string groupId, out string? error)
    {
        error = null;
        if (!TryGetCurrent(out var ancient, out var layout, out var group) ||
            !group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var container = layout.GetNodeOrNull<Node>("%AncientBgContainer");
        if (container == null)
        {
            error = "Ancient background container is unavailable";
            return false;
        }

        var oldRoots = container.GetChildren().Cast<Node>().ToArray();
        Node? newRoot = null;
        try
        {
            newRoot = ancient.CreateBackgroundScene()
                .Instantiate<Control>(PackedScene.GenEditState.Disabled);
            if (oldRoots.Length > 0)
            {
                newRoot.Name = oldRoots[0].Name;
            }

            foreach (var oldRoot in oldRoots)
            {
                container.RemoveChild(oldRoot);
            }

            container.AddChild(newRoot);
            ManagedAncientStaticBackground.Fit(newRoot);
            ManagedAncientLayeredImage.TryApply(group.Id, newRoot);
            ManagedAncientSceneAnimation.TryStart(group.Id, newRoot);
            foreach (var oldRoot in oldRoots)
            {
                oldRoot.QueueFree();
            }

            return true;
        }
        catch (Exception exception)
        {
            if (newRoot != null && GodotObject.IsInstanceValid(newRoot))
            {
                newRoot.GetParent()?.RemoveChild(newRoot);
                newRoot.QueueFree();
            }

            foreach (var oldRoot in oldRoots.Where(GodotObject.IsInstanceValid))
            {
                if (oldRoot.GetParent() == null)
                {
                    container.AddChild(oldRoot);
                }
            }

            error = exception.GetBaseException().Message;
            ModLog.Error("热重载当前先古场景失败：" + exception);
            return false;
        }
    }
}

internal static class ManagedAncientStaticBackground
{
    private const string ManagedRootMeta = "sts2_skin_changer_static_ancient_background";

    internal static void Mark(Control root) => root.SetMeta(ManagedRootMeta, true);

    internal static void Fit(Node sceneRoot)
    {
        if (sceneRoot is not Control root ||
            !root.HasMeta(ManagedRootMeta) ||
            !root.GetMeta(ManagedRootMeta).AsBool())
        {
            return;
        }

        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        if (root.GetParent() is not NAncientBgContainer container)
        {
            root.OffsetLeft = 0f;
            root.OffsetTop = 0f;
            root.OffsetRight = 0f;
            root.OffsetBottom = 0f;
            return;
        }

        // NAncientBgContainer deliberately scales and shifts the game's authored 2560x1200
        // scenes for each window ratio. A generated full-rect image would be scaled a second
        // time and expose the black room background. Expand it through the inverse container
        // transform so its visible result still covers the complete event layout.
        var scale = container.Scale;
        if (Mathf.IsZeroApprox(scale.X) || Mathf.IsZeroApprox(scale.Y))
        {
            return;
        }

        var pivot = container.PivotOffset;
        var position = container.Position;
        var size = container.Size;
        var topLeft = new Vector2(
            pivot.X + (-position.X - pivot.X) / scale.X,
            pivot.Y + (-position.Y - pivot.Y) / scale.Y);
        var bottomRight = new Vector2(
            pivot.X + (size.X - position.X - pivot.X) / scale.X,
            pivot.Y + (size.Y - position.Y - pivot.Y) / scale.Y);

        root.OffsetLeft = topLeft.X;
        root.OffsetTop = topLeft.Y;
        root.OffsetRight = bottomRight.X - size.X;
        root.OffsetBottom = bottomRight.Y - size.Y;
    }

    internal static void FitChildren(NAncientBgContainer container)
    {
        foreach (Node child in container.GetChildren())
        {
            Fit(child);
        }
    }
}

[HarmonyPatch(typeof(NAncientEventLayout), "InitializeVisuals")]
internal static class ManagedAncientSceneAnimationPatch
{
    private static void Postfix(NAncientEventLayout __instance)
    {
        try
        {
            if (!AncientRuntimeAppearance.TryGet(__instance, out _, out var group))
            {
                return;
            }

            var container = __instance.GetNodeOrNull<Node>("%AncientBgContainer");
            var sceneRoot = container?.GetChildCount() > 0 ? container.GetChild(0) : null;
            if (sceneRoot != null)
            {
                ManagedAncientStaticBackground.Fit(sceneRoot);
                ManagedAncientLayeredImage.TryApply(group.Id, sceneRoot);
                ManagedAncientSceneAnimation.TryStart(group.Id, sceneRoot);
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("在游戏内启动先古 Spine 动画失败：" + exception.Message);
        }
    }
}

[HarmonyPatch(typeof(NAncientBgContainer), "OnWindowChange")]
internal static class ManagedAncientStaticBackgroundWindowPatch
{
    private static void Postfix(NAncientBgContainer __instance) =>
        ManagedAncientStaticBackground.FitChildren(__instance);
}

[HarmonyPatch(typeof(NCompendiumSubmenu), nameof(NCompendiumSubmenu._Ready))]
internal static class AncientCompendiumEntryPatch
{
    private static void Postfix(NCompendiumSubmenu __instance) => AncientCompendiumEntry.Attach(__instance);
}

[HarmonyPatch(typeof(EventModel), nameof(EventModel.CreateBackgroundScene))]
internal static class AncientSceneResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(EventModel __instance, ref PackedScene __result)
    {
        if (__instance is not AncientEventModel ancient)
        {
            return;
        }

        var group = AncientCompendiumEntry.FindGroup(ancient.Id.Entry);
        if (group == null)
        {
            return;
        }

        try
        {
            if (SkinService.IsExternalRuntimeProviderSelected(group.Id))
            {
                __result = AncientCompendiumScreen.BuildSelectedRuntimeImageScene(group.Id);
                return;
            }

            var scenePath = AncientCompendiumEntry.GetScenePath(ancient);
            var scene = SkinService.GetOrLoadRuntimeScene(group.Id, scenePath);
            __result = scene;
        }
        catch (Exception exception)
        {
            ModLog.Error($"最终应用 {ancient.Id.Entry} 的先古皮肤失败：{exception}");
        }
    }
}

[HarmonyPatch(typeof(AncientEventModel), nameof(AncientEventModel.MapIcon), MethodType.Getter)]
internal static class AncientMapIconResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(AncientEventModel __instance, ref Texture2D __result)
    {
        var id = __instance.Id.Entry.ToLowerInvariant();
        var path = ImageHelper.GetImagePath("packed/map/ancients/ancient_node_" + id + ".png");
        AncientCompendiumEntry.ReplaceAncientIcon(__instance, path, ref __result);
    }
}

[HarmonyPatch(typeof(AncientEventModel), nameof(AncientEventModel.MapIconOutline), MethodType.Getter)]
internal static class AncientMapIconOutlineResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(AncientEventModel __instance, ref Texture2D __result)
    {
        var id = __instance.Id.Entry.ToLowerInvariant();
        var path = ImageHelper.GetImagePath("packed/map/ancients/ancient_node_" + id + "_outline.png");
        AncientCompendiumEntry.ReplaceAncientIcon(__instance, path, ref __result);
    }
}

[HarmonyPatch(typeof(AncientEventModel), nameof(AncientEventModel.RunHistoryIcon), MethodType.Getter)]
internal static class AncientRunHistoryIconResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(AncientEventModel __instance, ref Texture2D __result)
    {
        var id = __instance.Id.Entry.ToLowerInvariant();
        var path = ImageHelper.GetImagePath("ui/run_history/" + id + ".png");
        AncientCompendiumEntry.ReplaceAncientIcon(__instance, path, ref __result);
    }
}

[HarmonyPatch(typeof(AncientEventModel), nameof(AncientEventModel.RunHistoryIconOutline), MethodType.Getter)]
internal static class AncientRunHistoryIconOutlineResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(AncientEventModel __instance, ref Texture2D __result)
    {
        var id = __instance.Id.Entry.ToLowerInvariant();
        var path = ImageHelper.GetImagePath("ui/run_history/" + id + "_outline.png");
        AncientCompendiumEntry.ReplaceAncientIcon(__instance, path, ref __result);
    }
}
