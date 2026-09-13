using System.Text.Json;

namespace STS2SkinChanger.Core;

internal sealed record GitHubRepoRef(string Owner, string Name)
{
    public string FullName => Owner + "/" + Name;
    public string Url => "https://github.com/" + FullName;
}

internal sealed record GitHubRepoInfo(GitHubRepoRef Repo, string Description, int Stars, DateTimeOffset PushedAt);

internal sealed record GitHubReleaseAsset(string Name, long Size, string Url);

internal sealed record GitHubReleaseInfo(string Tag, string Title, DateTimeOffset PublishedAt, GitHubReleaseAsset[] Assets);

// Everything GitHub returns is untrusted input: parse defensively, cap every field,
// and never let one malformed repository abort the whole refresh.
internal static class GitHubSkinPolicy
{
    public const string SelfRepo = "gurio-wine/STS2-SkinChanger";
    public const string Topic = "sts2-sc-mod";
    public const long MaxAssetBytes = 128L * 1024 * 1024;
    public const int SearchPageSize = 100;
    public const int MaxSearchPages = 3;
    private const int MaxJsonBytes = 4 * 1024 * 1024;

    private static readonly string[] Hosts =
        ["api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
         "codeload.github.com", "opengraph.githubassets.com"];

    public static Uri SearchUri(int page) => new(
        $"https://api.github.com/search/repositories?q=topic%3A{Topic}&sort=updated&order=desc&per_page={SearchPageSize}&page={Math.Clamp(page, 1, MaxSearchPages)}");

    public static Uri LatestReleaseUri(GitHubRepoRef repo) => new($"https://api.github.com/repos/{repo.FullName}/releases/latest");

    public static string CoverUrl(GitHubRepoRef repo) => $"https://opengraph.githubassets.com/1/{repo.FullName}";

    public static bool IsAllowedHost(string? host) => host != null && Hosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    public static bool TryParseFullName(string? fullName, out GitHubRepoRef repo)
    {
        repo = null!;
        var parts = (fullName ?? "").Split('/');
        if (parts.Length != 2 || !SafeName(parts[0]) || !SafeName(parts[1])) return false;
        repo = new(parts[0], parts[1]);
        return true;
    }

    private static bool SafeName(string value) => value.Length is > 0 and <= 100 &&
        // Every segment must carry at least one real character: "." and ".." are path tricks, not names.
        value.Any(c => c != '.') &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static bool Eligible(GitHubRepoRef repo, bool fork) =>
        !fork && !repo.FullName.Equals(SelfRepo, StringComparison.OrdinalIgnoreCase);

    public static GitHubRepoInfo[] ParseSearch(string json)
    {
        if (json.Length is 0 or > MaxJsonBytes) return [];
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
            var result = new List<GitHubRepoInfo>();
            foreach (var item in items.EnumerateArray().Take(1000))
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("full_name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !TryParseFullName(name.GetString(), out var repo) ||
                    !item.TryGetProperty("fork", out var fork) || fork.ValueKind != JsonValueKind.False || !Eligible(repo, false)) continue;
                var stars = item.TryGetProperty("stargazers_count", out var count) && count.TryGetInt32(out var value) ? Math.Max(0, value) : 0;
                var pushed = item.TryGetProperty("pushed_at", out var stamp) && stamp.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(stamp.GetString(), out var parsed) ? parsed : default;
                var description = item.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString() ?? "" : "";
                result.Add(new(repo, description[..Math.Min(description.Length, 1024)], stars, pushed));
            }
            return result.DistinctBy(info => info.Repo.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException) { return []; }
    }

    public static GitHubReleaseInfo? ParseRelease(string json)
    {
        if (json.Length is 0 or > MaxJsonBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            var tag = root.TryGetProperty("tag_name", out var tagElement) && tagElement.ValueKind == JsonValueKind.String
                ? tagElement.GetString() ?? "" : "";
            var title = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? "" : "";
            var published = root.TryGetProperty("published_at", out var stamp) && stamp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(stamp.GetString(), out var parsed) ? parsed : default;
            var list = new List<GitHubReleaseAsset>();
            foreach (var asset in assets.EnumerateArray().Take(64))
            {
                if (asset.ValueKind != JsonValueKind.Object ||
                    !asset.TryGetProperty("name", out var assetName) || assetName.ValueKind != JsonValueKind.String ||
                    !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String) continue;
                var name = assetName.GetString() ?? "";
                var size = asset.TryGetProperty("size", out var bytes) && bytes.TryGetInt64(out var value) ? value : 0;
                if (name.Length is 0 or > 128 || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(uri.Host)) continue;
                list.Add(new(name, size, uri.AbsoluteUri));
            }
            return new(tag[..Math.Min(tag.Length, 128)], title[..Math.Min(title.Length, 256)], published, list.ToArray());
        }
        catch (JsonException) { return null; }
    }

    public static GitHubReleaseAsset? SelectAsset(GitHubReleaseInfo release, string repoName)
    {
        var zips = release.Assets.Where(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            asset.Size is > 0 and <= MaxAssetBytes && !asset.Name.Contains("..", StringComparison.Ordinal)).ToArray();
        return zips.FirstOrDefault(asset => Path.GetFileNameWithoutExtension(asset.Name).Equals(repoName, StringComparison.OrdinalIgnoreCase))
            ?? zips.OrderByDescending(asset => asset.Size).FirstOrDefault();
    }

    // A repository that was never recognized still belongs in the unfiltered list,
    // but it must not pretend to be a character, card or monster skin.
    public static bool KeepWithoutTargets(ulong id, int targetCount, string kind) =>
        targetCount == 0 && kind.Length == 0 && SkinSourceIdentity.IsGitHub(id);
}
