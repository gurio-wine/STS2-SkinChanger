using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;

namespace STS2SkinChanger.Core;

internal static class SkinService
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Resource> RuntimeResourceCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D> CardPortraitCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, System.Reflection.MethodInfo> AncientStyleMethods =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MissingAncientStyleMethods =
        new(StringComparer.OrdinalIgnoreCase);
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
                RuntimeResourceCache.Clear();
                CardPortraitCache.Clear();
                AncientStyleMethods.Clear();
                MissingAncientStyleMethods.Clear();
                CleanupOldOverlays();
                var executableDirectory = System.IO.Path.GetDirectoryName(OS.GetExecutablePath())!;
                var gamePckPath = System.IO.Path.Combine(executableDirectory, "SlayTheSpire2.pck");
                var mods = ModManager.GetLoadedMods()
                    .Where(mod => mod.manifest is { id: not null })
                    .Where(mod => !mod.manifest!.id!.Equals(Entry.ModId, StringComparison.OrdinalIgnoreCase))
                    .Select(mod => new SkinModDescriptor(
                        mod.manifest!.id!,
                        mod.manifest.name ?? mod.manifest.id!,
                        mod.manifest.hasPck
                            ? System.IO.Path.Combine(mod.path, mod.manifest.id + ".pck")
                            : null,
                        mod.manifest.affectsGameplay,
                        mod.path,
                        mod.manifest.hasDll))
                    .ToArray();

                Catalog = SkinCatalog.Build(gamePckPath, mods);
                Config = SkinConfig.Load(ConfigPath);
                SanitizeSelections();
                MountOverlay(Catalog.Groups.Select(group => group.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
                Config.Save(ConfigPath);
                ModLog.Info(
                    $"发现 {Catalog.Groups.Count} 个生物外观组和 {Catalog.CardGroups.Count} 个卡牌外观组。" +
                    "角色、怪物、远古者与卡牌选项已接入对应界面。");
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
                ClearRuntimeResourceCache(groupId);
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

    public static bool ApplyCardSelection(string groupId, string optionId)
    {
        lock (Sync)
        {
            if (Catalog == null)
            {
                LastError = "皮肤目录尚未初始化。";
                return false;
            }

            var group = Catalog.CardGroups.FirstOrDefault(group =>
                group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (group == null ||
                (optionId != SkinCatalog.BaseOptionId &&
                 group.Options.All(option => !option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))))
            {
                LastError = $"未知的卡牌皮肤选择：{groupId}/{optionId}";
                return false;
            }

            try
            {
                Config.Selections[CardSelectionKey(groupId)] = optionId;
                ClearCardPortraitCache(groupId);
                Config.Save(ConfigPath);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                ModLog.Error($"切换 {groupId} 卡牌皮肤失败：{exception}");
                return false;
            }
        }
    }

    public static string GetCardSelection(string groupId) =>
        Config.GetSelection(CardSelectionKey(groupId));

    public static bool ShouldRestoreStandardCardLayout(CardModel card)
    {
        lock (Sync)
        {
            var groupId = card.Pool.Title.ToLowerInvariant();
            var group = Catalog?.CardGroups.FirstOrDefault(group =>
                group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                return false;
            }

            var selection = GetCardSelection(groupId);
            var option = group.Options.FirstOrDefault(option =>
                option.Id.Equals(selection, StringComparison.OrdinalIgnoreCase));
            return option == null || !IsAncientStyleEnabled(option, card.GetType().Name);
        }
    }

    public static void ReplaceCardPortrait(CardModel card, ref Texture2D result)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            if (catalog == null)
            {
                return;
            }

            var groupId = card.Pool.Title.ToLowerInvariant();
            var group = catalog.CardGroups.FirstOrDefault(group =>
                group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                return;
            }

            var selection = GetCardSelection(groupId);
            var option = group.Options.FirstOrDefault(option =>
                option.Id.Equals(selection, StringComparison.OrdinalIgnoreCase));
            var cardType = card.GetType().Name;
            var path = option?.GetPortraitPath(
                cardType,
                IsAncientStyleEnabled(option, cardType));
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var cacheKey = $"{groupId}\n{selection}\n{path}";
            if (!CardPortraitCache.TryGetValue(cacheKey, out var portrait) ||
                !GodotObject.IsInstanceValid(portrait))
            {
                var loaded = ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse);
                if (loaded == null)
                {
                    return;
                }

                portrait = new AtlasTexture
                {
                    Atlas = loaded,
                    Region = new Rect2(0, 0, loaded.GetWidth(), loaded.GetHeight())
                };
                CardPortraitCache[cacheKey] = portrait;
            }

            result = portrait;
        }
    }

    private static bool IsAncientStyleEnabled(CardSkinOption option, string cardType)
    {
        if (!option.AncientPortraits.ContainsKey(cardType))
        {
            return false;
        }

        if (!AncientStyleMethods.TryGetValue(option.Id, out var method) &&
            !MissingAncientStyleMethods.Contains(option.Id))
        {
            method = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name?.Equals(
                    option.Id, StringComparison.OrdinalIgnoreCase) == true)
                .Select(assembly => assembly.GetType("CardPortraitsCore.ConfigHelper", throwOnError: false))
                .Where(type => type != null)
                .Select(type => type!.GetMethod(
                    "IsAncientStyleEnabled",
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic))
                .FirstOrDefault(candidate => candidate != null);
            if (method == null)
            {
                MissingAncientStyleMethods.Add(option.Id);
            }
            else
            {
                AncientStyleMethods[option.Id] = method;
            }
        }

        if (method == null)
        {
            return true;
        }

        try
        {
            return method.Invoke(null, [cardType]) as bool? ?? true;
        }
        catch (Exception exception)
        {
            ModLog.Warn($"读取 {option.Id} 的远古卡图样式设置失败：{exception.Message}");
            return true;
        }
    }

    public static PackedScene LoadRuntimeScene(string groupId, string scenePath)
    {
        return LoadRuntimeScenes(groupId, [scenePath])[scenePath];
    }

    public static PackedScene GetOrLoadRuntimeScene(string groupId, string scenePath)
    {
        return GetOrLoadRuntimeResource(groupId, scenePath) as PackedScene ??
               throw new InvalidOperationException($"独立皮肤资源不是场景：{scenePath}");
    }

    public static Resource GetOrLoadRuntimeResource(string groupId, string resourcePath)
    {
        lock (Sync)
        {
            var cacheKey = RuntimeResourceKey(groupId, resourcePath);
            if (RuntimeResourceCache.TryGetValue(cacheKey, out var cached) &&
                GodotObject.IsInstanceValid(cached))
            {
                return cached;
            }

            RuntimeResourceCache.Remove(cacheKey);
            return LoadRuntimeResources(groupId, [resourcePath])[resourcePath];
        }
    }

    public static bool IsRuntimeProviderSelected(string groupId)
    {
        lock (Sync)
        {
            return Catalog?.IsRuntimeProviderOption(groupId, Config.GetSelection(groupId)) == true;
        }
    }

    public static IReadOnlyDictionary<string, PackedScene> LoadRuntimeScenes(
        string groupId,
        IReadOnlyCollection<string> scenePaths)
    {
        return LoadRuntimeResources(groupId, scenePaths).ToDictionary(
            pair => pair.Key,
            pair => pair.Value as PackedScene ??
                    throw new InvalidOperationException($"独立皮肤资源不是场景：{pair.Key}"),
            StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, Resource> LoadRuntimeResources(
        string groupId,
        IReadOnlyCollection<string> resourcePaths)
    {
        lock (Sync)
        {
            var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
            var generation = ++_overlayGeneration;
            var aliasToken = $"{_sessionId}/{generation:D3}";
            var overlay = catalog.BuildRuntimeResourceOverlay(
                groupId,
                Config.GetSelection(groupId),
                resourcePaths,
                aliasToken);
            var overlayPath = System.IO.Path.Combine(
                OS.GetUserDataDir(),
                $"sts2_skin_overlay_{_sessionId}_{generation:D3}_runtime.pck");
            PckArchive.Write(overlayPath, overlay.Files);
            if (!ProjectSettings.LoadResourcePack(overlayPath, replaceFiles: true))
            {
                throw new InvalidOperationException("Godot 拒绝加载独立皮肤场景资源包。");
            }

            var resources = new Dictionary<string, Resource>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in overlay.ResourcePaths)
            {
                var resource = ResourceLoader.Load<Resource>(
                    pair.Value,
                    null,
                    ResourceLoader.CacheMode.IgnoreDeep);
                if (resource == null)
                {
                    throw new InvalidOperationException($"无法加载独立皮肤资源：{pair.Value}");
                }

                resources[pair.Key] = resource;
                RuntimeResourceCache[RuntimeResourceKey(groupId, pair.Key)] = resource;
            }

            ModLog.Info($"已从独立路径加载 {groupId} 的骨骼、图集、贴图与 {resources.Count} 个资源：{aliasToken}");
            return resources;
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

    private static void ClearRuntimeResourceCache(string groupId)
    {
        var prefix = groupId + "\n";
        foreach (var key in RuntimeResourceCache.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            RuntimeResourceCache.Remove(key);
        }
    }

    private static void ClearCardPortraitCache(string groupId)
    {
        var prefix = groupId + "\n";
        foreach (var key in CardPortraitCache.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            CardPortraitCache.Remove(key);
        }
    }

    private static string CardSelectionKey(string groupId) => "cards:" + groupId;

    private static string RuntimeResourceKey(string groupId, string resourcePath) =>
        groupId + "\n" + resourcePath;

    private static void SanitizeSelections()
    {
        foreach (var group in Catalog!.Groups)
        {
            if (!Config.Selections.TryGetValue(group.Id, out var selected) ||
                (selected != SkinCatalog.BaseOptionId && group.Options.All(option => option.Id != selected)))
            {
                Config.Selections[group.Id] = group.Options.FirstOrDefault()?.Id ?? SkinCatalog.BaseOptionId;
            }
        }

        foreach (var group in Catalog.CardGroups)
        {
            var key = CardSelectionKey(group.Id);
            if (!Config.Selections.TryGetValue(key, out var selected) ||
                (selected != SkinCatalog.BaseOptionId &&
                 group.Options.All(option => !option.Id.Equals(selected, StringComparison.OrdinalIgnoreCase))))
            {
                Config.Selections[key] = group.Options.FirstOrDefault()?.Id ?? SkinCatalog.BaseOptionId;
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
