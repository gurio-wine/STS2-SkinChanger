using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
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
    private static readonly HashSet<string> SharedCardPoolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "event",
        "token",
        "status",
        "curse",
        "quest",
        "deprived",
        "deprecated",
        "mock"
    };
    private static int _overlayGeneration;
    private static string _sessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    private static bool _initialized;
    private static bool _cardGroupsInitialized;
    private static string? _cardCatalogSignature;

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

    public static void InitializeCardGroupsAfterModels()
    {
        lock (Sync)
        {
            if (Catalog == null)
            {
                return;
            }

            try
            {
                var cards = ModelDb.AllCards.ToArray();
                var entries = cards.Select(card => new CardCatalogEntry(
                        card.GetType().Name,
                        card.PortraitPath,
                        GetCardPoolGroupId(card),
                        GetCardCatalogGroupId(card),
                        GetCardFilterGroupId(card)))
                    .ToArray();
                var signature = string.Join('\n', entries
                    .OrderBy(entry => entry.TypeName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.PortraitPath, StringComparer.OrdinalIgnoreCase)
                    .Select(entry =>
                        $"{entry.TypeName}|{entry.PortraitPath}|{entry.PoolGroupId}|" +
                        $"{entry.CatalogGroupId}|{entry.FilterGroupId}"));
                if (_cardGroupsInitialized && signature == _cardCatalogSignature)
                {
                    return;
                }

                Catalog.FinalizeCardGroups(entries);
                SanitizeCardSelections();
                MountCardOverlay(Catalog.CardGroups
                    .Select(group => group.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
                Config.Save(ConfigPath);
                _cardGroupsInitialized = true;
                _cardCatalogSignature = signature;
                LastError = null;
                ModLog.Info($"已按卡牌总览分类接入 {Catalog.CardGroups.Count} 个卡牌外观组。");
            }
            catch (Exception exception)
            {
                LastError = exception.ToString();
                ModLog.Error("按卡牌总览分类卡牌皮肤失败：" + exception);
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
                MountCardOverlay(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupId });
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
            var groupId = GetEffectiveCardGroupId(card);
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

            var groupId = GetEffectiveCardGroupId(card);
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
            if (!string.IsNullOrWhiteSpace(path))
            {
                ReplaceConfiguredCardPortrait(groupId, selection, path, ref result);
                return;
            }

            var originalPath = card.PortraitPath;
            var managed = group.Options
                    .SelectMany(candidate => candidate.Assets.Keys)
                    .Any(assetPath => CardArtMatches(assetPath, card));
            if (!managed)
            {
                return;
            }

            var selectedProviderPath = option?.Assets.Keys
                .Where(assetPath => CardArtMatches(assetPath, card))
                .OrderByDescending(assetPath => HasSameResourceExtension(assetPath, originalPath))
                .FirstOrDefault();
            var selectedPath = selectedProviderPath ?? originalPath;
            var cacheKey = $"{groupId}\n{selection}\npck\n{selectedPath}";
            if (!CardPortraitCache.TryGetValue(cacheKey, out var portrait) ||
                !GodotObject.IsInstanceValid(portrait))
            {
                portrait = LoadIsolatedCardPortrait(
                    groupId,
                    selection,
                    selectedPath,
                    selectedProviderPath != null);
                if (portrait == null)
                {
                    return;
                }

                CardPortraitCache[cacheKey] = portrait;
            }

            result = portrait;
        }
    }

    public static bool CardBelongsToGroup(CardModel card, string groupId) =>
        GetEffectiveCardGroupId(card).Equals(groupId, StringComparison.OrdinalIgnoreCase);

    private static string GetEffectiveCardGroupId(CardModel card)
    {
        var poolGroupId = GetCardPoolGroupId(card);
        var filterGroupId = GetCardFilterGroupId(card);
        if (!filterGroupId.Equals(poolGroupId, StringComparison.OrdinalIgnoreCase) &&
            CardGroupAffectsCard(filterGroupId, card))
        {
            return filterGroupId;
        }

        var cardType = card.GetType().Name;
        var configuredGroup = Catalog?.CardGroups.FirstOrDefault(group =>
            group.Options.Any(option =>
                option.NormalPortraits.ContainsKey(cardType) ||
                option.AncientPortraits.ContainsKey(cardType)));
        if (configuredGroup != null)
        {
            return configuredGroup.Id;
        }

        if (CardGroupAffectsCard(poolGroupId, card))
        {
            return poolGroupId;
        }

        var catalogGroupId = GetCardCatalogGroupId(card);
        return CardGroupAffectsCard(catalogGroupId, card) ? catalogGroupId : poolGroupId;
    }

    private static bool CardGroupAffectsCard(string groupId, CardModel card)
    {
        var group = Catalog?.CardGroups.FirstOrDefault(group =>
            group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            return false;
        }

        var cardType = card.GetType().Name;
        return group.Options.Any(option =>
            option.NormalPortraits.ContainsKey(cardType) ||
            option.AncientPortraits.ContainsKey(cardType) ||
            option.Assets.Keys.Any(assetPath => CardArtMatches(assetPath, card)));
    }

    private static string GetCardPoolGroupId(CardModel card) =>
        card.Pool.Title.ToLowerInvariant();

    private static string GetCardCatalogGroupId(CardModel card)
    {
        var poolGroupId = GetCardPoolGroupId(card);
        if (!SharedCardPoolIds.Contains(poolGroupId))
        {
            return poolGroupId;
        }

        return GetCardFilterGroupId(card);
    }

    private static string GetCardFilterGroupId(CardModel card)
    {
        if (card.Rarity == CardRarity.Ancient)
        {
            return "ancients";
        }

        var rarity = (int)card.Rarity;
        return rarity is >= (int)CardRarity.Event and <= (int)CardRarity.Quest
            ? "misc"
            : GetCardPoolGroupId(card);
    }

    private static void ReplaceConfiguredCardPortrait(
        string groupId,
        string selection,
        string path,
        ref Texture2D result)
    {
        var cacheKey = $"{groupId}\n{selection}\nconfig\n{path}";
        if (!CardPortraitCache.TryGetValue(cacheKey, out var portrait) ||
            !GodotObject.IsInstanceValid(portrait))
        {
            var loaded = LoadIsolatedCardPortrait(
                groupId,
                selection,
                path,
                useSelectedProvider: true);
            if (loaded == null)
            {
                return;
            }

            portrait = loaded is AtlasTexture
                ? loaded
                : new AtlasTexture
            {
                Atlas = loaded,
                Region = new Rect2(0, 0, loaded.GetWidth(), loaded.GetHeight())
            };
            CardPortraitCache[cacheKey] = portrait;
        }

        result = portrait;
    }

    private static Texture2D? LoadIsolatedCardPortrait(
        string groupId,
        string selection,
        string resourcePath,
        bool useSelectedProvider)
    {
        var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
        var generation = ++_overlayGeneration;
        var aliasToken = $"{_sessionId}/{generation:D3}_card";
        var overlay = catalog.BuildIsolatedCardResource(
            groupId,
            selection,
            resourcePath,
            useSelectedProvider,
            aliasToken);
        var overlayPath = System.IO.Path.Combine(
            OS.GetUserDataDir(),
            $"sts2_skin_overlay_{_sessionId}_{generation:D3}_card_resource.pck");
        PckArchive.Write(overlayPath, overlay.Files);
        if (!ProjectSettings.LoadResourcePack(overlayPath, replaceFiles: true))
        {
            throw new InvalidOperationException("Godot 拒绝加载独立卡图资源包。");
        }

        return ResourceLoader.Load<Texture2D>(
            overlay.ResourcePaths[resourcePath],
            null,
            ResourceLoader.CacheMode.IgnoreDeep);
    }

    private static bool CardArtMatches(string assetPath, CardModel card)
    {
        var assetIdentity = CardPortraitIdentity(assetPath);
        var portraitIdentity = CardPortraitIdentity(card.PortraitPath);
        if (assetIdentity == null || portraitIdentity == null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(assetIdentity.Value.Category) &&
            !assetIdentity.Value.Category.Equals(
                GetCardPoolGroupId(card), StringComparison.OrdinalIgnoreCase) &&
            !assetIdentity.Value.Category.Equals(
                portraitIdentity.Value.Category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var typeStem = NormalizeCardToken(card.GetType().Name);
        return CardStemsMatch(assetIdentity.Value.Stem, portraitIdentity.Value.Stem) ||
               CardStemsMatch(assetIdentity.Value.Stem, typeStem);
    }

    private static (string Category, string Stem)? CardPortraitIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var lowerPath = path.ToLowerInvariant();
        var markerIndex = -1;
        var markerLength = 0;
        foreach (var marker in new[]
                 {
                     "/card_portraits/",
                     "/card_atlas.sprites/",
                     "/cards/",
                     "/card/",
                     "/card_art/",
                     "/cardart/"
                 })
        {
            markerIndex = lowerPath.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                markerLength = marker.Length;
                break;
            }
        }

        var category = string.Empty;
        if (markerIndex >= 0)
        {
            var categoryStart = markerIndex + markerLength;
            var categoryEnd = lowerPath.IndexOf('/', categoryStart);
            if (categoryEnd > categoryStart)
            {
                category = lowerPath[categoryStart..categoryEnd];
            }
        }

        var fileName = lowerPath[(lowerPath.LastIndexOf('/') + 1)..];
        var extensionIndex = fileName.IndexOf('.');
        var stem = NormalizeCardToken(extensionIndex >= 0 ? fileName[..extensionIndex] : fileName);
        return (category, stem);
    }

    private static bool CardStemsMatch(string candidate, string expected) =>
        candidate.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
        candidate.Equals(expected + "ancient", StringComparison.OrdinalIgnoreCase) ||
        candidate.Equals(expected + "normal", StringComparison.OrdinalIgnoreCase) ||
        candidate.Equals(expected + "portrait", StringComparison.OrdinalIgnoreCase) ||
        candidate.Equals(expected + "art", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCardToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool HasSameResourceExtension(string left, string right)
    {
        var leftExtension = System.IO.Path.GetExtension(left);
        var rightExtension = System.IO.Path.GetExtension(right);
        return leftExtension.Equals(rightExtension, StringComparison.OrdinalIgnoreCase);
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

    private static void MountCardOverlay(IReadOnlySet<string> groups)
    {
        var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
        var files = catalog.BuildCardOverlay(Config.Selections, groups);
        if (files.Count == 0)
        {
            return;
        }

        var overlayPath = System.IO.Path.Combine(
            OS.GetUserDataDir(),
            $"sts2_skin_overlay_{_sessionId}_{++_overlayGeneration:D3}_cards.pck");
        var sources = files.ToDictionary(
            pair => pair.Key,
            pair => (pair.Value.Archive, pair.Value.Path),
            StringComparer.OrdinalIgnoreCase);
        PckArchive.WriteFromArchives(overlayPath, sources);
        if (!ProjectSettings.LoadResourcePack(overlayPath, replaceFiles: true))
        {
            throw new InvalidOperationException("Godot 拒绝加载生成的卡牌皮肤资源包。");
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

        SanitizeCardSelections();
    }

    private static void SanitizeCardSelections()
    {
        foreach (var group in Catalog!.CardGroups)
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
