namespace STS2SkinChanger.Core;

// The rules for the game's mods folder, as names and booleans: what a folder inside it may be called,
// which file names the loader reads as a manifest, which folder a new package goes into when the old
// one is in use, and which folder may be deleted again. GitHubFolderSwap is the half that touches the
// disk.
//
// Everything here follows the game's own loader (ModManager): a directory counts as a mod only when it
// holds a file whose name ends in ".json" (ReadModsInDirRecursive), the DLL and the PCK are then taken
// from that manifest's own directory (TryLoadMod), and the whole directory is read once, at the game's
// own start-up. Renaming a manifest therefore hides a folder from the next start without touching any
// file the running game holds open - which is what makes installing, updating and removing a skin a
// plain folder operation instead of work that has to wait for the game to exit.
internal static class GitHubModsPolicy
{
    // Appended to a manifest name to take a folder out of the loader's view.
    public const string DisabledSuffix = ".disabled";

    // One folder name inside the game's mods folder. The ledger stores dot-free tokens, while the
    // leftovers of an interrupted install carry a leading dot.
    public static bool IsModsFolderName(string? name) => name is { Length: > 0 and <= 64 } &&
        name is not "." and not ".." && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    // The names this mod itself writes while swapping a folder in a version that queued the swap for
    // after the game had exited. They may be deleted, and nothing a player created by hand.
    public static bool IsLeftoverFolderName(string? name) => name is { Length: > 2 } && name[0] == '.' &&
        (name.EndsWith(".installing", StringComparison.OrdinalIgnoreCase) ||
         name.EndsWith(".previous", StringComparison.OrdinalIgnoreCase)) &&
        IsModsFolderName(name);

    // The loader's own rule, spelled out so this mod can be held to it: a file counts as a manifest
    // when its name ends in ".json" (ReadModsInDirRecursive compares ordinally, so the extension is
    // case-sensitive there).
    public static bool IsManifestName(string? fileName) => fileName is { Length: > 5 } &&
        fileName.EndsWith(".json", StringComparison.Ordinal);

    public static string DisabledManifestName(string packageId) => packageId + ".json" + DisabledSuffix;

    // A folder this mod disabled earlier stays this mod's folder after a re-install, even when the
    // ledger that recorded it is gone.
    public static bool IsDisabledManifestName(string? fileName, string packageId) => packageId.Length > 0 &&
        string.Equals(fileName, DisabledManifestName(packageId), StringComparison.OrdinalIgnoreCase);

    // The short token a spare folder is named after. An unreadable digest yields no token at all:
    // half a hash would name a folder after a package nobody can identify again.
    public static string DigestToken(string? digest)
    {
        var normalized = GitHubSkinPolicy.NormalizeDigest(digest);
        return normalized.Length == 64 ? normalized[..8] : "";
    }

    // The folder a package goes into while the running game holds the canonical one open. The base is
    // always the canonical name - never the folder the ledger points at now - so the name cannot grow
    // a suffix with every update; the token is what keeps two packages apart.
    public static string FreshFolderName(string folder, string token)
    {
        if (!GitHubInstallPolicy.IsSafeToken(folder) || !GitHubInstallPolicy.IsSafeToken(token)) return "";
        var room = 64 - token.Length - 1;
        if (room < 2) return "";
        var name = (folder.Length > room ? folder[..room] : folder).TrimEnd('.');
        var fresh = name + "-" + token;
        return GitHubInstallPolicy.IsSafeToken(fresh) ? fresh : "";
    }

    // The one destructive step: only a folder this mod wrote may be deleted, and only while nothing on
    // disk claims it. The folder an install points at now is in use, and a folder that holds a live
    // manifest has been written again since - a re-install into the canonical name, or the player's own
    // copy - and is not the folder this mod hid any more.
    public static bool CanDeleteFolder(string? folder, bool live, bool holdsManifest) =>
        GitHubInstallPolicy.IsSafeToken(folder) && !live && !holdsManifest;
}
