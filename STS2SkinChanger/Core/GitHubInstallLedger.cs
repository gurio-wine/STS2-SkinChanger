using System.Text.Json;

namespace STS2SkinChanger.Core;

// Only what is needed to find and remove the folder again: the card is named by the repository
// in every state, so the name the package declared itself is not kept here.
internal sealed record GitHubInstall(string Folder, string PackageId, string Version);

// Which folder was written for which repository is the one thing that cannot be derived
// again: the catalog cache is dropped whenever its format changes, and a repository says
// nothing about the folder name the package inside it wants. This ledger keeps that record
// across sessions, so "installed" and "uninstall" only ever talk about a folder this mod
// wrote itself - a folder the player created by hand is never touched.
internal sealed class GitHubInstallLedger
{
    public const int Format = 1;
    private const int MaxEntries = 512;
    private const int MaxBytes = 1024 * 1024;
    private readonly Dictionary<string, GitHubInstall> _installs = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, GitHubInstall> Installs => _installs;
    public int Count => _installs.Count;

    public GitHubInstall? Find(string? fullName) => fullName == null ? null : _installs.GetValueOrDefault(fullName);

    public bool Record(string fullName, GitHubInstall install)
    {
        if (!GitHubSkinPolicy.TryParseFullName(fullName, out _) || !Valid(install) || _installs.Count >= MaxEntries && !_installs.ContainsKey(fullName))
            return false;
        _installs[fullName] = install;
        return true;
    }

    public bool Remove(string fullName) => _installs.Remove(fullName);

    public void Restore(string path)
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("GitHub install ledger too large.");
        GitHubInstallLedgerState? state;
        try { state = JsonSerializer.Deserialize<GitHubInstallLedgerState>(File.ReadAllText(path), new JsonSerializerOptions { MaxDepth = 8 }); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { throw new InvalidDataException("Unreadable GitHub install ledger.", ex); }
        if (state is not { Format: Format, Installs: not null } || state.Installs.Count > MaxEntries)
            throw new InvalidDataException("Unverified or invalid GitHub install ledger.");
        _installs.Clear();
        foreach (var (fullName, install) in state.Installs)
            if (GitHubSkinPolicy.TryParseFullName(fullName, out _) && Valid(install)) _installs[fullName] = install;
    }

    public void Save(string path, Action<string, Action>? writer)
    {
        var json = JsonSerializer.Serialize(new GitHubInstallLedgerState(Format, new Dictionary<string, GitHubInstall>(_installs)));
        void Write()
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        if (writer == null) Write(); else writer(path, Write);
    }

    // Every field becomes a path segment or a manifest file name on disk, so the same token
    // rules as the install folder apply here. An empty package id means the package shipped no
    // manifest at all (a bundle-only card art pack); the ledger entry is then the only proof.
    private static bool Valid(GitHubInstall? install) => install is { Folder: not null, PackageId: not null, Version: not null } &&
        GitHubInstallPolicy.IsSafeToken(install.Folder) &&
        (install.PackageId.Length == 0 || GitHubInstallPolicy.IsSafeToken(install.PackageId)) &&
        install.Version.Length <= 64 && !install.Version.Any(char.IsControl);
}

internal sealed record GitHubInstallLedgerState(int Format, Dictionary<string, GitHubInstall> Installs);
