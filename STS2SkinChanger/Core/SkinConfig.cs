using System.Text.Json;

namespace STS2SkinChanger.Core;

internal sealed class SkinConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public Dictionary<string, string> Selections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool SuppressLoadOrderWarning { get; set; }

    public static SkinConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<SkinConfig>(File.ReadAllText(path), JsonOptions) ?? new SkinConfig();
                // JSON 中显式的 null 会覆盖属性初始化器，反序列化后兜底一次。
                config.Selections ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return config;
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn($"读取皮肤配置失败，将使用默认配置：{exception.Message}");
        }

        return new SkinConfig();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, this, JsonOptions);
            // 先落盘再原子替换，避免崩溃留下空/半截的正式配置。
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public string GetSelection(string groupId) =>
        Selections.GetValueOrDefault(groupId, Catalog.SkinCatalog.BaseOptionId);
}
