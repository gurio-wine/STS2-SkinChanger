using System.Linq.Expressions;

namespace STS2SkinChanger.Core;

internal sealed record RuntimeProviderCandidate(
    string ProviderId,
    IReadOnlyCollection<string> GroupIds,
    bool IsRunWideMonsterProvider);

internal sealed record RuntimeProviderScope(
    IReadOnlyCollection<string> VisibleGroupIds,
    IReadOnlyCollection<string> RunEnvironmentProviderIds);

internal sealed record RuntimeProviderPriorityCandidate(
    string ProviderId,
    bool Enabled,
    bool IsRunWideMonsterProvider);

internal static class RuntimeProviderScopePolicy
{
    public static IReadOnlySet<string> SelectActiveProviders(
        IEnumerable<RuntimeProviderCandidate> candidates,
        RuntimeProviderScope? scope)
    {
        var available = candidates.ToArray();
        if (scope == null)
        {
            // Before the game has created any screen/room scope, complete each selected provider's
            // one-time initialization under the normal pre-asset lifecycle. Delaying that first
            // initialization until character select or combat is unsafe for providers that
            // register Godot scene classes or fill static presentation registries: their scenes
            // can already have been cached as plain Node2D nodes by then. Once a real scope is
            // established, only visible providers remain active.
            return available
                .Select(candidate => candidate.ProviderId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var visibleGroups = scope.VisibleGroupIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runEnvironmentProviders = scope.RunEnvironmentProviderIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        return available
            .Where(candidate =>
                candidate.GroupIds.Any(visibleGroups.Contains) ||
                (candidate.IsRunWideMonsterProvider &&
                 runEnvironmentProviders.Contains(candidate.ProviderId)))
            .Select(candidate => candidate.ProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlySet<string> SelectRunEnvironmentProviders(
        IEnumerable<RuntimeProviderPriorityCandidate> priorities)
    {
        var selected = priorities.FirstOrDefault(candidate =>
            candidate.Enabled && candidate.IsRunWideMonsterProvider);
        return selected == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>([selected.ProviderId], StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsRunEnvironmentPatchTarget(string typeName, string methodName) =>
        IsRunEnvironmentIdentifier(typeName) || IsRunEnvironmentIdentifier(methodName);

    public static bool IsRunEnvironmentCallback(string typeName, string methodName) =>
        (methodName.StartsWith("On", StringComparison.Ordinal) ||
         methodName.StartsWith("Set", StringComparison.Ordinal) ||
         methodName.StartsWith("Update", StringComparison.Ordinal) ||
         methodName.StartsWith("Play", StringComparison.Ordinal)) &&
        (IsRunEnvironmentIdentifier(typeName) || IsRunEnvironmentIdentifier(methodName));

    public static bool IsRunEnvironmentControllerType(string typeName) =>
        IsRunEnvironmentIdentifier(typeName);

    private static bool IsRunEnvironmentIdentifier(string value) =>
        value.Contains("music", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("bgm", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("background", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("battlefield", StringComparison.OrdinalIgnoreCase);
}

internal sealed class ScopedMonsterSelectionSnapshot
{
    private Dictionary<string, HashSet<string>> _selectedMonsterIdsByProvider =
        new(StringComparer.OrdinalIgnoreCase);

    public void Replace(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> selectedMonsterIdsByProvider)
    {
        var next = selectedMonsterIdsByProvider.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _selectedMonsterIdsByProvider, next);
    }

    public bool IsSelected(string providerId, string monsterId)
    {
        var snapshot = Volatile.Read(ref _selectedMonsterIdsByProvider);
        return snapshot.TryGetValue(providerId, out var monsterIds) &&
               monsterIds.Contains(monsterId);
    }
}

internal static class ScopedMonsterRoutePolicy
{
    public static Func<object, string?> CreateMonsterIdAccessor(Type profileType)
    {
        const System.Reflection.BindingFlags propertyFlags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;
        var targetProperty = profileType.GetProperty("Target", propertyFlags);
        var monsterIdProperty = targetProperty?.PropertyType.GetProperty(
            "MonsterId",
            propertyFlags);
        if (targetProperty == null || monsterIdProperty?.PropertyType != typeof(string))
        {
            return _ => null;
        }

        var profileParameter = Expression.Parameter(typeof(object), "profile");
        var typedProfile = Expression.Convert(profileParameter, profileType);
        var target = Expression.Property(typedProfile, targetProperty);
        Expression monsterId = Expression.Property(target, monsterIdProperty);
        if (!targetProperty.PropertyType.IsValueType ||
            Nullable.GetUnderlyingType(targetProperty.PropertyType) != null)
        {
            monsterId = Expression.Condition(
                Expression.Equal(target, Expression.Constant(null, targetProperty.PropertyType)),
                Expression.Constant(null, typeof(string)),
                monsterId);
        }

        return Expression.Lambda<Func<object, string?>>(monsterId, profileParameter).Compile();
    }
}

internal static class RuntimeResourceRetentionPolicy
{
    public static IReadOnlySet<string> SelectTransientCombatGroups(
        IEnumerable<string> visibleGroupIds,
        IEnumerable<string> persistentGroupIds)
    {
        var persistent = persistentGroupIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return visibleGroupIds
            .Where(groupId => !persistent.Contains(groupId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class RuntimeProviderScopeLeaseTracker
{
    private long _current;

    public long Current => _current;

    public long Claim()
    {
        _current++;
        if (_current == 0)
        {
            _current++;
        }

        return _current;
    }

    public bool IsCurrent(long lease) => lease != 0 && lease == _current;

    public void Reset() => _current = 0;
}
