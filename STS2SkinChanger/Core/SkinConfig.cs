using System.Text.Json;

namespace STS2SkinChanger.Core;

internal sealed class SkinConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public Dictionary<string, string> Selections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static SkinConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<SkinConfig>(File.ReadAllText(path), JsonOptions) ?? new SkinConfig();
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
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    public string GetSelection(string groupId) =>
        Selections.GetValueOrDefault(groupId, Catalog.SkinCatalog.BaseOptionId);
}
