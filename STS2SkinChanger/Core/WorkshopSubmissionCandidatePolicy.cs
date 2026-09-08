namespace STS2SkinChanger.Core;

internal static class WorkshopSubmissionCandidatePolicy
{
    // Recognition comes from the live skin catalog, not a cosmetic manifest or an [SC] label.
    internal static HashSet<ulong> EligibleIds(IEnumerable<WorkshopCatalogItem> recognized,
        IEnumerable<WorkshopCatalogItem> listed)
    {
        var ids = recognized.Where(item => item.Id > 0 && item.Id != 3787302680 && item.Targets.Length > 0)
            .Select(item => item.Id).ToHashSet();
        ids.ExceptWith(listed.Select(item => item.Id));
        return ids;
    }

    internal static WorkshopLocalSubmission[] Filter(IEnumerable<WorkshopLocalSubmission> installed,
        IEnumerable<WorkshopCatalogItem> recognized, IEnumerable<WorkshopCatalogItem> listed)
    {
        var ids = EligibleIds(recognized, listed);
        return installed.Where(item => ids.Contains(item.Id)).DistinctBy(item => item.Id)
            .OrderBy(item => item.Name, StringComparer.CurrentCulture).ThenBy(item => item.Id).ToArray();
    }
}
