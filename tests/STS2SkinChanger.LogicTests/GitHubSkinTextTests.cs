using STS2SkinChanger.Core;

internal static class GitHubSkinTextTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        var keys = Enum.GetValues<GitHubSkinTextKey>();
        foreach (var language in new[] { "zhs", "zht", "eng", "deu", "esp", "spa", "fra", "ita", "jpn", "kor", "pol", "ptb", "rus", "tha", "tur" })
            foreach (var key in keys)
                Require(!string.IsNullOrWhiteSpace(GitHubSkinText.ForLanguage(language, key)),
                    $"语言 {language} 的 {key} 不能为空，玩家界面不允许出现空白按钮。");
        Require(GitHubSkinText.ForLanguage("eng", GitHubSkinTextKey.Steam) == "Steam" &&
            GitHubSkinText.ForLanguage("eng", GitHubSkinTextKey.GitHub) == "GitHub",
            "来源名称在各语言保持原文，便于玩家对照。");
        foreach (var language in new[] { "zhs", "zht", "deu", "esp", "spa", "fra", "ita", "jpn", "kor", "pol", "ptb", "rus", "tha", "tur" })
            Require(GitHubSkinText.ForLanguage(language, GitHubSkinTextKey.Steam) == "Steam" &&
                GitHubSkinText.ForLanguage(language, GitHubSkinTextKey.GitHub) == "GitHub",
                $"语言 {language} 的来源名称必须保持原文，否则玩家无法对照工坊与 GitHub。");
        Require(GitHubSkinText.ForLanguage("unknown", GitHubSkinTextKey.Install) == GitHubSkinText.ForLanguage("eng", GitHubSkinTextKey.Install),
            "未知语言必须回退英文，而不是抛异常或空白。");
        Console.WriteLine("GitHub skin text passed: 15 languages, no empty labels.");
    }
}
