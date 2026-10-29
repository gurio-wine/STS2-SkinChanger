namespace STS2SkinChanger.Core;

internal readonly record struct GitHubInfoResult(WorkshopCatalogItem? Item, string Error);

// An author who wants the browser to treat a repository as a skin publishes the code the
// scan produced in a file called sc.info at the repository root. That file is the only
// thing this source trusts: a repository without one is listed as "not recognized"
// instead of being downloaded and scanned behind the player's back.
internal static class GitHubInfoPolicy
{
    public const string FileName = "sc.info";
    public const int MaxBytes = 64 * 1024;

    // The code is read back through the same decoder the Workshop submission flow uses, so a
    // code that the browser accepts here is a code the Workshop browser would accept too.
    // Everything that fails - a missing file, prose without a code, a code that belongs to
    // another repository, a broken checksum - is reported as one short token.
    public static GitHubInfoResult Read(string? text, ulong expectedId)
    {
        if (text is null) return new(null, "missing");
        if (text.Length > MaxBytes) return new(null, "too-large");
        if (string.IsNullOrWhiteSpace(text)) return new(null, "no-code");
        var read = WorkshopSubmissionCodec.Read([new WorkshopCodePost(text, "", 0, 0)]);
        foreach (var candidate in read.Candidates)
            if (candidate.Payload.Item.Id == expectedId) return new(candidate.Payload.Item, "");
        // A valid code for a different id is a copy-and-paste mistake by the author, not a
        // reason to bind this repository to somebody else's targets.
        if (read.Candidates.Length > 0) return new(null, "id-mismatch");
        if (read.Issues.Length > 0) return new(null, read.Issues[0].Error.ToString().ToLowerInvariant());
        return new(null, "no-code");
    }
}
