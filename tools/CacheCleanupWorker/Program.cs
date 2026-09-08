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
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (ParentAlive(plan))
            {
                if (DateTime.UtcNow >= deadline) return 3;
                Thread.Sleep(250);
            }
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var pending = 0;
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
