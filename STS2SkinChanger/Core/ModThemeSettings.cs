using System.Text.Json;
using System.Text.RegularExpressions;

namespace STS2SkinChanger.Core;

internal sealed record ModThemeSettings
{
    public string PanelColor { get; init; } = "#FFFFFF";
    public float PanelOpacity { get; init; } = .2f;
    public float PanelBlur { get; init; } = 3f;
    public string SelectionColor { get; init; } = "#FFFFFF";
    public float SelectionOpacity { get; init; } = .5f;
    public float SelectionBlur { get; init; } = 2f;
    public string SelectionHoverColor { get; init; } = "#FFFFFF";
    public float SelectionHoverOpacity { get; init; } = .5f;
    public float SelectionHoverBlur { get; init; } = 2f;
    public string ButtonColor { get; init; } = "#FFFFFF";
    public float ButtonOpacity { get; init; } = .21f;
    public float ButtonBlur { get; init; } = 2f;
    public string HoverColor { get; init; } = "#DFDFDF";
    public string DropdownColor { get; init; } = "#FFFFFF";
    public float DropdownOpacity { get; init; } = .19f;
    public float DropdownBlur { get; init; } = 3f;
    public string DropdownHoverColor { get; init; } = "#DFDFDF";
    public float DropdownHoverOpacity { get; init; } = .5f;
    public string DropdownSelectionColor { get; init; } = "#FFFFFF";
    public float DropdownSelectionOpacity { get; init; } = .3f;
    public string DropdownSelectionHoverColor { get; init; } = "#DFDFDF";
    public float DropdownSelectionHoverOpacity { get; init; } = .5f;
    public string DropdownBorderColor { get; init; } = "#D3D3D3";
    public int DropdownBorderWidth { get; init; } = 0;
    public int DropdownCornerRadius { get; init; } = 10;
    public string TextColor { get; init; } = "#FFF6E2";
    public string AccentColor { get; init; } = "#FFEAA9";
    public string BorderColor { get; init; } = "#D3D3D3";
    public int BorderWidth { get; init; } = 0;
    public int CornerRadius { get; init; } = 10;
    public float FontScale { get; init; } = 1f;
    public int TextOutline { get; init; } = 5;
    public bool TextShadowEnabled { get; init; } = true;
    public string TextShadowColor { get; init; } = "#000000";
    public float TextShadowOpacity { get; init; } = .5f;
    public int TextShadowOffsetX { get; init; } = 2;
    public int TextShadowOffsetY { get; init; } = 2;
    public int TextShadowSize { get; init; } = 3;

