using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using System.Security.Cryptography;
using System.Text;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;

namespace STS2SkinChanger.Core;

internal static class SkinService
{
    public const float MinimumMonsterScale = 0.5f;
    public const float MaximumMonsterScale = 2f;
    public const float MonsterScaleStep = 0.05f;
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
    private static readonly Dictionary<string, string> MountedOverlayCache =
        new(StringComparer.Ordinal);
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

    private static string ConfigPath => System.IO.Path.Combine(OS.GetUserDataDir(), "skin_changer.json");
    private static string LegacyConfigPath =>
        System.IO.Path.Combine(OS.GetUserDataDir(), "sts2_skin_switcher.json");

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

    public static bool ShouldShowLoadOrderWarning(bool isFirstInLoadOrder)
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            var movedAwayFromFirst =
                Config.LastKnownFirstInLoadOrder == true && !isFirstInLoadOrder;
            var stateChanged = Config.LastKnownFirstInLoadOrder != isFirstInLoadOrder;
            if (movedAwayFromFirst)
            {
                Config.SuppressLoadOrderWarning = false;
                ModLog.Info("检测到本 Mod 从加载顺序第一位移出，已恢复加载顺序提醒。");
            }

            Config.LastKnownFirstInLoadOrder = isFirstInLoadOrder;
            if (stateChanged || movedAwayFromFirst)
            {
                Config.Save(ConfigPath);
            }

