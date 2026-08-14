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
    private const string RailName = "STS2AncientCompendiumRail";
    private const string ScreenName = "STS2AncientCompendium";
    private static readonly System.Reflection.FieldInfo StackField = AccessTools.Field(typeof(NSubmenu), "_stack");

    public static void Attach(NCompendiumSubmenu compendium)
    {
        if (compendium.GetNodeOrNull<Control>(RailName) != null)
        {
            return;
        }

        var rail = new Control
        {
            Name = RailName,
            AnchorLeft = 1,
            AnchorTop = 0.5f,
            AnchorRight = 1,
            AnchorBottom = 0.5f,
            OffsetLeft = -190,
            OffsetTop = -300,
            OffsetRight = 0,
            OffsetBottom = -160,
            MouseFilter = Control.MouseFilterEnum.Pass,
            ZIndex = 5
        };
        compendium.AddChild(rail);

        var scenePath = SceneHelper.GetScenePath("screens/main_menu/compendium_bottom_button");
        var button = PreloadManager.Cache.GetScene(scenePath)
            .Instantiate<NCompendiumBottomButton>(PackedScene.GenEditState.Disabled);
        button.Name = "AncientCompendiumButton";
        button.Position = new Vector2(8, 4);
        button.Scale = Vector2.One * 0.62f;
        button.PivotOffset = Vector2.Zero;
        button.FocusMode = Control.FocusModeEnum.All;
        rail.AddChild(button);

        button.GetNode<MegaLabel>("Label").SetTextAutoSize("远古图鉴");
        var icon = button.GetNode<TextureRect>("Icon");
        var firstAncient = GetAncients().FirstOrDefault();
        if (firstAncient != null)
        {
            icon.Texture = firstAncient.MapIcon;
        }

        button.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NButton>(_ => Open(compendium)));

        var bestiary = compendium.GetNode<NShortSubmenuButton>("%BestiaryButton");
        bestiary.FocusNeighborRight = button.GetPath();
        button.FocusNeighborLeft = bestiary.GetPath();
        button.FocusNeighborRight = button.GetPath();
        button.FocusNeighborTop = button.GetPath();
        button.FocusNeighborBottom = button.GetPath();
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
        SceneHelper.GetScenePath("events/background_scenes/" + ancient.Id.Entry.ToLowerInvariant());

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
        var shade = new ColorRect
        {
            Color = new Color("101722e6"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(shade);

        var frame = new Panel
        {
            AnchorLeft = 0,
            AnchorTop = 0,
            AnchorRight = 0,
            AnchorBottom = 0,
            OffsetLeft = 108,
            OffsetTop = 198,
            OffsetRight = 1412,
            OffsetBottom = 942,
            MouseFilter = MouseFilterEnum.Ignore
        };
        frame.AddThemeStyleboxOverride(
            "panel",
            ContextualSkinControls.CreateStyleBox(new Color("172535"), new Color("7394ad"), 3));
        AddChild(frame);

        var previewContainer = new SubViewportContainer
        {
            AnchorLeft = 0,
            AnchorTop = 0,
            AnchorRight = 0,
            AnchorBottom = 0,
            OffsetLeft = 120,
            OffsetTop = 210,
            OffsetRight = 1400,
            OffsetBottom = 930,
            Stretch = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(previewContainer);

        _previewViewport = new SubViewport
        {
            Size = new Vector2I(1920, 1080),
            TransparentBg = false,
            GuiDisableInput = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always
        };
        previewContainer.AddChild(_previewViewport);

        _nameLabel = BuildLabel(48, new Color("efc850"));
        _nameLabel.AnchorLeft = 0;
        _nameLabel.AnchorRight = 0;
        _nameLabel.OffsetLeft = 120;
        _nameLabel.OffsetTop = 64;
        _nameLabel.OffsetRight = 1400;
        _nameLabel.OffsetBottom = 122;
        AddChild(_nameLabel);

        _epithetLabel = BuildLabel(24, new Color("87ceeB"));
        _epithetLabel.AnchorLeft = 0;
        _epithetLabel.AnchorRight = 0;
        _epithetLabel.OffsetLeft = 120;
        _epithetLabel.OffsetTop = 124;
        _epithetLabel.OffsetRight = 1400;
        _epithetLabel.OffsetBottom = 158;
        AddChild(_epithetLabel);

        _skinSelector = new HBoxContainer
        {
            AnchorLeft = 0,
            AnchorTop = 0,
            AnchorRight = 0,
            AnchorBottom = 0,
            OffsetLeft = 638,
            OffsetTop = 158,
            OffsetRight = 882,
            OffsetBottom = 202,
            Visible = false,
            ZIndex = 10
        };
        AddChild(_skinSelector);

        _skinDropdown = new OptionButton
        {
            CustomMinimumSize = new Vector2(244, 44),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FitToLongestItem = false,
            ClipText = true,
            Alignment = HorizontalAlignment.Center
        };
        ContextualSkinControls.ApplyGameTheme(_skinDropdown);
        _skinDropdown.ItemSelected += index => OnSkinSelected(checked((int)index));
        _skinSelector.AddChild(_skinDropdown);

        var sidebar = new PanelContainer
        {
            AnchorLeft = 1,
            AnchorTop = 0,
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetLeft = -400,
            OffsetTop = 0,
            OffsetRight = 0,
            OffsetBottom = 0
        };
        sidebar.AddThemeStyleboxOverride(
            "panel",
            ContextualSkinControls.CreateStyleBox(new Color("182638e8"), new Color("3c5f82"), 2));
        AddChild(sidebar);

        var sidebarMargin = new MarginContainer();
        sidebarMargin.AddThemeConstantOverride("margin_left", 46);
        sidebarMargin.AddThemeConstantOverride("margin_top", 58);
        sidebarMargin.AddThemeConstantOverride("margin_right", 46);
        sidebarMargin.AddThemeConstantOverride("margin_bottom", 90);
        sidebar.AddChild(sidebarMargin);

        var sidebarContent = new VBoxContainer();
        sidebarContent.AddThemeConstantOverride("separation", 22);
        sidebarMargin.AddChild(sidebarContent);

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
            CustomMinimumSize = new Vector2(292, 0),
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
                CustomMinimumSize = new Vector2(292, 58),
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

        PopulateSkinDropdown(FindGroup(ancient.Id.Entry));
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
            PopulateSkinDropdown(FindGroup(_selectedAncient.Id.Entry));
            return;
        }

        Callable.From(() => RebuildPreview(_selectedAncient)).CallDeferred();
    }

    private void RebuildPreview(AncientEventModel ancient)
    {
        try
        {
            ClearPreview();
            var scenePath = AncientCompendiumEntry.GetScenePath(ancient);
            var group = FindGroup(ancient.Id.Entry);
            PackedScene scene;
            if (group != null)
            {
                scene = SkinService.LoadRuntimeScene(group.Id, scenePath);
                PreloadManager.Cache.SetAsset(scenePath, scene);
            }
            else
            {
                scene = ResourceLoader.Load<PackedScene>(scenePath, null, ResourceLoader.CacheMode.IgnoreDeep)
                        ?? throw new InvalidOperationException($"无法加载远古场景：{scenePath}");
            }

            var preview = scene.Instantiate<Control>(PackedScene.GenEditState.Disabled);
            preview.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _previewViewport.AddChild(preview);
            ModLog.Info($"远古图鉴已展示 {ancient.Id.Entry}。");
        }
        catch (Exception exception)
        {
            ModLog.Error($"远古图鉴预览 {ancient.Id.Entry} 失败：{exception}");
        }
    }

    private void ClearPreview()
    {
        foreach (var child in _previewViewport.GetChildren())
        {
            _previewViewport.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static SkinGroup? FindGroup(string modelId)
    {
        var token = NormalizeToken(modelId);
        return SkinService.Catalog?.Groups.FirstOrDefault(group => NormalizeToken(group.Id) == token);
    }

    private static string NormalizeToken(string value) =>
        NonAlphanumericRegex().Replace(value, string.Empty).ToLowerInvariant();

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
                selected ? new Color("45104e") : new Color("2a465f"),
                selected ? gold : new Color("507690"),
                selected ? 3 : 1));
        button.AddThemeStyleboxOverride(
            "hover",
            ContextualSkinControls.CreateStyleBox(new Color("3c627e"), new Color("afcdde"), 2));
        button.AddThemeStyleboxOverride(
            "pressed",
            ContextualSkinControls.CreateStyleBox(new Color("45104e"), gold, 2));
        button.AddThemeStyleboxOverride(
            "focus",
            ContextualSkinControls.CreateStyleBox(new Color("2a465f"), gold, 3));
    }

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NonAlphanumericRegex();
}

[HarmonyPatch(typeof(NCompendiumSubmenu), nameof(NCompendiumSubmenu._Ready))]
internal static class AncientCompendiumEntryPatch
{
    private static void Postfix(NCompendiumSubmenu __instance) => AncientCompendiumEntry.Attach(__instance);
}
