using System.Text.Json;

namespace STS2SkinChanger.Core;

internal sealed record ModThemePreset(string Id, string Name, ModThemeSettings Settings);

// Independent of skin providers, skin presets and run saves. Default is part of the
// release, not the first theme somebody happens to have when opening this window.
internal sealed class ModThemePresets
{
    public const string DefaultId = "default";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private List<ModThemePreset> _users;
    public IReadOnlyList<ModThemePreset> Presets => [new(DefaultId, "", new()), .. _users];

    public ModThemePresets(string path) { _path = path; _users = Load(path); }

    public string Create(string name, ModThemeSettings settings)
    {
        var preset = new ModThemePreset(Guid.NewGuid().ToString("N"), ValidName(name), settings.Normalize());
        Save([.. _users, preset]);
        return preset.Id;
    }

    public bool Rename(string id, string name)
    {
        var index = _users.FindIndex(p => p.Id == id);
        if (index < 0) return false;
        var next = new List<ModThemePreset>(_users);
        next[index] = next[index] with { Name = ValidName(name, id) };
        Save(next);
        return true;
    }

    public bool Overwrite(string id, ModThemeSettings settings)
    {
        var index = _users.FindIndex(p => p.Id == id);
        if (index < 0) return false;
        var next = new List<ModThemePreset>(_users);
        next[index] = next[index] with { Settings = settings.Normalize() };
        Save(next);
        return true;
    }

    public bool Delete(string id)
    {
        if (!_users.Any(p => p.Id == id)) return false;
        Save(_users.Where(p => p.Id != id).ToList());
        return true;
    }

    private string ValidName(string name, string? excluding = null)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 100 || _users.Any(p => p.Id != excluding &&
                p.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Preset name must be unique and contain 1–100 characters.", nameof(name));
        return trimmed;
    }

    private static List<ModThemePreset> Load(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var data = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(candidate), Options);
                if (data?.Version != 1 || data.Presets == null) throw new JsonException("Invalid theme preset file.");
                var ids = new HashSet<string>(StringComparer.Ordinal) { DefaultId };
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var result = new List<ModThemePreset>();
                foreach (var preset in data.Presets)
                {
                    if (preset?.Settings == null || string.IsNullOrWhiteSpace(preset.Id) ||
                        string.IsNullOrWhiteSpace(preset.Name)) continue;
                    var name = preset.Name.Trim();
                    if (name.Length > 100 || ids.Contains(preset.Id) || names.Contains(name)) continue;
                    ids.Add(preset.Id); names.Add(name);
                    result.Add(preset with { Name = name, Settings = preset.Settings.Normalize() });
                }
                return result;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { System.Diagnostics.Trace.TraceWarning("读取主题预设失败，将尝试备份/默认预设：" + e.Message); }
        }
        return [];
    }

    private void Save(List<ModThemePreset> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = _path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new PresetFile { Version = 1, Presets = next }, Options));
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        _users = next; // Never report an in-memory success before the primary file commits.
        try { File.Copy(_path, _path + ".bak", true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("主题预设已保存，但备份更新失败：" + e.Message); }
    }

    private sealed class PresetFile
    {
        public int Version { get; init; }
        public List<ModThemePreset>? Presets { get; init; }
    }
}

internal sealed class ThemeSaveFeedback
{
    public const double DurationSeconds = 1;
    private int _revision;
    public bool Active { get; private set; }
    public int Begin() { Active = true; return ++_revision; }
    public void Cancel() { Active = false; ++_revision; }
    public bool Expire(int revision)
    {
        if (!Active || revision != _revision) return false;
        Active = false;
        return true;
    }
}
