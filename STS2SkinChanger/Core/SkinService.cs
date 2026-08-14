using Godot;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;

namespace STS2SkinChanger.Core;

internal static class SkinService
{
    private static readonly object Sync = new();
    private static int _overlayGeneration;
    private static string _sessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    private static bool _initialized;

    public static SkinCatalog? Catalog { get; private set; }
    public static SkinConfig Config { get; private set; } = new();
    public static string? LastError { get; private set; }

    private static string ConfigPath => System.IO.Path.Combine(OS.GetUserDataDir(), "sts2_skin_switcher.json");

    public static void InitializeBeforeAssets()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            try
            {
                CleanupOldOverlays();
                var executableDirectory = System.IO.Path.GetDirectoryName(OS.GetExecutablePath())!;
                var gamePckPath = System.IO.Path.Combine(executableDirectory, "SlayTheSpire2.pck");
                var mods = ModManager.GetLoadedMods()
                    .Where(mod => mod.manifest is { hasPck: true, id: not null })
                    .Where(mod => !mod.manifest!.id!.Equals(Entry.ModId, StringComparison.OrdinalIgnoreCase))
                    .Select(mod => new SkinModDescriptor(
                        mod.manifest!.id!,
                        mod.manifest.name ?? mod.manifest.id!,
                        System.IO.Path.Combine(mod.path, mod.manifest.id + ".pck"),
                        mod.manifest.affectsGameplay))
                    .ToArray();

                Catalog = SkinCatalog.Build(gamePckPath, mods);
                Config = SkinConfig.Load(ConfigPath);
                SanitizeSelections();
                MountOverlay(Catalog.Groups.Select(group => group.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
                Config.Save(ConfigPath);
                ModLog.Info($"发现 {Catalog.Groups.Count} 个可切换外观组。角色与怪物选项已接入对应界面。");
            }
            catch (Exception exception)
            {
                LastError = exception.ToString();
                ModLog.Error("初始化失败：" + exception);
            }
        }
    }

    public static bool ApplySelection(string groupId, string optionId)
    {
        lock (Sync)
        {
            if (Catalog == null)
            {
                LastError = "皮肤目录尚未初始化。";
                return false;
            }

            var group = Catalog.Groups.FirstOrDefault(group => group.Id == groupId);
            if (group == null ||
                (optionId != SkinCatalog.BaseOptionId && group.Options.All(option => option.Id != optionId)))
            {
                LastError = $"未知的皮肤选择：{groupId}/{optionId}";
                return false;
            }

            try
            {
                Config.Selections[groupId] = optionId;
                MountOverlay(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupId });
                Config.Save(ConfigPath);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                ModLog.Error($"切换 {groupId} 失败：{exception}");
                return false;
            }
        }
    }

    private static void MountOverlay(IReadOnlySet<string> groups)
    {
        var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
        var files = catalog.BuildOverlay(Config.Selections, groups);
        if (files.Count == 0)
        {
            return;
        }

        var overlayPath = System.IO.Path.Combine(
            OS.GetUserDataDir(),
            $"sts2_skin_overlay_{_sessionId}_{++_overlayGeneration:D3}.pck");
        var sources = files.ToDictionary(
            pair => pair.Key,
            pair => (pair.Value.Archive, pair.Value.Path),
            StringComparer.OrdinalIgnoreCase);
        PckArchive.WriteFromArchives(overlayPath, sources);
        if (!ProjectSettings.LoadResourcePack(overlayPath, replaceFiles: true))
        {
            throw new InvalidOperationException("Godot 拒绝加载生成的皮肤资源包。");
        }

    }

    private static void SanitizeSelections()
    {
        foreach (var group in Catalog!.Groups)
        {
            var selected = Config.GetSelection(group.Id);
            if (selected != SkinCatalog.BaseOptionId && group.Options.All(option => option.Id != selected))
            {
                Config.Selections[group.Id] = SkinCatalog.BaseOptionId;
            }
        }
    }

    private static void CleanupOldOverlays()
    {
        var directory = OS.GetUserDataDir();
        foreach (var file in Directory.EnumerateFiles(directory, "sts2_skin_overlay_*.pck"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception)
            {
                ModLog.Warn($"无法清理旧皮肤缓存 {file}：{exception.Message}");
            }
        }
    }
}
