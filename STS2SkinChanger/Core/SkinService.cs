using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;

namespace STS2SkinChanger.Core;

internal static class SkinService
{
    public const string InheritCardSelectionId = "__inherit__";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Resource> RuntimeResourceCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D> CardPortraitCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, System.Reflection.MethodInfo> AncientStyleMethods =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MissingAncientStyleMethods =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FailedAncientStyleMethods =
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
    private static bool _configLoaded;
    private static bool _cardGroupsInitialized;
    private static string? _cardCatalogSignature;

    public static SkinCatalog? Catalog { get; private set; }
    public static SkinConfig Config { get; private set; } = new();

    private static volatile string? _lastError;

    // 用 volatile 保证异常路径下 UI 线程的可见性。
    public static string? LastError
    {
        get => _lastError;
        private set => _lastError = value;
    }

    private static string ConfigPath => System.IO.Path.Combine(OS.GetUserDataDir(), "sts2_skin_switcher.json");

    public static void SuppressLoadOrderWarning()
    {
        lock (Sync)
        {
            // 目录可能尚未初始化：先读取现有配置，避免用默认实例覆盖用户已保存的选择。
            EnsureConfigLoaded();
            Config.SuppressLoadOrderWarning = true;
            Config.Save(ConfigPath);
        }
    }

    public static void EnsureConfigLoaded()
    {
        lock (Sync)
        {
            if (!_configLoaded)
            {
                Config = SkinConfig.Load(ConfigPath);
                _configLoaded = true;
            }
        }
    }

