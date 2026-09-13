using System.Text.Json;

namespace STS2SkinChanger.Core;

// Only what is needed to find and remove the folder again, plus the digest of the package that
// was written there: the card is named by the repository in every state, so the name the package
// declared itself is not kept here.
internal sealed record GitHubInstall(string Folder, string PackageId, string Version, string Digest = "");

internal enum GitHubModsOperationKind { Remove, Replace }

// A folder operation an older version (1.0.12) queued for a helper process that ran after the game
// had exited: Replace put the staged package into the folder, Remove deleted the folder this mod
// wrote. Nothing writes these any more - the current model finishes inside the mods folder at the
// next start - but a record written back then is still read, and carried out, on the next refresh.
internal sealed record GitHubPendingOp(GitHubModsOperationKind Kind, string Folder, string Staging);

// Which folder was written for which repository is the one thing that cannot be derived
// again: the catalog cache is dropped whenever its format changes, and a repository says
// nothing about the folder name the package inside it wants. This ledger keeps that record
// across sessions, so "installed" and "uninstall" only ever talk about a folder this mod
// wrote itself - a folder the player created by hand is never touched.
internal sealed class GitHubInstallLedger
{
    // Format 2 stays: the digest is the one optional field, and the folders waiting for deletion are
    // one more optional list, so a file written before either existed still reads back whole - the
    // installation survives and only the update check falls back to comparing the version numbers.
    public const int Format = 2;
    private const int MaxEntries = 512;
    private const int MaxPending = 64;
    private const int MaxDeletes = 64;
    private const int MaxBytes = 1024 * 1024;
    private readonly Dictionary<string, GitHubInstall> _installs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GitHubPendingOp> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _deletes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, GitHubInstall> Installs => _installs;
    public IReadOnlyDictionary<string, GitHubPendingOp> Pending => _pending;
    public int Count => _installs.Count;

    public GitHubInstall? Find(string? fullName) => fullName == null ? null : _installs.GetValueOrDefault(fullName);

    public bool Record(string fullName, GitHubInstall install)
    {
        install = Normalized(install);
        if (!GitHubSkinPolicy.TryParseFullName(fullName, out _) || !Valid(install) || _installs.Count >= MaxEntries && !_installs.ContainsKey(fullName))
            return false;
        _installs[fullName] = install;
        return true;
    }

    public bool Remove(string fullName) => _installs.Remove(fullName);

    public GitHubPendingOp? PendingFor(string? fullName) => fullName == null ? null : _pending.GetValueOrDefault(fullName);

    // A folder whose manifest is already renamed - the game no longer loads it - and whose files can
    // only go once the process that holds them is gone. One repository can collect more than one of
    // them inside a single session (updating a loaded skin writes a new folder each time) - hence the
    // list rather than a single name.
    public IReadOnlyDictionary<string, string[]> Deletes => _deletes;

    public bool HasDelete(string? fullName) => fullName != null && _deletes.ContainsKey(fullName);

