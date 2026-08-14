using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Bestiary;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static partial class ContextualSkinControls
{
    private const string SelectorName = "STS2SkinSelector";
    private const string DropdownName = "SkinDropdown";
    private const string GroupMeta = "sts2_skin_group";
    private const string UpdatingMeta = "sts2_skin_updating";
    private static readonly Dictionary<ulong, Action> RefreshActions = [];
    private static readonly System.Reflection.FieldInfo BestiarySelectedEntryField =
        AccessTools.Field(typeof(NBestiary), "_selectedEntry");
    private static readonly System.Reflection.MethodInfo BestiarySelectMonsterMethod =
        AccessTools.Method(typeof(NBestiary), "SelectMonster", [typeof(NBestiaryEntry)]);

    public static void ShowCharacter(NCharacterSelectScreen screen, CharacterModel character)
    {
        var selector = EnsureCharacterSelector(screen);
        var group = FindGroup(character.Id.Entry);
        RegisterRefresh(selector, group == null ? null : () => RebuildCharacterDisplay(screen, character, group.Id));
        Populate(selector, group);
        if (group != null)
        {
            RunRefresh(() => RebuildCharacterDisplay(screen, character, group.Id));
        }
    }

    public static void ShowMonster(NBestiary screen, NBestiaryEntry entry)
    {
        var selector = EnsureMonsterSelector(screen);
        var monster = entry.IsDiscovered ? entry.Entry.monsterModel : null;
        var group = monster == null ? null : FindGroup(monster.Id.Entry);
        RegisterRefresh(
            selector,
            group == null || monster == null ? null : () => RebuildMonsterDisplay(screen, entry, monster, group.Id));
        Populate(selector, group);
    }

    private static HBoxContainer EnsureCharacterSelector(NCharacterSelectScreen screen)
    {
        var infoPanel = screen.GetNode<Control>("InfoPanel");
        var existing = infoPanel.GetNodeOrNull<HBoxContainer>(SelectorName);
        if (existing != null)
        {
            return existing;
        }

        var selector = BuildSelector();
        selector.AnchorLeft = 0.5f;
        selector.AnchorTop = 0;
        selector.AnchorRight = 0.5f;
        selector.AnchorBottom = 0;
        selector.OffsetLeft = -122;
        selector.OffsetTop = -80;
        selector.OffsetRight = 122;
        selector.OffsetBottom = -36;
        infoPanel.AddChild(selector);
        return selector;
    }

    private static HBoxContainer EnsureMonsterSelector(NBestiary screen)
    {
        var existing = screen.GetNodeOrNull<HBoxContainer>(SelectorName);
        if (existing != null)
        {
            return existing;
        }

        var selector = BuildSelector();
        selector.AnchorLeft = 0.5f;
        selector.AnchorRight = 0.5f;
        selector.OffsetLeft = -122;
        selector.OffsetTop = 168;
        selector.OffsetRight = 122;
        selector.OffsetBottom = 212;
        screen.AddChild(selector);
        return selector;
    }

    private static HBoxContainer BuildSelector()
    {
        var selector = new HBoxContainer
        {
            Name = SelectorName,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 50
        };
        var dropdown = new OptionButton
        {
            Name = DropdownName,
            CustomMinimumSize = new Vector2(244, 44),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FitToLongestItem = false,
            ClipText = true,
            Alignment = HorizontalAlignment.Center
        };
        ApplyGameTheme(dropdown);
        dropdown.ItemSelected += index => ApplyDropdownSelection(selector, dropdown, checked((int)index));
        selector.AddChild(dropdown);
        selector.TreeExited += () => RefreshActions.Remove(selector.GetInstanceId());
        return selector;
    }

    internal static void ApplyGameTheme(OptionButton dropdown)
    {
        var font = ResourceLoader.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres");
        var ivory = new Color("fff6e2");
        var gold = new Color("efc850");
        dropdown.AddThemeColorOverride("font_color", ivory);
        dropdown.AddThemeColorOverride("font_hover_color", Colors.White);
        dropdown.AddThemeColorOverride("font_pressed_color", gold);
        dropdown.AddThemeColorOverride("font_focus_color", Colors.White);
        dropdown.AddThemeFontSizeOverride("font_size", 23);
        if (font != null)
        {
            dropdown.AddThemeFontOverride("font", font);
        }

        dropdown.AddThemeStyleboxOverride("normal", CreateStyleBox(new Color("3c5f82"), new Color("7394ad")));
        dropdown.AddThemeStyleboxOverride("hover", CreateStyleBox(new Color("4b7392"), new Color("afcdde")));
        dropdown.AddThemeStyleboxOverride("pressed", CreateStyleBox(new Color("45104e"), gold));
        dropdown.AddThemeStyleboxOverride("focus", CreateStyleBox(new Color("3c5f82"), gold, 2));
        dropdown.AddThemeStyleboxOverride("disabled", CreateStyleBox(new Color("293b4c"), new Color("50606b")));

        var popup = dropdown.GetPopup();
        popup.AddThemeColorOverride("font_color", ivory);
        popup.AddThemeColorOverride("font_hover_color", Colors.White);
        popup.AddThemeColorOverride("font_separator_color", gold);
        popup.AddThemeFontSizeOverride("font_size", 22);
        popup.AddThemeStyleboxOverride("panel", CreateStyleBox(new Color("45104e"), new Color("79547e"), 2));
        popup.AddThemeStyleboxOverride("hover", CreateStyleBox(new Color("2c586f"), new Color("afcdde")));
        if (font != null)
        {
            popup.AddThemeFontOverride("font", font);
        }
    }

    internal static bool ShouldIgnoreBackgroundMute(NMuteInBackgroundHandler handler, int notification)
    {
        const int ApplicationFocusOut = 1005;
        return notification == ApplicationFocusOut && handler.GetWindow().HasFocus();
    }

    internal static StyleBoxFlat CreateStyleBox(Color background, Color border, int borderWidth = 1)
    {
        return new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = 12,
            CornerRadiusTopRight = 12,
            CornerRadiusBottomRight = 12,
            CornerRadiusBottomLeft = 12,
            ContentMarginLeft = 12,
            ContentMarginRight = 12
        };
    }

    private static void Populate(HBoxContainer selector, SkinGroup? group)
    {
        var dropdown = selector.GetNode<OptionButton>(DropdownName);
        if (group == null || group.Options.Count == 0)
        {
            selector.Visible = false;
            dropdown.Clear();
            return;
        }

        selector.SetMeta(UpdatingMeta, true);
        selector.SetMeta(GroupMeta, group.Id);
        dropdown.Clear();
        dropdown.AddItem("游戏默认");
        dropdown.SetItemMetadata(0, SkinCatalog.BaseOptionId);
        foreach (var option in group.Options)
        {
            var index = dropdown.ItemCount;
            dropdown.AddItem(option.Name);
            dropdown.SetItemMetadata(index, option.Id);
        }

        var selected = SkinService.Config.GetSelection(group.Id);
        var selectedIndex = Enumerable.Range(0, dropdown.ItemCount)
            .FirstOrDefault(index => dropdown.GetItemMetadata(index).AsString() == selected);
        dropdown.Select(selectedIndex);
        selector.SetMeta(UpdatingMeta, false);
        selector.Visible = true;
    }

    private static void ApplyDropdownSelection(HBoxContainer selector, OptionButton dropdown, int index)
    {
        if (selector.GetMeta(UpdatingMeta, false).AsBool())
        {
            return;
        }

        var groupId = selector.GetMeta(GroupMeta, string.Empty).AsString();
        var optionId = dropdown.GetItemMetadata(index).AsString();
        if (!SkinService.ApplySelection(groupId, optionId))
        {
            ModLog.Error($"界面切换失败：{SkinService.LastError}");
            var current = SkinService.Config.GetSelection(groupId);
            var currentIndex = Enumerable.Range(0, dropdown.ItemCount)
                .FirstOrDefault(item => dropdown.GetItemMetadata(item).AsString() == current);
            dropdown.Select(currentIndex);
            return;
        }

        if (RefreshActions.TryGetValue(selector.GetInstanceId(), out var refresh))
        {
            Callable.From(() => RunRefresh(refresh)).CallDeferred();
        }
    }

    private static void RunRefresh(Action refresh)
    {
        try
        {
            refresh();
        }
        catch (Exception exception)
        {
            ModLog.Error("重建皮肤展示失败：" + exception);
        }
    }

    private static void RegisterRefresh(HBoxContainer selector, Action? refresh)
    {
        var id = selector.GetInstanceId();
        if (refresh == null)
        {
            RefreshActions.Remove(id);
        }
        else
        {
            RefreshActions[id] = refresh;
        }
    }

    private static void RebuildCharacterDisplay(NCharacterSelectScreen screen, CharacterModel character, string groupId)
    {
        var scenePaths = new[]
        {
            character.CharacterSelectBg,
            SceneHelper.GetScenePath("creature_visuals/" + character.Id.Entry.ToLowerInvariant()),
            character.RestSiteAnimPath,
            character.MerchantAnimPath
        };
        var characterSelectTextures = character.AssetPathsCharacterSelect
            .Where(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var resourcePaths = scenePaths
            .Concat(characterSelectTextures)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var resources = SkinService.LoadRuntimeResources(groupId, resourcePaths);
        foreach (var pair in resources)
        {
            PreloadManager.Cache.SetAsset(pair.Key, pair.Value);
        }

        var scene = resources[character.CharacterSelectBg] as PackedScene ??
                    throw new InvalidOperationException($"角色选角资源不是场景：{character.CharacterSelectBg}");
        var container = screen.GetNode<Control>("AnimatedBg");
        foreach (var child in container.GetChildren())
        {
            container.RemoveChild(child);
            child.QueueFree();
        }

        var background = scene.Instantiate<Control>(PackedScene.GenEditState.Disabled);
        background.Name = character.Id.Entry + "_bg";
        container.AddChild(background);
        RefreshCharacterButtonIcon(screen, character, characterSelectTextures, resources);
        ModLog.Info($"已完整重建 {character.Id.Entry} 的选角展示。");
    }

    private static void RefreshCharacterButtonIcon(
        NCharacterSelectScreen screen,
        CharacterModel character,
        IReadOnlyCollection<string> texturePaths,
        IReadOnlyDictionary<string, Resource> resources)
    {
        var button = FindDescendant<NCharacterSelectButton>(screen, candidate =>
            candidate.Character?.Id.Entry.Equals(character.Id.Entry, StringComparison.OrdinalIgnoreCase) == true);
        if (button == null)
        {
            return;
        }

        var iconPath = texturePaths.FirstOrDefault(path =>
            path.Contains("/packed/character_select/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(button.IsLocked ? "_locked.png" : $"_{character.Id.Entry.ToLowerInvariant()}.png",
                StringComparison.OrdinalIgnoreCase));
        if (iconPath == null || !resources.TryGetValue(iconPath, out var resource) || resource is not Texture2D texture)
        {
            return;
        }

        button.GetNode<TextureRect>("%Icon").Texture = texture;
        ModLog.Info($"已刷新 {character.Id.Entry} 的角色列表头像。");
    }

    private static T? FindDescendant<T>(Node root, Func<T, bool> predicate) where T : Node
    {
        foreach (var child in root.GetChildren())
        {
            if (child is T match && predicate(match))
            {
                return match;
            }

            var descendant = FindDescendant(child, predicate);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static void RebuildMonsterDisplay(
        NBestiary screen,
        NBestiaryEntry entry,
        MonsterModel monster,
        string groupId)
    {
        var visualsPath = SceneHelper.GetScenePath("creature_visuals/" + monster.Id.Entry.ToLowerInvariant());
        var scene = SkinService.LoadRuntimeScene(groupId, visualsPath);
        PreloadManager.Cache.SetAsset(visualsPath, scene);

        BestiarySelectedEntryField.SetValue(screen, null);
        BestiarySelectMonsterMethod.Invoke(screen, [entry]);
        ModLog.Info($"已完整重建 {monster.Id.Entry} 的图鉴展示。");
    }

    private static SkinGroup? FindGroup(string modelId)
    {
        var token = NormalizeToken(modelId);
        return SkinService.Catalog?.Groups.FirstOrDefault(group => NormalizeToken(group.Id) == token);
    }

    private static string NormalizeToken(string value) => NonAlphanumericRegex().Replace(value, string.Empty).ToLowerInvariant();

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NonAlphanumericRegex();

}

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.SelectCharacter))]
internal static class CharacterSelectionSkinPatch
{
    private static void Postfix(
        NCharacterSelectScreen __instance,
        CharacterModel characterModel) =>
        ContextualSkinControls.ShowCharacter(__instance, characterModel);
}

[HarmonyPatch(typeof(NBestiary), "SelectMonster")]
internal static class BestiarySelectionSkinPatch
{
    private static void Postfix(NBestiary __instance, NBestiaryEntry entry) =>
        ContextualSkinControls.ShowMonster(__instance, entry);
}

[HarmonyPatch(typeof(NMuteInBackgroundHandler), nameof(NMuteInBackgroundHandler._Notification))]
internal static class SkinPopupBackgroundMutePatch
{
    private static bool Prefix(NMuteInBackgroundHandler __instance, int what) =>
        !ContextualSkinControls.ShouldIgnoreBackgroundMute(__instance, what);
}
