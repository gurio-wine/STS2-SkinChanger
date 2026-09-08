using System.Text;
using System.Text.RegularExpressions;

namespace STS2SkinChanger.Core;

internal sealed record WorkshopCodeProblem(string Key, ulong Id, string Name, WorkshopCodeIssue[] Issues)
{
    public WorkshopCodeSource[] Sources => Issues.SelectMany(i => i.Sources).DistinctBy(s => (s.Code, s.Url)).OrderBy(s => s.Order).ToArray();
}
internal static class WorkshopCodeDiagnostics
{
    // Read-only display scope, not catalog membership or permission to subscribe/load a Mod.
    internal static ulong[] MetadataIds(IEnumerable<WorkshopCatalogItem> catalog, IEnumerable<WorkshopCodeIssue> issues) =>
        catalog.Select(item => item.Id).Concat(issues.Select(issue => issue.Id)).Where(id => id > 0).Distinct().ToArray();

    internal static WorkshopCodeProblem[] Group(IEnumerable<WorkshopCodeIssue> issues) => issues
        .GroupBy(i => i.Id > 0 ? "id:" + i.Id : "code:" + i.Key).Select(g => new WorkshopCodeProblem(g.Key, g.First().Id,
            g.Select(i => i.ActualName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "", g.ToArray()))
        .OrderBy(g => g.Sources.Length == 0 ? int.MaxValue : g.Sources.Min(s => s.Order)).ToArray();
    internal static bool SafeSource(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Host == "steamcommunity.com" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.AbsolutePath.TrimEnd('/') == new Uri(WorkshopDiscussionSource.Url).AbsolutePath.TrimEnd('/') &&
        Regex.IsMatch(uri.Query, @"^(?:\?ctp=[0-9]{1,5})?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) &&
        Regex.IsMatch(uri.Fragment, @"^(?:#c[0-9]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    internal static string Report(WorkshopCodeProblem problem, string language)
    {
        string T(WorkshopCodeUi key, params object[] args) => string.Format(WorkshopCodeErrorText.ForLanguage(language, (int)key), args);
        var report = new StringBuilder().AppendLine(problem.Name.Length > 0 ? problem.Name : T(WorkshopCodeUi.Unknown)).AppendLine("Workshop ID: " + problem.Id);
        foreach (var issue in problem.Issues)
        {
            report.AppendLine($"[SCM-{issue.Error}] " + WorkshopCodeErrorText.ForLanguage(language, 13 + (int)issue.Error));
            if(issue.ActualName.Length>0)report.AppendLine(T(WorkshopCodeUi.Actual, issue.ActualName));
            if (issue.Group.Length > 0) report.AppendLine(T(WorkshopCodeUi.Group, issue.Group));
            if (issue.Total > 0) report.AppendLine(T(WorkshopCodeUi.Parts, issue.Sources.Where(s => s.Part > 0).Select(s => s.Part).Distinct().Count(), issue.Total));
            if (issue.Missing.Length > 0) report.AppendLine(T(WorkshopCodeUi.Missing, string.Join(", ", issue.Missing)));
        }
        foreach (var source in problem.Sources)
        {
            report.AppendLine(T(WorkshopCodeUi.Code, WorkshopSubmissionCodec.Fingerprint(source.Code)));
            report.AppendLine(source.Reply == 0 ? T(WorkshopCodeUi.MainPost) : T(WorkshopCodeUi.Source, source.Page, source.Reply)).AppendLine(source.Url).AppendLine(source.Code);
        }
        return report.ToString();
    }
}
