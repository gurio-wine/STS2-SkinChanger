using System.Text.Json;

namespace STS2SkinChanger.Core;

internal sealed record CharacterCombatTransform(
    float Scale = 1f,
    float OffsetX = 0f,
    float OffsetY = 0f)
{
    public float HealthBarScale { get; init; } = 1f;

    public float HealthBarOffsetX { get; init; }

    public float HealthBarOffsetY { get; init; }

    public bool HealthBarFollowsModelScale { get; init; }

    public bool HealthBarFollowsModelMovement { get; init; } = true;

    public float IntentScale { get; init; } = 1f;

    public float IntentOffsetX { get; init; }

    public float IntentOffsetY { get; init; }

    public bool IntentFollowsModelScale { get; init; }

    public bool IntentFollowsModelMovement { get; init; } = true;
}

internal sealed record CardSkinPriorityEntry(
    string OptionId,
    bool Enabled);

internal sealed class SkinConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public Dictionary<string, string> Selections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<CardSkinPriorityEntry>> CardSkinPriorities { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int CardPriorityDefaultsVersion { get; set; }

    public Dictionary<string, Dictionary<string, float>> MonsterScales { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, CharacterCombatTransform>> CharacterCombatTransforms { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool SuppressLoadOrderWarning { get; set; }

    public bool? LastKnownFirstInLoadOrder { get; set; }

    public static SkinConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<SkinConfig>(File.ReadAllText(path), JsonOptions) ?? new SkinConfig();
                config.Selections ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                config.CardSkinPriorities ??=
                    new Dictionary<string, List<CardSkinPriorityEntry>>(StringComparer.OrdinalIgnoreCase);
                config.CardSkinPriorities = config.CardSkinPriorities.ToDictionary(
                    pair => pair.Key,
                    pair => (pair.Value ?? [])
                        .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.OptionId))
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
                config.MonsterScales ??=
                    new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
                config.MonsterScales = config.MonsterScales.ToDictionary(
                    pair => pair.Key,
                    pair => new Dictionary<string, float>(
                        pair.Value ?? new Dictionary<string, float>(),
                        StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
                config.CharacterCombatTransforms ??=
                    new Dictionary<string, Dictionary<string, CharacterCombatTransform>>(
                        StringComparer.OrdinalIgnoreCase);
                config.CharacterCombatTransforms = config.CharacterCombatTransforms.ToDictionary(
                    pair => pair.Key,
                    pair => new Dictionary<string, CharacterCombatTransform>(
                        (pair.Value ?? new Dictionary<string, CharacterCombatTransform>())
                        .Where(option => option.Value != null)
                        .ToDictionary(option => option.Key, option => option.Value),
                        StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
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
