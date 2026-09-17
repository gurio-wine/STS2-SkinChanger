using Godot;

namespace STS2SkinChanger.Core;

/// <summary>
/// Reads the engine command line for the launcher-injected game pack. Kept apart from
/// <see cref="GamePackLocator"/> so the locator stays free of engine calls and testable,
/// and so an unreadable command line degrades to "no launcher pack" instead of a failed start.
/// </summary>
internal static class GameCommandLine
{
    public static IReadOnlyList<string> Arguments()
    {
        try
        {
            return OS.GetCmdlineArgs();
        }
        catch (Exception exception)
        {
            ModLog.Warn("无法读取启动参数，跳过由启动器注入的游戏资源包：" + exception.GetBaseException().Message);
            return [];
        }
    }
}
