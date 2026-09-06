using System.Text.Json;
using System.Text.RegularExpressions;

namespace STS2SkinChanger.Core;

internal sealed record ModThemeSettings
{
    public string PanelColor { get; init; } = "#FFFFFF";
    public float PanelOpacity { get; init; } = .18f;
    public float PanelBlur { get; init; } = 2.5f;
    public string SelectionColor { get; init; } = "#FFFFFF";
    public float SelectionOpacity { get; init; } = .28f;
    public float SelectionBlur { get; init; } = 3.1f;
    public string ButtonColor { get; init; } = "#3C5F82";
    public float ButtonOpacity { get; init; } = .85f;
    public float ButtonBlur { get; init; } = 0f;
    public string HoverColor { get; init; } = "#4B7392";
    public string TextColor { get; init; } = "#FFF6E2";
    public string AccentColor { get; init; } = "#EFC850";
    public string BorderColor { get; init; } = "#7394AD";
    public int BorderWidth { get; init; } = 1;
    public int CornerRadius { get; init; } = 12;
    public float FontScale { get; init; } = 1f;
    public int TextOutline { get; init; } = 3;
    public bool TextShadowEnabled { get; init; } = false;
    public string TextShadowColor { get; init; } = "#000000";
    public float TextShadowOpacity { get; init; } = .55f;
    public int TextShadowOffsetX { get; init; } = 2;
    public int TextShadowOffsetY { get; init; } = 2;
    public int TextShadowSize { get; init; } = 1;

    public ModThemeSettings Normalize()
    {
        var defaults = new ModThemeSettings();
        return this with
        {
            PanelColor = Hex(PanelColor, defaults.PanelColor),
            SelectionColor = Hex(SelectionColor, defaults.SelectionColor),
            ButtonColor = Hex(ButtonColor, defaults.ButtonColor),
            HoverColor = Hex(HoverColor, defaults.HoverColor),
            TextColor = Hex(TextColor, defaults.TextColor),
            AccentColor = Hex(AccentColor, defaults.AccentColor),
            BorderColor = Hex(BorderColor, defaults.BorderColor),
            TextShadowColor = Hex(TextShadowColor, defaults.TextShadowColor),
            PanelOpacity = Number(PanelOpacity, 0, 1, defaults.PanelOpacity),
            SelectionOpacity = Number(SelectionOpacity, 0, 1, defaults.SelectionOpacity),
            ButtonOpacity = Number(ButtonOpacity, 0, 1, defaults.ButtonOpacity),
            PanelBlur = Number(PanelBlur, 0, 5, defaults.PanelBlur),
            SelectionBlur = Number(SelectionBlur, 0, 5, defaults.SelectionBlur),
            ButtonBlur = Number(ButtonBlur, 0, 5, defaults.ButtonBlur),
            TextShadowOpacity = Number(TextShadowOpacity, 0, 1, defaults.TextShadowOpacity),
            TextShadowOffsetX = Math.Clamp(TextShadowOffsetX, -12, 12),
            TextShadowOffsetY = Math.Clamp(TextShadowOffsetY, -12, 12),
            TextShadowSize = Math.Clamp(TextShadowSize, 0, 8),
            BorderWidth = Math.Clamp(BorderWidth, 0, 5), CornerRadius = Math.Clamp(CornerRadius, 0, 24),
            FontScale = Number(FontScale, .75f, 1.5f, 1), TextOutline = Math.Clamp(TextOutline, 0, 8)
        };
    }

    private static string Hex(string? value, string fallback) =>
        value != null && Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value.ToUpperInvariant() : fallback;
    private static float Number(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

internal static class ModThemeStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static ModThemeSettings Load(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try { return (JsonSerializer.Deserialize<ModThemeSettings>(File.ReadAllText(candidate), Options) ?? new()).Normalize(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { System.Diagnostics.Trace.TraceWarning("读取界面主题失败，将尝试备份/默认主题：" + e.Message); }
        }
        return new();
    }

    public static void Save(string path, ModThemeSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalize(), Options));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        try { File.Copy(path, path + ".bak", true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("主题已保存，但备份更新失败：" + e.Message); }
    }
}

internal sealed class ModThemeSession(ModThemeSettings saved)
{
    private ModThemeSettings _saved = saved.Normalize();
    public ModThemeSettings Current { get; private set; } = saved.Normalize();
    public event Action? Changed;
    public void Preview(ModThemeSettings settings)
    {
        var next = settings.Normalize();
        if (next == Current) return;
        Current = next;
        Changed?.Invoke();
    }
    public void Revert() => Preview(_saved);
    public void Reset() => Preview(new());
    public void Save(string path) { ModThemeStore.Save(path, Current); _saved = Current; }
}
