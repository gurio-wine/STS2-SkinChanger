using System.Text;

namespace STS2SkinChanger.Core;

internal enum WorkshopCodeError
{
    Format, MissingName, NameMismatch, MissingParts, Conflict, Checksum, Version,
    TooLarge, InvalidData, WrongGame, Unavailable, Legacy
}
internal sealed record WorkshopCodePost(string Text, string Url, int Page, int Reply);
internal sealed record WorkshopCodeSource(string Code, string Url, int Page, int Reply, int Order, int Part = 0);
internal sealed record WorkshopCodeIssue(string Key, ulong Id, string Name, string ActualName,
    WorkshopCodeError Error, string Group, int Total, int[] Missing, WorkshopCodeSource[] Sources);
internal sealed record WorkshopNamedPayload(int Format, uint App, string Name, string Scanner, string Game, WorkshopCatalogItem Item);
internal sealed record WorkshopCodeCandidate(WorkshopNamedPayload Payload, string Group, WorkshopCodeSource[] Sources);
internal sealed record WorkshopCodeRead(WorkshopCodeCandidate[] Candidates, WorkshopCodeIssue[] Issues);
internal sealed record WorkshopIdentity(uint App, string Name);
internal sealed record WorkshopVerifiedSubmission(WorkshopCatalogItem Item, string Name, string Group);
internal sealed record WorkshopCommunityState(int Format, WorkshopVerifiedSubmission[] Entries, WorkshopCodeIssue[] Issues)
{
    internal static WorkshopCommunityState Empty => new(2, [], []);
}

internal static class WorkshopSubmissionIntegrity
{
    // The generator and verifier both query English, independently of each player's UI language.
    // Do not remove punctuation, ignore case or fuzzy-match different Mods.
    internal static string NormalizeName(string name) => name.Normalize(NormalizationForm.FormC).Trim();
    internal static WorkshopCommunityState Verify(WorkshopCodeRead read, IReadOnlyDictionary<ulong, WorkshopIdentity> actual,
        WorkshopCommunityState previous)
    {
        var issues = read.Issues.Select(i => i with { ActualName = actual.GetValueOrDefault(i.Id)?.Name ?? "" }).ToList();
        var accepted = new List<WorkshopVerifiedSubmission>();
        foreach (var candidate in read.Candidates.OrderBy(c => c.Sources.Max(s => s.Order)))
        {
            var payload = candidate.Payload;
            var found = actual.GetValueOrDefault(payload.Item.Id);
            WorkshopCodeError? error = string.IsNullOrWhiteSpace(payload.Name) ? WorkshopCodeError.MissingName :
                found == null || string.IsNullOrWhiteSpace(found.Name) ? WorkshopCodeError.Unavailable :
                found.App != WorkshopCatalogPolicy.AppId ? WorkshopCodeError.WrongGame :
                NormalizeName(found.Name) != NormalizeName(payload.Name) ? WorkshopCodeError.NameMismatch : null;
            if (error is { } invalid)
                issues.Add(new(candidate.Group, payload.Item.Id, payload.Name, found?.Name ?? "", invalid,
                    candidate.Group, candidate.Sources.Max(s => s.Part), [], candidate.Sources));
            else accepted.Add(new(payload.Item, payload.Name, candidate.Group));
        }
        // A bad new submission cannot poison a previously verified entry. Its name must still
        // match Steam; renamed, removed or foreign-game items are not silently restored.
        var blocked = issues.Where(i => i.Id > 0).Select(i => i.Id).ToHashSet();
        var chosen = accepted.GroupBy(e => e.Item.Id).ToDictionary(g => g.Key, g => g.Last());
        foreach (var old in previous.Entries)
            if (!chosen.ContainsKey(old.Item.Id) && blocked.Contains(old.Item.Id) && actual.TryGetValue(old.Item.Id, out var identity) &&
                identity.App == WorkshopCatalogPolicy.AppId && NormalizeName(identity.Name) == NormalizeName(old.Name)) chosen[old.Item.Id] = old;
        return new(2, chosen.Values.OrderBy(e => e.Item.Id).ToArray(), issues.ToArray());
    }
}
