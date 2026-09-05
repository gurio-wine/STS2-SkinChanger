using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace STS2SkinChanger.Core;

/// <summary>
/// Structural opt-in for embedded skin frameworks. An arbitrary AppliesTo predicate (for
/// example a gameplay rule) is not evidence of a skin. Discovery never reads property values.
/// Only assemblies already owned by a managed skin are passed to Install.
/// </summary>
internal static class SkinBehaviorContract
{
    public static MethodInfo[] Find(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
        if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters ||
            !HasProperty(type, "ProfileId", typeof(string)) ||
            !HasProperty(type, "TargetCharacterType", typeof(Type)) ||
            !HasProperty(type, "BodyTexturePath", typeof(string)) ||
            !HasProperty(type, "BodySkeletonDataPath", typeof(string))) return [];

        return type.GetMethods(flags).Where(method =>
            method.Name == "AppliesTo" && method.ReturnType == typeof(bool) &&
            !method.IsGenericMethod && method.GetMethodBody() != null &&
            method.GetParameters() is [{ ParameterType: var target }] &&
            (target == typeof(CharacterModel) || target == typeof(Player))).ToArray();
    }

    private static bool HasProperty(Type type, string name, Type propertyType) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public) is { } property &&
        property.PropertyType == propertyType && property.GetIndexParameters().Length == 0 &&
        property.GetMethod is { IsStatic: false };
}

/// <summary>
/// Adds selection ownership to the author's own predicate, without changing settings, UI,
/// resources or false results. Persistent delegates calling this predicate are checked at
/// execution time too; patch removal alone cannot revoke a C# event subscription.
/// </summary>
internal sealed class ScopedSkinBehavior(
    string harmonyId,
    Func<Assembly, CharacterModel, Player?, bool?> isSelected)
{
    private static readonly ConcurrentDictionary<MethodBase, ScopedSkinBehavior> Owners = new();
    [ThreadStatic] private static Player? _player;
    private readonly Harmony _harmony = new(harmonyId);
    private readonly Func<Assembly, CharacterModel, Player?, bool?> _isSelected = isSelected;
    private readonly HashSet<Assembly> _installed = [];
    private readonly ConcurrentDictionary<MethodBase, byte> _reportedFailures = new();

    public int Install(Assembly assembly)
    {
        if (!_installed.Add(assembly)) return 0;
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { types = exception.Types.OfType<Type>().ToArray(); }

        var count = 0;
        foreach (var method in types.SelectMany(SkinBehaviorContract.Find))
        {
            if (!Owners.TryAdd(method, this)) continue;
            try
            {
                _harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(ScopedSkinBehavior), nameof(Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(ScopedSkinBehavior), nameof(Postfix)) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(typeof(ScopedSkinBehavior), nameof(Finalizer)));
                count++;
            }
            catch
            {
                Owners.TryRemove(method, out _);
                throw;
            }
        }
        return count;
    }

    private static void Prefix(object? __0, out Player? __state)
    {
        __state = _player;
        if (__0 is Player player) _player = player;
    }

    private static void Postfix(MethodBase __originalMethod, object? __0, ref bool __result)
    {
        if (!__result || !Owners.TryGetValue(__originalMethod, out var owner)) return;
        var character = __0 as CharacterModel ?? (__0 as Player)?.Character;
        if (character == null) return;
        var player = __0 as Player;
        if (player == null && _player != null && ReferenceEquals(_player.Character, character)) player = _player;
        try
        {
            // null means the service has no confirmed group/ownership yet. Preserve the author
            // result during startup rather than turning missing information into a denial.
            if (owner._isSelected(__originalMethod.Module.Assembly, character, player) == false) __result = false;
        }
        catch (Exception exception)
        {
            if (owner._reportedFailures.TryAdd(__originalMethod, 0))
                ModLog.Warn("检查皮肤行为归属失败，保留原判断：" + exception.GetBaseException().Message);
        }
    }

    private static void Finalizer(Player? __state) => _player = __state;
}

internal static class ManagedSkinBehaviorBridge
{
    private static readonly ScopedSkinBehavior Router = new(
        Entry.ModId + ".skin-behavior-ownership", SkinService.IsCharacterBehaviorSelected);

    public static void Install(Assembly assembly)
    {
        try
        {
            var count = Router.Install(assembly);
            if (count > 0)
                ModLog.Info($"已为 {assembly.GetName().Name} 的 {count} 个皮肤适用入口接入对象归属检查；保留原作者设置与原判断。");
        }
        catch (Exception exception)
        {
            ModLog.Warn("接入原皮肤行为范围失败，未修改原设置：" + exception.GetBaseException().Message);
        }
    }
}
