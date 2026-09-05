using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using STS2SkinChanger.Ui;

namespace STS2SkinChanger.Core;

internal static class ManagedMerchantSettingsBridge
{
    private sealed record Session(MerchantSettingsContract Contract)
    {
        public ProviderSettingsTargets<Node> Targets { get; } = new();
        public bool Pending;
        public bool Applying;
    }

    private static readonly Dictionary<Assembly, Session> Sessions = [];
    private static readonly HashSet<Assembly> Inspected = [];
    private static readonly Harmony Observer = new(Entry.ModId + ".provider_settings");

    internal static void Install(Assembly assembly)
    {
        if (!Inspected.Add(assembly)) return;
        try
        {
            // Only adapted assemblies reference this bridge. Avoid resolving every type in a
            // large unrelated skin just to discover that it has no supported settings contract.
            if (!assembly.GetReferencedAssemblies().Any(reference => reference.Name == typeof(Entry).Assembly.GetName().Name)) return;
            var contract = MerchantSettingsContract.TryCreate(assembly);
            if (contract == null) return;
            var session = new Session(contract);
            Sessions.Add(assembly, session);
            Observer.Patch(contract.Save, postfix: new HarmonyMethod(typeof(ManagedMerchantSettingsBridge), nameof(AfterSave)));
            // Its .cctor can now load the author's config but its world walks already call our
            // ownership-checked bridge. No provider initializer or global PatchAll is invoked.
            var command = (AbstractConsoleCmd)Activator.CreateInstance(contract.CommandType)!;
            ProviderSettingsControls.Add(command);
            ModLog.Info($"已接入原商人设置：{assembly.GetName().Name}；原配置保留，手脚应用限定当前所选对象。");
        }
        catch (Exception exception)
        {
            if (Sessions.Remove(assembly, out var session)) Observer.Unpatch(session.Contract.Save,
                AccessTools.Method(typeof(ManagedMerchantSettingsBridge), nameof(AfterSave)));
            ModLog.Warn("接入原皮肤设置失败，未放开全局修改：" + exception.GetBaseException().Message);
        }
    }

    internal static bool OwnsSettingsPatch(MethodInfo callback) =>
        Sessions.Values.Any(session => session.Contract.SettingsPostfix == callback);

    internal static void Bind(Node node, string groupId, string providerId)
    {
        if (node is not (NMerchantHand or NMerchantInventory)) return;
        foreach (var (assembly, session) in Sessions)
        {
            if (!ManagedSkinModLoader.IsProviderAssemblyFor(providerId, assembly)) continue;
            session.Targets.Bind(node, groupId, providerId);
            // Ready may have completed after a settings change. Read the latest original config,
            // rather than replaying a saved value captured before the skeleton was ready.
            if (IsLive(node) && SkinService.GetSelectedFullRuntimeProvider(groupId) == providerId)
                ApplyNode(session, node);
        }
    }

    internal static int Refresh(Assembly assembly)
    {
        if (!Sessions.TryGetValue(assembly, out var session) || session.Applying) return 0;
        session.Applying = true;
        try
        {
            return session.Targets.Refresh(
                IsLive,
                MerchantRuntimeAppearance.ResolveMerchantGroupId,
                SkinService.GetSelectedFullRuntimeProvider,
                node => ApplyNode(session, node));
        }
        catch (Exception exception)
        {
            ModLog.Warn("原皮肤设置定向刷新失败：" + exception.GetBaseException().Message);
            return 0;
        }
        finally { session.Applying = false; }
    }

    private static bool IsLive(Node node) =>
        GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion() && node.IsInsideTree();

    private static void ApplyNode(Session session, Node node)
    {
        try
        {
            if (node is NMerchantHand && node.GetParent() is { } parent &&
                new MegaSprite(parent).IsAnimationStateReady())
            {
                // The legacy helper schedules an unguarded callback when not ready. Do not enter
                // that branch: the existing selected Ready bridge will bind/apply after readiness.
                session.Contract.ApplyHand.Invoke(null, [node]);
            }
            else if (node is NMerchantInventory)
                session.Contract.ApplyLegs.Invoke(null, [node, !(bool)session.Contract.UseFoot.Invoke(null, null)!]);
        }
        catch (Exception exception)
        {
            ModLog.Warn("刷新当前商人内部设置失败：" + exception.GetBaseException().Message);
        }
    }

    private static void AfterSave(MethodBase __originalMethod) => RequestRefresh(__originalMethod.Module.Assembly);

    internal static int RequestRefresh(Assembly assembly)
    {
        if (!Sessions.TryGetValue(assembly, out var session)) return 0;
        try
        {
            var hands = 0;
            session.Targets.Refresh(IsLive, MerchantRuntimeAppearance.ResolveMerchantGroupId,
                SkinService.GetSelectedFullRuntimeProvider, node => { if (node is NMerchantHand) hands++; });
            if (session.Pending) return hands;
            session.Pending = true;
            Callable.From(() =>
            {
                session.Pending = false;
                Refresh(assembly); // Re-check ownership at execution, never trust the Save-time choice.
            }).CallDeferred();
            return hands;
        }
        catch (Exception exception)
        {
            session.Pending = false;
            ModLog.Warn("延迟刷新皮肤内部设置失败：" + exception.GetBaseException().Message);
            return 0;
        }
    }

    internal static void FillSettings(NModInfoContainer container, Mod mod)
    {
        // Also invoke on OTHER Mod rows so the author's existing hide logic removes its buttons.
        foreach (var session in Sessions.Values.ToArray())
        {
            try { session.Contract.SettingsPostfix?.Invoke(null, [container, mod]); }
            catch (Exception exception)
            {
                ModLog.Warn("显示原皮肤设置失败：" + exception.GetBaseException().Message);
            }
        }
    }
}

[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Fill))]
internal static class ProviderSettingsModInfoPatch
{
    private static void Postfix(NModInfoContainer __instance, Mod mod)
    {
        ManagedSkinModLoader.EnsureProviderSettings(mod);
        ManagedMerchantSettingsBridge.FillSettings(__instance, mod);
    }
}
