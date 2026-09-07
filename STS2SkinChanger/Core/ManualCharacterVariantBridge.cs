using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Ui;

namespace STS2SkinChanger.Core;

internal static class ManualCharacterVariantBridge
{
    private static readonly Harmony Observer = new(Entry.ModId + ".manual_variants");
    private static readonly HashSet<MethodInfo> Observed = [];
    private static readonly ConditionalWeakTable<NCharacterSelectScreen, ScreenBinding> Screens = new();
    private static readonly List<WeakReference<NCharacterSelectScreen>> ScreenRefs = [];
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal static IDisposable? BeginScope(string groupId)
    {
        var mode = SkinService.GetSelectedManualCharacterVariant(groupId);
        if (mode?.State.IsStatic != true || GetField(groupId, mode.State) is not { } field) return null;
        var previous = (bool)field.GetValue(null)!;
        field.SetValue(null, mode.Value);
        return new RestoreField(field, previous);
    }

    internal static void ApplyLocalChoice(string groupId)
    {
        try { ApplyLocalChoiceCore(groupId); }
        catch (Exception exception) { ModLog.Warn("同步皮肤原配色设置失败：" + exception.GetBaseException().Message); }
    }

    private static void ApplyLocalChoiceCore(string groupId)
    {
        var mode = SkinService.GetSelectedManualCharacterVariant(groupId);
        if (mode?.State.IsStatic != true || GetField(groupId, mode.State) is not { } field) return;
        if (Equals(field.GetValue(null), mode.Value)) return;
        field.SetValue(null, mode.Value);
        // Persist only a deliberate local/UI selection, never a remote player's render scope.
        if (mode.State.SaveMethod is { } save)
            field.DeclaringType!.GetMethod(save, Static, Type.EmptyTypes)?.Invoke(null, null);
    }

    private static FieldInfo? GetField(string groupId, ManualCharacterState state)
    {
        var provider = SkinService.GetSelectedRuntimeProvider(groupId);
        return provider == null ? null : ManagedSkinModLoader.GetActiveProviderAssembly(provider)?
            .GetType(state.TypeName)?.GetField(state.FieldName, Static);
    }

    internal static void BindScreen(NCharacterSelectScreen screen, string groupId, string providerId)
    {
        var mode = SkinService.GetSelectedManualCharacterVariant(groupId);
        if (mode == null) return;
        if (!Screens.TryGetValue(screen, out _)) ScreenRefs.Add(new(screen));
        Screens.Remove(screen);
        Screens.Add(screen, new(groupId, providerId, mode.State));
        if (!mode.State.IsStatic) return; // SlotToggleContract observes the existing _Input method.
        var assembly = ManagedSkinModLoader.GetActiveProviderAssembly(providerId);
        var toggle = assembly?.GetType(mode.State.ToggleTypeName)?.GetMethod(mode.State.ToggleMethod,
            Static | BindingFlags.Instance, Type.EmptyTypes);
        if (toggle == null || !Observed.Add(toggle)) return;
        try { Observer.Patch(toggle, postfix: new HarmonyMethod(typeof(ManualCharacterVariantBridge), nameof(AfterToggle))); }
        catch { Observed.Remove(toggle); throw; }
    }

    private static void AfterToggle(MethodBase __originalMethod)
    {
        try { ObserveToggle(__originalMethod); }
        catch (Exception exception) { ModLog.Warn("同步作者皮肤开关失败：" + exception.GetBaseException().Message); }
    }

    private static void ObserveToggle(MethodBase __originalMethod)
    {
        ScreenRefs.RemoveAll(reference => !reference.TryGetTarget(out var node) || !GodotObject.IsInstanceValid(node));
        foreach (var reference in ScreenRefs.ToArray())
        {
            if (!reference.TryGetTarget(out var screen) || !Screens.TryGetValue(screen, out var binding) ||
                binding.State.ToggleTypeName != __originalMethod.DeclaringType?.FullName ||
                binding.State.ToggleMethod != __originalMethod.Name ||
                !ManagedSkinModLoader.IsProviderAssemblyFor(binding.ProviderId, __originalMethod.DeclaringType.Assembly)) continue;
            if (GetField(binding.GroupId, binding.State)?.GetValue(null) is bool value)
                Request(screen, binding, value);
        }
    }

    internal static void AfterSlotToggle(Control control, string groupId, string providerId, string stateId, bool value)
    {
        for (Node? parent = control; parent != null; parent = parent.GetParent())
        {
            if (parent is not NCharacterSelectScreen screen || !Screens.TryGetValue(screen, out var binding)) continue;
            if (binding.GroupId == groupId && binding.ProviderId == providerId && binding.State.Id == stateId)
                Request(screen, binding, value);
            return;
        }
    }

    private static void Request(NCharacterSelectScreen screen, ScreenBinding binding, bool value)
    {
        var generation = ++binding.Generation;
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree() || !screen.IsVisibleInTree() ||
                !Screens.TryGetValue(screen, out var current) || !ReferenceEquals(current, binding) ||
                generation != binding.Generation || SkinService.GetSelectedRuntimeProvider(binding.GroupId) != binding.ProviderId) return;
            var option = SkinService.Catalog?.Groups.FirstOrDefault(group => group.Id == binding.GroupId)?.Options
                .FirstOrDefault(candidate => !candidate.IsComposition && candidate.EffectiveProviderId == binding.ProviderId &&
                    candidate.ManualCharacterVariant is { } variant && variant.State.Id == binding.State.Id && variant.Value == value);
            if (option != null) ContextualSkinControls.RequestManualVariantSelection(screen, binding.GroupId, option.Id);
        }).CallDeferred();
    }

    private sealed class ScreenBinding(string group, string provider, ManualCharacterState state)
    {
        public readonly string GroupId = group;
        public readonly string ProviderId = provider;
        public readonly ManualCharacterState State = state;
        public long Generation;
    }

    private sealed class RestoreField(FieldInfo field, bool value) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; field.SetValue(null, value); }
    }
}
