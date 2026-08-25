using System.Reflection;
using System.Runtime.Loader;
using Godot;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

/// <summary>
/// Applies a selected provider-owned visual mode without running the provider's full initializer.
/// This preserves its scene-side animation bridge while keeping unrelated card and settings
/// patches under SkinChanger's control.
/// </summary>
internal static class RuntimeMonsterVisualModeBridge
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, ModeBinding> Bindings =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FailedBindings =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RegisteredAssemblies =
        new(StringComparer.OrdinalIgnoreCase);

    public static void ApplySelected(string groupId)
    {
        var mode = SkinService.GetSelectedRuntimeMonsterVisualMode(groupId);
        if (mode == null)
        {
            return;
        }

        lock (Sync)
        {
            var key = mode.AssemblyPath + "\n" + mode.ServiceTypeName + "\n" + mode.EnumTypeName;
            if (FailedBindings.Contains(key))
            {
                return;
            }

            try
            {
                if (!Bindings.TryGetValue(key, out var binding))
                {
                    binding = CreateBinding(mode);
                    Bindings[key] = binding;
                }

                EnsureServiceNode(binding);
                var selected = Enum.Parse(binding.EnumType, mode.ModeName, ignoreCase: false);
                var current = binding.CurrentMode?.GetValue(null);
                if (!Equals(current, selected))
                {
                    binding.Setter.Invoke(null, [selected]);
                }
            }
            catch (Exception exception)
            {
                FailedBindings.Add(key);
                ModLog.Error(
                    $"应用 {mode.ProviderId} 的运行时外观模式 {mode.ModeName} 失败：{exception}");
            }
        }
    }

    private static ModeBinding CreateBinding(RuntimeMonsterVisualMode mode)
    {
        var assembly = GetOrLoadAssembly(mode.AssemblyPath);
        RegisterGodotScripts(assembly, mode.AssemblyPath);
        var serviceType = assembly.GetType(mode.ServiceTypeName, throwOnError: true)!;
        var enumType = assembly.GetType(mode.EnumTypeName, throwOnError: true)!;
        if (!enumType.IsEnum || !typeof(Node).IsAssignableFrom(serviceType))
        {
            throw new InvalidOperationException(
                $"{mode.ServiceTypeName}/{mode.EnumTypeName} 不是可用的 Godot 外观模式服务。");
        }

        var setter = serviceType.GetMethod(
                         mode.SetterName,
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                         binder: null,
                         types: [enumType],
                         modifiers: null) ??
                     throw new MissingMethodException(serviceType.FullName, mode.SetterName);
        var currentMode = serviceType.GetProperty(
            "CurrentMode",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return new ModeBinding(serviceType, enumType, setter, currentMode);
    }

    private static Assembly GetOrLoadAssembly(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
        {
            try
            {
                return !candidate.IsDynamic &&
                       Path.GetFullPath(candidate.Location)
                           .Equals(fullPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });
        if (loaded != null)
        {
            return loaded;
        }

        var context = AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly());
        return context?.LoadFromAssemblyPath(fullPath) ?? Assembly.LoadFrom(fullPath);
    }

    private static void RegisterGodotScripts(Assembly assembly, string assemblyPath)
    {
        if (!RegisteredAssemblies.Add(assemblyPath))
        {
            return;
        }

        try
        {
            var bridgeType = typeof(GodotObject).Assembly.GetType("Godot.Bridge.ScriptManagerBridge");
            var lookupMethod = bridgeType?.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name.Equals("LookupScriptsInAssembly", StringComparison.Ordinal) &&
                    method.GetParameters() is [{ ParameterType: var parameterType }] &&
                    parameterType == typeof(Assembly));
            lookupMethod?.Invoke(null, [assembly]);
        }
        catch
        {
            RegisteredAssemblies.Remove(assemblyPath);
            throw;
        }
    }

    private static void EnsureServiceNode(ModeBinding binding)
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root ??
                   throw new InvalidOperationException("Godot 场景树尚未创建。");
        if (root.GetChildren().Any(binding.ServiceType.IsInstanceOfType))
        {
            return;
        }

        var service = Activator.CreateInstance(binding.ServiceType) as Node ??
                      throw new InvalidOperationException(
                          $"无法创建外观模式服务 {binding.ServiceType.FullName}。");
        root.AddChild(service);
    }

    private sealed record ModeBinding(
        Type ServiceType,
        Type EnumType,
        MethodInfo Setter,
        PropertyInfo? CurrentMode);
}