            return !isFirstInLoadOrder && !Config.SuppressLoadOrderWarning;
        }
    }

    public static void EnsureConfigLoaded()
    {
        lock (Sync)
        {
            if (!_configLoaded)
            {
                Config = LoadConfig();
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
                Catalog?.Dispose();
                Catalog = null;
                RuntimeResourceCache.Clear();
                CardPortraitCache.Clear();
                AncientStyleMethods.Clear();
                MissingAncientStyleMethods.Clear();
                FailedAncientStyleMethods.Clear();
                MountedOverlayCache.Clear();
                CleanupOldOverlays();
                var executableDirectory = System.IO.Path.GetDirectoryName(OS.GetExecutablePath())!;
                var gamePckPath = System.IO.Path.Combine(executableDirectory, "SlayTheSpire2.pck");
                var loadedMods = ModManager.GetLoadedMods()
                    .Where(mod => mod.manifest is { id: not null })
                    .Where(mod => !Entry.IsSelfModId(mod.manifest!.id))
                    .ToArray();
                var mods = loadedMods
                    .Select(mod => new SkinModDescriptor(
                        mod.manifest!.id!,
                        mod.manifest.name ?? mod.manifest.id!,
                        mod.manifest.hasPck
                            ? System.IO.Path.Combine(mod.path, mod.manifest.id + ".pck")
                            : null,
                        mod.manifest.affectsGameplay ||
                        ManagedSkinModLoader.IsRequiredByAnotherMod(mod, loadedMods),
                        mod.path,
                        mod.manifest.hasDll))
                    .ToArray();

                Catalog = SkinCatalog.Build(gamePckPath, mods);
                Config = LoadConfig();
                _configLoaded = true;
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
                Catalog?.Dispose();
                Catalog = null;
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

            var group = Catalog.Groups.FirstOrDefault(group =>
                group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (group == null ||
                (!optionId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) &&
                 group.Options.All(option => !option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))))
            {
                LastError = $"未知的皮肤选择：{groupId}/{optionId}";
                return false;
            }

            var updates = Catalog.BuildVisualSelectionTransaction(
                groupId,
                optionId,
                Config.Selections);
            var previousSelections = updates.Keys.ToDictionary(
                key => key,
                key => Config.Selections.TryGetValue(key, out var previous)
                    ? (HadValue: true, Value: (string?)previous)
                    : (HadValue: false, Value: (string?)null),
                StringComparer.OrdinalIgnoreCase);
            var affectedGroups = updates.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var update in updates)
                {
                    Config.Selections[update.Key] = update.Value;
                    ClearRuntimeResourceCache(update.Key);
                }

                MountOverlay(affectedGroups);
                Config.Save(ConfigPath);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                foreach (var previous in previousSelections)
                {
                    RestoreSelection(previous.Key, previous.Value.Value, previous.Value.HadValue);
                    ClearRuntimeResourceCache(previous.Key);
                }

                TryRestoreOverlay(affectedGroups, cardOverlay: false);
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

            var selectionKey = CardSelectionKey(groupId);
            var hadPrevious = Config.Selections.TryGetValue(selectionKey, out var previous);
            try
            {
                Config.Selections[selectionKey] = optionId;
                ClearCardPortraitCache(groupId);
                MountCardOverlay(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupId });
                Config.Save(ConfigPath);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                RestoreSelection(selectionKey, previous, hadPrevious);
                ClearCardPortraitCache(groupId);
                TryRestoreOverlay(groupId, cardOverlay: true);
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

    public static bool ShouldDriveManagedCharacterAnimations(string groupId)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            if (catalog == null)
            {
                return false;
            }

            var selectedId = Config.GetSelection(groupId);
            return catalog.IsRuntimeProviderOption(groupId, selectedId) &&
                   catalog.ProviderUsesManagedCharacterScene(groupId, selectedId);
        }
    }

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

    public static CardPresentationDefinition? GetCardPresentation(CardModel card)
    {
        lock (Sync)
        {
            var group = GetCardGroup(card);
            var selection = GetEffectiveCardSelection(card);
            var option = group?.Options.FirstOrDefault(candidate =>
                candidate.Id.Equals(selection, StringComparison.OrdinalIgnoreCase) &&
                CardOptionAffectsCard(candidate, card));
            return option?.CardPresentations.GetValueOrDefault(card.GetType().Name);
        }
    }

    public static T? LoadCardPresentationResource<T>(CardModel card, string? resourcePath)
        where T : Resource
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            return null;
        }

        lock (Sync)
        {
            var group = GetCardGroup(card);
            var selection = GetEffectiveCardSelection(card);
            var option = group?.Options.FirstOrDefault(candidate =>
                candidate.Id.Equals(selection, StringComparison.OrdinalIgnoreCase) &&
                CardOptionAffectsCard(candidate, card));
            var cacheKey = $"card-presentation:{typeof(T).FullName}:{selection}:{resourcePath}";
            if (RuntimeResourceCache.TryGetValue(cacheKey, out var cached) &&
                cached is T typedCached &&
                GodotObject.IsInstanceValid(typedCached))
            {
                return typedCached;
            }

            T? resource = null;
            if (group != null)
            {
                foreach (var useSelectedProvider in new[] { true, false })
                {
                    if (useSelectedProvider && option?.ProviderRootPath == null)
                    {
                        continue;
                    }

                    try
                    {
                        var generation = ++_overlayGeneration;
                        var sourceName = useSelectedProvider ? "provider" : "base";
                        var overlay = Catalog!.BuildIsolatedCardResource(
                            group.Id,
                            selection,
                            resourcePath,
                            useSelectedProvider,
                            $"{_sessionId}/{generation:D3}_card_presentation_{sourceName}");
                        var overlayPath = System.IO.Path.Combine(
                            OS.GetUserDataDir(),
                            $"sts2_skin_overlay_{_sessionId}_{generation:D3}_card_presentation_{sourceName}.pck");
                        PckArchive.Write(overlayPath, overlay.Files);
                        if (ProjectSettings.LoadResourcePack(overlayPath, replaceFiles: true))
                        {
                            resource = ResourceLoader.Load<T>(
                                overlay.ResourcePaths[resourcePath],
                                null,
                                ResourceLoader.CacheMode.IgnoreDeep);
                        }
                    }
                    catch
                    {
                        // Provider configs may intentionally point at a base-game resource. Try the
                        // isolated baseline next so another globally loaded skin cannot supply it.
                    }

                    if (resource != null)
                    {
                        break;
                    }
                }
            }

            resource ??= ResourceLoader.Load<T>(
                resourcePath,
                null,
                ResourceLoader.CacheMode.Reuse);
            if (resource != null)
            {
                RuntimeResourceCache[cacheKey] = resource;
            }
            return resource;
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

            var key = IndividualCardSelectionKey(card);
            var hadPrevious = Config.Selections.TryGetValue(key, out var previous);
            try
            {
                if (optionId.Equals(InheritCardSelectionId, StringComparison.OrdinalIgnoreCase))
                {
                    Config.Selections.Remove(key);
                }
                else
                {
                    Config.Selections[key] = optionId;
                }

                ClearCardPortraitCache(group.Id);
                MountCardOverlay(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Id });
                Config.Save(ConfigPath);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                RestoreSelection(key, previous, hadPrevious);
                ClearCardPortraitCache(group.Id);
                TryRestoreOverlay(group.Id, cardOverlay: true);
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
            var originalPath = card.PortraitPath;
            if (selection.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase))
            {
                var hasManagedSkin = group.Options.Any(option =>
                    option.Assets.Keys.Any(assetPath => CardArtMatches(assetPath, card)) ||
                    option.NormalPortraits.ContainsKey(card.GetType().Name) ||
                    option.AncientPortraits.ContainsKey(card.GetType().Name));
                if (hasManagedSkin)
                {
                    ReplaceBaselineCardPortrait(groupId, originalPath, ref result);
                }

                return;
            }

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

            if (option == null)
            {
                return;
            }

            var selectedProviderPath = option.Assets.Keys
                .Where(assetPath => CardArtMatches(assetPath, card))
                .OrderByDescending(assetPath => CardArtSelectionScore(assetPath, card, originalPath))
                .ThenBy(assetPath => assetPath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (selectedProviderPath == null)
            {
                // 该变体没有覆盖此卡时保留玩法 Mod 已返回的卡图，包括它自己的设置。
                return;
            }

            var cacheKey = $"{groupId}\n{selection}\npck\n{selectedProviderPath}";
            if (!CardPortraitCache.TryGetValue(cacheKey, out var portrait) ||
                !GodotObject.IsInstanceValid(portrait))
            {
                portrait = LoadIsolatedCardPortrait(
                    groupId,
                    selection,
                    selectedProviderPath,
                    useSelectedProvider: true);
                if (portrait == null)
                {
                    return;
                }

                CardPortraitCache[cacheKey] = portrait;
            }

            result = portrait;
        }
    }

    private static void ReplaceBaselineCardPortrait(
        string groupId,
        string resourcePath,
        ref Texture2D result)
    {
        var cacheKey = $"{groupId}\n{SkinCatalog.BaseOptionId}\npck\n{resourcePath}";
        if (!CardPortraitCache.TryGetValue(cacheKey, out var portrait) ||
            !GodotObject.IsInstanceValid(portrait))
        {
            portrait = LoadIsolatedCardPortrait(
                groupId,
                SkinCatalog.BaseOptionId,
                resourcePath,
                useSelectedProvider: false);
            if (portrait == null)
            {
                return;
            }

            CardPortraitCache[cacheKey] = portrait;
        }

        result = portrait;
    }

    private static int CardArtSelectionScore(
        string assetPath,
        CardModel card,
        string originalPath)
    {
        var score = HasSameResourceExtension(assetPath, originalPath) ? 20 : 0;
        var assetUsesBeta = assetPath.Contains("/beta/", StringComparison.OrdinalIgnoreCase);
        var originalUsesBeta = originalPath.Contains("/beta/", StringComparison.OrdinalIgnoreCase);
        if (assetUsesBeta == originalUsesBeta)
        {
            score += 100;
        }

        var asset = CardPortraitIdentity(assetPath);
        var original = CardPortraitIdentity(originalPath);
        var typeStem = NormalizeCardToken(card.GetType().Name);
        var expectedStem = original?.Stem ?? typeStem;
        if (asset?.Stem.Equals(expectedStem, StringComparison.OrdinalIgnoreCase) == true ||
            asset?.Stem.Equals(typeStem, StringComparison.OrdinalIgnoreCase) == true)
        {
            score += 30;
        }

        var level = GetCardPresentationLevel(card);
        foreach (var expected in new[] { expectedStem, typeStem }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (asset == null ||
                !asset.Value.Stem.StartsWith(expected, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(asset.Value.Stem[expected.Length..], out var variantNumber))
            {
                continue;
            }

            score += variantNumber == level + 1
                ? 40
                : Math.Max(0, 10 - Math.Abs(variantNumber - level - 1));
            break;
        }

        return score;
    }

    private static int GetCardPresentationLevel(CardModel card)
    {
        foreach (var propertyName in new[] { "FakeUpgradeLevel", "CurrentUpgradeLevel" })
        {
            try
            {
                var property = AccessTools.Property(card.GetType(), propertyName);
                if (property?.GetValue(card) is int level)
                {
                    return Math.Max(0, level);
                }
            }
            catch
            {
                // Provider-specific levels are optional; fall back to the base presentation.
            }
        }

        return 0;
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
               option.CardPresentations.ContainsKey(cardType) ||
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
        var portraitIdentity = CardPortraitIdentity(card.PortraitPath);
        var poolGroupId = GetCardPoolGroupId(card);
        var assetIdentity = CardPortraitIdentity(
            assetPath,
            poolGroupId,
            portraitIdentity?.Category);
        if (assetIdentity == null || portraitIdentity == null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(assetIdentity.Value.Category) &&
            !assetIdentity.Value.Category.Equals(
                poolGroupId, StringComparison.OrdinalIgnoreCase) &&
            !assetIdentity.Value.Category.Equals(
                portraitIdentity.Value.Category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var typeStem = NormalizeCardToken(card.GetType().Name);
        return CardStemsMatch(assetIdentity.Value.Stem, portraitIdentity.Value.Stem) ||
               CardStemsMatch(assetIdentity.Value.Stem, typeStem);
    }

    private static (string Category, string Stem)? CardPortraitIdentity(
        string? path,
        string? expectedCategory = null,
        string? alternateCategory = null)
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
            var directoryStart = markerIndex + markerLength;
            var fileSeparator = lowerPath.LastIndexOf('/');
            if (fileSeparator >= directoryStart)
            {
                var directories = lowerPath[directoryStart..fileSeparator]
                    .Split('/', StringSplitOptions.RemoveEmptyEntries);
                var categoryIndex = Array.FindIndex(directories, candidate =>
                    (!string.IsNullOrWhiteSpace(expectedCategory) &&
                     candidate.Equals(expectedCategory, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(alternateCategory) &&
                     candidate.Equals(alternateCategory, StringComparison.OrdinalIgnoreCase)));
                if (categoryIndex < 0 && directories.Length > 0)
                {
                    categoryIndex = 0;
                }

                if (categoryIndex >= 0)
                {
                    category = directories[categoryIndex];
                }
            }
        }

        var fileName = lowerPath[(lowerPath.LastIndexOf('/') + 1)..];
        var extensionIndex = fileName.LastIndexOf('.');
        var rawStem = extensionIndex >= 0 ? fileName[..extensionIndex] : fileName;
        var typeSeparator = rawStem.LastIndexOf('.');
        if (typeSeparator >= 0)
        {
            rawStem = rawStem[(typeSeparator + 1)..];
        }

        foreach (var suffix in new[]
                 {
                     "_card_art", "-card-art", " card art", "card_art", "cardart"
                 })
        {
            if (rawStem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                rawStem = rawStem[..^suffix.Length];
                break;
            }
        }

        var stem = NormalizeCardToken(rawStem);
        return (category, stem);
    }

    private static bool CardStemsMatch(string candidate, string expected) =>
        candidate.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
        candidate.Equals(expected + "ancient", StringComparison.OrdinalIgnoreCase) ||
        candidate.Equals(expected + "normal", StringComparison.OrdinalIgnoreCase) ||
        candidate.Equals(expected + "portrait", StringComparison.OrdinalIgnoreCase) ||
        candidate.Equals(expected + "art", StringComparison.OrdinalIgnoreCase) ||
        IsNumberedCardVariant(candidate, expected);

    private static bool IsNumberedCardVariant(string candidate, string expected) =>
        candidate.StartsWith(expected, StringComparison.OrdinalIgnoreCase) &&
        candidate.Length > expected.Length &&
        candidate[expected.Length..].All(char.IsDigit);

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
                    option.ProviderId ?? option.Id, StringComparison.OrdinalIgnoreCase) == true)
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
        return GetOrLoadRuntimeResource(
                   groupId,
                   scenePath,
                   includeProviderDependencies: true) as PackedScene ??
               throw new InvalidOperationException($"独立皮肤资源不是场景：{scenePath}");
    }

    public static Resource GetOrLoadRuntimeResource(
        string groupId,
        string resourcePath,
        bool includeProviderDependencies = false)
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
            return LoadRuntimeResources(
                groupId,
                [resourcePath],
                includeProviderDependencies)[resourcePath];
        }
    }

    public static bool IsRuntimeProviderSelected(string groupId)
    {
        lock (Sync)
        {
            return Catalog?.IsRuntimeProviderOption(groupId, Config.GetSelection(groupId)) == true;
        }
    }

    public static string? GetSelectedFullRuntimeProvider(string groupId)
    {
        lock (Sync)
        {
            if (Catalog == null)
            {
                return null;
            }

            var selection = Config.GetSelection(groupId);
            return Catalog.IsRuntimeProviderOption(groupId, selection) &&
                   Catalog.ProviderUsesFullRuntime(selection) &&
                   Catalog.IsFullRuntimeProviderFullySelected(selection, Config.Selections)
                ? selection
                : null;
        }
    }

    public static void ApplySelectedVisualPostfix(
        string groupId,
        object model,
        ref MegaCrit.Sts2.Core.Nodes.Combat.NCreatureVisuals visuals)
    {
        string? providerId;
        lock (Sync)
        {
            var selection = Config.GetSelection(groupId);
            var catalog = Catalog;
            providerId = catalog?.IsRuntimeProviderOption(groupId, selection) == true &&
                         (!catalog.ProviderUsesFullRuntime(selection) ||
                          catalog.IsFullRuntimeProviderFullySelected(selection, Config.Selections))
                ? selection
                : null;
        }

        if (providerId != null)
        {
            ManagedSkinModLoader.ApplySelectedVisualPostfix(providerId, model, ref visuals);
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

    public static bool IsManagedResourceOptionSelected(string groupId)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            return catalog != null &&
                   catalog.IsResourceBackedOption(groupId, Config.GetSelection(groupId));
        }
    }

    public static float GetSelectedMonsterScale(string groupId)
    {
        lock (Sync)
        {
            var optionId = Config.GetSelection(groupId);
            return Config.MonsterScales.TryGetValue(groupId, out var options) &&
                   options.TryGetValue(optionId, out var scale)
                ? Mathf.Clamp(scale, MinimumMonsterScale, MaximumMonsterScale)
                : 1f;
        }
    }

    public static void SetSelectedMonsterScale(string groupId, float scale)
    {
        lock (Sync)
        {
            var normalized = Mathf.Clamp(
                Mathf.Round(scale / MonsterScaleStep) * MonsterScaleStep,
                MinimumMonsterScale,
                MaximumMonsterScale);
            var optionId = Config.GetSelection(groupId);
            if (Mathf.IsEqualApprox(normalized, 1f))
            {
                if (Config.MonsterScales.TryGetValue(groupId, out var existing))
                {
                    existing.Remove(optionId);
                    if (existing.Count == 0)
                    {
                        Config.MonsterScales.Remove(groupId);
                    }
                }
            }
            else
            {
                if (!Config.MonsterScales.TryGetValue(groupId, out var options))
                {
                    options = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                    Config.MonsterScales[groupId] = options;
                }

                options[optionId] = normalized;
            }

            Config.Save(ConfigPath);
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

    public static AncientLayeredImageTextures? GetSelectedAncientLayeredImageTextures(string groupId)
    {
        lock (Sync)
        {
            var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
            var paths = catalog.GetAncientLayeredImagePaths(
                groupId,
                Config.GetSelection(groupId));
            if (paths == null)
            {
                return null;
            }

            var requestedPaths = new[]
                {
                    paths.Character,
                    paths.BackgroundCover,
                    paths.Mask,
                    paths.SleepingCharacter
                }
                .Where(path => path != null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var resources = LoadRuntimeResources(groupId, requestedPaths);

            Texture2D Required(string path) =>
                resources.GetValueOrDefault(path) as Texture2D ??
                throw new InvalidOperationException($"远古图层资源不是贴图：{path}");
            Texture2D? Optional(string? path) =>
                path == null ? null : Required(path);

            return new AncientLayeredImageTextures(
                Required(paths.Character),
                Optional(paths.BackgroundCover),
                Optional(paths.Mask),
                Optional(paths.SleepingCharacter));
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
            var restoreGlobalSelections = includeProviderDependencies &&
                                          catalog.IsResourceBackedOption(
                                              groupId,
                                              Config.GetSelection(groupId));
            try
            {
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
            }
            finally
            {
                if (restoreGlobalSelections)
                {
                    // 二进制场景无法改写其内部路径，加载时可能临时挂载同一提供者的
                    // 其它分组依赖。资源对象创建后立即重新覆盖全部当前选择，避免这些
                    // 临时依赖继续占用其它角色、怪物或远古的全局路径。
                    MountOverlay(catalog.Groups
                        .Select(group => group.Id)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase));
                }
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

    private static void RestoreSelection(string key, string? previous, bool hadPrevious)
    {
        if (hadPrevious && previous != null)
        {
            Config.Selections[key] = previous;
        }
        else
        {
            Config.Selections.Remove(key);
        }
    }

    private static void TryRestoreOverlay(string groupId, bool cardOverlay)
    {
        TryRestoreOverlay(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupId },
            cardOverlay);
    }

    private static void TryRestoreOverlay(IReadOnlySet<string> groups, bool cardOverlay)
    {
        try
        {
            if (cardOverlay)
            {
                MountCardOverlay(groups);
            }
            else
            {
                MountOverlay(groups);
            }
        }
        catch (Exception restoreException)
        {
            ModLog.Error($"回滚 {string.Join(", ", groups)} 的皮肤覆盖失败：{restoreException}");
        }
    }

    private static void MountOverlay(IReadOnlySet<string> groups)
    {
        var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
        var selectedFullRuntimeProviders = catalog.GetFullySelectedFullRuntimeProviders(
            Config.Selections);
        // Provider callbacks must be gone before a baseline replacement pack is mounted. Otherwise
        // a stale AssetCache/TakeOverPath callback can immediately reclaim the path being restored.
        ManagedSkinModLoader.DeactivateProvidersExcept(selectedFullRuntimeProviders);

        var files = catalog.BuildOverlay(Config.Selections, groups);
        MountArchiveOverlay(files, "visual", "Godot 拒绝加载生成的皮肤资源包。");
        RefreshLocalizationIfNeeded(files.Keys);

        // Register scripts and run third-party initializers only after every private scene, atlas,
        // imported payload and frame directory is visible at its original res:// path. Static
        // resource fields in provider assemblies are often initialized on their first type access.
        foreach (var group in catalog.Groups.Where(group => groups.Contains(group.Id)))
        {
            var selectedId = Config.GetSelection(group.Id);
            if (catalog.IsRuntimeProviderOption(group.Id, selectedId) &&
                catalog.ProviderUsesManagedGodotScripts(selectedId))
            {
                ManagedSkinModLoader.EnsureProviderGodotScripts(selectedId);
            }
        }

        ManagedSkinModLoader.ActivateSelectedProviders(selectedFullRuntimeProviders);
    }

    private static void RefreshLocalizationIfNeeded(IEnumerable<string> mountedPaths)
    {
        if (!mountedPaths.Any(path =>
                path.Contains("/localization/", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        try
        {
            var manager = LocManager.Instance;
            if (manager == null || string.IsNullOrWhiteSpace(manager.Language))
            {
                // During boot LocManager initializes after Mod PCK mounting and reads the
                // selected files itself. Only an in-session switch needs an explicit reload.
                return;
            }

            manager.SetLanguage(manager.Language);
            ModLog.Info($"已刷新 {manager.Language} 本地化缓存。");
        }
        catch (Exception exception)
        {
            // A broken optional translation must not make an otherwise valid visual switch fail.
            ModLog.Warn("刷新皮肤本地化缓存失败：" + exception.GetBaseException().Message);
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

        MountArchiveOverlay(files, "cards", "Godot 拒绝加载生成的卡牌皮肤资源包。");
    }

    private static void MountArchiveOverlay(
        IReadOnlyDictionary<string, ResourceFile> files,
        string category,
        string failureMessage)
    {
        if (files.Count == 0)
        {
            return;
        }

        var signature = BuildOverlaySignature(files, category);
        if (!MountedOverlayCache.TryGetValue(signature, out var overlayPath) || !File.Exists(overlayPath))
        {
            overlayPath = System.IO.Path.Combine(
                OS.GetUserDataDir(),
                $"sts2_skin_overlay_{_sessionId}_{++_overlayGeneration:D3}_{category}.pck");
            var sources = files.ToDictionary(
                pair => pair.Key,
                pair => (pair.Value.Archive, pair.Value.Path),
                StringComparer.OrdinalIgnoreCase);
            PckArchive.WriteFromArchives(overlayPath, sources);
            MountedOverlayCache[signature] = overlayPath;
        }

        // Loading the same pack again is intentional. Godot gives the most recently loaded pack
        // priority, so an existing deterministic pack can restore paths shadowed by a temporary
        // runtime pack without writing another multi-hundred-megabyte copy to disk.
        if (!ProjectSettings.LoadResourcePack(overlayPath, replaceFiles: true))
        {
            MountedOverlayCache.Remove(signature);
            throw new InvalidOperationException(failureMessage);
        }
    }

    private static string BuildOverlaySignature(
        IReadOnlyDictionary<string, ResourceFile> files,
        string category)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSignaturePart(hash, category);
        foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            AppendSignaturePart(hash, pair.Key);
            AppendSignaturePart(hash, pair.Value.Archive.Path);
            AppendSignaturePart(hash, pair.Value.Path);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendSignaturePart(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
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

    private static SkinConfig LoadConfig()
    {
        if (File.Exists(ConfigPath))
        {
            return SkinConfig.Load(ConfigPath);
        }

        var config = SkinConfig.Load(LegacyConfigPath);
        if (File.Exists(LegacyConfigPath))
        {
            ModLog.Info("已将旧版 STS2SkinChanger 设置迁移到皮肤切换器-Skin Changer。");
        }
        return config;
    }

    private static void SanitizeSelections()
    {
        foreach (var group in Catalog!.Groups)
        {
            if (!Config.Selections.ContainsKey(group.Id))
            {
                Config.Selections[group.Id] = group.Options.FirstOrDefault()?.Id ?? SkinCatalog.BaseOptionId;
            }
        }

        // Older configurations could contain only one half of a multi-group DLL skin. Prefer the
        // provider selected by the greatest number of its groups, then make each winning bundle
        // coherent. Conflicting partial bundles are reset instead of leaving active callbacks able
        // to force resources belonging to another selection.
        var selectedBundles = Catalog.Groups
            .Select(group => Config.GetSelection(group.Id))
            .Where(Catalog.ProviderUsesFullRuntime)
            .GroupBy(providerId => providerId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .ToArray();
        var claimedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var providerId in selectedBundles)
        {
            var ownedGroups = Catalog.GetFullRuntimeProviderGroups(providerId);
            var conflictsWithExplicitChoice = ownedGroups.Any(ownedGroupId =>
            {
                var selectedId = Config.GetSelection(ownedGroupId);
                return !selectedId.Equals(providerId, StringComparison.OrdinalIgnoreCase) &&
                       !selectedId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase);
            });
            if (conflictsWithExplicitChoice || ownedGroups.Any(claimedGroups.Contains))
            {
                foreach (var ownedGroupId in ownedGroups.Where(ownedGroupId =>
                             Config.GetSelection(ownedGroupId)
                                 .Equals(providerId, StringComparison.OrdinalIgnoreCase)))
                {
                    Config.Selections[ownedGroupId] = SkinCatalog.BaseOptionId;
                }

                continue;
            }

            foreach (var ownedGroupId in ownedGroups)
            {
                Config.Selections[ownedGroupId] = providerId;
                claimedGroups.Add(ownedGroupId);
            }
        }

        SanitizeCardSelections();
    }

    private static void SanitizeCardSelections()
    {
        foreach (var group in Catalog!.CardGroups)
        {
            var key = CardSelectionKey(group.Id);
            if (!Config.Selections.ContainsKey(key))
            {
                Config.Selections[key] = group.Options.FirstOrDefault()?.Id ?? SkinCatalog.BaseOptionId;
            }
        }

        // Never erase explicit choices merely because a provider is temporarily unavailable
        // or its catalog was incomplete during this startup. Runtime lookup falls back safely,
        // and the stored choice becomes active again when the provider returns.
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

internal sealed record AncientLayeredImageTextures(
    Texture2D Character,
    Texture2D? BackgroundCover,
    Texture2D? Mask,
    Texture2D? SleepingCharacter);
