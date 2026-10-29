using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace STS2SkinChanger.Core;

/// <summary>
/// The verdict for one cosmetic provider DLL on the running game. It is evidence, not a promise:
/// every ordinary method was JIT-compiled (never executed) against the current game assembly, so a
/// missing member, an invalid signature or un-loadable IL is reported before the package is used.
/// </summary>
internal sealed record ProviderRuntimeAudit(bool CanRun, int PreparedMethods, string Failure)
{
    public string Describe() => CanRun
        ? $"已核验 {PreparedMethods} 个方法可在当前游戏运行"
        : Failure;
}

internal static partial class ProviderAssemblyCompatibility
{
    /// <summary>
    /// Proves that the shipped provider DLL can run on the game that is running now. The assembly is
    /// loaded into its own collectible context, every method with a body is compiled and the context
    /// is unloaded again; no provider code, initializer or patch is executed.
    /// </summary>
    public static ProviderRuntimeAudit AuditForCurrentGame(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
        {
            return new(false, 0, "皮肤 DLL 不存在");
        }

        try
        {
            var prepared = PrepareForCurrentGame(assemblyPath);
            if (prepared.Report.Failure != null)
            {
                prepared.Assembly?.Dispose();
                return new(false, 0, prepared.Report.Failure);
            }

            var context = new AssemblyLoadContext("sc-provider-audit", isCollectible: true);
            // A package may ship private managed dependencies next to its own DLL (video, JSON or
            // helper libraries). The game's loader resolves those from the package folder, so the
            // audit has to resolve them the same way - otherwise a package is reported as unable to
            // run here only because the audit could not see a file that ships with it.
            var providerDirectory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
            if (!string.IsNullOrWhiteSpace(providerDirectory))
            {
                context.Resolving += (_, name) => ResolvePrivateDependency(context, providerDirectory, name);
            }

            try
            {
                Assembly assembly;
                if (prepared.Assembly is { } rewritten)
                {
                    using (rewritten)
                    {
                        assembly = context.LoadFromStream(rewritten);
                    }
                }
                else
                {
                    // Read, never map: an audited package must stay replaceable and removable, and
                    // Skin Changer swaps provider folders while the game is running.
                    using var bytes = new MemoryStream(File.ReadAllBytes(assemblyPath), writable: false);
                    assembly = context.LoadFromStream(bytes);
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    return new(false, 0, string.Join("；", exception.LoaderExceptions.Take(3)
                        .Select(loader => loader?.GetBaseException().Message ?? "未知类型加载失败")));
                }

                var preparedMethods = 0;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var candidates = types
                    .SelectMany(type => type.GetMethods(flags)
                        .Select(method => (Owner: type.FullName ?? type.Name, Method: method)))
                    .Concat(assembly.ManifestModule.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                               BindingFlags.Static)
                        .Select(method => (Owner: "<Module>", Method: method)));
                foreach (var (owner, method) in candidates)
                {
                    if (method.ContainsGenericParameters || method.GetMethodBody() == null)
                    {
                        continue;
                    }

                    try
                    {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        preparedMethods++;
                    }
                    catch (Exception exception)
                    {
                        return new(false, preparedMethods,
                            $"{owner}.{method.Name} 无法在当前游戏编译：" +
                            exception.GetBaseException().Message);
                    }
                }

                return new(true, preparedMethods, string.Empty);
            }
            finally
            {
                // The audited copy must never be mistaken for the copy the game loads later.
                context.Unload();
            }
        }
        catch (Exception exception)
        {
            return new(false, 0, exception.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Resolves a provider's own private dependency from the package folder. Only a file that is
    /// actually shipped there is used, and a dependency that cannot be loaded stays unresolved so
    /// the audit keeps reporting it instead of silently accepting the package.
    /// </summary>
    private static Assembly? ResolvePrivateDependency(
        AssemblyLoadContext context,
        string providerDirectory,
        AssemblyName name)
    {
        if (string.IsNullOrWhiteSpace(name.Name))
        {
            return null;
        }

        var candidate = Path.Combine(providerDirectory, name.Name + ".dll");
        if (!File.Exists(candidate))
        {
            return null;
        }

        try
        {
            return context.LoadFromAssemblyPath(candidate);
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException)
        {
            return null;
        }
    }
}
