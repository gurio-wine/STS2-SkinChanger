namespace STS2SkinChanger.Core;

internal static class ProviderRuntimeTypeLookupPolicy
{
    public static Type? TryResolve(string? providerTypeName, Func<string, Type?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (string.IsNullOrWhiteSpace(providerTypeName))
        {
            return null;
        }

        try
        {
            return lookup(providerTypeName.Replace('/', '+'));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            TypeLoadException or
            FileLoadException or
            FileNotFoundException or
            BadImageFormatException)
        {
            // Mono.Cecil formats closed generic names with angle brackets. Reflection's
            // type parser can reject those names before Harmony has a chance to return null.
            // Failure to resolve a receiver means it is unsafe to rewrite that one Dispose
            // call; it must not abort compatibility scanning for the rest of the assembly.
            return null;
        }
    }
}
