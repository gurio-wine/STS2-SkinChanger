namespace STS2SkinChanger.Core;

internal enum WorkshopCodeError
{
    // Keep the two retired name error values for old diagnostic-cache compatibility.
    Format, MissingName, NameMismatch, MissingParts, Conflict, Checksum, Version,
    TooLarge, InvalidData, WrongGame, Unavailable, Legacy
}
internal sealed record WorkshopCodePost(string Text, string Url, int Page, int Reply);
internal sealed record WorkshopCodeSource(string Code, string Url, int Page, int Reply, int Order, int Part = 0);
internal sealed record WorkshopCodeIssue(string Key, ulong Id, string Name, string ActualName,
    WorkshopCodeError Error, string Group, int Total, int[] Missing, WorkshopCodeSource[] Sources);
internal sealed record WorkshopCodePayload(int Format, uint App, string Scanner, string Game, WorkshopCatalogItem Item);
internal sealed record WorkshopCodeCandidate(WorkshopCodePayload Payload, string Group, WorkshopCodeSource[] Sources);
internal sealed record WorkshopCodeRead(WorkshopCodeCandidate[] Candidates, WorkshopCodeIssue[] Issues);
internal sealed record WorkshopIdentity(uint App, string Name);
internal sealed record WorkshopVerifiedSubmission(WorkshopCatalogItem Item, string Name, string Group)
{
    // Missing in older caches. Zero is the original post, not "no discussion source".
    public int? Reply { get; init; }
}
internal sealed record WorkshopCommunityState(int Format, WorkshopVerifiedSubmission[] Entries, WorkshopCodeIssue[] Issues)
{
    internal static WorkshopCommunityState Empty => new(2, [], []);
}

internal static class WorkshopSubmissionIntegrity
{
    internal static WorkshopCommunityState Verify(WorkshopCodeRead read, IReadOnlyDictionary<ulong, WorkshopIdentity> actual,
        WorkshopCommunityState previous)
    {
        var issues = read.Issues.Select(i => i with { ActualName = actual.GetValueOrDefault(i.Id)?.Name ?? "" }).ToList();
        var accepted = new List<WorkshopVerifiedSubmission>();
        foreach (var candidate in read.Candidates.OrderBy(c => c.Sources.Max(s => s.Order)))
        {
            var payload = candidate.Payload;
            var found = actual.GetValueOrDefault(payload.Item.Id);
            WorkshopCodeError? error = found == null ? WorkshopCodeError.Unavailable :
                found.App != WorkshopCatalogPolicy.AppId ? WorkshopCodeError.WrongGame : null;
            if (error is { } invalid)
                issues.Add(new(candidate.Group, payload.Item.Id, "", found?.Name ?? "", invalid,
                    candidate.Group, candidate.Sources.Max(s => s.Part), [], candidate.Sources));
            else accepted.Add(new(payload.Item, found!.Name, candidate.Group)
            { Reply = candidate.Sources.Max(source => source.Reply) });
        }
        // This is a successfully read full discussion snapshot. Only its valid codes remain
        // listed; edited/deleted/broken codes must not resurrect entries from the old cache.
        // Network/Steam failures preserve the entire snapshot in WorkshopCommunityCatalog.
        var chosen = accepted.GroupBy(e => e.Item.Id).ToDictionary(g => g.Key, g => g.Last());
        return new(2, chosen.Values.OrderBy(e => e.Item.Id).ToArray(), issues.ToArray());
    }
}
