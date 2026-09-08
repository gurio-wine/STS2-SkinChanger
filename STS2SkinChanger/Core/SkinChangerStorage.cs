namespace STS2SkinChanger.Core;

// No engine calls here: migration/deletion is exercised against real temporary files in tests.
internal sealed class SkinChangerStorage : IDisposable
{
    private static readonly string[] ConfigurationNames =
    [
        "skin_changer.json", "skin_changer_theme.json", "skin_changer_theme_presets.json",
        "skin_changer_bundle_run_restore.json", "sts2_skin_switcher.json"
    ];
    private readonly object _cacheLock = new();
    private FileStream? _lease;
    private bool _closing;
    public string UserDirectory { get; }
    public string TempDirectory { get; }
    public string RootDirectory { get; }
    public string CacheDirectory { get; }
    public List<string> Warnings { get; } = [];

    public SkinChangerStorage(string userDirectory, string tempDirectory, string sessionId)
    {
        if (sessionId.Length == 0 || sessionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Invalid cache session id.", nameof(sessionId));
        UserDirectory = Path.GetFullPath(userDirectory);
        TempDirectory = Path.GetFullPath(tempDirectory);
        RootDirectory = Path.Combine(UserDirectory, "Gurio.SkinChanger");
        CacheDirectory = Path.Combine(RootDirectory, "cache", sessionId);
    }

    public void MigrateConfiguration()
    {
        foreach (var name in ConfigurationNames)
            if (MigrateFile(Path.Combine(UserDirectory, name), Path.Combine(RootDirectory, name)))
                MigrateFile(Path.Combine(UserDirectory, name + ".bak"), Path.Combine(RootDirectory, name + ".bak"));
        var runs = Path.Combine(UserDirectory, "skin_changer_bundle_runs");
        if (SafePath(runs, UserDirectory) && Directory.Exists(runs))
        {
            foreach (var name in Directory.EnumerateFiles(runs).Select(Path.GetFileName)
                         .Where(n => n!.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase))
                         .Select(n => n!.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n).Distinct())
            {
                var destination = Path.Combine(RootDirectory, "skin_changer_bundle_runs", name);
                if (MigrateFile(Path.Combine(runs, name), destination)) MigrateFile(Path.Combine(runs, name + ".bak"), destination + ".bak");
            }
            try { if (!Directory.EnumerateFileSystemEntries(runs).Any()) Directory.Delete(runs); }
            catch (Exception e) when (IsIoError(e)) { Warnings.Add(e.Message); }
        }
    }

