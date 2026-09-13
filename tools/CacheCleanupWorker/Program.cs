using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;

namespace SkinChanger.CacheCleanup;

[DataContract]
internal sealed class CleanupPlan
{
    [DataMember] public int ParentPid { get; set; }
    [DataMember] public long ParentStartTicks { get; set; }
    [DataMember] public string UserDirectory = "";
    [DataMember] public string TempDirectory = "";
    [DataMember] public string[] Files = Array.Empty<string>();
    [DataMember] public string[] Directories = Array.Empty<string>();
    // Folder work inside the game's own mods folder, which the running game made impossible:
    // it keeps the assembly and the resource pack of a loaded skin open, so such a folder can be
    // neither deleted nor renamed from inside the session.
    [DataMember] public string ModsDirectory = "";
    [DataMember] public ModsOperation[] ModsOperations = Array.Empty<ModsOperation>();
}

[DataContract]
internal sealed class ModsOperation
{
    [DataMember] public string Kind = "";
    [DataMember] public string Folder = "";
    [DataMember] public string Staging = "";
}

internal static class Program
{
    // No shell, network, subscriptions, elevation, runtime install or scheduled tasks.
    private static int Main(string[] args)
    {
        if (args.Length != 1 || Path.GetFileName(args[0]) != "targets.json") return 2;
        var manifest = Path.GetFullPath(args[0]);
        var control = Path.GetDirectoryName(manifest)!;
        try
        {
            CleanupPlan plan;
            using (var stream = File.OpenRead(manifest))
                plan = (CleanupPlan)new DataContractJsonSerializer(typeof(CleanupPlan)).ReadObject(stream)!;
            var root = Path.Combine(plan.UserDirectory, "Gurio.SkinChanger");
            if (!SafePath(control, Path.Combine(root, "cleanup")) || !SafePath(root, plan.UserDirectory)) return 2;
            var mods = ModsTarget(plan);
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (ParentAlive(plan))
            {
                if (DateTime.UtcNow >= deadline) return 3;
                Thread.Sleep(250);
            }
            for (var attempt = 0; attempt < 12; attempt++)
            {
                // The mods folder first: a swap that has to happen before the next start must not
                // wait behind a long cache sweep.
                var pending = ApplyModsOperations(plan, mods);
                foreach (var file in plan.Files)
                {
                    if (!IsCacheTarget(plan, file, false)) continue;
                    try { File.Delete(file); }
                    catch (IOException) { pending++; }
                    catch (UnauthorizedAccessException) { pending++; }
                }
                foreach (var directory in plan.Directories.OrderByDescending(p => p.Length))
                {
                    if (!IsCacheTarget(plan, directory, true)) continue;
                    try { if (Directory.Exists(directory)) Directory.Delete(directory, false); }
                    catch (IOException) { pending++; }
                    catch (UnauthorizedAccessException) { pending++; }
                }
                if (pending == 0) break;
                Thread.Sleep(250);
            }
            File.Delete(manifest);
            Directory.Delete(control, false);
            return 0;
        }
        catch { return 1; }
    }

    private static bool ParentAlive(CleanupPlan plan)
    {
        try
        {
            using (var parent = Process.GetProcessById(plan.ParentPid))
                return !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartTicks;
        }
        catch (ArgumentException) { return false; }
    }

    // The one folder this helper may write into is the game's own mods folder, and only when the
    // plan really points at one: never the configuration directory, the player's cache or a drive
    // root, whatever a plan file claims.
    private static string? ModsTarget(CleanupPlan plan)
    {
        if (plan.ModsOperations == null || plan.ModsOperations.Length == 0) return null;
        var declared = plan.ModsDirectory ?? "";
        if (declared.Length == 0 || !Path.IsPathRooted(declared)) return null;
        var full = Path.GetFullPath(declared);
        if (!Directory.Exists(full) || !Path.GetFileName(full).Equals("mods", StringComparison.OrdinalIgnoreCase)) return null;
        if (IsUnder(full, plan.UserDirectory) || IsUnder(full, plan.TempDirectory)) return null;
        return full;
    }

