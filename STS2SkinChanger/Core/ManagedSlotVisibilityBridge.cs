using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using STS2SkinChanger.Ui;

namespace STS2SkinChanger.Core;

/// <summary>
/// Preserves the selected provider's interactive slot toggles across newly instantiated scenes.
/// Observes the author's existing input; never creates interactive controls in combat or replays
/// provider initializers. All render writes are to per-instance slots, not shared skeleton data.
/// </summary>
internal static class ManagedSlotVisibilityBridge
{
    private static readonly Dictionary<Type, SlotToggleContract?> Contracts = [];
    private static readonly HashSet<MethodInfo> PatchedInputs = [];
    private static readonly ConditionalWeakTable<object, PreviewBinding> Previews = new();
    private static readonly ConditionalWeakTable<Node, LiveBinding> LiveBindings = new();
    private static readonly ConditionalWeakTable<Node, object> BoundRoots = new();
    private static readonly Harmony Observer = new(Entry.ModId + ".slot_visibility");

    internal static void BindPreview(Node root, string groupId, string providerId)
    {
        foreach (var node in Walk(root).OfType<Control>())
        {
            try
            {
                var type = node.GetType();
                if (!ManagedSkinModLoader.IsProviderAssemblyFor(providerId, type.Assembly)) continue;
                if (!Contracts.TryGetValue(type, out var contract))
                {
                    contract = SlotToggleContract.TryCreate(type);
                    Contracts[type] = contract;
                }
                if (contract == null || contract.Slots.GetValue(node) is not string[] slots ||
                    slots.Length is 0 or > 256 || slots.Any(string.IsNullOrWhiteSpace)) continue;
                if (!PatchedInputs.Contains(contract.Input))
                {
                    Observer.Patch(contract.Input, postfix: new HarmonyMethod(
                        typeof(ManagedSlotVisibilityBridge), nameof(AfterProviderInput)));
                    PatchedInputs.Add(contract.Input);
                }
                var saved = SkinService.GetSlotVisibilitySelections(groupId, providerId)
                    .FirstOrDefault(state => state.ToggleId == contract.Id &&
                        state.SourceSlots.ToHashSet(StringComparer.Ordinal).SetEquals(slots));
                if (saved != null)
                {
                    contract.State.SetValue(node, saved.Hidden);
                    contract.Apply.Invoke(node, null);
                }
                Previews.Remove(node);
                Previews.Add(node, new PreviewBinding(groupId, providerId, contract, slots.ToArray(),
                    (bool)contract.State.GetValue(node)!));
                ModLog.Info($"部件显隐开关已接入 group={groupId} provider={providerId} toggle={contract.Id} hidden={saved?.Hidden ?? false}");
            }
            catch (Exception exception)
            {
                ModLog.Warn("接入皮肤部件开关失败，保留原交互：" + exception.GetBaseException().Message);
            }
        }
    }

    private static void AfterProviderInput(object __instance)
    {
        try
        {
            if (!Previews.TryGetValue(__instance, out var binding) || __instance is not Control control ||
                !GodotObject.IsInstanceValid(control) || !control.IsInsideTree() || !control.IsVisibleInTree()) return;
            var hidden = (bool)binding.Contract.State.GetValue(__instance)!;
            if (hidden == binding.Hidden) return;
            binding.Hidden = hidden;
            SkinService.SaveSlotVisibilitySelection(new(binding.GroupId, binding.ProviderId,
                binding.Contract.Id, hidden, binding.SourceSlots));
            ModLog.Info($"部件显隐选择已保存 group={binding.GroupId} provider={binding.ProviderId} hidden={hidden} slots={binding.SourceSlots.Length}");
        }
        catch (Exception exception)
        {
            // Never propagate a disk/config error through a provider input callback.
            ModLog.Warn("保存皮肤部件开关失败：" + exception.GetBaseException().Message);
        }
    }

    internal static void BindPlayerScene(Node root, Player? player)
    {
        try
        {
            // These author-local interaction preferences are not multiplayer skin selections.
            // Never apply this machine's hidden parts to somebody else's character.
            if (player == null || CharacterAppearanceRuntime.GetLocalPlayer()?.NetId != player.NetId) return;
            var group = ContextualSkinControls.FindGroup(player.Character.Id.Entry, player.Character.GetType().Name);
            if (group == null) return;
            var provider = SkinService.GetSelectedCreatureRuntimeProvider(group.Id);
            var selections = provider == null ? [] : SkinService.GetSlotVisibilitySelections(group.Id, provider);
            var hidden = selections.Where(state => state.Hidden).ToArray();
            // Most providers have no remembered toggles: do not walk their potentially large
            // visual trees. Retain a cheap marker only for roots that may need an old mask cleared.
            if (hidden.Length == 0 && !BoundRoots.TryGetValue(root, out _)) return;
            if (hidden.Length > 0) BoundRoots.GetOrCreateValue(root);
            foreach (var spineNode in Walk(root).Where(node => node.GetClass().ToString() == "SpineSprite"))
            {
                if (LiveBindings.TryGetValue(spineNode, out var old))
                {
                    if (old.Matches(group.Id, provider, hidden)) continue;
                    old.Dispose();
                }
                LiveBindings.Remove(spineNode);
                if (hidden.Length == 0) continue;
                var binding = new LiveBinding(spineNode, group.Id, provider!, hidden);
                LiveBindings.Add(spineNode, binding);
                binding.Start();
            }
            if (hidden.Length == 0) BoundRoots.Remove(root);
        }
        catch (Exception exception)
        {
            ModLog.Warn("恢复皮肤部件显隐失败，保留模型：" + exception.GetBaseException().Message);
        }
    }

