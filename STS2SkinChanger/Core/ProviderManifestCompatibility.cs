using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal static class ProviderManifestCompatibility
{
    private const string MigrationKey = "MOD_ERROR.MIGRATION_REQUIRED";
    private const string BranchKey = "MOD_ERROR.STEAM_BRANCH_UNSUPPORTED";

    public static MemoryStream? PrepareLegacyStream(Stream source)
    {
        if (!source.CanSeek || source.Length - source.Position > 1024 * 1024) return null;
        var position = source.Position;
        var converted = false;
        try
        {
            var json = JsonNode.Parse(source);
            if (json is not JsonObject || json["id"] is not JsonValue id || !id.TryGetValue<string>(out _) ||
                json["dependencies"] is not JsonArray { Count: > 0 } dependencies ||
                dependencies.Any(value => value is not JsonValue item || !item.TryGetValue<string>(out var name) ||
                    string.IsNullOrWhiteSpace(name))) return null;
            json["dependencies"] = new JsonArray(dependencies.Select(value => (JsonNode)new JsonObject
                { ["id"] = value!.GetValue<string>(), ["min_version"] = null }).ToArray());
            var result = new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()), writable: false);
            converted = true;
            return result;
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (!converted) source.Position = position; // Let the native reader report invalid input.
        }
    }

    public static void AcknowledgeParsedManifests(IEnumerable<Mod> mods)
    {
        // Initial manifests were read before any Mod initializer could install a hook. The
        // game has already migrated them. Verify that exact migration before acknowledging
        // its historical warning; never drop missing dependencies or invent minimum versions.
        foreach (var mod in mods)
        {
            if (mod.manifest?.id == null || mod.errors?.Any(error => IsError(error, MigrationKey)) != true) continue;
            try
            {
                var paths = SkinPackagePaths.FindManifests(mod.path, mod.manifest.id);
                if (paths.Count != 1) continue;
                using var input = File.OpenRead(paths[0]);
                using var normalized = PrepareLegacyStream(input);
                if (normalized == null) continue;
                var json = JsonNode.Parse(normalized)!;
                var expected = json["dependencies"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ToArray();
                if (mod.manifest.dependencies == null || mod.manifest.dependencies.Any(dep => dep.minVersion != null) ||
                    !mod.manifest.dependencies.Select(dep => dep.id).SequenceEqual(expected, StringComparer.Ordinal)) continue;
                mod.errors!.RemoveAll(error => IsError(error, MigrationKey));
                ModLog.Info($"已核验 {mod.manifest.id} 的旧依赖清单等价迁移：{string.Join("、", expected)}；" +
                    "依赖检查保留，原清单未修改；迁移提醒已处理。");
            }
            catch (Exception exception)
            {
                ModLog.Warn($"核验 {mod.manifest.id} 的清单迁移失败，保留原提示：{exception.GetBaseException().Message}");
            }
        }
    }

    public static void AcknowledgeManagedBranch(Mod mod, bool hasResourceBackedCosmetics)
    {
        // Only the successfully intercepted cosmetic loader calls this. Leave functional
        // mods, failed interception, missing files and unknown APIs under the native warning.
        if (!hasResourceBackedCosmetics || mod.state is not (ModLoadState.Loaded or ModLoadState.DisabledDuplicate) ||
            mod.manifest is not { id: not null, affectsGameplay: false } manifest ||
            mod.errors?.Any(error => IsError(error, BranchKey)) != true) return;
        try
        {
            if (manifest.hasPck && !File.Exists(SkinPackagePaths.Resolve(mod.path, manifest.id, ".pck"))) return;
            var conversions = 0;
            if (manifest.hasDll)
            {
                var path = SkinPackagePaths.Resolve(mod.path, manifest.id, ".dll");
                if (!File.Exists(path)) return;
                var preparation = ProviderAssemblyCompatibility.PrepareForCurrentGame(path);
                using var bytes = preparation.Assembly;
                if (preparation.Report.Failure != null || preparation.Report.UnresolvedReferences.Count != 0 ||
                    preparation.Report.UncheckedGenericReferences != 0) return;
                conversions = preparation.Report.Changes.Count;
            }
            mod.errors!.RemoveAll(error => IsError(error, BranchKey));
            ModLog.Info($"{manifest.id} 的工坊版本声明不含当前分支；本次已接管其皮肤资源并核验静态游戏接口" +
                $"（转换 {conversions} 项），不再把原声明作为加载错误显示。" +
                "工坊声明与原文件未修改；反射及实际游戏行为仍需运行验证。");
        }
        catch (Exception exception)
        {
            ModLog.Warn($"核验 {manifest.id} 的版本兼容状态失败，保留原提示：{exception.GetBaseException().Message}");
        }
    }

    private static bool IsError(LocString error, string key) => error.LocTable == "main_menu_ui" && error.LocEntryKey == key;
}

[HarmonyPatch(typeof(ModManifest), nameof(ModManifest.ReadFromStream))]
internal static class LegacyManifestReadCompatibilityPatch
{
    private static void Prefix(ref Stream stream, out MemoryStream? __state)
    {
        __state = ProviderManifestCompatibility.PrepareLegacyStream(stream);
        if (__state != null) stream = __state;
    }

    private static void Finalizer(MemoryStream? __state) => __state?.Dispose();
}
