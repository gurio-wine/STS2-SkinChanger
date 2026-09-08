using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STS2SkinChanger.Core;

internal sealed record WorkshopDiscussionPage(int Total, int Start, int PageSize, string[] Posts);
internal static class WorkshopDiscussionSource
{
    public const string Topic = "592940620292752301";
    public const string Url = "https://steamcommunity.com/workshop/filedetails/discussion/3787302680/" + Topic + "/";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex Initialization = Pattern("InitializeCommentThread\\s*\\(\\s*\"ForumTopic\"[^\\{]*\\{");
    private static readonly Regex Divs = Pattern("</?div\\b[^>]*>");
    private static readonly Regex Classes = Pattern("\\bclass\\s*=\\s*[\"']([^\"']*)[\"']");
    private static readonly Regex Tags = Pattern("<[^>]*>");
    private static readonly Regex Scripts = Pattern("<(script|style)\\b[^>]*>.*?</\\1\\s*>");

    public static WorkshopDiscussionPage Parse(string html)
    {
        if (html.Length > 2 * 1024 * 1024) throw new InvalidDataException("Discussion page too large.");
        var script = Scripts.Matches(html).Cast<Match>().FirstOrDefault(m => m.Groups[1].Value.Equals("script", StringComparison.OrdinalIgnoreCase) && Initialization.IsMatch(m.Value))?.Value;
        if (script == null) throw new InvalidDataException("Steam discussion is unavailable or changed format.");
        var init = Initialization.Match(script);
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(script[(init.Index + init.Length - 1)..]));
        using var metadata = JsonDocument.ParseValue(ref reader);
        var data = metadata.RootElement;
        if (data.GetProperty("feature2").GetString() != Topic) throw new InvalidDataException("Unexpected discussion topic.");
        var total = data.GetProperty("total_count").GetInt32();
        var start = data.GetProperty("start").GetInt32();
        var size = data.GetProperty("pagesize").GetInt32();
        if (total is < 0 or > 30000 || start < 0 || size is < 1 or > 100 || start > total)
            throw new InvalidDataException("Invalid discussion pagination.");
        var posts = new List<string>();
        html = Scripts.Replace(html, "");
        var depth = 0;
        var contentStart = -1;
        var replies = 0;
        foreach (Match div in Divs.Matches(html))
        {
            var closing = div.Value.StartsWith("</", StringComparison.Ordinal);
            if (contentStart >= 0)
            {
                depth += closing ? -1 : 1;
                if (depth == 0)
                {
                    posts.Add(WebUtility.HtmlDecode(Tags.Replace(html[contentStart..div.Index], " ")));
                    contentStart = -1;
                }
            }
            else if (!closing)
            {
                var classes = Classes.Match(div.Value).Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (classes.Contains("forum_op") || classes.Contains("commentthread_comment_text"))
                {
                    if (classes.Contains("commentthread_comment_text")) replies++;
                    contentStart = div.Index + div.Length; depth = 1;
                }
            }
        }
        if (contentStart >= 0 || posts.Count == 0 || replies != Math.Min(size, total - start))
            throw new InvalidDataException("Incomplete discussion body.");
        return new(total, start, size, posts.ToArray());
    }

    public static async Task<WorkshopCatalogItem[]> ReadAll(Func<Uri, CancellationToken, Task<string>> fetch,
        IProgress<(int Current, int Total)>? progress, CancellationToken token)
    {
        var first = Parse(await fetch(new Uri(Url), token));
        if (first.Start != 0) throw new InvalidDataException("Discussion did not start at page one.");
        var pages = Math.Max(1, (first.Total + first.PageSize - 1) / first.PageSize);
        var posts = new List<string>(first.Posts);
        progress?.Report((1, pages));
        for (var page = 2; page <= pages; page++)
        {
            token.ThrowIfCancellationRequested();
            var next = Parse(await fetch(new Uri(Url + "?ctp=" + page), token));
            if (next.Total != first.Total || next.PageSize != first.PageSize || next.Start != (page - 1) * first.PageSize)
                throw new InvalidDataException("Discussion changed during pagination; retain the last complete catalog.");
            posts.AddRange(next.Posts);
            if (posts.Sum(p => (long)p.Length) > 32 * 1024 * 1024) throw new InvalidDataException("Discussion exceeds refresh budget.");
            progress?.Report((page, pages));
        }
        return WorkshopSubmissionCode.ReadPosts(posts);
    }

    internal static async Task<string> Fetch(Uri uri, CancellationToken token)
    {
        if (uri.Scheme != "https" || uri.Host != "steamcommunity.com" || uri.AbsolutePath != new Uri(Url).AbsolutePath)
            throw new InvalidDataException("Unexpected discussion URL.");
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("Discussion response too large.");
        using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("Discussion response too large.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
