using System.Text.Json;

namespace STS2SkinChanger.Core;

// Where a package now lives, and which folder had to be hidden to get it there.
internal sealed record GitHubPlacement(string Folder, string? Disabled);

// Everything this mod does inside the game's mods directory, built on how the game itself reads that
// directory (ModManager): a folder counts as a mod only when it holds a file whose name ends in
// ".json", the DLL and the PCK are then taken from that manifest's own directory, and the whole
// folder is read once, at the game's own start-up.
//
// Two consequences carry the whole swap. Renaming a manifest takes a folder out of the loader's view
// without touching the files the running game holds open - a rename of an *open* file is refused by
// Windows, a rename of the closed manifest beside it is not. And a package written into a folder
// next to it is what the next start loads. Deleting the hidden files is the only step that really
// needs the process gone, and that happens at the next start, when nothing has them open any more.
//
// The caller says what the game has loaded (ModManager); the tests say what this process holds open,
// so both drive the same code against the same obstacle.
internal static class GitHubFolderSwap
{
    // Writes `sourceDirectory` into the mods directory and reports the folder it ended up in.
    // `recordedFolder` is the folder this mod wrote for that skin before, null when it never did.
    public static GitHubPlacement Place(string modsDirectory, string? recordedFolder, string canonicalFolder, string packageId,
        string digest, string sourceDirectory, Func<string, bool> isLoaded, Action<string> warn)
    {
        if (!GitHubInstallPolicy.IsSafeToken(canonicalFolder))
            throw new InvalidOperationException("Invalid install folder name: " + canonicalFolder);
        Directory.CreateDirectory(modsDirectory);
        var current = Owned(modsDirectory, recordedFolder, canonicalFolder, packageId);
        var direct = current ?? canonicalFolder;
        var directPath = Path.Combine(modsDirectory, direct);
        if (current == null && !Owns(directPath, packageId))
        {
            // The canonical name is taken by something this mod did not write. Replacing it would
            // destroy another mod's folder, and writing beside it would register this skin's id twice
            // at the next start - so the install fails and says why.
            if (isLoaded(directPath))
                throw new InvalidOperationException($"mods/{direct} is in use and was not installed by this mod.");
            if (HoldsManifest(directPath))
                throw new InvalidOperationException($"mods/{direct} belongs to another mod.");
        }
        if (current == null || !isLoaded(directPath))
        {
            // Nothing has this folder open: the package goes straight in, which keeps the folder name
            // stable across updates instead of collecting a suffix per version.
            try
            {
                Replace(modsDirectory, directPath, sourceDirectory, warn);
                return new(direct, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (current == null) throw;
                warn($"目录 mods/{direct} 无法直接替换，改写进旁边的备用目录：{ex.Message}");
            }
        }
        // The running game holds this folder open. Renaming its manifests hides it from the next scan
        // - no file the game holds is touched by that - and the new package goes into a folder beside
        // it, which is what that scan picks up.
        if (!Disable(directPath, warn))
            throw new InvalidOperationException($"Could not disable the loaded copy mods/{direct}.");
        var fresh = GitHubModsPolicy.FreshFolderName(canonicalFolder, GitHubModsPolicy.DigestToken(digest));
        if (fresh.Length == 0)
            throw new InvalidOperationException("Could not build a folder name for the new package.");
        var freshPath = Path.Combine(modsDirectory, fresh);
        // The very same package may already sit there - a repeated click, or the update the player
        // restarted into - and then there is nothing left to write.
        if (!isLoaded(freshPath)) Replace(modsDirectory, freshPath, sourceDirectory, warn);
        return new(fresh, direct);
    }

    // Renames every manifest inside the folder. Reports whether the folder is now invisible to the
    // loader, checked on the disk rather than on the attempts: a rename that failed halfway would
    // otherwise leave this folder registered next to the new one, under the same id.
    public static bool Disable(string folder, Action<string> warn)
    {
        if (!Directory.Exists(folder)) return true;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToArray())
        {
            if (ManifestId(file) is null) continue;
            try { File.Move(file, file + GitHubModsPolicy.DisabledSuffix, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warn($"无法停用清单 {file}：{ex.Message}"); }
        }
        return !HoldsManifest(folder);
    }

    // What the loader would register from this folder: a file it reads as a manifest, which needs an
    // id. The extension test is case-insensitive here on purpose - this decides a deletion, and a name
    // that merely looks like a manifest is enough to hold it back.
    public static bool HoldsManifest(string folder) => Directory.Exists(folder) &&
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(file =>
            Path.GetFileName(file).EndsWith(".json", StringComparison.OrdinalIgnoreCase) && ManifestId(file) is not null);

    // Deletes the folders that were hidden earlier, now that nothing has them open. A folder an
    // install points at, or one that holds a live manifest again, is dropped from the list instead:
    // it is not the folder this mod hid any more. Only a folder that is merely still open stays on
    // it, and is retried at the next start.
    public static string[] Sweep(string modsDirectory, IEnumerable<(string FullName, string Folder)> pending,
        IReadOnlySet<string> liveFolders, Action<string> log)
    {
        var remaining = new List<string>();
        foreach (var folder in pending.Select(entry => entry.Folder).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!GitHubInstallPolicy.IsSafeToken(folder))
            {
                log($"跳过删除 {folder}：目录名不合法。");
                continue;
            }
            var path = Path.Combine(modsDirectory, folder);
            if (!GitHubModsPolicy.CanDeleteFolder(folder, liveFolders.Contains(folder), HoldsManifest(path)))
            {
                log($"跳过删除 mods/{folder}：它不是在用目录就是已经重新装上了清单。");
                continue;
            }
            if (!Directory.Exists(path)) continue;
            try
            {
                Directory.Delete(path, recursive: true);
                log($"已删除 GitHub 皮肤的旧目录：mods/{folder}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                remaining.Add(folder);
                log($"mods/{folder} 仍被占用，留到下次启动再删：{ex.Message}");
            }
        }
        return [.. remaining];
    }

    // Which folder in the mods directory belongs to this skin: the one this mod recorded, or - when
    // that record is gone - the folder under the skin's canonical name, but only while it really
    // holds this skin's manifest or the renamed remains of it. A folder the player put there is never
    // claimed, and a package without a manifest never had a folder of its own to claim.
    private static string? Owned(string modsDirectory, string? recordedFolder, string canonicalFolder, string packageId)
    {
        if (GitHubInstallPolicy.IsSafeToken(recordedFolder)) return recordedFolder;
        if (packageId.Length == 0) return null;
        var path = Path.Combine(modsDirectory, canonicalFolder);
        return Owns(path, packageId) ? canonicalFolder : null;
    }

    private static bool Owns(string folder, string packageId) =>
        packageId.Length > 0 && Directory.Exists(folder) &&
        (Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
             .Where(file => Path.GetFileName(file).Equals(packageId + ".json", StringComparison.OrdinalIgnoreCase))
             .Any(file => string.Equals(ManifestId(file), packageId, StringComparison.OrdinalIgnoreCase)) ||
         File.Exists(Path.Combine(folder, GitHubModsPolicy.DisabledManifestName(packageId))));

    // The id a file declares, or null when the loader would not read it as a manifest at all
    // (ModManager.ReadModManifest: an object with a non-empty id).
    private static string? ManifestId(string file)
    {
        try
        {
            if (new FileInfo(file).Length > 1024 * 1024) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(file).TrimStart('\uFEFF'));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return null;
            var value = id.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    // The old copy is moved aside first: renaming a folder that is in use fails without touching
    // anything, while a recursive delete stops halfway and leaves a package the game can no longer
    // load. Whatever is left of the moved copy is cleaned up here, or by the next sweep.
    private static void Replace(string modsDirectory, string target, string source, Action<string> warn)
    {
        if (Directory.Exists(target))
        {
            var previous = Path.Combine(modsDirectory, "." + Path.GetFileName(target) + ".previous");
            TryDelete(previous);
            Directory.Move(target, previous);
            // A leftover that could not be deleted must not be loadable: the next start walks every
            // subdirectory of mods, and a second manifest with the same id is a conflict.
            Disable(previous, warn);
            TryDelete(previous);
        }
        CopyDirectory(source, target);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith(".git", StringComparison.OrdinalIgnoreCase))) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
