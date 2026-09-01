using System.Reflection;

namespace STS2SkinChanger.Core;

/// <summary>
/// Finds an optional card provider's Ancient-style switch by behavior instead of namespace.
/// Exported providers generate their own root namespace, so requiring one historical full type
/// name makes an otherwise valid switch invisible and leaves the Ancient layout on vanilla art.
/// </summary>
internal static class AncientStyleMethodPolicy
{
    public static bool ResolveWithoutProviderMethod(
        bool isNativeAncient,
        bool requestsAncientLayout) =>
        isNativeAncient || requestsAncientLayout;

    public static MethodInfo? Find(Assembly assembly) => Find(GetLoadableTypes(assembly));

    public static MethodInfo? Find(IEnumerable<Type> types) =>
        types
            .SelectMany(type => type.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method =>
                    method.Name.Equals("IsAncientStyleEnabled", StringComparison.Ordinal) &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters() is [{ ParameterType: var parameterType }] &&
                    parameterType == typeof(string))
                .Select(method => new
                {
                    Method = method,
                    Score = type.FullName?.Equals(
                        "CardPortraitsCore.ConfigHelper",
                        StringComparison.Ordinal) == true
                        ? 2
                        : type.Name.Equals("ConfigHelper", StringComparison.Ordinal)
                            ? 1
                            : 0
                }))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Method.DeclaringType?.FullName, StringComparer.Ordinal)
            .Select(candidate => candidate.Method)
            .FirstOrDefault();

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null).Cast<Type>();
        }
    }
}
