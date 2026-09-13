using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

/// <summary>
/// Read-only check that a GitHub release folder is recognized through the same offline path
/// the Workshop submission scan uses: the folder is staged below a "github/&lt;id&gt;" path so the
/// catalog binds the synthetic source id, and the exported targets must match the package.
/// No provider initializer runs and no game process is started.
/// </summary>
internal static class GitHubRecognitionAudit
{
    internal static void Run(string gamePck, string root, string owner, string repo)
    {
        var id = SkinSourceIdentity.GitHubId(owner, repo);
        Console.WriteLine($"source id={id} github={SkinSourceIdentity.IsGitHub(id)} root={root}");
        Console.WriteLine($"workshop source id={SkinCatalog.WorkshopSourceId(root)} (expected {id})");
        var cards = CardArtPackRecognitionAudit.CardEntries(gamePck);
        var result = WorkshopSubmissionScanner.Scan(new(id, repo, root), gamePck, cards, "0.107.1", new Dictionary<string, string>());
        Console.WriteLine(result.Item == null
            ? $"recognition failed: {result.Error}"
            : "recognition targets=" + string.Join("|", result.Item.Targets.Select(target => target.Kind + ":" + target.Target)) +
              $" restart={result.Item.RestartRequired} provider={result.ProviderId}");
        Console.WriteLine("descriptors: " + string.Join(", ", WorkshopSubmissionScanner.ReadDescriptors(root)
            .Select(mod => mod.Id + "@" + mod.RootPath)));
    }
}
