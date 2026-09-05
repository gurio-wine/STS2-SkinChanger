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
    private static readonly Dictionary<string, long> Requests = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> PendingOptions = new(StringComparer.OrdinalIgnoreCase);
    private static long _requestGeneration;
    private static bool _refreshQueued;
    private static readonly ConditionalWeakTable<FrameworkCharacterSkinContract,
        Dictionary<int, FrameworkCharacterSkinContract>> Filtered = new();
    public static bool IsActive => _session != null;

    public static bool HasRegistrationCallbacks(Assembly provider) => _session?.HasRegistrationCallbacks(provider) == true;
    public static void RegisterProvider(Assembly provider) => _session?.RegisterProvider(provider);

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
        // Coordinate only conflicting refreshes. The original injector, preview implementation
        // and presentation patches remain installed and execute their own code.
        var refreshMethods = new[] { "Refresh", "LoadPreview" }
            .Select(name => AccessTools.Method(selector, name)
                ?? throw new MissingMethodException(selector.FullName, name)).ToArray();
        var cycleMethods = new[] { "OnPrevPressed", "OnNextPressed" }
            .Select(name => AccessTools.Method(selector, name)
                ?? throw new MissingMethodException(selector.FullName, name)).ToArray();
        session.Install();
        _session = session;
        _selectorType = selector;
        var harmony = new Harmony(Entry.ModId + ".native-framework-controls");
        try
        {
            foreach (var method in refreshMethods)
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(FrameworkRegistryCooperation), nameof(CanRefreshControl)),
                    postfix: new HarmonyMethod(typeof(FrameworkRegistryCooperation), nameof(UpdateLabel)));
            foreach (var method in cycleMethods)
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(FrameworkRegistryCooperation), nameof(CycleControl)));
            harmony.Patch(AccessTools.Method(selector, "_Ready"),
                postfix: new HarmonyMethod(typeof(FrameworkRegistryCooperation), nameof(TrackControl)));
            FrameworkNativeUiPatch.Install(assembly, harmony);
        }
        catch (Exception exception)
        {
            harmony.UnpatchAll(harmony.Id);
            ModLog.Warn("原管理器附加入口无法接入；注册/设置协作及 SC 入口继续可用：" +
                        exception.GetBaseException().Message);
        }
        ModLog.Info("已启用原皮肤管理器协作：原 UI、预览、设置、存档和呈现补丁保留；" +
                    "双方显式选择经热切换事务同步，未加载内置同名后备接口。");
    }

    internal static bool UsesNativePresentation(FrameworkCharacterSkinContract contract) =>
        _session != null && contract.FrameworkAssemblyName == _session.Assembly.GetName().Name &&
        _session.IsRegistered(contract.TargetGroupId, contract.SkinId);

    public static void SynchronizeLocalSelections(SkinCatalog catalog)
    {
        if (_session == null || !ModelDb.Contains(typeof(Ironclad))) return;
        foreach (var character in ModelDb.AllCharacters)
        {
            var group = Normalize(character.Id.Entry);
            var native = catalog.TryGetSelectedFrameworkContract(group, SkinService.Config.GetSelection(group), out var contract) &&
                         contract.FrameworkAssemblyName == _session.Assembly.GetName().Name;
            _session.PublishSelection(character.Id, native ? contract.SkinId : null);
        }
    }

    private static void RequestSelection(string groupId, string skinId)
    {
        var option = skinId == "default" ? SkinCatalog.BaseOptionId : SkinService.Catalog?.Groups
            .FirstOrDefault(group => group.Id == groupId)?.Options
            .FirstOrDefault(option => option.FrameworkContract is { } contract && contract.SkinId == skinId &&
                contract.FrameworkAssemblyName == _session?.Assembly.GetName().Name)?.Id;
        if (option == null) return;
        RequestOptionSelection(groupId, option);
    }

    private static void RequestOptionSelection(string groupId, string optionId)
    {
        var generation = ++_requestGeneration;
        Requests[groupId] = generation;
        PendingOptions[groupId] = optionId;
        // Requests may originate in the framework's settings callbacks. Defer into the selected
        // character screen, recheck the group there, and keep save/multiplayer/preview in one path.
        Callable.From(() =>
        {
            if (!Requests.TryGetValue(groupId, out var current) || current != generation) return;
            foreach (var node in LiveControls())
            {
                if (CharacterId(node) is not { } id || Normalize(id.Entry) != groupId) continue;
                for (Node? parent = node; parent != null; parent = parent.GetParent())
                {
                    if (parent is MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen screen &&
                        ContextualSkinControls.RequestFrameworkSelection(screen, groupId, optionId)) return;
                }
            }
            Requests.Remove(groupId);
            PendingOptions.Remove(groupId);
            ModLog.Info($"已忽略不在当前选角界面中的原管理器切肤请求：{groupId}/{optionId}");
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
        foreach (var control in LiveControls())
        {
            try
            {
                if (CanRefreshControl(control)) AccessTools.Method(_selectorType, "Refresh")!.Invoke(control, null);
            }
            catch (Exception exception)
            {
                ModLog.Warn("原管理器预览刷新失败，已保留 SC 当前选择：" + exception.GetBaseException().Message);
            }
            // A failed native preview must not leave its label naming the previous skin.
            UpdateLabel(control);
        }
    }

    public static void SelectionStarting(string groupId, string optionId)
    {
        if (_session == null) return;
        Requests.Remove(groupId);
        PendingOptions[groupId] = optionId;
    }

    public static void QueueRefreshControls()
    {
        if (_session == null || _refreshQueued) return;
        _refreshQueued = true;
        // MountOverlay calls synchronization before replacing resources. Never run native UI
        // reconstruction on that stack; it must see the completed resource transaction.
        Callable.From(() => { _refreshQueued = false; RefreshControls(); }).CallDeferred();
    }

    private static NCharacterSelectScreen? CharacterScreen(Node control)
    {
        for (Node? parent = control; parent != null; parent = parent.GetParent())
            if (parent is NCharacterSelectScreen screen) return screen;
        return null;
    }

    private static void UpdateLabel(Node __instance)
    {
        if (CharacterId(__instance) is not { } id || CharacterScreen(__instance) is not { } screen) return;
        var group = Normalize(id.Entry);
        var name = ContextualSkinControls.GetFrameworkSelectionName(screen, group, PendingOptions.GetValueOrDefault(group));
        if (name != null && __instance.GetNodeOrNull<Label>("HBoxContainer/ScrollTextContainer/SkinName") is { } label)
            label.Text = name;
    }

    private static void TrackControl(Node __instance)
    {
        if (!LiveControls().Any(node => node == __instance)) Controls.Add(new(__instance));
    }

    private static bool CanRefreshControl(Node __instance)
    {
        TrackControl(__instance);
        if (CharacterId(__instance) is not { } id) return false;
        var group = Normalize(id.Entry);
        for (Node? parent = __instance; parent != null; parent = parent.GetParent())
            if (parent is NCharacterSelectScreen screen && ContextualSkinControls.IsCharacterSelectionLoading(screen)) return false;
        if (Requests.ContainsKey(group)) return false;
        PendingOptions.Remove(group);
        if (SkinService.TryGetSelectedFrameworkContract(group, out var contract) && UsesNativePresentation(contract) ||
            SkinService.Config.GetSelection(group) == SkinCatalog.BaseOptionId) return true;
        // Its preview assumes every foreign model has a child named Visuals and would rebuild
        // the main backdrop. Keep the manager controls, but do not touch somebody else's model.
        UpdateLabel(__instance);
        if (__instance.GetNodeOrNull<Node>("VisualContainer/PreviewSprite") is { } preview)
        { preview.GetParent().RemoveChild(preview); preview.QueueFree(); }
        return false;
    }

    private static bool CycleControl(Node __instance, MethodBase __originalMethod)
    {
        if (CharacterId(__instance) is not { } id || CharacterScreen(__instance) is not { } screen) return false;
        var groupId = Normalize(id.Entry);
        var offset = __originalMethod.Name == "OnNextPressed" ? 1 : -1;
        var option = ContextualSkinControls.GetFrameworkCycleOption(
            screen, groupId, PendingOptions.GetValueOrDefault(groupId), offset);
        if (option != null) RequestOptionSelection(groupId, option);
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
