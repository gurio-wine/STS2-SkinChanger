using STS2SkinChanger.Core;

internal static class SkinSourceTests
{
    private static readonly string[] Languages =
        ["zhs", "zht", "eng", "deu", "esp", "spa", "fra", "ita", "jpn", "kor", "pol", "ptb", "rus", "tha", "tur"];

    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        // The picker once appended the current value as a third entry, which showed up as a
        // second entry labelled "GitHub" that actually selected Steam.
        Require(SkinSources.Options.Length == 2 && SkinSources.Options[0] == SkinSources.Steam &&
            SkinSources.Options[1] == SkinSources.GitHub,
            "来源只有两个选项，Steam 在前，默认就是 Steam。");
        Require(SkinSources.OptionAt(0) == SkinSources.Steam && SkinSources.OptionAt(1) == SkinSources.GitHub &&
            SkinSources.OptionAt(-1) == SkinSources.Steam && SkinSources.OptionAt(7) == SkinSources.Steam,
            "越界的下拉索引必须回到默认来源，不能抛出或选到别的来源。");
        Require(SkinSources.IndexOf(SkinSources.Steam) == 0 && SkinSources.IndexOf(SkinSources.GitHub) == 1 &&
            SkinSources.IndexOf("") == 0 && SkinSources.IndexOf("steam") == 0,
            "选中项必须与当前来源一致，未知值按默认来源处理。");
        Require(SkinSources.Normalize(null) == SkinSources.Steam && SkinSources.Normalize("GITHUB") == SkinSources.GitHub,
            "来源值大小写不敏感，空值按 Steam 处理。");
        foreach (var language in Languages)
        {
            var names = SkinSources.Options.Select(option => SkinSources.Name(language, option)).ToArray();
            Require(names.Distinct(StringComparer.Ordinal).Count() == 2,
                $"语言 {language} 的两个来源名称必须不同，否则玩家无法分辨，也会选错来源。");
            Require(names[0] == "Steam" && names[1] == "GitHub",
                $"语言 {language} 的来源名称保持原文，便于对照工坊与 GitHub。");
        }
        Console.WriteLine("Skin source passed: two distinct options, Steam default.");
    }
}
