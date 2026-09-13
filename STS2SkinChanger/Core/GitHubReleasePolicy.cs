namespace STS2SkinChanger.Core;

// Reading a release costs one API request, so the answer is cached next to the repository. That
// cache may only answer the refresh's question - "is there a newer package than the installed
// one?" - which the tag alone answers. An explicit scan or install asks a different question:
// "where is the package right now?". A release is not immutable, its author can attach the
// package after publishing or replace the file under the same tag, so the cached answer must not
// be reused there. Reusing it is what left a player facing "This release has no downloadable zip
// package." for a release whose zip had been uploaded 22 minutes after the first read.
internal static class GitHubReleasePolicy
{
    public static bool ReuseCachedRead(string cachedTag, bool packageRequested) =>
        cachedTag.Length > 0 && !packageRequested;

    // The pure half of the read: whatever the API returned, turned into the fields the card and
    // the download use. Kept free of the client so the shape of a release can be tested offline.
    public static GitHubSkinEntry WithRelease(GitHubSkinEntry entry, string? json)
    {
        var release = json == null ? null : GitHubSkinPolicy.ParseRelease(json);
        if (release == null) return entry;
        var asset = GitHubSkinPolicy.SelectAsset(release, entry.RepoName);
        return entry with
        {
            ReleaseTag = release.Tag, AssetUrl = asset?.Url ?? "", AssetSize = asset?.Size ?? 0, AssetDigest = asset?.Digest ?? ""
        };
    }
}