    public bool SetDelete(string fullName, string folder)
    {
        if (!GitHubSkinPolicy.TryParseFullName(fullName, out _) || !ValidDelete(folder)) return false;
        var folders = _deletes.TryGetValue(fullName, out var existing) ? existing : [];
        if (folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return true;
        if (TotalDeletes + 1 > MaxDeletes) return false;
        _deletes[fullName] = [.. folders, folder];
        return true;
    }

    public bool ClearDelete(string fullName, string folder)
    {
        if (!_deletes.TryGetValue(fullName, out var folders)) return false;
        var rest = folders.Where(name => !name.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rest.Length == folders.Length) return false;
        if (rest.Length == 0) _deletes.Remove(fullName); else _deletes[fullName] = rest;
        return true;
    }

    public bool ClearDeletes(string fullName) => _deletes.Remove(fullName);

    private int TotalDeletes => _deletes.Values.Sum(folders => folders.Length);

    // Returns the step that was waiting, so the caller can drop the staged package it pointed at.
    public GitHubPendingOp? ClearPending(string fullName) => _pending.Remove(fullName, out var operation) ? operation : null;

    public void Restore(string path)
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("GitHub install ledger too large.");
        GitHubInstallLedgerState? state;
        try { state = JsonSerializer.Deserialize<GitHubInstallLedgerState>(File.ReadAllText(path), new JsonSerializerOptions { MaxDepth = 8 }); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { throw new InvalidDataException("Unreadable GitHub install ledger.", ex); }
        if (state is not { Format: Format, Installs: not null } || state.Installs.Count > MaxEntries ||
            state.Pending is { Count: > MaxPending } || state.Deletes is { Count: > MaxDeletes })
            throw new InvalidDataException("Unverified or invalid GitHub install ledger.");
        _installs.Clear();
        foreach (var (fullName, install) in state.Installs)
            // Valid runs first so a null entry from a hand-edited file is dropped, not dereferenced;
            // the digest is then normalized away if this browser cannot read it.
            if (GitHubSkinPolicy.TryParseFullName(fullName, out _) && Valid(install)) _installs[fullName] = Normalized(install);
        _pending.Clear();
        foreach (var (fullName, operation) in state.Pending ?? [])
            // A replace step is only meaningful for the folder the same repository is recorded
            // with; a mismatch means the file was edited or half written, so it is dropped.
            if (GitHubSkinPolicy.TryParseFullName(fullName, out _) && ValidPending(operation) &&
                (operation.Kind != GitHubModsOperationKind.Replace ||
                 _installs.GetValueOrDefault(fullName)?.Folder == operation.Folder))
                _pending[fullName] = operation;
        _deletes.Clear();
        foreach (var (fullName, folders) in state.Deletes ?? [])
            // A delete target becomes a real folder this mod removes, so the same token rules as the
            // install folder apply; an entry that loses every folder is dropped rather than kept empty.
            if (GitHubSkinPolicy.TryParseFullName(fullName, out _) && folders != null)
            {
                var valid = folders.Where(ValidDelete).ToArray();
                if (valid.Length > 0 && TotalDeletes + valid.Length <= MaxDeletes) _deletes[fullName] = valid;
            }
    }

    public void Save(string path, Action<string, Action>? writer)
    {
        var json = JsonSerializer.Serialize(new GitHubInstallLedgerState(Format,
            new Dictionary<string, GitHubInstall>(_installs), new Dictionary<string, GitHubPendingOp>(_pending),
            new Dictionary<string, string[]>(_deletes)));
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
        install.Version.Length <= 64 && !install.Version.Any(char.IsControl) &&
        install.Digest is null or { Length: <= 64 };

    // A digest this browser cannot read is dropped instead of being taken as a reason to forget
    // the installation: the entry is what keeps the folder uninstallable, while the digest only
    // decides whether the card offers an update.
    private static GitHubInstall Normalized(GitHubInstall install) =>
        install with { Digest = GitHubSkinPolicy.NormalizeDigest(install.Digest) };

    // A folder waiting to be deleted ends up as a real path this mod removes, so it has to be a single
    // safe name inside the mods folder - never a path, a parent reference or a leftover name.
    private static bool ValidDelete(string? folder) => GitHubInstallPolicy.IsSafeToken(folder);

    // The staged path of an old replace step becomes a folder this mod copies from, so it has to be an
    // absolute, placeholder-free path; a remove step carries none at all.
    private static bool ValidPending(GitHubPendingOp? operation) => operation is { Folder: not null, Staging: not null } &&
        operation.Kind is GitHubModsOperationKind.Remove or GitHubModsOperationKind.Replace &&
        GitHubModsPolicy.IsModsFolderName(operation.Folder) && operation.Folder[0] != '.' &&
        (operation.Kind == GitHubModsOperationKind.Remove
            ? operation.Staging.Length == 0
            : operation.Staging is { Length: > 0 and <= 512 } && Path.IsPathRooted(operation.Staging) && !operation.Staging.Any(char.IsControl));
}

internal sealed record GitHubInstallLedgerState(int Format, Dictionary<string, GitHubInstall> Installs,
    Dictionary<string, GitHubPendingOp>? Pending, Dictionary<string, string[]>? Deletes);
