using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace STS2SkinChanger.Core;

/// <summary>
/// Separates character-context card overrides from per-card skin ownership. Keep intrinsic
/// Mod-card asset definitions and the framework itself; only gate its borrowed character layer.
/// </summary>
internal static class FrameworkCardVisualGuard
{
    private const string HelperName = "STS2RitsuLib.Scaffolding.Content.Patches.ModCharacterOwnedVisualOverrideHelper";
    private static readonly object Sync = new();
    private static readonly Harmony Harmony = new(Entry.ModId + ".FrameworkCardVisualGuard");
    private static readonly HashSet<MethodBase> Patched = [];
    private static readonly HashSet<(Assembly Assembly, string Error)> ReportedCardFailures = [];
    private static volatile bool _discoveryDirty = true;
    private static int _reportedLookupFailure;
    [ThreadStatic] private static CardModel? _baselineCard;
    [ThreadStatic] private static bool _checkingOwnership;

    static FrameworkCardVisualGuard() =>
        AppDomain.CurrentDomain.AssemblyLoad += (_, _) => _discoveryDirty = true;

    internal static void EnsureInstalled()
    {
        if (!_discoveryDirty) return;
        lock (Sync)
        {
            if (!_discoveryDirty) return;
            _discoveryDirty = false;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var helper = assembly.GetType(HelperName, throwOnError: false);
                if (helper == null) continue;
                var method = AccessTools.DeclaredMethod(helper, "TryGetOwningCharacterOverrides", [typeof(CardModel)]);
                if (method == null || !method.IsStatic || method.ReturnType.FullName !=
                    "STS2RitsuLib.Scaffolding.Characters.IModCharacterAssetOverrides" || Patched.Contains(method)) continue;
                try
                {
                    Harmony.Patch(method, prefix: new HarmonyMethod(typeof(FrameworkCardVisualGuard), nameof(CharacterOverridePrefix))
                        { priority = Priority.First });
                    Patched.Add(method);
                    ModLog.Info($"已协调 {assembly.GetName().Name} 的角色卡面覆盖：已接管卡牌遵循单卡选择，未接管卡牌保留框架外观。");
                }
                catch (Exception exception)
                {
                    ModLog.Warn("安装可选框架卡面协调失败，不影响其它接管功能：" + exception.GetBaseException().Message);
                }
            }
        }
    }

    internal static string GetBaselinePortraitPath(CardModel card)
    {
        EnsureInstalled();
        var previous = _baselineCard;
        _baselineCard = card;
        try { return card.PortraitPath; }
        finally { _baselineCard = previous; }
    }

    internal static bool TryGetBaselinePortraitPath(CardModel card, out string path)
    {
        try
        {
            path = GetBaselinePortraitPath(card);
            return !string.IsNullOrWhiteSpace(path);
        }
        catch (Exception exception)
        {
            ReportCardReadFailure(card, exception);
            path = string.Empty;
            return false;
        }
    }

    internal static void ReportCardReadFailure(CardModel card, Exception exception)
    {
        var type = card.GetType();
        var cause = exception.GetBaseException();
        // One incompatible base profile may affect hundreds of derived cards. Deduplicate
        // the diagnostic, not the result: a later successful read must still be usable.
        lock (Sync)
            if (!ReportedCardFailures.Add((type.Assembly, cause.GetType().FullName + ":" + cause.Message))) return;
        ModLog.Warn($"跳过无法读取外观定义的卡牌 {type.FullName}（{type.Assembly.GetName().Name}）；" +
                    "继续处理其它卡牌，不修改该卡的原始资源定义。此保护不代表其依赖兼容问题已解决：" + cause);
    }

    private static bool CharacterOverridePrefix(CardModel __0, ref object? __result)
    {
        if (__0 == null) return true;
        if (ReferenceEquals(_baselineCard, __0))
        {
            // Vanilla card catalog identities must not depend on the selected character or
            // hover-tip context. Mod-defined cards still need their own framework paths.
            if (__0.GetType().Assembly != typeof(CardModel).Assembly) return true;
        }
        else
        {
            // Lookup may itself read portrait identities. Do not recursively create the same
            // weak-cache entry or interfere with other cards queried by optional frameworks.
            if (_checkingOwnership) return true;
            _checkingOwnership = true;
            try { if (!SkinService.HasCardSkin(__0)) return true; }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _reportedLookupFailure, 1) == 0)
                    ModLog.Warn("卡牌外观归属尚不可用，暂时保留框架行为：" + exception.GetBaseException().Message);
                return true;
            }
            finally { _checkingOwnership = false; }
        }
        __result = null;
        return false;
    }
}
