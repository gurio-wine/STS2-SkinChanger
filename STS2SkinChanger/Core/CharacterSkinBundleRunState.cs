using System.Text.Json;

namespace STS2SkinChanger.Core;

// Contains appearance choices only, never game state or third-party assets. One record per
// local profile and SP/MP save slot; identity prevents a reused seed/slot from borrowing a run.
internal sealed class CharacterSkinBundleRunState
{
    public int Version { get; set; } = 1;
    public string RunIdentity { get; set; } = string.Empty;
    public string CharacterGroupId { get; set; } = string.Empty;
    public string BundleName { get; set; } = string.Empty;
    public string? RandomCharacterOptionId { get; set; }
    public List<CardSkinPreset> Cards { get; set; } = [];
    public List<MonsterSkinPreset> Monsters { get; set; } = [];

    internal static string Identity(long startTime, string seed, ulong localPlayer, IEnumerable<ulong> players) =>
        JsonSerializer.Serialize(new { startTime, seed, localPlayer, players = players.Order().ToArray() });
}

internal static class CharacterSkinBundleRunStore
{
    internal static CharacterSkinBundleRunState? LoadMatching(string path, string identity)
    {
        if (!File.Exists(path)) return null;
        CharacterSkinBundleRunState? state;
        try { state = JsonSerializer.Deserialize<CharacterSkinBundleRunState>(File.ReadAllText(path)); }
        catch (JsonException) when (File.Exists(path + ".bak"))
        {
            state = JsonSerializer.Deserialize<CharacterSkinBundleRunState>(File.ReadAllText(path + ".bak"));
        }
        if (state?.Version != 1 || state.Cards == null || state.Monsters == null)
            throw new InvalidDataException("皮肤包对局记录格式无效。");
        return state.RunIdentity == identity ? state : null;
    }

    internal static void Save(string path, CharacterSkinBundleRunState state)
    {
        var json = JsonSerializer.Serialize(state);
        if (File.Exists(path) && File.ReadAllText(path) == json) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        try { File.Copy(path, path + ".bak", overwrite: true); }
        catch (IOException exception) { ModLog.Warn("皮肤包对局记录已保存，但备份失败：" + exception.Message); }
    }
}