    private static IEnumerable<Node> Walk(Node root)
    {
        yield return root;
        foreach (var child in root.GetChildren())
            foreach (var descendant in Walk(child)) yield return descendant;
    }

    private sealed class PreviewBinding(string groupId, string providerId, SlotToggleContract contract, string[] sourceSlots, bool hidden)
    {
        public readonly string GroupId = groupId;
        public readonly string ProviderId = providerId;
        public readonly SlotToggleContract Contract = contract;
        public readonly string[] SourceSlots = sourceSlots;
        public bool Hidden = hidden;
    }

    private sealed class LiveBinding(Node spineNode, string groupId, string providerId, SlotVisibilitySelection[] selections) : IDisposable
    {
        private readonly List<(GodotObject Slot, SlotAlphaMask Mask)> _targets = [];
        private Callable _callback;
        private bool _connected;
        private bool _disposed;

        public bool Matches(string group, string? provider, SlotVisibilitySelection[] current) =>
            !_disposed && groupId.Equals(group, StringComparison.OrdinalIgnoreCase) &&
            providerId.Equals(provider, StringComparison.OrdinalIgnoreCase) && selections.SequenceEqual(current);

        public void Start()
        {
            spineNode.TreeExiting += Dispose;
            var sprite = new MegaSprite(spineNode);
            spineNode.RunWhenSpineReady(sprite, _ => Initialize(sprite));
        }

        private void Initialize(MegaSprite sprite)
        {
            if (_disposed || !GodotObject.IsInstanceValid(spineNode) || !spineNode.IsInsideTree()) return;
            try
            {
                var slots = new Dictionary<string, GodotObject>(StringComparer.Ordinal);
                var skeleton = sprite.GetSkeleton()?.BoundObject;
                if (skeleton == null) { Dispose(); return; }
                foreach (var value in skeleton.Call("get_slots").AsGodotArray())
                {
                    var slot = value.AsGodotObject();
                    var name = slot.Call("get_data").AsGodotObject().Call("get_name").AsString();
                    slots.TryAdd(name, slot);
                }
                var selected = selections.SelectMany(state => SlotVisibilityPolicy.ResolveSlots(state.SourceSlots, slots.Keys))
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (selected.Length == 0)
                {
                    ModLog.Warn($"部件显隐未找到已验证的骨骼映射 group={groupId} provider={providerId} scene={spineNode.SceneFilePath}");
                    Dispose();
                    return;
                }
                foreach (var name in selected) _targets.Add((slots[name], new SlotAlphaMask()));
                // Apply after each skeleton update just as the original select controller does.
                // Only the selected slots are revisited; no frame-by-frame scene tree/catalog scan.
                if (spineNode.HasSignal("world_transforms_changed"))
                {
                    _callback = Callable.From<Variant>(_ => Apply());
                    spineNode.Connect("world_transforms_changed", _callback);
                    _connected = true;
                }
                Apply();
                ModLog.Info($"部件显隐已恢复 group={groupId} provider={providerId} slots={string.Join(",", selected)}");
            }
            catch (Exception exception)
            {
                Dispose();
                ModLog.Warn("初始化骨骼部件显隐失败：" + exception.GetBaseException().Message);
            }
        }

        private void Apply()
        {
            if (_disposed) return;
            try
            {
                foreach (var (slot, mask) in _targets)
                {
                    if (!GodotObject.IsInstanceValid(slot)) continue;
                    var color = slot.Call("get_color").AsColor();
                    color.A = mask.Hide(color.A);
                    slot.Call("set_color", color);
                }
            }
            catch (Exception exception)
            {
                Dispose();
                ModLog.Warn("骨骼部件已失效，停止显隐刷新：" + exception.GetBaseException().Message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (GodotObject.IsInstanceValid(spineNode))
            {
                spineNode.TreeExiting -= Dispose;
                if (_connected && spineNode.IsConnected("world_transforms_changed", _callback))
                    spineNode.Disconnect("world_transforms_changed", _callback);
            }
            foreach (var (slot, mask) in _targets)
            {
                if (!GodotObject.IsInstanceValid(slot)) continue;
                try
                {
                    var color = slot.Call("get_color").AsColor();
                    color.A = mask.Restore(color.A);
                    slot.Call("set_color", color);
                }
                catch { /* The skeleton may already be releasing its native slots. */ }
            }
            _targets.Clear();
        }
    }
}
