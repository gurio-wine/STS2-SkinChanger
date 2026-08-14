using MegaCrit.Sts2.Core.Logging;

namespace STS2SkinChanger.Core;

internal static class ModLog
{
    private const string Prefix = "[STS2SkinChanger] ";

    public static void Info(string message) => Log.Info(Prefix + message);

    public static void Warn(string message) => Log.Warn(Prefix + message);

    public static void Error(string message) => Log.Error(Prefix + message);
}
