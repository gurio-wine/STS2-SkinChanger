using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Ui;

namespace STS2SkinChanger.Core;

internal static class FrameworkRegistryCooperation
{
    private static FrameworkRegistrySession? _session;
    private static Type? _selectorType;
    private static readonly List<WeakReference<Node>> Controls = [];
    private static readonly ConditionalWeakTable<FrameworkCharacterSkinContract,
        Dictionary<int, FrameworkCharacterSkinContract>> Filtered = new();
    public static bool IsActive => _session != null;

    public static bool IsNativeProvider(string providerId) => _session != null &&
        SkinService.Catalog?.Groups.SelectMany(group => group.Options).Any(option =>
            option.FrameworkContract is { } contract &&
            contract.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase) &&
            contract.FrameworkAssemblyName == _session.Assembly.GetName().Name) == true;

    public static void Bind(Assembly assembly)
    {
        if (_session != null) return;
        var session = new FrameworkRegistrySession(assembly,
            id => SkinService.TryGetSelectedFrameworkContract(Normalize(id.Entry), out var contract)
                ? contract.SkinId : null,
            (id, skinId) => RequestSelection(Normalize(id.Entry), skinId));
        session.PrepareCharacters = () =>
        {
            if (!ModelDb.Contains(typeof(Ironclad))) return;
            foreach (var character in ModelDb.AllCharacters) session.EnsureCharacter(character.Id);
        };
        var selector = assembly.GetType("thunninoiSkinManager.thunninoiSkinManagerCode.SkinSelector", true)!;
        // Keep its original arrows/label, but never let a second UI implementation reconstruct
        // AnimatedBg or lobby icons. Both selectors now request the existing SC hot-reload path.
        var refreshMethods = new[] { "Refresh", "LoadPreview" }
            .Select(name => AccessTools.Method(selector, name)
                ?? throw new MissingMethodException(selector.FullName, name)).ToArray();
        var cycleMethods = new[] { "OnPrevPressed", "OnNextPressed" }
            .Select(name => AccessTools.Method(selector, name)
                ?? throw new MissingMethodException(selector.FullName, name)).ToArray();
        session.Install();
        _session = session;
        _selectorType = selector;
        RemoveDuplicatePresentationPatches(assembly);
        var harmony = new Harmony(Entry.ModId + ".native-framework-controls");
        try
        {
            foreach (var method in refreshMethods)
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(FrameworkRegistryCooperation), nameof(RefreshControl)));
            foreach (var method in cycleMethods)
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(FrameworkRegistryCooperation), nameof(CycleControl)));
            harmony.Patch(AccessTools.Method(typeof(NCharacterSelectScreen), "SelectCharacter"),
                postfix: new HarmonyMethod(typeof(FrameworkRegistryCooperation), nameof(AttachControl)));
        }
        catch (Exception exception)
        {
            harmony.UnpatchAll(harmony.Id);
            ModLog.Warn("原管理器附加入口无法接入；注册/设置协作及 SC 入口继续可用：" +
                        exception.GetBaseException().Message);
        }
        ModLog.Info("已启用原皮肤管理器协作：保留原生注册、作者设置和控制入口；" +
                    "皮肤选择、预览与资源由 SC 统一管理，未加载内置同名后备接口。");
    }

    public static bool IsControlPatch(MethodInfo method) => _session?.Assembly == method.Module.Assembly &&
        method.DeclaringType?.Name is "ManagerSetup" or "FinalizeSkinDb";

    private static void AttachControl(NCharacterSelectScreen __instance, CharacterModel __0)
    {
        if (_selectorType == null) return;
        try
        {
            // Instantiate the ORIGINAL control scene. The original injector additionally reads a
            // hard-coded DEFECT button just to log its children; UI replacements may remove it.
            // That diagnostic must not make every character selection throw.
            if (__instance.GetNodeOrNull<Node>("SkinSelector") is { } old)
            {
                __instance.RemoveChild(old);
                old.QueueFree();
            }
            if (__0 is RandomCharacter) return;
            var scene = ResourceLoader.Load<PackedScene>("res://thunninoiSkinManager/SkinSelector.tscn");
            if (scene == null) return;
            var control = scene.Instantiate<Control>();
            control.Name = "SkinSelector";
            control.Position = new Vector2(910, 710); // Original manager's layout, not a second SC panel.
            _selectorType.GetProperty("characterModel")!.SetValue(control, __0);
            _selectorType.GetProperty("characterId")!.SetValue(control, __0.Id);
            __instance.AddChild(control);
        }
        catch (Exception exception)
        {
            ModLog.Warn("无法显示原管理器控制入口，SC 皮肤选择仍可使用：" + exception.GetBaseException().Message);
        }
    }

    private static void RemoveDuplicatePresentationPatches(Assembly assembly)
    {
        var harmony = new Harmony(Entry.ModId + ".native-framework-presentation");
        foreach (var target in Harmony.GetAllPatchedMethods().ToArray())
        {
            var info = Harmony.GetPatchInfo(target);
            if (info == null) continue;
            foreach (var callback in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers)
                         .Concat(info.Finalizers).Select(patch => patch.PatchMethod).Distinct())
            {
                if (callback.Module.Assembly == assembly && !IsControlPatch(callback))
                    harmony.Unpatch(target, callback);
            }
        }
    }

    private static void RequestSelection(string groupId, string skinId)
    {
        var option = skinId == "default" ? SkinCatalog.BaseOptionId : SkinService.Catalog?.Groups
            .FirstOrDefault(group => group.Id == groupId)?.Options
            .FirstOrDefault(option => option.FrameworkContract?.SkinId == skinId)?.Id;
        if (option == null) return;
        // Requests may originate in the framework's settings callbacks. Defer into the selected
        // character screen, recheck the group there, and keep save/multiplayer/preview in one path.
        Callable.From(() =>
        {
            foreach (var node in LiveControls())
            {
                if (CharacterId(node) is not { } id || Normalize(id.Entry) != groupId) continue;
                for (Node? parent = node; parent != null; parent = parent.GetParent())
                {
                    if (parent is MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen screen &&
                        ContextualSkinControls.RequestFrameworkSelection(screen, groupId, option)) return;
                }
            }
            ModLog.Info($"已忽略不在当前选角界面中的原管理器切肤请求：{groupId}/{skinId}");
        }).CallDeferred();
    }

    private static ModelId? CharacterId(Node control) =>
        _selectorType?.GetProperty("characterId")?.GetValue(control) as ModelId;

    private static IEnumerable<Node> LiveControls()
    {
        Controls.RemoveAll(reference => !reference.TryGetTarget(out var node) ||
            !GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion());
        return Controls.Select(reference => reference.TryGetTarget(out var node) ? node : null)
            .OfType<Node>().Where(node => node.IsInsideTree()).ToArray();
    }

    public static void RefreshControls()
    {
        foreach (var control in LiveControls()) UpdateLabel(control);
    }

    private static void UpdateLabel(Node control)
    {
        if (CharacterId(control) is not { } id) return;
        var group = Normalize(id.Entry);
        var name = SkinService.TryGetSelectedFrameworkContract(group, out var contract)
            ? contract.DisplayName : "Default";
        if (control.GetNodeOrNull<Label>("HBoxContainer/ScrollTextContainer/SkinName") is { } label)
            label.Text = name;
    }

    private static bool RefreshControl(Node __instance)
    {
        if (!LiveControls().Any(node => node == __instance)) Controls.Add(new(__instance));
        UpdateLabel(__instance);
        return false;
    }

    private static bool CycleControl(Node __instance, MethodBase __originalMethod)
    {
        if (CharacterId(__instance) is not { } id) return false;
        var groupId = Normalize(id.Entry);
        var options = SkinService.Catalog?.Groups.FirstOrDefault(group => group.Id == groupId)?.Options
            .Select(option => option.FrameworkContract).OfType<FrameworkCharacterSkinContract>()
            .Where(contract => contract.FrameworkAssemblyName == _session!.Assembly.GetName().Name)
            .Select(contract => contract.SkinId).Distinct().Prepend("default").ToArray() ?? ["default"];
        var current = SkinService.TryGetSelectedFrameworkContract(groupId, out var selected) ? selected.SkinId : "default";
        var offset = __originalMethod.Name == "OnNextPressed" ? 1 : -1;
        var index = Math.Max(0, Array.IndexOf(options, current));
        RequestSelection(groupId, options[(index + options.Length + offset) % options.Length]);
        return false;
    }

    internal static FrameworkCharacterSkinContract Filter(FrameworkCharacterSkinContract contract)
    {
        if (_session == null || contract.FrameworkAssemblyName != _session.Assembly.GetName().Name) return contract;
        return Filter(contract, key => _session.IsConfigEnabled(contract.TargetGroupId, contract.SkinId, key));
    }

    public static string CacheSuffix(string groupId, string selection)
    {
        if (_session == null || SkinService.Catalog == null ||
            !SkinService.Catalog.TryGetSelectedFrameworkContract(groupId, selection, out var contract) ||
            contract.FrameworkAssemblyName != _session.Assembly.GetName().Name) return string.Empty;
        return "\nframework-settings:" + SettingsMask(key =>
            _session.IsConfigEnabled(contract.TargetGroupId, contract.SkinId, key));
    }

    public static SkinOption? FilterAssets(SkinOption? option)
    {
        if (option?.FrameworkContract is not { } contract) return option;
        var filtered = Filter(contract);
        if (ReferenceEquals(contract, filtered)) return option;
        var excluded = contract.CharacterResources.Keys.Except(filtered.CharacterResources.Keys)
            .Select(key => (Canonical: SkinCatalog.GetFrameworkCharacterCanonicalPath(key, contract.TargetGroupId),
                Source: contract.CharacterResources[key]))
            // A composition can obtain this slot from a different source. The framework's
            // setting may disable its own asset, never somebody else's higher-priority slot.
            .Where(pair => pair.Canonical != null && option.Assets.TryGetValue(pair.Canonical, out var asset) &&
                asset.SourcePath.Equals(pair.Source, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Canonical!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return option with { Assets = option.Assets.Where(pair => !excluded.Contains(pair.Key)).ToDictionary() };
    }

    private static int SettingsMask(Func<string, bool> enabled) =>
        (enabled("UseCardFrame") ? 1 : 0) | (enabled("UseEnergy") ? 2 : 0) |
        (enabled("UseHands") ? 4 : 0) | (enabled("UseDefectOrbs") ? 8 : 0) |
        (enabled("SilentRecolorShiv") ? 16 : 0);

    // Pure resource filtering is shared by native config and regression tests. No real gameplay
    // properties are altered, and a disabled sub-feature never borrows a different provider.
    public static FrameworkCharacterSkinContract Filter(FrameworkCharacterSkinContract contract, Func<string, bool> enabled)
    {
        var mask = SettingsMask(enabled);
        if (mask == 31) return contract;
        var variants = Filtered.GetOrCreateValue(contract);
        if (variants.TryGetValue(mask, out var cached)) return cached;
        bool Include(string property) => property switch
        {
            "CardFrameMaterial" or "CardTrail" => (mask & 1) != 0,
            "EnergyIcon" or "EnergyLayers" or "EnergyLabelColor" or "EnergyLabelOutlineColor" => (mask & 2) != 0,
            "HandPoint" or "HandRock" or "HandPaper" or "HandScissors" => (mask & 4) != 0,
            "ShivTintColor" => (mask & 16) != 0,
            _ => true
        };
        var filtered = contract with
        {
            CharacterResources = contract.CharacterResources.Where(pair => Include(pair.Key)).ToDictionary(),
            CharacterResourceLists = contract.CharacterResourceLists.Where(pair => Include(pair.Key)).ToDictionary(),
            CharacterValues = contract.CharacterValues.Where(pair => Include(pair.Key)).ToDictionary(),
            Orbs = (mask & 8) == 0 ? [] : contract.Orbs
        };
        variants[mask] = filtered;
        return filtered;
    }
    private static string Normalize(string value) => FrameworkSkinRuntime.NormalizeToken(value);
}
