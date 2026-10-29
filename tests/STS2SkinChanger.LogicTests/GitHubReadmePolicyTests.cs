using STS2SkinChanger.Core;

internal static class GitHubReadmePolicyTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        var readme = """
            # Silent Skin

            [![build](https://img.shields.io/badge/build-passing-green)](https://example.com/ci)
            ![preview](docs/preview.png)

            Replaces the **Silent** character with a raven-themed <b>skin</b>.
            See the [repository](https://github.com/qbyoffice/SilentSkinMod) for details.

            ```bash
            dotnet build
            ```

            | key | value |
            | --- | ----- |
            | id  | raven |

            1. Install the mod
            2. Restart the game

            ---
            """;
        var excerpt = GitHubReadmePolicy.ToExcerpt(readme, GitHubReadmePolicy.MaxExcerptChars);
        Require(excerpt.Contains("Silent Skin") && excerpt.Contains("raven-themed skin"),
            "标题与正文必须保留，工具提示要给出可读的简介。");
        Require(excerpt.Contains("See the repository for details"),
            "链接要降级成它的文字，不能整段消失。");
        Require(!excerpt.Contains("shields.io") && !excerpt.Contains("preview.png") && !excerpt.Contains("<b>") &&
            !excerpt.Contains('|') && !excerpt.Contains('`') && !excerpt.Contains("dotnet build"),
            "徽章、图片、HTML、表格符、代码块都不是给玩家看的简介。");
        Require(excerpt.Contains("Install the mod") && excerpt.Contains("Restart the game") && !excerpt.Contains("1."),
            "有序列表要保留文字、去掉编号。");
        Require(!excerpt.Contains("---") && !excerpt.Contains("\n\n"),
            "分隔线与空行不能进入简介，否则七行里全是空白。");

        Require(GitHubReadmePolicy.ToExcerpt(null, 100) == "" && GitHubReadmePolicy.ToExcerpt("   ", 100) == "" &&
            GitHubReadmePolicy.ToExcerpt("text", 0) == "",
            "空简介、空白简介和零长度上限都必须返回空串而不是抛异常。");
        Require(GitHubReadmePolicy.ToExcerpt("[![a](x)](y)", 100) == "",
            "只有徽章的 README 不能变成一串空括号或图片地址。");

        var long_ = GitHubReadmePolicy.ToExcerpt(string.Join(' ', Enumerable.Repeat("word", 500)), 100);
        Require(long_.Length <= 100 && !long_.EndsWith(' ') && long_.Split(' ').All(part => part == "word"),
            "超长简介必须截断、不留半个单词，并遵守长度上限。");
        Console.WriteLine("GitHub readme policy passed: markdown furniture removed, excerpt capped.");
    }
}
