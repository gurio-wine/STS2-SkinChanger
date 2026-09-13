namespace STS2SkinChanger.Core;

internal enum GitHubModsOperationKind { Remove, Replace }

// One filesystem step that can only run once the game is gone. A skin the running game has loaded
// keeps its DLL and its resource pack open, and Windows refuses to delete - or even rename - a
// folder that holds an open file; the same attempt against the real game folder answers
// "Access to the path ... is denied". Steam says the same thing about its own subscriptions and
// removes those files after the game exits, so the quit helper does it here.
internal sealed record GitHubModsOperation(GitHubModsOperationKind Kind, string Folder, string Staging);

// What the quit helper has to do for the game's mods folder, and which folder that is. Empty
// when there is nothing to do.
internal sealed record GitHubModsPlan(string ModsDirectory, GitHubModsOperation[] Operations);

internal static class GitHubModsPolicy
{
    // A staged package waits outside the mods folder: anything inside it is picked up by the
    // game's own start-up scan, and a second folder holding the same manifest id is a conflict.
    public static string StagingDirectory(string rootDirectory, ulong id) =>
        Path.Combine(rootDirectory, "github-pending", id.ToString());

    // One folder name inside the game's mods folder. The ledger stores dot-free tokens, while the
    // leftovers of an interrupted install carry a leading dot.
    public static bool IsModsFolderName(string? name) => name is { Length: > 0 and <= 64 } &&
        name is not "." and not ".." && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    // The names this mod itself writes while swapping a folder. A remove step may delete these and
    // nothing a player could have created by hand.
    public static bool IsLeftoverFolderName(string? name) => name is { Length: > 2 } && name[0] == '.' &&
        (name.EndsWith(".installing", StringComparison.OrdinalIgnoreCase) ||
         name.EndsWith(".previous", StringComparison.OrdinalIgnoreCase)) &&
        IsModsFolderName(name);

    // Queued steps first, one step per folder, every replace before every remove: a folder that is
    // about to receive a new package must never be deleted afterwards, whatever order the ledger
    // handed the steps over in. Leftovers follow, once each.
    public static GitHubModsOperation[] QuitPlan(IEnumerable<GitHubModsOperation> queued, IEnumerable<string> leftovers)
    {
        var plan = new List<GitHubModsOperation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in queued.Where(step => step.Kind == GitHubModsOperationKind.Replace)
                     .Concat(queued.Where(step => step.Kind != GitHubModsOperationKind.Replace)))
            if (IsModsFolderName(operation.Folder) && seen.Add(operation.Folder)) plan.Add(operation);
        foreach (var name in leftovers)
            if (IsLeftoverFolderName(name) && seen.Add(name)) plan.Add(new(GitHubModsOperationKind.Remove, name, ""));
        return [.. plan];
    }

    // A queued step is dropped once the disk shows it is either done or pointless: the folder is
    // gone (the helper ran, or the player deleted it by hand) or the staged package it needs is
    // gone. Dropping is what keeps a step from being retried forever.
    public static bool KeepQueued(GitHubModsOperationKind kind, bool folderExists, bool stagingExists) =>
        folderExists && (kind != GitHubModsOperationKind.Replace || stagingExists);
}
