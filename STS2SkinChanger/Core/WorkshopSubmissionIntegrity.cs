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
internal sealed record WorkshopVerifiedSubmission(WorkshopCatalogItem Item, string Name, string Group);
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
            else accepted.Add(new(payload.Item, found!.Name, candidate.Group));
        }
        // A bad new submission cannot poison a previously verified entry. Revalidate its ID
        // and game; Steam titles are display-only and may change at any time.
        var blocked = issues.Where(i => i.Id > 0).Select(i => i.Id).ToHashSet();
        var chosen = accepted.GroupBy(e => e.Item.Id).ToDictionary(g => g.Key, g => g.Last());
        foreach (var old in previous.Entries)
            if (!chosen.ContainsKey(old.Item.Id) && blocked.Contains(old.Item.Id) && actual.TryGetValue(old.Item.Id, out var identity) &&
                identity.App == WorkshopCatalogPolicy.AppId) chosen[old.Item.Id] = old with { Name = identity.Name };
        return new(2, chosen.Values.OrderBy(e => e.Item.Id).ToArray(), issues.ToArray());
    }
}
