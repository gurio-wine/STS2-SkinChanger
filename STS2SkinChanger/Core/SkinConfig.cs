using System.Text.Json;

namespace STS2SkinChanger.Core;

internal sealed record CardSkinPriorityEntry(
    string OptionId,
    bool Enabled);

internal sealed class CardSkinPreset
{
    public string Name { get; set; } = string.Empty;

    // Empty for presets written by versions before presets were scoped to a card category.
    // SkinService migrates those entries once the card catalogue is available.
    public string? CategoryId { get; set; }

    public Dictionary<string, List<CardSkinPriorityEntry>> CardSkinPriorities { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Selections { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record MonsterSkinPriorityEntry(
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

    // Kept separate from the full character appearance so a lightweight icon pack can be
    // combined with any model/animation skin. Missing icon kinds fall back to that skin.
    public Dictionary<string, string> CharacterIconSelections { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> VisualProviderPriority { get; set; } = [];

    public int VisualSelectionDefaultsVersion { get; set; }

    public Dictionary<string, List<CardSkinPriorityEntry>> CardSkinPriorities { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int CardPriorityDefaultsVersion { get; set; }

    public List<CardSkinPreset> CardSkinPresets { get; set; } = [];

    // Active preset is now tracked independently for each card catalogue category. Keep the
    // single value below so old configuration files can be migrated without losing the active
    // preset name.
    public Dictionary<string, string> ActiveCardSkinPresets { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string? ActiveCardSkinPreset { get; set; }

    public Dictionary<string, List<MonsterSkinPriorityEntry>> MonsterSkinPriorities { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> MonsterSkinCategoryGroups { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // Legacy migration state from the short-lived region master switch implementation.
    public List<string> EnabledMonsterSkinPriorityCategories { get; set; } = [];

    public List<string> MonsterGroupsFollowingCategory { get; set; } = [];

    public List<string> MonsterGroupsWithManualSelection { get; set; } = [];

    public int MonsterPriorityDefaultsVersion { get; set; }

    public Dictionary<string, Dictionary<string, float>> MonsterScales { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, CharacterCombatTransform>> CharacterCombatTransforms { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool SuppressLoadOrderWarning { get; set; }

    // Legacy state retained so the first run after upgrading can still detect a transition from a
    // previously safe order. New versions only require Skin Changer to precede every skin provider.
    public bool? LastKnownFirstInLoadOrder { get; set; }

    public bool? LastKnownBeforeAllSkinMods { get; set; }

    public bool LoadOtherPlayersCustomSkins { get; set; } = true;

    public bool ShowInRunAppearanceEntry { get; set; } = true;

    public bool CharacterSelectorTopRight { get; set; }

    public static SkinConfig Load(string path)
    {
        var backupPath = path + ".bak";
        try
        {
            if (File.Exists(path))
            {
                return Deserialize(File.ReadAllText(path));
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn($"读取皮肤配置失败，将尝试备份：{exception.Message}");
        }

        try
        {
            if (File.Exists(backupPath))
            {
                var recovered = Deserialize(File.ReadAllText(backupPath));
                ModLog.Warn("已从 skin_changer.json.bak 恢复皮肤选择设置。");
                return recovered;
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn($"读取皮肤配置备份失败，将使用默认配置：{exception.Message}");
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
        try
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }
        catch (Exception exception)
        {
            // 主配置已经安全落盘；备份失败不应让调用方回滚内存中的成功选择。
            ModLog.Warn("更新皮肤配置备份失败：" + exception.Message);
        }
    }

    private static SkinConfig Deserialize(string json)
    {
        var config = JsonSerializer.Deserialize<SkinConfig>(json, JsonOptions) ?? new SkinConfig();
        config.Selections ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        config.Selections = new Dictionary<string, string>(
            config.Selections,
            StringComparer.OrdinalIgnoreCase);
        config.CharacterIconSelections ??=
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        config.CharacterIconSelections = config.CharacterIconSelections
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) &&
                           !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        config.VisualProviderPriority ??= [];
        config.VisualProviderPriority = config.VisualProviderPriority
            .Where(providerId => !string.IsNullOrWhiteSpace(providerId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.CardSkinPriorities ??=
            new Dictionary<string, List<CardSkinPriorityEntry>>(StringComparer.OrdinalIgnoreCase);
        config.CardSkinPriorities = config.CardSkinPriorities.ToDictionary(
            pair => pair.Key,
            pair => (pair.Value ?? [])
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.OptionId))
                .ToList(),
            StringComparer.OrdinalIgnoreCase);
        config.CardSkinPresets ??= [];
        config.CardSkinPresets = config.CardSkinPresets
            .Where(preset => preset != null && !string.IsNullOrWhiteSpace(preset.Name))
            .Select(preset =>
            {
                preset.Name = preset.Name.Trim();
                preset.CategoryId = string.IsNullOrWhiteSpace(preset.CategoryId)
                    ? null
                    : preset.CategoryId.Trim().ToLowerInvariant();
                preset.CardSkinPriorities ??=
                    new Dictionary<string, List<CardSkinPriorityEntry>>(StringComparer.OrdinalIgnoreCase);
                preset.CardSkinPriorities = preset.CardSkinPriorities.ToDictionary(
                    pair => pair.Key,
                    pair => (pair.Value ?? [])
                        .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.OptionId))
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
                preset.Selections ??=
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                preset.Selections = preset.Selections
                    .Where(pair => pair.Key.StartsWith("cards:", StringComparison.OrdinalIgnoreCase) &&
                                   !string.IsNullOrWhiteSpace(pair.Value))
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase);
                return preset;
            })
            .DistinctBy(
                preset => (preset.CategoryId ?? string.Empty) + "\n" + preset.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.ActiveCardSkinPresets ??=
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        config.ActiveCardSkinPresets = config.ActiveCardSkinPresets
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) &&
                           !string.IsNullOrWhiteSpace(pair.Value) &&
                           config.CardSkinPresets.Any(preset =>
                               preset.CategoryId?.Equals(
                                   pair.Key,
                                   StringComparison.OrdinalIgnoreCase) == true &&
                               preset.Name.Equals(
                                   pair.Value,
                                   StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(
                pair => pair.Key.Trim().ToLowerInvariant(),
                pair => pair.Value.Trim(),
                StringComparer.OrdinalIgnoreCase);
        config.ActiveCardSkinPreset = config.CardSkinPresets.Any(preset =>
            preset.Name.Equals(config.ActiveCardSkinPreset, StringComparison.OrdinalIgnoreCase))
                ? config.CardSkinPresets.First(preset =>
                    preset.Name.Equals(config.ActiveCardSkinPreset, StringComparison.OrdinalIgnoreCase)).Name
                : null;
        config.MonsterSkinPriorities ??=
            new Dictionary<string, List<MonsterSkinPriorityEntry>>(StringComparer.OrdinalIgnoreCase);
        config.MonsterSkinPriorities = config.MonsterSkinPriorities.ToDictionary(
            pair => pair.Key,
            pair => (pair.Value ?? [])
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.OptionId))
                .ToList(),
            StringComparer.OrdinalIgnoreCase);
        config.MonsterSkinCategoryGroups ??=
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        config.MonsterSkinCategoryGroups = config.MonsterSkinCategoryGroups.ToDictionary(
            pair => pair.Key,
            pair => (pair.Value ?? [])
                .Where(groupId => !string.IsNullOrWhiteSpace(groupId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);
        config.EnabledMonsterSkinPriorityCategories ??= [];
        config.EnabledMonsterSkinPriorityCategories = config.EnabledMonsterSkinPriorityCategories
            .Where(categoryId => !string.IsNullOrWhiteSpace(categoryId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.MonsterGroupsFollowingCategory ??= [];
        config.MonsterGroupsFollowingCategory = config.MonsterGroupsFollowingCategory
            .Where(groupId => !string.IsNullOrWhiteSpace(groupId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.MonsterGroupsWithManualSelection ??= [];
        config.MonsterGroupsWithManualSelection = config.MonsterGroupsWithManualSelection
            .Where(groupId => !string.IsNullOrWhiteSpace(groupId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    public string GetSelection(string groupId) =>
        Selections.GetValueOrDefault(groupId, Catalog.SkinCatalog.BaseOptionId);

    public string GetCharacterIconSelection(string groupId) =>
        CharacterIconSelections.GetValueOrDefault(
            groupId,
            CharacterIconSelectionPolicy.FollowCharacterSkinOptionId);
}
