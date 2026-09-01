using System.Reflection;
using System.Runtime.Loader;

namespace STS2SkinChanger.Core;

internal static class FrameworkAssemblyLoadContextPolicy
{
    public static Assembly LoadFromAssemblyPath(Assembly hostAssembly, string assemblyPath)
    {
        var hostContext = AssemblyLoadContext.GetLoadContext(hostAssembly) ??
                          AssemblyLoadContext.Default;
        return hostContext.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
    }
}
