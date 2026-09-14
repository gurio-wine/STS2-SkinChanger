using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace STS2SkinChanger.Core;

/// <summary>
/// Lets a third-party Mod resolve the private dependencies it ships in its own package folder —
/// the managed assemblies and the native libraries that belong to them. This runs only after the
/// game's loader has already failed to resolve the dependency, and only for code that lives
/// outside the game folder, so no assembly the game or the runtime ships is affected.
/// </summary>
internal static class ModPrivateDependencyResolver
{
    private static readonly object Sync = new();
    private static readonly ConditionalWeakTable<Assembly, object> NativeProbes = new();
    private static readonly HashSet<string> Reported = new(StringComparer.OrdinalIgnoreCase);
    private static Action<string>? _info;
    private static Action<string>? _warn;
    private static bool _installed;

    /// <summary>
    /// The two reporting channels are the game's log. They stay injectable so the offline checks can
    /// exercise the same resolution path without a game logger being available.
    /// </summary>
    public static void Install(Action<string>? info = null, Action<string>? warn = null)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            _installed = true;
            _info = info;
            _warn = warn;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            var requesting = args.RequestingAssembly;
            if (requesting == null)
            {
                return null;
            }

            var requested = new AssemblyName(args.Name).Name;
            var candidate = ModPrivateDependencyPolicy.CandidatePath(
                requesting.Location,
                requested,
                AppContext.BaseDirectory,
                RuntimeEnvironment.GetRuntimeDirectory(),
                File.Exists);
            if (candidate == null)
            {
                return null;
            }

            var context = AssemblyLoadContext.GetLoadContext(requesting) ?? AssemblyLoadContext.Default;
            var loaded = context.Assemblies.FirstOrDefault(assembly => string.Equals(
                assembly.GetName().Name,
                requested,
                StringComparison.OrdinalIgnoreCase));
            if (loaded != null)
            {
                return loaded;
            }

            var resolved = context.LoadFromAssemblyPath(candidate);
            AttachNativeProbe(resolved, Path.GetDirectoryName(candidate)!);
            Report(requesting, resolved);
            return resolved;
        }
        catch (Exception exception)
        {
            _warn?.Invoke("按 Mod 自身目录解析随包依赖失败：" + exception.GetBaseException().Message);
            return null;
        }
    }

    /// <summary>
    /// The resolved assembly may declare native imports (OnnxRuntime loads <c>onnxruntime.dll</c>).
    /// The default native probing covers the game folder only, so point that assembly at its own
    /// package folder and leave every other name to the default resolution.
    /// </summary>
    private static void AttachNativeProbe(Assembly assembly, string directory)
    {
        if (NativeProbes.TryGetValue(assembly, out _))
        {
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => TryLoadNativeProbe(directory, name));
            NativeProbes.Add(assembly, new object());
        }
        catch (Exception exception)
        {
            _warn?.Invoke(
                $"无法为 {assembly.GetName().Name} 挂载随包原生依赖探测：" +
                exception.GetBaseException().Message);
        }
    }

    /// <summary>Loads <paramref name="libraryName"/> from the package folder; zero defers to the default probing.</summary>
    internal static IntPtr TryLoadNativeProbe(string directory, string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return IntPtr.Zero;
        }

        var path = Path.Combine(directory, libraryName + ".dll");
        if (!File.Exists(path))
        {
            return IntPtr.Zero;
        }

        try
        {
            return NativeLibrary.Load(path);
        }
        catch (Exception exception)
        {
            _warn?.Invoke($"随包原生依赖 {path} 加载失败：" + exception.GetBaseException().Message);
            return IntPtr.Zero;
        }
    }

    private static void Report(Assembly requesting, Assembly resolved)
    {
        var key = requesting.GetName().Name + " -> " + resolved.GetName().Name;
        lock (Sync)
        {
            if (!Reported.Add(key))
            {
                return;
            }
        }

        _info?.Invoke(
            $"已按 {requesting.GetName().Name} 自己的目录解析随包依赖 {resolved.GetName().Name}；" +
            "游戏加载器只探测游戏目录，这类依赖原本无法加载。");
    }
}
