using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2SkinChanger.Core;

// Recognized means the repository published a sc.info code this browser could read back.
// Unrecognized is the normal state of a repository that never did; Failed means the file
// could not be read this time and the check is retried on the next refresh.
internal enum GitHubRecognition { Pending, Recognized, Unrecognized, Failed }

internal sealed record GitHubSkinEntry(
    string FullName, string Name, string Description, int Stars, DateTimeOffset PushedAt, string Readme,
    GitHubRecognition Recognition, string Error, WorkshopTarget[] Targets, bool RestartRequired,
    string ReleaseTag, string AssetUrl, long AssetSize)
{
    [JsonIgnore] public ulong Id => SkinSourceIdentity.GitHubId(Owner, RepoName);
    [JsonIgnore] public string Owner => FullName.Split('/')[0];
    [JsonIgnore] public string RepoName => FullName.Split('/')[1];
    [JsonIgnore] public GitHubRepoRef Repo => new(Owner, RepoName);
    [JsonIgnore] public string CoverUrl => GitHubSkinPolicy.CoverUrl(Repo);
}

internal sealed record GitHubSkinState(int Format, GitHubSkinEntry[] Entries)
{
    public static GitHubSkinState Empty => new(GitHubSkinCatalog.Format, []);
}

// Recognition results are cached across sessions; the search list alone would make the
// player re-read a sc.info file for every repository on each start.
internal sealed class GitHubSkinCatalog
{
    // Format 3 replaced the downloaded-package recognition with the published sc.info code,
    // so a format-2 cache (which held package titles and versions) is dropped and refetched.
    public const int Format = 3;
    private const int MaxCacheBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 1000;

    public GitHubSkinState State { get; private set; } = GitHubSkinState.Empty;
    public GitHubSkinEntry[] Entries => State.Entries;

    public static GitHubSkinEntry FromRepo(GitHubRepoInfo info) => new(info.Repo.FullName, info.Repo.Name,
        info.Description, info.Stars, info.PushedAt, "", GitHubRecognition.Pending, "", [], true, "", "", 0);

    public void Merge(IEnumerable<GitHubSkinEntry> entries)
    {
        var next = State.Entries.ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (next.TryGetValue(entry.FullName, out var known) && known.PushedAt == entry.PushedAt)
            {
                // Keep the recognition result; only the volatile search fields are refreshed.
                next[entry.FullName] = known with { Stars = entry.Stars, Description = entry.Description, PushedAt = entry.PushedAt };
                continue;
            }
            // A new commit invalidates everything that was read from the old revision: the code,
            // the release it pointed at and the README text.
            next[entry.FullName] = entry with
            {
                Recognition = GitHubRecognition.Pending, Error = "", Targets = [], ReleaseTag = "", AssetUrl = "", AssetSize = 0, Readme = ""
            };
        }
        Publish(next.Values.OrderByDescending(entry => entry.PushedAt).ToArray());
    }

    public void Publish(GitHubSkinEntry entry)
    {
        var list = State.Entries.ToList();
        var index = list.FindIndex(known => known.FullName.Equals(entry.FullName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) list[index] = entry; else list.Add(entry);
        Publish(list.ToArray());
    }

    private void Publish(GitHubSkinEntry[] entries) => State = new(Format, entries);

    public void Restore(string path)
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > MaxCacheBytes) throw new InvalidDataException("GitHub catalog cache too large.");
        GitHubSkinState? state;
        try
        {
            state = JsonSerializer.Deserialize<GitHubSkinState>(File.ReadAllText(path), new JsonSerializerOptions { MaxDepth = 16 });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The caller keeps an empty catalog and re-fetches; a half-understood cache is worse than none.
            throw new InvalidDataException("Unreadable GitHub catalog cache.", ex);
        }
        Validate(state);
        State = state!;
    }

    public void Save(string path, Action<string, Action>? cacheWriter)
    {
        var json = JsonSerializer.Serialize(State);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxCacheBytes)
            json = JsonSerializer.Serialize(State with { Entries = State.Entries.Take(MaxEntries - 1).ToArray() });
        void Write()
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        if (cacheWriter == null) Write(); else cacheWriter(path, Write);
    }

    private static void Validate(GitHubSkinState? state)
    {
        if (state is not { Format: Format, Entries: not null } || state.Entries.Length > MaxEntries ||
            state.Entries.Any(entry => entry == null || !GitHubSkinPolicy.TryParseFullName(entry.FullName, out _) ||
                entry.Name is not { Length: <= 128 } || entry.Description is not { Length: <= 1024 } ||
                entry.Stars < 0 || entry.ReleaseTag is not { Length: <= 128 } ||
                entry.Error is not { Length: <= 512 } || entry.AssetSize is < 0 or > GitHubSkinPolicy.MaxAssetBytes ||
                entry.Readme is not { Length: <= GitHubReadmePolicy.MaxExcerptChars } ||
                entry.AssetUrl is not { Length: <= 4096 } ||
                (entry.AssetUrl.Length > 0 && (!Uri.TryCreate(entry.AssetUrl, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || !GitHubSkinPolicy.IsAllowedHost(uri.Host))) ||
                !Enum.IsDefined(entry.Recognition) || entry.Targets is null || entry.Targets.Length > 512 ||
                entry.Targets.Any(target => target == null || string.IsNullOrWhiteSpace(target.Kind) || target.Kind.Length > 32 ||
                    string.IsNullOrWhiteSpace(target.Target) || target.Target.Length > 160) ||
                entry.Recognition == GitHubRecognition.Recognized && entry.Targets.Length == 0))
            throw new InvalidDataException("Unverified or invalid GitHub catalog cache.");
    }
}

internal enum GitHubSort { Updated, Stars, Name }

internal static class GitHubSortPolicy
{
    public static ulong[] OrderIds(IEnumerable<ulong> ids, IReadOnlyDictionary<ulong, GitHubSkinEntry> entries,
        GitHubSort sort, bool reverse = false)
    {
        var keyed = ids.Select(id => (Id: id, Key: entries.TryGetValue(id, out var entry) ? Key(entry, sort) : null)).ToArray();
        // Dates and stars read newest/biggest first, names read A→Z; the reverse toggle flips that.
        var descending = (sort != GitHubSort.Name) != reverse;
        var ordered = descending
            ? keyed.OrderBy(item => item.Key == null).ThenByDescending(item => item.Key, StringComparer.Ordinal)
            : keyed.OrderBy(item => item.Key == null).ThenBy(item => item.Key, StringComparer.Ordinal);
        return ordered.ThenBy(item => item.Id).Select(item => item.Id).ToArray();
    }

    private static string? Key(GitHubSkinEntry entry, GitHubSort sort) => sort switch
    {
        GitHubSort.Updated => entry.PushedAt.ToString("O"),
        GitHubSort.Stars => entry.Stars.ToString("D10", CultureInfo.InvariantCulture),
        _ => entry.Name.ToUpperInvariant()
    };

    public static string Metric(GitHubSkinEntry? entry, GitHubSort sort) => entry == null ? "—" : sort switch
    {
        // Pure numbers only: the column shows values, labels come from the panel's language table.
        GitHubSort.Stars => entry.Stars.ToString("N0", CultureInfo.CurrentCulture),
        GitHubSort.Updated => entry.PushedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => entry.Name
    };
}
