using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
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
        RegisterRefresh(selector, group == null ? null : () => RebuildCharacterDisplay(screen, character));
        Populate(selector, group);
    }

    public static void ShowMonster(NBestiary screen, NBestiaryEntry entry)
    {
        var selector = EnsureMonsterSelector(screen);
        var monster = entry.IsDiscovered ? entry.Entry.monsterModel : null;
        var group = monster == null ? null : FindGroup(monster.Id.Entry);
        RegisterRefresh(
            selector,
            group == null || monster == null ? null : () => RebuildMonsterDisplay(screen, entry, monster));
        Populate(selector, group);
    }

    private static HBoxContainer EnsureCharacterSelector(NCharacterSelectScreen screen)
    {
        var existing = screen.GetNodeOrNull<HBoxContainer>(SelectorName);
        if (existing != null)
        {
            return existing;
        }

        var selector = BuildSelector();
        selector.AnchorLeft = 0.5f;
        selector.AnchorTop = 0.5f;
        selector.AnchorRight = 0.5f;
        selector.AnchorBottom = 0.5f;
        selector.OffsetLeft = -677;
        selector.OffsetTop = 250;
        selector.OffsetRight = -115;
        selector.OffsetBottom = 302;
        screen.AddChild(selector);
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
        selector.OffsetLeft = -285;
        selector.OffsetTop = 168;
        selector.OffsetRight = 285;
        selector.OffsetBottom = 220;
        screen.AddChild(selector);
        return selector;
    }

    private static HBoxContainer BuildSelector()
    {
        var selector = new HBoxContainer
        {
            Name = SelectorName,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        selector.AddThemeConstantOverride("separation", 12);

        selector.AddChild(new Label
        {
            Text = "皮肤",
            CustomMinimumSize = new Vector2(82, 48),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        });

        var dropdown = new OptionButton
        {
            Name = DropdownName,
            CustomMinimumSize = new Vector2(390, 48),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FitToLongestItem = false
        };
        dropdown.ItemSelected += index => ApplyDropdownSelection(selector, dropdown, checked((int)index));
        selector.AddChild(dropdown);
        selector.TreeExited += () => RefreshActions.Remove(selector.GetInstanceId());
        return selector;
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

    private static void RebuildCharacterDisplay(NCharacterSelectScreen screen, CharacterModel character)
    {
        ReloadScene(character.CharacterSelectBg);
        var container = screen.GetNode<Control>("AnimatedBg");
        foreach (var child in container.GetChildren())
        {
            container.RemoveChild(child);
            child.QueueFree();
        }

        var background = PreloadManager.Cache.GetScene(character.CharacterSelectBg)
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        background.Name = character.Id.Entry + "_bg";
        container.AddChild(background);
        ModLog.Info($"已完整重建 {character.Id.Entry} 的选角展示。");
    }

    private static void RebuildMonsterDisplay(NBestiary screen, NBestiaryEntry entry, MonsterModel monster)
    {
        var visualsPath = SceneHelper.GetScenePath("creature_visuals/" + monster.Id.Entry.ToLowerInvariant());
        ReloadScene(visualsPath);

        BestiarySelectedEntryField.SetValue(screen, null);
        BestiarySelectMonsterMethod.Invoke(screen, [entry]);
        ModLog.Info($"已完整重建 {monster.Id.Entry} 的图鉴展示。");
    }

    private static void ReloadScene(string path)
    {
        var scene = ResourceLoader.Load<PackedScene>(path, null, ResourceLoader.CacheMode.IgnoreDeep)
            ?? throw new InvalidOperationException($"无法重新加载场景：{path}");
        PreloadManager.Cache.SetAsset(path, scene);
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
