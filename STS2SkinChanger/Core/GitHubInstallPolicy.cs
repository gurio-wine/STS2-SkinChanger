using System.Security.Cryptography;
using System.Text.Json;

namespace STS2SkinChanger.Core;

internal enum GitHubInstallState { NotInstalled, Installed, Updatable }

internal static class GitHubInstallPolicy
{
    // Mirrors GamePackLocator: a macOS bundle keeps game data in Contents/Resources,
    // while OS.GetExecutablePath() points into Contents/MacOS.
    public static string ModsDirectory(string executablePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        if (Path.GetFileName(directory).Equals("MacOS", StringComparison.OrdinalIgnoreCase))
            directory = Path.GetFullPath(Path.Combine(directory, "..", "Resources"));
        return Path.Combine(directory, "mods");
    }

    public static string FolderName(string packageId, string repoName)
    {
        // The manifest id is used verbatim or not at all: a "sanitized" id would name the
        // folder something the manifest inside it does not claim to be.
        if (IsSafeToken(packageId)) return packageId;
        var fallback = new string(repoName.Where(char.IsAsciiLetterOrDigit).ToArray());
        return fallback.Length is > 0 and <= 64 ? fallback : "Skin";
    }

    // Public because the install ledger stores these tokens and has to re-validate them.
    public static bool IsSafeToken(string? value) => value is { Length: > 0 and <= 64 } &&
        value[0] != '.' && value[^1] != '.' &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static int CompareVersions(string? left, string? right)
    {
        var a = Segments(left);
        var b = Segments(right);
        if (a.Length == 0 || b.Length == 0) return a.Length == b.Length ? 0 : a.Length == 0 ? -1 : 1;
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static int[] Segments(string? version) => (version ?? "").Trim().TrimStart('v', 'V')
        .Split('.', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => int.TryParse(new string(part.TakeWhile(char.IsAsciiDigit).ToArray()), out var value) ? value : -1).ToArray();

    // The tag says nothing about which package is behind a release, so the choice between the two
    // answers is made from the published digest of the package: the browser records the digest of
    // the file it installed and compares it with the digest of the release it sees now. Only when
    // neither side has one (an asset published before GitHub reported digests) does it fall back
    // to reading the tag as a version number.
    public static GitHubInstallState Classify(string? installedVersion, string? installedDigest, string? availableVersion, string? availableDigest)
    {
        if (string.IsNullOrWhiteSpace(installedVersion) && string.IsNullOrWhiteSpace(installedDigest))
            return GitHubInstallState.NotInstalled;
        var installed = GitHubSkinPolicy.NormalizeDigest(installedDigest);
        var available = GitHubSkinPolicy.NormalizeDigest(availableDigest);
        if (installed.Length > 0 && available.Length > 0)
            return installed.Equals(available, StringComparison.OrdinalIgnoreCase)
                ? GitHubInstallState.Installed : GitHubInstallState.Updatable;
        return !string.IsNullOrWhiteSpace(availableVersion) && CompareVersions(installedVersion, availableVersion) < 0
            ? GitHubInstallState.Updatable : GitHubInstallState.Installed;
    }

    // A downloaded package may only be reused when it is the one the release publishes now. The
    // size alone cannot say that - an author may replace the file with one of the same size - so
    // the published digest decides whenever there is one. With no digest on the release there is
    // nothing to compare, and the cache behaves as it did before digests existed.
    public static bool CanReusePackage(string declaredDigest, long expectedSize, long cachedSize, string cachedDigest)
    {
        if (cachedSize < 0 || cachedSize != expectedSize) return false;
        var declared = GitHubSkinPolicy.NormalizeDigest(declaredDigest);
        return declared.Length == 0 || cachedDigest.Equals(declared, StringComparison.OrdinalIgnoreCase);
    }

    // What is recorded at install time: the digest of the package file that was actually written.
    // An unreadable file has no digest, which reads as "cannot tell" rather than as a change.
    public static string DigestOfFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    // Only a folder that still holds the manifest this mod installed may be removed: a folder
    // the player made by hand must never be deleted just because it shares the name. A package
    // without any manifest (a bundle-only card art pack) has no id to verify, and there the
    // ledger entry itself is the proof that this mod wrote the folder.
    public static string? UninstallTarget(string modsDirectory, string folder, string packageId)
    {
        if (!IsSafeToken(folder)) return null;
        var path = Path.Combine(modsDirectory, folder);
        if (!Directory.Exists(path)) return null;
        return packageId.Length == 0 || ReadManifestVersion(path, packageId) != null ? path : null;
    }

    public static string? ReadManifestVersion(string modRoot, string packageId)
    {
        if (!IsSafeToken(packageId)) return null;
        var path = Path.Combine(modRoot, packageId + ".json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path).TrimStart('\uFEFF'));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
}
