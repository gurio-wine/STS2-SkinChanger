using System.Text.Json;

namespace STS2SkinChanger.Catalog;

/// <summary>Locates a skin's physical files without changing its manifest/config/resource ID.</summary>
internal static class SkinPackagePaths
{
    public static string Resolve(string root, string id, string extension)
    {
        if (!SafeName(id) || extension is not (".dll" or ".pck"))
            throw new ArgumentException("无效的皮肤资源文件名。");
        var expected = Path.Combine(root, id + extension);
        // Never mix the DLL from an ID-named package with a PCK from an alternate package.
        // This fast path also avoids rescanning normal providers during every selection.
        if (File.Exists(Path.Combine(root, id + ".dll")) || File.Exists(Path.Combine(root, id + ".pck")))
            return expected;
        try
        {
            var manifests = FindManifests(root, id);
            if (manifests.Count != 1) return expected;
            var manifest = manifests[0];
            using var document = JsonDocument.Parse(File.ReadAllText(manifest).TrimStart('\uFEFF'));
            var json = document.RootElement;
            if (!json.TryGetProperty("affects_gameplay", out var gameplay) || gameplay.ValueKind != JsonValueKind.False)
                return expected;
            var hasDll = json.TryGetProperty("has_dll", out var dll) && dll.ValueKind == JsonValueKind.True;
            var hasPck = json.TryGetProperty("has_pck", out var pck) && pck.ValueKind == JsonValueKind.True;
            if ((!hasDll && !hasPck) || (extension == ".dll" && !hasDll) || (extension == ".pck" && !hasPck))
                return expected;
            var stem = Path.GetFileNameWithoutExtension(manifest);
            if (!SafeName(stem) || (hasDll && !File.Exists(Path.Combine(root, stem + ".dll"))) ||
                (hasPck && !File.Exists(Path.Combine(root, stem + ".pck")))) return expected;
            return Path.Combine(root, stem + extension);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return expected; // A partial download or unreadable manifest must not select a sibling.
        }
    }

    internal static IReadOnlyList<string> FindManifests(string root, string id)
    {
        if (!Directory.Exists(root)) return [];
        var matches = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)))
        {
            if (new FileInfo(path).Length > 1024 * 1024) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path).TrimStart('\uFEFF'));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String &&
                    string.Equals(value.GetString(), id, StringComparison.OrdinalIgnoreCase)) matches.Add(path);
            }
            catch (JsonException) { } // Ordinary author settings are not manifests.
        }
        return matches;
    }

    private static bool SafeName(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && value.IndexOfAny(['/', '\\', ':']) < 0;
}
