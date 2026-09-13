namespace STS2SkinChanger.Core;

// Reading a release costs one API request, and a release is not immutable: its author can attach
// the package after publishing, replace the file under a tag that never changes, or publish a new
// release without pushing a commit. So no cached answer may ever be reused for either of the two
// questions asked here.
// "Is there a newer package than the one I installed?" is answered by the release the repository
// publishes now, compared with the digest of the package that was written into the mods folder.
// Every refresh reads that release for every installed repository - and for no other, because a
// repository nobody installed has nothing to compare against. This is what the panel's "updatable"
// state is computed from, and it cannot be answered from a cache: caching the first read is what
// left a card saying "up to date" for a package the author had already replaced.
// "Where is the package right now?" is asked by an explicit scan or install, which reads the live
// release too: reusing the cache there once left a player facing "This release has no downloadable
// zip package." for a release whose zip had been uploaded 22 minutes after the first read.
internal static class GitHubReleasePolicy
{
    // The repositories a refresh spends one request on each: the installed ones, most recently
    // pushed first. The cap keeps a large mods folder from turning one refresh into a long queue;
    // whatever is left is checked on the next round.
    public static GitHubSkinEntry[] ReleaseChecks(IEnumerable<GitHubSkinEntry> entries, Func<ulong, bool> installed, int max) =>
        entries.Where(entry => installed(entry.Id)).OrderByDescending(entry => entry.PushedAt).Take(max).ToArray();

    // The pure half of the read: whatever the API returned, turned into the fields the card and
    // the download use. Kept free of the client so the shape of a release can be tested offline.
    // A response that cannot be read leaves the entry as it was - "no answer" is not "no package".
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