    public ModThemeSettings Normalize()
    {
        var defaults = new ModThemeSettings();
        return this with
        {
            PanelColor = Hex(PanelColor, defaults.PanelColor),
            SelectionColor = Hex(SelectionColor, defaults.SelectionColor),
            SelectionHoverColor = Hex(SelectionHoverColor, defaults.SelectionHoverColor),
            SelectionHoverOpacity = Number(SelectionHoverOpacity, 0, 1, defaults.SelectionHoverOpacity),
            SelectionHoverBlur = Number(SelectionHoverBlur, 0, 5, defaults.SelectionHoverBlur),
            ButtonColor = Hex(ButtonColor, defaults.ButtonColor),
            HoverColor = Hex(HoverColor, defaults.HoverColor),
            DropdownColor = Hex(DropdownColor, defaults.DropdownColor),
            DropdownHoverColor = Hex(DropdownHoverColor, defaults.DropdownHoverColor),
            DropdownSelectionColor = Hex(DropdownSelectionColor, defaults.DropdownSelectionColor),
            DropdownSelectionHoverColor = Hex(DropdownSelectionHoverColor, defaults.DropdownSelectionHoverColor),
            DropdownSelectionHoverOpacity = Number(DropdownSelectionHoverOpacity, 0, 1, defaults.DropdownSelectionHoverOpacity),
            DropdownBorderColor = Hex(DropdownBorderColor, defaults.DropdownBorderColor),
            DropdownOpacity = Number(DropdownOpacity, 0, 1, defaults.DropdownOpacity),
            DropdownBlur = Number(DropdownBlur, 0, 5, defaults.DropdownBlur),
            DropdownHoverOpacity = Number(DropdownHoverOpacity, 0, 1, defaults.DropdownHoverOpacity),
            DropdownSelectionOpacity = Number(DropdownSelectionOpacity, 0, 1, defaults.DropdownSelectionOpacity),
            DropdownBorderWidth = Math.Clamp(DropdownBorderWidth, 0, 5),
            DropdownCornerRadius = Math.Clamp(DropdownCornerRadius, 0, 24),
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
            try { return Decode(File.ReadAllText(candidate)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { System.Diagnostics.Trace.TraceWarning("读取界面主题失败，将尝试备份/默认主题：" + e.Message); }
        }
        return new();
    }

    private static ModThemeSettings Decode(string json)
    {
        var settings = (JsonSerializer.Deserialize<ModThemeSettings>(json, Options) ?? new()).Normalize();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return settings;
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // One-time initialization only: saved independent fields never inherit later edits
        // to the panel, buttons or compendium selection theme.
        return settings with
        {
            SelectionHoverColor = keys.Contains(nameof(settings.SelectionHoverColor)) ? settings.SelectionHoverColor : settings.SelectionColor,
            SelectionHoverOpacity = keys.Contains(nameof(settings.SelectionHoverOpacity)) ? settings.SelectionHoverOpacity : settings.SelectionOpacity,
            SelectionHoverBlur = keys.Contains(nameof(settings.SelectionHoverBlur)) ? settings.SelectionHoverBlur : settings.SelectionBlur,
            DropdownSelectionHoverColor = keys.Contains(nameof(settings.DropdownSelectionHoverColor)) ? settings.DropdownSelectionHoverColor :
                keys.Contains(nameof(settings.DropdownHoverColor)) ? settings.DropdownHoverColor : settings.HoverColor,
            DropdownSelectionHoverOpacity = keys.Contains(nameof(settings.DropdownSelectionHoverOpacity)) ? settings.DropdownSelectionHoverOpacity :
                keys.Contains(nameof(settings.DropdownHoverOpacity)) ? settings.DropdownHoverOpacity : settings.ButtonOpacity,
            DropdownColor = keys.Contains(nameof(settings.DropdownColor)) ? settings.DropdownColor : settings.PanelColor,
            DropdownOpacity = keys.Contains(nameof(settings.DropdownOpacity)) ? settings.DropdownOpacity : settings.PanelOpacity,
            DropdownHoverColor = keys.Contains(nameof(settings.DropdownHoverColor)) ? settings.DropdownHoverColor : settings.HoverColor,
            DropdownHoverOpacity = keys.Contains(nameof(settings.DropdownHoverOpacity)) ? settings.DropdownHoverOpacity : settings.ButtonOpacity,
            DropdownSelectionColor = keys.Contains(nameof(settings.DropdownSelectionColor)) ? settings.DropdownSelectionColor : settings.SelectionColor,
            DropdownSelectionOpacity = keys.Contains(nameof(settings.DropdownSelectionOpacity)) ? settings.DropdownSelectionOpacity : settings.SelectionOpacity,
            DropdownBorderColor = keys.Contains(nameof(settings.DropdownBorderColor)) ? settings.DropdownBorderColor : settings.BorderColor,
            DropdownBorderWidth = keys.Contains(nameof(settings.DropdownBorderWidth)) ? settings.DropdownBorderWidth : settings.BorderWidth,
            DropdownCornerRadius = keys.Contains(nameof(settings.DropdownCornerRadius)) ? settings.DropdownCornerRadius : settings.CornerRadius
        };
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
