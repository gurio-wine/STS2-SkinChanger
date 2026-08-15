using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class AncientCompendiumEntry
{
    private const string ButtonName = "STS2AncientCompendiumButton";
    private const string ScreenName = "STS2AncientCompendium";
    private static readonly System.Reflection.FieldInfo StackField = AccessTools.Field(typeof(NSubmenu), "_stack");

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
            ModLog.Error("图鉴底部缺少按钮行节点，远古图鉴入口未添加。");
            return;
        }

        bottomRow.AddChild(button);
        var statistics = compendium.GetNode<NCompendiumBottomButton>("%StatisticsButton");
        var runHistory = compendium.GetNode<NCompendiumBottomButton>("%RunHistoryButton");
        bottomRow.MoveChild(button, statistics.GetIndex() + 1);

        button.GetNode<MegaLabel>("Label").SetTextAutoSize("远古图鉴");
        var icon = button.GetNode<TextureRect>("Icon");
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

        button.Connect(
            NClickableControl.SignalName.Released,
            Callable.From((Action<NButton>)(_ => Open(compendium))));

        var bestiary = compendium.GetNode<NShortSubmenuButton>("%BestiaryButton");
        statistics.FocusNeighborRight = button.GetPath();
        button.FocusNeighborLeft = statistics.GetPath();
        button.FocusNeighborRight = runHistory.GetPath();
        button.FocusNeighborTop = bestiary.GetPath();
        button.FocusNeighborBottom = button.GetPath();
        runHistory.FocusNeighborLeft = button.GetPath();
        bestiary.FocusNeighborBottom = button.GetPath();
    }

    private static void Open(NCompendiumSubmenu compendium)
    {
        if (StackField.GetValue(compendium) is not NSubmenuStack stack)
        {
            ModLog.Error("无法取得图鉴菜单栈，远古图鉴未打开。");
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

    internal static AncientEventModel[] GetAncients() => ModelDb.All
        .OfType<AncientEventModel>()
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
            if (SkinService.IsExternalRuntimeProviderSelected(group.Id))
            {
                result = SkinService.GetSelectedRuntimeImageTexture(group.Id);
                return;
            }

            result = SkinService.GetOrLoadRuntimeResource(group.Id, resourcePath) as Texture2D ??
                     throw new InvalidOperationException($"独立远古皮肤资源不是贴图：{resourcePath}");
        }
        catch (Exception exception)
        {
            ModLog.Error($"最终接管远古头像 {resourcePath} 失败：{exception}");
        }
    }

    internal static void RefreshCompendiumEntryIcon(Node context)
    {
        var button = context.GetTree().Root.FindChild(ButtonName, recursive: true, owned: false)
            as NCompendiumBottomButton;
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
    private HBoxContainer _skinSelector = null!;
    private OptionButton _skinDropdown = null!;
    private SubViewport _previewViewport = null!;
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
        RefreshAncients();
    }

    private void BuildUi()
    {
        var previewContainer = new SubViewportContainer
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
        AddChild(previewContainer);

        _previewViewport = new SubViewport
        {
            Size = new Vector2I(1920, 1080),
            TransparentBg = false,
            GuiDisableInput = true,
            // 屏幕被弹出后仍常驻场景树，改为可见时渲染避免整个会话持续渲染 1080p 离屏画面。
            RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible
        };
        previewContainer.AddChild(_previewViewport);

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

        var heading = BuildLabel(34, new Color("efc850"));
        heading.Text = "远古图鉴";
        heading.CustomMinimumSize = new Vector2(0, 54);
        sidebarContent.AddChild(heading);

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
            _nameLabel.Text = "没有可预览的远古者";
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

        _skinDropdown.AddItem("游戏默认");
        _skinDropdown.SetItemMetadata(0, SkinCatalog.BaseOptionId);
        foreach (var option in group.Options)
        {
            var index = _skinDropdown.ItemCount;
            _skinDropdown.AddItem(option.Name);
            _skinDropdown.SetItemMetadata(index, option.Id);
        }

        var current = SkinService.Config.GetSelection(group.Id);
        var selectedIndex = Enumerable.Range(0, _skinDropdown.ItemCount)
            .FirstOrDefault(index => _skinDropdown.GetItemMetadata(index).AsString() == current);
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
            ModLog.Error($"远古皮肤切换失败：{SkinService.LastError}");
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
                        ?? throw new InvalidOperationException($"无法加载远古场景：{scenePath}");
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
            ModLog.Info($"远古图鉴已展示 {ancient.Id.Entry}。");
        }
        catch (Exception exception)
        {
            ModLog.Error($"远古图鉴预览 {ancient.Id.Entry} 失败：{exception}");
        }
    }

    internal static PackedScene BuildSelectedRuntimeImageScene(string groupId)
    {
        var root = new Control { Name = "RuntimeAncientBackground" };
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
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
            throw new InvalidOperationException($"无法创建远古图片场景：{error}");
        }

        return scene;
    }

    private void ClearPreview()
    {
        foreach (var child in _previewViewport.GetChildren())
        {
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
        var font = ResourceLoader.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres");
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
        var font = ResourceLoader.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres");
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
            ModLog.Error($"最终应用 {ancient.Id.Entry} 的远古皮肤失败：{exception}");
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