    private bool MigrateFile(string source, string destination)
    {
        if (!File.Exists(source)) return true;
        try
        {
            if (!SafePath(source, UserDirectory) || !SafePath(destination, UserDirectory))
                throw new IOException("Refusing linked configuration path: " + source);
            // New files always win. Preserve conflicting legacy files in our own directory;
            // don't merge arbitrary JSON or silently discard the player's older presets.
            if (File.Exists(destination))
                destination = Path.Combine(RootDirectory, "migration-backup",
                    Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(source));
            if (!SafePath(destination, UserDirectory))
                throw new IOException("Refusing linked migration backup path: " + destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // The source stays intact if rename fails. Retry/fallback on the next launch.
            File.Move(source, destination);
            return true;
        }
        catch (Exception e) when (IsIoError(e)) { Warnings.Add("配置迁移保留旧文件：" + source + "；" + e.Message); return false; }
    }

    public string ConfigurationFile(string name)
    {
        if (!ConfigurationNames.Contains(name)) throw new ArgumentException("Unknown SC configuration.", nameof(name));
        var next = Path.Combine(RootDirectory, name);
        var old = Path.Combine(UserDirectory, name);
        return !File.Exists(next) && !File.Exists(next + ".bak") &&
               (File.Exists(old) || File.Exists(old + ".bak")) ? old : next;
    }

    public string RunRecordFile(string name)
    {
        if (Path.GetFileName(name) != name || !name.EndsWith(".json", StringComparison.Ordinal))
            throw new ArgumentException("Invalid run record.", nameof(name));
        var next = Path.Combine(RootDirectory, "skin_changer_bundle_runs", name);
        var old = Path.Combine(UserDirectory, "skin_changer_bundle_runs", name);
        return !File.Exists(next) && !File.Exists(next + ".bak") && (File.Exists(old) || File.Exists(old + ".bak")) ? old : next;
    }

    public void StartCacheSession()
    {
        lock (_cacheLock)
        {
            if (_closing || _lease != null) return;
            if (!SafePath(CacheDirectory, UserDirectory)) throw new IOException("Unsafe SC cache directory.");
            Directory.CreateDirectory(CacheDirectory);
            _lease = new FileStream(Path.Combine(CacheDirectory, ".active"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
    }

    public bool WriteCache(string path, Action write)
    {
        lock (_cacheLock)
        {
            if (_closing) return false;
            if (!SafePath(path, CacheDirectory)) throw new IOException("Cache write outside the current SC session.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            write();
            return true;
        }
    }

    public void StopCacheWrites()
    {
        lock (_cacheLock)
        {
            _closing = true;
            _lease?.Dispose();
            _lease = null;
        }
    }

    public CacheCleanupReport CleanCaches()
    {
        var report = new CacheCleanupReport(UserDirectory, TempDirectory);
        // Never delete the user directory, our configuration root, game saves, Steam content
        // or author settings. Only cache-specific children and exact legacy filename patterns.
        void Attempt(Action clean)
        {
            try { clean(); }
            catch (Exception e) when (IsIoError(e)) { report.Errors.Add(e.Message); }
        }
        Attempt(() => CleanSessionRoot(Path.Combine(RootDirectory, "cache"), report));
        Attempt(() => CleanSessionRoot(Path.Combine(TempDirectory, "Gurio.SkinChanger", "runtime"), report));
        Attempt(() => CleanSessionRoot(Path.Combine(TempDirectory, "Gurio.SkinChanger", "online"), report));
        Attempt(() => CleanDirectory(Path.Combine(UserDirectory, "skin_changer_workshop"), report));
        Attempt(() =>
        {
            if (!Directory.Exists(UserDirectory)) return;
            foreach (var pattern in new[] { "sts2_skin_overlay_*.pck", "sts2_skin_provider_namespace_*.pck" })
                foreach (var file in Directory.EnumerateFiles(UserDirectory, pattern)) CleanFile(file, report);
        });
        return report;
    }

    private void CleanSessionRoot(string root, CacheCleanupReport report)
    {
        if (!SafePath(root, root.StartsWith(UserDirectory, StringComparison.Ordinal) ? UserDirectory : TempDirectory) || !Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!SafePath(directory, root) || directory == CacheDirectory && _lease != null) continue;
            var lease = Path.Combine(directory, ".active");
            if (File.Exists(lease))
            {
                try { using var probe = new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { continue; } // Another live game session owns these files.
                catch (UnauthorizedAccessException) { continue; }
            }
            CleanDirectory(directory, report);
        }
    }

    private void CleanDirectory(string directory, CacheCleanupReport report)
    {
        if (!IsCacheTarget(directory, true) || !Directory.Exists(directory)) return;
        // Do not traverse even a nested junction/symlink.
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (!SafePath(child, directory)) continue;
            CleanDirectory(child, report);
        }
        foreach (var file in Directory.EnumerateFiles(directory)) CleanFile(file, report);
        try { Directory.Delete(directory, recursive: false); }
        catch (Exception e) when (IsIoError(e)) { report.Directories.Add(directory); }
    }

    private void CleanFile(string file, CacheCleanupReport report)
    {
        if (!IsCacheTarget(file, false)) return;
        try { File.Delete(file); report.RemovedFiles++; }
        catch (Exception e) when (IsIoError(e)) { report.Files.Add(file); }
    }

    public bool IsCacheTarget(string path, bool directory)
    {
        path = Path.GetFullPath(path);
        foreach (var root in new[] { Path.Combine(RootDirectory, "cache"), Path.Combine(UserDirectory, "skin_changer_workshop"),
                     Path.Combine(TempDirectory, "Gurio.SkinChanger", "runtime"), Path.Combine(TempDirectory, "Gurio.SkinChanger", "online") })
        {
            // The workshop legacy root is cache-only. The other roots are not removed.
            if (path == root && root.EndsWith("skin_changer_workshop", StringComparison.Ordinal) && SafePath(path, UserDirectory)) return true;
            if (SafePath(path, root) && SafePath(root, root.StartsWith(UserDirectory, StringComparison.Ordinal) ? UserDirectory : TempDirectory)) return true;
        }
        var name = Path.GetFileName(path);
        return !directory && Path.GetDirectoryName(path) == UserDirectory && SafePath(path, UserDirectory) &&
               name.EndsWith(".pck", StringComparison.Ordinal) &&
               (name.StartsWith("sts2_skin_overlay_", StringComparison.Ordinal) || name.StartsWith("sts2_skin_provider_namespace_", StringComparison.Ordinal));
    }

    internal static bool SafePath(string path, string root)
    {
        path = Path.GetFullPath(path);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison)) return false;
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            if (current.Equals(root, comparison)) break;
        }
        return true;
    }

    private static bool IsIoError(Exception e) => e is IOException or UnauthorizedAccessException;
    public void Dispose() => StopCacheWrites();
}

internal sealed record CacheCleanupReport(string UserDirectory, string TempDirectory)
{
    public int RemovedFiles { get; set; }
    public List<string> Files { get; } = [];
    public List<string> Directories { get; } = [];
    public List<string> Errors { get; } = [];
}