    public static void InitializeBeforeAssets()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                RuntimeResourceCache.Clear();
                CardPortraitCache.Clear();
                AncientStyleMethods.Clear();
                MissingAncientStyleMethods.Clear();
                FailedAncientStyleMethods.Clear();
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
                // 仅在完整成功后才标记已初始化，失败时允许后续调用重试而不是整个会话失效。
                _initialized = true;
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
                SanitizeCardSelections(includeIndividualCards: true);
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
                (!optionId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) &&
                 group.Options.All(option => !option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))))
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
                (!optionId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) &&
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

    // 以下读取不持锁：所有写操作都发生在 Godot 主线程，与 UI 读取同线程；
    // LastError 用 volatile 保证异常路径下的可见性。
    public static string GetCardSelection(string groupId) =>
        Config.GetSelection(CardSelectionKey(groupId));

    public static IReadOnlyList<CardSkinOption> GetCardOptions(CardModel card)
    {
        lock (Sync)
        {
            var group = GetCardGroup(card);
            return group?.Options
                       .Where(option => CardOptionAffectsCard(option, card))
                       .ToArray() ?? [];
        }
    }

    public static bool HasCardSkin(CardModel card) => GetCardOptions(card).Count > 0;

    public static string GetCardOverrideSelection(CardModel card)
    {
        lock (Sync)
        {
            return Config.Selections.GetValueOrDefault(
                IndividualCardSelectionKey(card),
                InheritCardSelectionId);
        }
    }

    public static string GetEffectiveCardSelection(CardModel card)
    {
        lock (Sync)
        {
            var groupId = GetEffectiveCardGroupId(card);
            var individual = GetCardOverrideSelection(card);
            if (individual.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) ||
                GetCardOptions(card).Any(option =>
                    option.Id.Equals(individual, StringComparison.OrdinalIgnoreCase)))
            {
                return individual;
            }

            return GetCardSelection(groupId);
        }
    }

    public static string? GetCardPresentationProviderRoot(CardModel card)
    {
        lock (Sync)
        {
            var group = GetCardGroup(card);
            var selection = GetEffectiveCardSelection(card);
            return group?.Options.FirstOrDefault(option =>
                       option.Id.Equals(selection, StringComparison.OrdinalIgnoreCase) &&
                       CardOptionAffectsCard(option, card))
                   ?.ProviderRootPath;
        }
    }

    public static bool ApplyCardSelection(CardModel card, string optionId)
    {
        lock (Sync)
        {
            var group = GetCardGroup(card);
            if (group == null)
            {
                LastError = $"没有找到卡牌 {card.Id} 的皮肤分类。";
                return false;
            }

            if (!optionId.Equals(InheritCardSelectionId, StringComparison.OrdinalIgnoreCase) &&
                !optionId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) &&
                !group.Options.Any(option =>
                    option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase) &&
                    CardOptionAffectsCard(option, card)))
            {
                LastError = $"未知的单卡皮肤选择：{card.Id}/{optionId}";
                return false;
            }

            try
            {
                var key = IndividualCardSelectionKey(card);
                if (optionId.Equals(InheritCardSelectionId, StringComparison.OrdinalIgnoreCase))
                {
                    Config.Selections.Remove(key);
                }
                else
                {
                    Config.Selections[key] = optionId;
                }

                ClearCardPortraitCache(group.Id);
                Config.Save(ConfigPath);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                ModLog.Error($"切换单卡 {card.Id} 皮肤失败：{exception}");
                return false;
            }
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

            var selection = GetEffectiveCardSelection(card);
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

        return group.Options.Any(option => CardOptionAffectsCard(option, card));
    }

    private static CardSkinGroup? GetCardGroup(CardModel card)
    {
        var groupId = GetEffectiveCardGroupId(card);
        return Catalog?.CardGroups.FirstOrDefault(group =>
            group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool CardOptionAffectsCard(CardSkinOption option, CardModel card)
    {
        var cardType = card.GetType().Name;
        return option.NormalPortraits.ContainsKey(cardType) ||
               option.AncientPortraits.ContainsKey(cardType) ||
               option.Assets.Keys.Any(assetPath => CardArtMatches(assetPath, card));
    }

    private static string GetCardPoolGroupId(CardModel card) =>
        (card.Pool?.Title ?? string.Empty).ToLowerInvariant();

    private static string GetCardCatalogGroupId(CardModel card)
    {
        var poolGroupId = GetCardPoolGroupId(card);
        if (!SharedCardPoolIds.Contains(poolGroupId))
        {
            return poolGroupId;
        }

        return GetCardFilterGroupId(card);
    }

    private static readonly HashSet<CardRarity> MiscCardRarities =
    [
        CardRarity.Event,
        CardRarity.Token,
        CardRarity.Status,
        CardRarity.Curse,
        CardRarity.Quest
    ];

    private static string GetCardFilterGroupId(CardModel card)
    {
        if (card.Rarity == CardRarity.Ancient)
        {
            return "ancients";
        }

        return MiscCardRarities.Contains(card.Rarity)
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
            // 反射调用失败时每次渲染都会重复触发，只警告一次避免刷日志。
            if (FailedAncientStyleMethods.Add(option.Id))
            {
                ModLog.Warn($"读取 {option.Id} 的远古卡图样式设置失败：{exception.Message}");
            }

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

    public static bool IsExternalRuntimeProviderSelected(string groupId)
    {
        lock (Sync)
        {
            if (Catalog == null)
            {
                return false;
            }

            var selection = Config.GetSelection(groupId);
            return Catalog.IsRuntimeProviderOption(groupId, selection) &&
                   !Catalog.IsResourceBackedOption(groupId, selection) &&
                   Catalog.GetRuntimeImagePath(groupId, selection) != null;
        }
    }

    public static Texture2D GetSelectedRuntimeImageTexture(string groupId)
    {
        lock (Sync)
        {
            var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
            var selection = Config.GetSelection(groupId);
            var imagePath = catalog.GetRuntimeImagePath(groupId, selection) ??
                            throw new InvalidOperationException($"{groupId}/{selection} 没有独立图片资源。");
            var cacheKey = RuntimeResourceKey(groupId, "external-image:" + imagePath);
            if (RuntimeResourceCache.TryGetValue(cacheKey, out var cached) &&
                GodotObject.IsInstanceValid(cached) && cached is Texture2D cachedTexture)
            {
                return cachedTexture;
            }

            var image = Image.LoadFromFile(imagePath) ??
                        throw new InvalidOperationException($"无法读取独立皮肤图片：{imagePath}");
            var texture = ImageTexture.CreateFromImage(image);
            RuntimeResourceCache[cacheKey] = texture;
            return texture;
        }
    }

    public static IReadOnlyDictionary<string, PackedScene> LoadRuntimeScenes(
        string groupId,
        IReadOnlyCollection<string> scenePaths)
    {
        return LoadRuntimeResources(groupId, scenePaths, includeProviderDependencies: true).ToDictionary(
            pair => pair.Key,
            pair => pair.Value as PackedScene ??
                    throw new InvalidOperationException($"独立皮肤资源不是场景：{pair.Key}"),
            StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, Resource> LoadRuntimeResources(
        string groupId,
        IReadOnlyCollection<string> resourcePaths,
        bool includeProviderDependencies = false)
    {
        lock (Sync)
        {
            var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
            if (TryGetCachedRuntimeResources(groupId, resourcePaths, out var cached))
            {
                return cached;
            }

            var generation = ++_overlayGeneration;
            var aliasToken = $"{_sessionId}/{generation:D3}";
            var overlay = catalog.BuildRuntimeResourceOverlay(
                groupId,
                Config.GetSelection(groupId),
                resourcePaths,
                aliasToken,
                includeProviderDependencies);
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

    private static bool TryGetCachedRuntimeResources(
        string groupId,
        IReadOnlyCollection<string> resourcePaths,
        out IReadOnlyDictionary<string, Resource> resources)
    {
        var loaded = new Dictionary<string, Resource>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourcePath in resourcePaths)
        {
            var cacheKey = RuntimeResourceKey(groupId, resourcePath);
            if (!RuntimeResourceCache.TryGetValue(cacheKey, out var cached) ||
                !GodotObject.IsInstanceValid(cached))
            {
                resources = null!;
                return false;
            }

            loaded[resourcePath] = cached;
        }

        resources = loaded;
        return true;
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

    private static string IndividualCardSelectionKey(CardModel card) =>
        "cards:item:" + card.Id.ToString().ToLowerInvariant();

    private static string RuntimeResourceKey(string groupId, string resourcePath) =>
        groupId + "\n" + resourcePath;

    private static void SanitizeSelections()
    {
        foreach (var group in Catalog!.Groups)
        {
            if (!Config.Selections.TryGetValue(group.Id, out var selected) ||
                (!selected.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) &&
                 group.Options.All(option => !option.Id.Equals(selected, StringComparison.OrdinalIgnoreCase))))
            {
                // 无效或缺失的选择回退到游戏原版，而不是自动启用第一个皮肤。
                Config.Selections[group.Id] = SkinCatalog.BaseOptionId;
            }
        }

        SanitizeCardSelections();
    }

    private static void SanitizeCardSelections(bool includeIndividualCards = false)
    {
        foreach (var group in Catalog!.CardGroups)
        {
            var key = CardSelectionKey(group.Id);
            if (!Config.Selections.TryGetValue(key, out var selected) ||
                (!selected.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) &&
                 group.Options.All(option => !option.Id.Equals(selected, StringComparison.OrdinalIgnoreCase))))
            {
                Config.Selections[key] = SkinCatalog.BaseOptionId;
            }
        }

        if (!includeIndividualCards)
        {
            return;
        }

        var cards = ModelDb.AllCards
            .GroupBy(IndividualCardSelectionKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var key in Config.Selections.Keys
                     .Where(key => key.StartsWith("cards:item:", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            var selected = Config.Selections[key];
            if (!cards.TryGetValue(key, out var card) ||
                (!selected.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) &&
                 !GetCardOptions(card).Any(option =>
                     option.Id.Equals(selected, StringComparison.OrdinalIgnoreCase))))
            {
                Config.Selections.Remove(key);
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