    private static int ApplyModsOperations(CleanupPlan plan, string? modsDirectory)
    {
        if (modsDirectory == null) return 0;
        var stagingRoot = Path.Combine(plan.UserDirectory, "Gurio.SkinChanger", "github-pending");
        var pending = 0;
        foreach (var operation in plan.ModsOperations)
        {
            var replace = string.Equals(operation.Kind, "replace", StringComparison.Ordinal);
            if (!replace && !string.Equals(operation.Kind, "remove", StringComparison.Ordinal)) continue;
            if (!IsFolderName(operation.Folder)) continue;
            var target = Path.Combine(modsDirectory, operation.Folder);
            if (!SafePath(target, modsDirectory)) continue;
            // A replace without its package is dropped by the mod on the next start; here it is
            // simply nothing to do, and it must not delete the folder it cannot refill.
            if (replace && (!SafePath(operation.Staging, stagingRoot) || !Directory.Exists(operation.Staging))) continue;
            try
            {
                if (Directory.Exists(target))
                {
                    // Renaming first: a folder that is still in use fails here without losing a
                    // single file, while a recursive delete would stop halfway through it.
                    var previous = Path.Combine(modsDirectory, "." + operation.Folder + ".previous");
                    if (Directory.Exists(previous)) Directory.Delete(previous, true);
                    Directory.Move(target, previous);
                    Directory.Delete(previous, true);
                }
                // A remove is finished once the folder is gone; a replace refills it - by copy,
                // never by move, because a cross-volume move is a copy anyway and a copy that
                // fails halfway can simply be retried.
                if (replace)
                {
                    CopyTree(operation.Staging, target);
                    Directory.Delete(operation.Staging, true);
                }
            }
            catch (IOException) { pending++; }
            catch (UnauthorizedAccessException) { pending++; }
        }
        return pending;
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Relative(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static string Relative(string root, string path) =>
        path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // One folder name inside the mods folder, mirroring the rule the mod writes plans with.
    private static bool IsFolderName(string? name)
    {
        if (name is not { Length: > 0 and <= 64 } || name is "." or "..") return false;
        foreach (var character in name)
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.'))
                return false;
        return true;
    }

    private static bool IsUnder(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(full, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCacheTarget(CleanupPlan plan, string path, bool directory)
    {
        path = Path.GetFullPath(path);
        foreach (var root in new[] { Path.Combine(plan.UserDirectory, "Gurio.SkinChanger", "cache"),
                     Path.Combine(plan.UserDirectory, "skin_changer_workshop"), Path.Combine(plan.TempDirectory, "Gurio.SkinChanger", "runtime"),
                     Path.Combine(plan.TempDirectory, "Gurio.SkinChanger", "online") })
        {
            var parent = root.StartsWith(plan.UserDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? plan.UserDirectory : plan.TempDirectory;
            if (!SafePath(root, parent)) continue;
            if (path.Equals(Path.Combine(plan.UserDirectory, "skin_changer_workshop"), StringComparison.OrdinalIgnoreCase)) return true;
            if (SafePath(path, root)) return true;
        }
        var name = Path.GetFileName(path);
        return !directory && Path.GetDirectoryName(path).Equals(plan.UserDirectory, StringComparison.OrdinalIgnoreCase) && SafePath(path, plan.UserDirectory) &&
               name.EndsWith(".pck", StringComparison.Ordinal) &&
               (name.StartsWith("sts2_skin_overlay_", StringComparison.Ordinal) || name.StartsWith("sts2_skin_provider_namespace_", StringComparison.Ordinal));
    }

    private static bool SafePath(string path, string root)
    {
        path = Path.GetFullPath(path);
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return true;
    }
}
