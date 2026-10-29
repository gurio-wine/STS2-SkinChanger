using System.Text.RegularExpressions;

namespace STS2SkinChanger.Core;

// A repository README is the only description a GitHub skin has, and it is written for the
// repository page, not for a seven-line tooltip: badges, HTML, tables and code fences all
// have to go before the text is worth showing to a player.
internal static class GitHubReadmePolicy
{
    // The excerpt is persisted in the catalog, so it stays well inside the cached field cap.
    public const int MaxExcerptChars = 800;
    private const int MaxInputChars = 64 * 1024;
    private static readonly Regex Fences = new(@"```.*?```|~~~.*?~~~", RegexOptions.Singleline);
    private static readonly Regex Comments = new(@"<!--.*?-->", RegexOptions.Singleline);
    private static readonly Regex Images = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Singleline);
    private static readonly Regex Links = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Singleline);
    private static readonly Regex Html = new("<[^>]+>", RegexOptions.Singleline);
    private static readonly Regex Ordered = new(@"^\d+[.)]\s+");
    private static readonly Regex Spaces = new(@"\s+");

    public static string ToExcerpt(string? markdown, int maxChars)
    {
        if (maxChars <= 0 || string.IsNullOrWhiteSpace(markdown)) return "";
        var text = markdown.Length > MaxInputChars ? markdown[..MaxInputChars] : markdown;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Fences.Replace(text, " ");
        text = Comments.Replace(text, " ");
        text = Images.Replace(text, " ");
        text = Links.Replace(text, "$1");
        text = Html.Replace(text, " ");
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            // Table pipes and runs of whitespace first, so a table's rule row is recognisable
            // as punctuation instead of surviving as a line of dashes.
            var line = Spaces.Replace(raw.Replace("|", " "), " ").Trim();
            // A rule row, a horizontal rule and an empty line all mean nothing in a tooltip.
            if (line.Length == 0 || line.All(c => c is ' ' or '-' or ':' or '=' or '+' or '*' or '_' or '#' or '>' or '~')) continue;
            // Line furniture: headings, quotes and bullets are not part of the sentence.
            line = line.TrimStart('#', '>', '-', '*', '+', '=');
            line = Ordered.Replace(line, "");
            line = line.Replace("**", "").Replace("__", "").Replace("`", "").Replace("~~", "").Trim();
            if (line.Length > 0) lines.Add(line);
        }
        var joined = string.Join(" ", lines);
        if (joined.Length <= maxChars) return joined;
        var cut = joined[..maxChars];
        // Never end mid-word when a boundary exists; the label trims the rest with an ellipsis.
        var space = cut.LastIndexOf(' ');
        if (space >= maxChars / 2) cut = cut[..space];
        return cut.TrimEnd();
    }
}
