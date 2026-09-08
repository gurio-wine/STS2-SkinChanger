using Godot;

namespace STS2SkinChanger.Core;

internal static class SkinChangerPaths
{
    private static readonly Lazy<SkinChangerStorage> Current = new(() =>
    {
        var storage = new SkinChangerStorage(OS.GetUserDataDir(), Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try { storage.MigrateConfiguration(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { ModLog.Warn("配置迁移未完成，将保留并尝试读取旧配置：" + e.Message); }
        foreach (var warning in storage.Warnings) ModLog.Warn(warning);
        try { storage.StartCacheSession(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { ModLog.Warn("皮肤缓存目录无法打开，部分换肤可能不可用：" + e.Message); }
        ModLog.Info("配置目录：" + storage.RootDirectory);
        return storage;
    });
    public static SkinChangerStorage Storage => Current.Value;
    public static string Configuration(string name) => Storage.ConfigurationFile(name);
    public static string CacheDirectory => Storage.CacheDirectory;
    public static string WorkshopCacheDirectory => Path.Combine(CacheDirectory, "workshop");
    public static string RuntimeCacheDirectory => Path.Combine(CacheDirectory, "runtime");
    public static void WriteCache(string path, Action write)
    {
        if (!Storage.WriteCache(path, write)) throw new OperationCanceledException("Skin Changer is quitting.");
    }
    public static void Initialize() => _ = Storage;
}
