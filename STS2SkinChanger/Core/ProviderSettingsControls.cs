using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;

namespace STS2SkinChanger.Core;

/// <summary>Settings commands are services, not permission to globally enable a skin DLL.</summary>
internal static class ProviderSettingsControls
{
    private static readonly FieldInfo? CommandsField = AccessTools.Field(typeof(DevConsole), "_commands");
    private static readonly List<(WeakReference<DevConsole> Console, bool Debug)> Consoles = [];
    private static readonly List<AbstractConsoleCmd> Commands = [];

    internal static bool TryRegisterCommand(
        IDictionary<string, AbstractConsoleCmd> commands, AbstractConsoleCmd command, bool allowDebug)
    {
        var name = command.CmdName;
        if ((command.DebugOnly && !allowDebug) || string.IsNullOrWhiteSpace(name) ||
            commands.Keys.Any(key => key.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
        commands.Add(name.ToLowerInvariant(), command);
        return true;
    }

    internal static void Add(AbstractConsoleCmd command)
    {
        if (Commands.Any(existing => existing.GetType() == command.GetType())) return;
        Commands.Add(command);
        Consoles.RemoveAll(entry => !entry.Console.TryGetTarget(out _));
        foreach (var entry in Consoles)
            if (entry.Console.TryGetTarget(out var console)) Register(console, entry.Debug, command);
        ModLog.Info($"已接入原皮肤设置命令：{command.CmdName}；不重新注册提供者全局类型或资源。");
    }

    internal static void Attach(DevConsole console, bool allowDebug)
    {
        Consoles.RemoveAll(entry => !entry.Console.TryGetTarget(out var existing) || ReferenceEquals(existing, console));
        Consoles.Add((new(console), allowDebug));
        foreach (var command in Commands) Register(console, allowDebug, command);
    }

    private static void Register(DevConsole console, bool allowDebug, AbstractConsoleCmd command)
    {
        if (CommandsField?.GetValue(console) is IDictionary<string, AbstractConsoleCmd> commands)
            TryRegisterCommand(commands, command, allowDebug);
    }
}

[HarmonyPatch(typeof(DevConsole), MethodType.Constructor, [typeof(bool)])]
internal static class ProviderSettingsConsolePatch
{
    private static void Postfix(DevConsole __instance, bool __0) => ProviderSettingsControls.Attach(__instance, __0);
}

/// <summary>Small weak target set; no frame polling and no permanent ownership of scene nodes.</summary>
internal sealed class ProviderSettingsTargets<T> where T : class
{
    private sealed record Binding(WeakReference<T> Target, string GroupId, string ProviderId);
    private readonly List<Binding> bindings = [];

    public void Bind(T target, string groupId, string providerId)
    {
        bindings.RemoveAll(entry => !entry.Target.TryGetTarget(out var existing) || ReferenceEquals(existing, target));
        bindings.Add(new(new(target), groupId, providerId));
    }

    public int Refresh(Func<T, bool> isAlive, Func<T, string?> resolveGroup,
        Func<string, string?> selectedProvider, Action<T> apply)
    {
        bindings.RemoveAll(entry => !entry.Target.TryGetTarget(out var target) || !isAlive(target));
        var count = 0;
        foreach (var binding in bindings.ToArray())
        {
            if (!binding.Target.TryGetTarget(out var target) || !isAlive(target) ||
                !string.Equals(resolveGroup(target), binding.GroupId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(selectedProvider(binding.GroupId), binding.ProviderId, StringComparison.OrdinalIgnoreCase)) continue;
            apply(target);
            count++;
        }
        return count;
    }
}
