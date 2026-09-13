namespace STS2SkinChanger.Core;

internal enum GitHubZipEntry { Reject, Skip, Accept }

// A release archive is untrusted: entries may try to escape the staging folder,
// carry another platform's junk, or expand into a decompression bomb.
internal static class GitHubZipPolicy
{
    public const int MaxEntries = 20000;
    public const long MaxEntryBytes = 256L * 1024 * 1024;
    public const long MaxTotalBytes = 512L * 1024 * 1024;

    public static GitHubZipEntry Classify(string entryName, long length, int index, long totalSoFar, out string relative)
    {
        relative = "";
        if (index >= MaxEntries || length > MaxEntryBytes || totalSoFar + length > MaxTotalBytes) return GitHubZipEntry.Reject;
        var name = entryName.Replace('\\', '/');
        if (name.Length == 0 || name.EndsWith('/')) return GitHubZipEntry.Skip;
        // An absolute path or a drive letter is never a legitimate package layout,
        // and silently trimming it would hide what the archive actually asked for.
        if (name.StartsWith('/') || name.Contains(':')) return GitHubZipEntry.Reject;
        var parts = name.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or "..") ||
            parts.Any(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) || name.Length > 240) return GitHubZipEntry.Reject;
        if (parts[0].StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase) || parts[0] == ".git" ||
            parts[^1].Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)) return GitHubZipEntry.Skip;
        relative = string.Join('/', parts);
        return GitHubZipEntry.Accept;
    }
}
