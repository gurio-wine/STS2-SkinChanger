using System.Text.Json;
using MegaCrit.Sts2.Core.Debug;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal enum WorkshopLoadReason { None, StartupCode, Dependency, Version, VersionRule, IncompleteResources, ChangedFiles, DuplicateId, InvalidPackage, Gameplay }
internal sealed record WorkshopPackageAssessment(SkinModDescriptor[] Mods, WorkshopLoadReason Reason)
{
    public bool CanInspectResources => Reason == WorkshopLoadReason.None && Mods.Length > 0;
}

// Only reads installed files. It never loads a DLL or invokes a Mod initializer.
internal static class WorkshopPackagePolicy
{
    public static WorkshopPackageAssessment Assess(string directory, string? gameVersion, IReadOnlyDictionary<string, string> loadedDependencies)
    {
        WorkshopPackageAssessment Reject(WorkshopLoadReason reason) => new([], reason);
        if (!Directory.Exists(directory)) return Reject(WorkshopLoadReason.InvalidPackage);
        var mods = new List<SkinModDescriptor>();
        var dependencies = new List<(string Id, string? Minimum)>();
        try
        {
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Take(20001).ToArray();
            if (files.Length > 20000) return Reject(WorkshopLoadReason.InvalidPackage);
            foreach (var manifest in files.Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)))
            {
                if (new FileInfo(manifest).Length > 1024 * 1024) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(File.ReadAllText(manifest).TrimStart('\uFEFF')); }
                catch (JsonException)
                {
                    if (File.Exists(Path.ChangeExtension(manifest, ".pck")) || File.Exists(Path.ChangeExtension(manifest, ".dll")))
                        return Reject(WorkshopLoadReason.InvalidPackage);
                    continue; // An unrelated author configuration is not a manifest.
                }
                using (doc)
                {
                    var j = doc.RootElement;
                    if (j.ValueKind != JsonValueKind.Object || !j.TryGetProperty("id", out var idField) || !j.TryGetProperty("has_pck", out var hp)) continue;
                    var id = idField.GetString();
                    var pckName = j.TryGetProperty("pck_name", out var pn) ? pn.GetString() : id;
                    if (!SafeName(id) || !SafeName(pckName)) return Reject(WorkshopLoadReason.InvalidPackage);
                    if (!j.TryGetProperty("affects_gameplay", out var ag) || ag.GetBoolean()) return Reject(WorkshopLoadReason.Gameplay);
                    if (!hp.GetBoolean()) return Reject(WorkshopLoadReason.IncompleteResources);
                    var root = Path.GetDirectoryName(manifest)!;
                    var pck = Path.Combine(root, pckName + ".pck");
                    if (!File.Exists(pck)) pck = SkinPackagePaths.Resolve(root, id!, ".pck");
                    if (!File.Exists(pck)) return Reject(WorkshopLoadReason.InvalidPackage);
                    var versionReason = CheckVersion(j, gameVersion);
                    if (versionReason != WorkshopLoadReason.None) return Reject(versionReason);
                    if (j.TryGetProperty("dependencies", out var deps) && deps.ValueKind != JsonValueKind.Null)
                    {
                        if (deps.ValueKind != JsonValueKind.Array) return Reject(WorkshopLoadReason.InvalidPackage);
                        foreach (var dep in deps.EnumerateArray())
                        {
                            var depId = dep.ValueKind == JsonValueKind.String ? dep.GetString() : dep.GetProperty("id").GetString();
                            if (string.IsNullOrWhiteSpace(depId)) return Reject(WorkshopLoadReason.InvalidPackage);
                            var minimum = dep.ValueKind == JsonValueKind.Object && dep.TryGetProperty("min_version", out var min) ? min.GetString() : null;
                            dependencies.Add((depId, minimum));
                        }
                    }
                    var hasDll = j.TryGetProperty("has_dll", out var hd) && hd.GetBoolean();
                    if (hasDll && !File.Exists(SkinPackagePaths.Resolve(root, id!, ".dll"))) return Reject(WorkshopLoadReason.InvalidPackage);
                    // The directory keeps the DLL for declaration scanners, but it must not be
                    // classified as a live code provider after the code-free proof below.
                    mods.Add(new(id!, j.TryGetProperty("name", out var n) ? n.GetString() ?? id! : id!, pck, false, root, false));
                }
            }
            if (mods.Count == 0 || mods.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mods.Count)
                return Reject(WorkshopLoadReason.InvalidPackage);
            foreach (var dep in dependencies)
            {
                var loaded = loadedDependencies.FirstOrDefault(pair => pair.Key.Equals(dep.Id, StringComparison.OrdinalIgnoreCase));
                if (loaded.Key == null || !MeetsMinimum(loaded.Value, dep.Minimum)) return Reject(WorkshopLoadReason.Dependency);
            }
            if (RequiresCodeAtStartup(directory)) return Reject(WorkshopLoadReason.StartupCode);
            return new(mods.ToArray(), WorkshopLoadReason.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { return Reject(WorkshopLoadReason.InvalidPackage); }
    }

    public static bool RequiresCodeAtStartup(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(path =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".dll" => !WorkshopCodeInspection.CanOmitExecution(path),
            ".so" or ".dylib" or ".gd" or ".gdc" or ".gdextension" => true,
            _ => false
        });

    internal static Dictionary<string, (long Length, DateTime Modified)> Snapshot(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Take(20001).ToArray();
        if (files.Length > 20000) throw new InvalidDataException("Workshop package contains too many files.");
        return files.ToDictionary(path => path, path => { var file = new FileInfo(path); return (file.Length, file.LastWriteTimeUtc); }, StringComparer.OrdinalIgnoreCase);
    }

    internal static bool Unchanged(string directory, Dictionary<string, (long Length, DateTime Modified)> before)
    {
        var after = Snapshot(directory);
        return before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var stamp) && stamp == pair.Value);
    }

    private static bool SafeName(string? name) => !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." && name.IndexOfAny(['/', '\\', ':']) < 0;
    private static bool MeetsMinimum(string actual, string? minimum) => string.IsNullOrWhiteSpace(minimum) ||
        (SemanticVersion.TryFromString(actual, out var version) && SemanticVersion.TryFromString(minimum, out var required) && version!.CompareTo(required) >= 0);
    private static WorkshopLoadReason CheckVersion(JsonElement j, string? gameVersion)
    {
        foreach (var p in j.EnumerateObject())
        {
            if (!p.Name.Contains("version", StringComparison.OrdinalIgnoreCase) || p.Name is "version" or "internal_test_version") continue;
            if (p.Name is not "min_game_version" and not "max_game_version") return WorkshopLoadReason.VersionRule;
            if (p.Value.ValueKind == JsonValueKind.Null) continue;
            if (gameVersion == null || !SemanticVersion.TryFromString(gameVersion, out var actual) ||
                !SemanticVersion.TryFromString(p.Value.GetString()!, out var bound)) return WorkshopLoadReason.VersionRule;
            var comparison = actual!.CompareTo(bound);
            if (p.Name == "min_game_version" ? comparison < 0 : comparison > 0) return WorkshopLoadReason.Version;
        }
        return WorkshopLoadReason.None;
    }
}
