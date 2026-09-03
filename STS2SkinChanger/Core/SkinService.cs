using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Combat;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;

namespace STS2SkinChanger.Core;

internal static class SkinService
{
    public const float MinimumMonsterScale = 0.2f;
    public const float MaximumMonsterScale = 5f;
    public const float MonsterScaleStep = 0.05f;
    public const float MinimumCharacterScale = 0.2f;
    public const float MaximumCharacterScale = 5f;
    public const float CharacterScaleStep = 0.05f;
    public const float MinimumCharacterOffset = -1000f;
    public const float MaximumCharacterOffset = 1000f;
    public const float CharacterOffsetStep = 1f;
    public const string InheritCardSelectionId = "__inherit__";
    public const string InheritMonsterSelectionId = "__monster_category__";
    private const string LegacyFollowCharacterSkinIconSelectionId = "__follow_character_skin__";
    public const int CardSkinPresetNameMaxLength = 40;
    private const long DirectRuntimeProviderPackThresholdBytes = 64L * 1024L * 1024L;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Resource> RuntimeResourceCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PreparedRuntimeOverlay> PreparedRuntimeOverlays =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, RuntimeResourceBundleState> RuntimeResourceBundles =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCardPortraitCacheEntries = 64;
    private static readonly BoundedLruCache<string, Texture2D> CardPortraitCache =
        new(MaxCardPortraitCacheEntries, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ExternalCardProviderIdentityPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D> BaselineRelicAtlasCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D> BaselineRelicIconCache =
        new(StringComparer.OrdinalIgnoreCase);
    // A malformed or incomplete provider card must never throw through CardModel.Portrait.
    // Cache the failed request for this session so every redraw does not rebuild the same
    // overlay (and so the vanilla portrait remains usable in choice screens).
    private static readonly HashSet<string> FailedCardPortraitRequests =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CardCoverageState> CardCoverageCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IsolatedCardOverlayState> IsolatedCardOverlayCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly CanonicalResourceOwnershipTracker CardCanonicalResourceOwners = new();
    private static readonly Dictionary<string, string> CardPreviewSelections =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> CharacterPreviewSelections =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, System.Reflection.MethodInfo> AncientStyleMethods =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MissingAncientStyleMethods =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FailedAncientStyleMethods =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> MountedOverlayCache =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> MountedScopedRuntimeProviderPacks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MountedProviderPackState> MountedLargeRuntimeProviderPacks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> RuntimeCanonicalDependencyPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WarmedRuntimeProviderPacks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WarmingRuntimeProviderPacks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim RuntimePackWarmGate = new(1, 1);
    private static readonly ScopedMonsterSelectionSnapshot ScopedMonsterSelections = new();
    private static RuntimeProviderScope? _runtimeProviderBehaviorScope;
    private static readonly RuntimeProviderScopeLeaseTracker RuntimeProviderScopeLeases = new();
    private static readonly Dictionary<string, ResourceFile> MountedLocalizationFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LocalizationCacheState> LocalizationStateCache =
        new(StringComparer.Ordinal);
    private static readonly System.Reflection.FieldInfo? LocTablesField =
        AccessTools.Field(typeof(LocManager), "_tables");
    private static readonly System.Reflection.MethodInfo? SetLanguageInternalMethod =
        AccessTools.Method(typeof(LocManager), "SetLanguageInternal");
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
    private static int _runtimePackWarmGeneration;
    private static string _sessionId =
        $"{DateTime.Now:yyyyMMdd-HHmmss}-{System.Environment.ProcessId}-{Guid.NewGuid():N}";
    private static bool _initialized;
    private static bool _configLoaded;
    private static bool _cardGroupsInitialized;
    private static bool _loadOrderAutoReorderLogged;
    private static string? _mountedLocalizationSignature;
    private static ConditionalWeakTable<CardModel, CardLookup> _cardLookupCache = new();

    public static SkinCatalog? Catalog { get; private set; }
    public static SkinConfig Config { get; private set; } = new();

    private static volatile string? _lastError;

    // 用 volatile 保证异常路径下 UI 线程的可见性。
    public static string? LastError
    {
        get => _lastError;
        private set => _lastError = value;
    }

    private static string GetVisualSelection(string groupId) =>
        MultiplayerSkinSync.GetScopedSelection(groupId) ??
        CharacterPreviewSelections.GetValueOrDefault(groupId) ??
        Config.GetSelection(groupId);

    private static IReadOnlyDictionary<string, string> GetVisualSelections()
    {
        return VisualSelectionOverlayPolicy.Merge(
            Config.Selections,
            CharacterPreviewSelections,
            MultiplayerSkinSync.GetScopedSelections());
    }

    internal static bool TryGetSelectedFrameworkContract(
        string groupId,
        out FrameworkCharacterSkinContract contract)
    {
        lock (Sync)
        {
            contract = null!;
            return Catalog != null && Catalog.TryGetSelectedFrameworkContract(
                groupId,
                GetVisualSelection(groupId),
                out contract);
        }
    }

    internal static IReadOnlyList<FrameworkCharacterSkinContract>
        GetSelectedFrameworkContracts()
    {
        lock (Sync)
        {
            if (Catalog == null)
            {
                return [];
            }

            return Catalog.Groups
                .Select(group => Catalog.TryGetSelectedFrameworkContract(
                    group.Id,
                    GetVisualSelection(group.Id),
                    out var contract)
                    ? contract
                    : null)
                .Where(contract => contract != null)
                .Cast<FrameworkCharacterSkinContract>()
                .ToArray();
        }
    }

    public static bool TryBuildSessionCharacterSelection(
        string groupId,
        string optionId,
        out IReadOnlyDictionary<string, string> selectionOverrides)
    {
        lock (Sync)
        {
            selectionOverrides = null!;
            var catalog = Catalog;
            var group = catalog?.Groups.FirstOrDefault(candidate =>
                candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (catalog == null || group == null || !catalog.IsCharacterAppearanceGroup(group.Id) ||
                (!optionId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) &&
                 group.Options.All(option => !option.Id.Equals(
                     optionId,
                     StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }

            var workingSelections = new Dictionary<string, string>(
                Config.Selections,
                StringComparer.OrdinalIgnoreCase);
            selectionOverrides = catalog.BuildVisualSelectionTransaction(
                group.Id,
                optionId,
                workingSelections);
            return true;
        }
    }

    public static bool TryBuildSessionCharacterComposition(
        string groupId,
        IReadOnlyList<string> sourceOptionIds,
        out IReadOnlyDictionary<string, string> selectionOverrides,
        out string resolvedOptionId)
    {
        lock (Sync)
        {
            selectionOverrides = null!;
            resolvedOptionId = SkinCatalog.BaseOptionId;
            var catalog = Catalog;
            if (catalog == null || !catalog.IsCharacterAppearanceGroup(groupId))
            {
                return false;
            }

            if (!catalog.TryCreateSessionCharacterComposition(
                    groupId,
                    sourceOptionIds,
                    out resolvedOptionId))
            {
                resolvedOptionId = SkinCatalog.BaseOptionId;
            }

            return TryBuildSessionCharacterSelection(
                groupId,
                resolvedOptionId,
                out selectionOverrides);
        }
    }

    internal static void ClearSessionCharacterCompositions()
    {
        lock (Sync)
        {
            if (Catalog == null)
            {
                return;
            }

            foreach (var groupId in Catalog.ClearSessionCharacterCompositions())
            {
                ClearRuntimeResourceCache(groupId);
            }
        }
    }

    public static IReadOnlyList<SkinOption> GetCharacterSkinOptions(string groupId)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            var group = catalog?.Groups.FirstOrDefault(candidate => candidate.Id.Equals(
                groupId,
                StringComparison.OrdinalIgnoreCase));
            if (catalog == null || group == null || !catalog.IsCharacterAppearanceGroup(groupId))
            {
                return [];
            }

            var visibleRawIds = CharacterSkinCompositionPolicy.VisibleRawOptionIds(
                    groupId,
                    catalog.GetRawCharacterOptions(groupId).Select(option => option.Id),
                    Config.CharacterSkinCompositions)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return group.Options.Where(option =>
                    option.IsComposition
                        ? !option.IsSessionComposition
                        : visibleRawIds.Contains(option.Id))
                .ToArray();
        }
    }

    public static IReadOnlyList<SkinOption> GetRawCharacterSkinOptions(string groupId)
    {
        lock (Sync)
        {
            return Catalog?.GetRawCharacterOptions(groupId) ?? [];
        }
    }

    public static IReadOnlyList<CharacterSkinComposition> GetCharacterSkinCompositions(
        string groupId)
    {
        lock (Sync)
        {
            return Config.CharacterSkinCompositions
                .Where(composition => composition.GroupId.Equals(
                    groupId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(CloneCharacterSkinComposition)
                .ToArray();
        }
    }

    public static IReadOnlyList<string> GetCharacterSelectionSourceIds(
        string groupId,
        string optionId)
    {
        lock (Sync)
        {
            return Catalog?.GetCompositionSourceOptionIds(groupId, optionId) ?? [];
        }
    }

    public static IReadOnlyList<string> GetAvailableCharacterSelectionSourceIds(
        string groupId,
        IReadOnlyList<string> sourceOptionIds)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            if (catalog == null || !catalog.IsCharacterAppearanceGroup(groupId))
            {
                return [];
            }

            return CharacterSkinCompositionPolicy.ResolveAvailableSourceIds(
                sourceOptionIds,
                catalog.GetRawCharacterOptions(groupId).Select(option => option.Id));
        }
    }

    public static bool SaveCharacterSkinComposition(
        string groupId,
        string? compositionId,
        string? name,
        IReadOnlyList<string> sourceOptionIds,
        bool hideSources,
        out string savedId)
    {
        lock (Sync)
        {
            savedId = string.Empty;
            var catalog = Catalog;
            var rawOptions = catalog?.GetRawCharacterOptions(groupId) ?? [];
            if (catalog == null || rawOptions.Count == 0 ||
                !catalog.IsCharacterAppearanceGroup(groupId))
            {
                LastError = $"找不到角色皮肤分组：{groupId}";
                return false;
            }

            var existing = string.IsNullOrWhiteSpace(compositionId)
                ? null
                : Config.CharacterSkinCompositions.FirstOrDefault(composition =>
                    composition.Id.Equals(compositionId, StringComparison.OrdinalIgnoreCase) &&
                    composition.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(compositionId) && existing == null)
            {
                LastError = $"找不到合并皮肤：{compositionId}";
                return false;
            }

            var knownRawIds = rawOptions.Select(option => option.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var retainedMissingIds = existing?.SourceOptionIds
                .Where(optionId => !knownRawIds.Contains(optionId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ??
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalizedSources = sourceOptionIds
                .Where(optionId => !string.IsNullOrWhiteSpace(optionId))
                .Select(optionId => optionId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedSources.Count == 0 || normalizedSources.Any(optionId =>
                    !knownRawIds.Contains(optionId) && !retainedMissingIds.Contains(optionId)))
            {
                LastError = "合并皮肤至少需要一个当前角色的原始皮肤来源。";
                return false;
            }

            var previousCompositions = Config.CharacterSkinCompositions
                .Select(CloneCharacterSkinComposition)
                .ToList();
            var previousSelections = new Dictionary<string, string>(
                Config.Selections,
                StringComparer.OrdinalIgnoreCase);
            var previousProviderPriority = Config.VisualProviderPriority.ToList();
            var id = existing?.Id ?? CharacterSkinCompositionPolicy.CreateId();
            var uniqueName = CharacterSkinCompositionPolicy.UniqueName(
                name,
                Config.CharacterSkinCompositions
                    .Where(composition =>
                        composition.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase) &&
                        !composition.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    .Select(composition => composition.Name),
                ModLocalization.Get(ModText.CombinedSkinDefaultName));
            var updated = new CharacterSkinComposition
            {
                Id = id,
                GroupId = groupId,
                Name = uniqueName,
                SourceOptionIds = normalizedSources,
                HideSources = hideSources
            };
            var wasSelected = existing != null && Config.GetSelection(groupId).Equals(
                existing.Id,
                StringComparison.OrdinalIgnoreCase);
            var shouldApply = CharacterSkinCompositionPolicy.ShouldApplyAfterSave(
                existing == null,
                wasSelected);
            var exitUpdates = wasSelected
                ? catalog.BuildVisualSelectionTransaction(
                    groupId,
                    SkinCatalog.BaseOptionId,
                    Config.Selections)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var affectedGroups = exitUpdates.Keys
                .Append(groupId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (existing == null)
                {
                    Config.CharacterSkinCompositions.Add(updated);
                }
                else
                {
                    var index = Config.CharacterSkinCompositions.IndexOf(existing);
                    Config.CharacterSkinCompositions[index] = updated;
                }

                catalog.SynchronizeCharacterSkinCompositions(Config.CharacterSkinCompositions);
                if (catalog.Groups.First(group => group.Id.Equals(
                        groupId,
                        StringComparison.OrdinalIgnoreCase)).Options.All(option =>
                        !option.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("合并皮肤没有当前可用的来源，无法应用。");
                }

                if (!shouldApply)
                {
                    Config.Save(ConfigPath);
                    savedId = id;
                    LastError = null;
                    return true;
                }

                var transitionSelections = new Dictionary<string, string>(
                    Config.Selections,
                    StringComparer.OrdinalIgnoreCase);
                foreach (var exitUpdate in exitUpdates)
                {
                    transitionSelections[exitUpdate.Key] = exitUpdate.Value;
                }
                var enterUpdates = catalog.BuildVisualSelectionTransaction(
                    groupId,
                    id,
                    transitionSelections);
                var updates = CharacterSkinCompositionPolicy.MergeSelectionUpdates(
                    [exitUpdates, enterUpdates]);
                affectedGroups.UnionWith(updates.Keys);
                foreach (var update in updates)
                {
                    Config.Selections[update.Key] = update.Value;
                    ClearRuntimeResourceCache(update.Key);
                }

                UpdateVisualProviderPriority(groupId, id);
                MountOverlay(affectedGroups);
                Config.Save(ConfigPath);
                savedId = id;
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                Config.CharacterSkinCompositions = previousCompositions;
                Config.Selections = previousSelections;
                Config.VisualProviderPriority = previousProviderPriority;
                catalog.SynchronizeCharacterSkinCompositions(Config.CharacterSkinCompositions);
                foreach (var affectedGroup in affectedGroups)
                {
                    ClearRuntimeResourceCache(affectedGroup);
                }
                TryRestoreOverlay(affectedGroups, cardOverlay: false);
                LastError = exception.GetBaseException().Message;
                ModLog.Error($"保存 {groupId} 的合并皮肤失败：{exception}");
                return false;
            }
        }
    }

    public static bool DeleteCharacterSkinComposition(string groupId, string compositionId)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            var existing = Config.CharacterSkinCompositions.FirstOrDefault(composition =>
                composition.Id.Equals(compositionId, StringComparison.OrdinalIgnoreCase) &&
                composition.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (catalog == null || existing == null)
            {
                LastError = $"找不到合并皮肤：{compositionId}";
                return false;
            }

            var previousCompositions = Config.CharacterSkinCompositions
                .Select(CloneCharacterSkinComposition)
                .ToList();
            var previousSelections = new Dictionary<string, string>(
                Config.Selections,
                StringComparer.OrdinalIgnoreCase);
            var previousProviderPriority = Config.VisualProviderPriority.ToList();
            var updates = Config.GetSelection(groupId).Equals(
                    compositionId,
                    StringComparison.OrdinalIgnoreCase)
                ? catalog.BuildVisualSelectionTransaction(
                    groupId,
                    SkinCatalog.BaseOptionId,
                    Config.Selections)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var affectedGroups = updates.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var update in updates)
                {
                    Config.Selections[update.Key] = update.Value;
                    ClearRuntimeResourceCache(update.Key);
                }
                Config.CharacterSkinCompositions.Remove(existing);
                catalog.SynchronizeCharacterSkinCompositions(Config.CharacterSkinCompositions);
                SanitizeVisualProviderPriority();
                if (affectedGroups.Count > 0)
                {
                    MountOverlay(affectedGroups);
                }
                Config.Save(ConfigPath);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                Config.CharacterSkinCompositions = previousCompositions;
                Config.Selections = previousSelections;
                Config.VisualProviderPriority = previousProviderPriority;
                catalog.SynchronizeCharacterSkinCompositions(Config.CharacterSkinCompositions);
                foreach (var affectedGroup in affectedGroups)
                {
                    ClearRuntimeResourceCache(affectedGroup);
                }
                TryRestoreOverlay(affectedGroups, cardOverlay: false);
                LastError = exception.GetBaseException().Message;
                ModLog.Error($"删除 {groupId}/{compositionId} 失败：{exception}");
                return false;
            }
        }
    }

    public static bool ApplyCharacterPreviewSelection(string groupId, string optionId)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            var group = catalog?.Groups.FirstOrDefault(candidate =>
                candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (catalog == null || group == null || !catalog.IsCharacterAppearanceGroup(group.Id) ||
                (!optionId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) &&
                 group.Options.All(option =>
                     !option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))))
            {
                LastError = $"未知的角色皮肤预览：{groupId}/{optionId}";
                return false;
            }

            var nextPreview = catalog.BuildVisualSelectionTransaction(
                group.Id,
                optionId,
                Config.Selections);
            var previousPreview = new Dictionary<string, string>(
                CharacterPreviewSelections,
                StringComparer.OrdinalIgnoreCase);
            var affectedGroups = VisualSelectionOverlayPolicy.AffectedGroups(
                previousPreview.Keys,
                nextPreview.Keys);
            try
            {
                CharacterPreviewSelections.Clear();
                foreach (var pair in nextPreview)
                {
                    CharacterPreviewSelections[pair.Key] = pair.Value;
                }

                MountOverlay(affectedGroups);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                CharacterPreviewSelections.Clear();
                foreach (var pair in previousPreview)
                {
                    CharacterPreviewSelections[pair.Key] = pair.Value;
                }

                TryRestoreOverlay(affectedGroups, cardOverlay: false);
                LastError = exception.Message;
                ModLog.Error($"预览 {groupId}/{optionId} 失败：{exception}");
                return false;
            }
        }
    }

    public static bool ClearCharacterPreviewSelection(bool restoreOverlay)
    {
        lock (Sync)
        {
            if (CharacterPreviewSelections.Count == 0)
            {
                return false;
            }

            var affectedGroups = CharacterPreviewSelections.Keys
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            CharacterPreviewSelections.Clear();
            if (restoreOverlay)
            {
                TryRestoreOverlay(affectedGroups, cardOverlay: false);
            }
            return true;
        }
    }

    internal static bool TryRegisterOnlineSessionProvider(
        string optionId,
        string optionName,
        string pckPath,
        string groupId,
        IReadOnlyDictionary<string, VisualResourceBinding> resourceBindings,
        out string error)
    {
        lock (Sync)
        {
            if (Catalog == null)
            {
                error = "皮肤目录尚未初始化。";
                return false;
            }

            return Catalog.TryAddSessionVisualProvider(
                optionId,
                optionName,
                pckPath,
                groupId,
                resourceBindings,
                out error);
        }
    }

    internal static bool TryReadBaseGameResource(string resourcePath, out byte[] bytes)
    {
        lock (Sync)
        {
            if (Catalog == null || !Catalog.IsBaseGameResource(resourcePath))
            {
                bytes = [];
                return false;
            }

            return Catalog.TryReadBaseGameResource(resourcePath, out bytes);
        }
    }

    internal static void RemoveOnlineSessionProvider(string optionId)
    {
        lock (Sync)
        {
            if (Catalog == null)
            {
                return;
            }

            var affectedGroups = Catalog.Groups
                .Where(group => group.Options.Any(option => option.Id.Equals(
                    optionId,
                    StringComparison.OrdinalIgnoreCase)))
                .Select(group => group.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (affectedGroups.Count > 0)
            {
                MountOverlay(affectedGroups);
            }

            Catalog.RemoveSessionVisualProvider(optionId);
            var cacheMarker = "\n" + optionId + "\n";
            foreach (var key in RuntimeResourceCache.Keys.Where(key =>
                         key.Contains(cacheMarker, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                RuntimeResourceCache.Remove(key);
            }
            foreach (var key in PreparedRuntimeOverlays.Keys.Where(key =>
                         key.Contains(cacheMarker, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                RuntimeResourceBundles.Remove(key);
                var overlayPath = PreparedRuntimeOverlays[key].OverlayPath;
                PreparedRuntimeOverlays.Remove(key);
                if (overlayPath != null)
                {
                    try
                    {
                        File.Delete(overlayPath);
                    }
                    catch (Exception exception)
                    {
                        ModLog.Info(
                            "联机皮肤运行缓存仍被 Godot 占用，将在下次启动时清理：" +
                            exception.GetBaseException().Message);
                    }
                }
            }
        }
    }

    internal static bool IsBaseGameResource(string resourcePath)
    {
        lock (Sync)
        {
            return Catalog?.IsBaseGameResource(resourcePath) == true;
        }
    }

    public static void RefreshSessionRuntimeProviders()
    {
        lock (Sync)
        {
            if (Catalog != null)
            {
                MountOverlay(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }
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
            ModLog.Info("已保存旧版“不再提示”设置；当前版本会自动调整加载顺序，不再用该设置跳过提示。");
        }
    }

    public static bool ShouldLoadOtherPlayersCustomSkins()
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            return Config.LoadOtherPlayersCustomSkins;
        }
    }

    public static bool ShouldSynchronizeMultiplayerSkins()
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            return Config.MultiplayerSkinSyncEnabled;
        }
    }

    public static void SetMultiplayerSkinSyncEnabled(bool enabled)
    {
        bool changed;
        lock (Sync)
        {
            EnsureConfigLoaded();
            changed = Config.MultiplayerSkinSyncEnabled != enabled;
            if (changed)
            {
                Config.MultiplayerSkinSyncEnabled = enabled;
                Config.Save(ConfigPath);
            }
        }

        if (changed)
        {
            MultiplayerSkinSync.OnSynchronizationPreferenceChanged(enabled);
        }
    }

    public static void SetLoadOtherPlayersCustomSkins(bool enabled)
    {
        bool changed;
        lock (Sync)
        {
            EnsureConfigLoaded();
            changed = Config.LoadOtherPlayersCustomSkins != enabled;
            if (changed)
            {
                Config.LoadOtherPlayersCustomSkins = enabled;
                Config.Save(ConfigPath);
            }
        }

        if (changed)
        {
            MultiplayerSkinSync.OnRemoteSkinLoadingPreferenceChanged(enabled);
        }
    }

    public static bool ShouldShowInRunAppearanceEntry()
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            return Config.ShowInRunAppearanceEntry;
        }
    }

    public static void SetShowInRunAppearanceEntry(bool enabled)
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            if (Config.ShowInRunAppearanceEntry == enabled)
            {
                return;
            }

            Config.ShowInRunAppearanceEntry = enabled;
            Config.Save(ConfigPath);
        }
    }

    public static bool ShouldPlaceCharacterSelectorTopRight()
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            return Config.CharacterSelectorTopRight;
        }
    }

    public static void SetCharacterSelectorTopRight(bool enabled)
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            if (Config.CharacterSelectorTopRight == enabled)
            {
                return;
            }

            Config.CharacterSelectorTopRight = enabled;
            Config.Save(ConfigPath);
        }
    }

    public static bool TryGetIndividualCardSkinSelectorPosition(out float x, out float y)
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            if (Config.IndividualCardSkinSelectorX is { } storedX &&
                Config.IndividualCardSkinSelectorY is { } storedY &&
                float.IsFinite(storedX) &&
                float.IsFinite(storedY))
            {
                x = Math.Clamp(storedX, 0f, 1f);
                y = Math.Clamp(storedY, 0f, 1f);
                return true;
            }

            x = 0f;
            y = 0f;
            return false;
        }
    }

    public static void SetIndividualCardSkinSelectorPosition(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            return;
        }

        lock (Sync)
        {
            EnsureConfigLoaded();
            Config.IndividualCardSkinSelectorX = Math.Clamp(x, 0f, 1f);
            Config.IndividualCardSkinSelectorY = Math.Clamp(y, 0f, 1f);
            Config.Save(ConfigPath);
        }
    }

    public static void ResetIndividualCardSkinSelectorPosition()
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            if (Config.IndividualCardSkinSelectorX == null &&
                Config.IndividualCardSkinSelectorY == null)
            {
                return;
            }

            Config.IndividualCardSkinSelectorX = null;
            Config.IndividualCardSkinSelectorY = null;
            Config.Save(ConfigPath);
        }
    }

    public static bool ShouldShowLoadOrderWarning(bool isBeforeAllSkinMods)
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            var previousSafeState = Config.LastKnownBeforeAllSkinMods ??
                                    Config.LastKnownFirstInLoadOrder;
            var movedBehindSkinMod = previousSafeState == true && !isBeforeAllSkinMods;
            var stateChanged = Config.LastKnownBeforeAllSkinMods != isBeforeAllSkinMods;
            if (movedBehindSkinMod)
            {
                Config.SuppressLoadOrderWarning = false;
                ModLog.Info("检测到有皮肤 Mod 被移到本 Mod 之前，已恢复加载顺序提醒。");
            }

            Config.LastKnownBeforeAllSkinMods = isBeforeAllSkinMods;
            if (stateChanged || movedBehindSkinMod)
            {
                Config.Save(ConfigPath);
            }

            var shouldShow = !isBeforeAllSkinMods;
            if (isBeforeAllSkinMods)
            {
                _loadOrderAutoReorderLogged = false;
            }
            else if (!_loadOrderAutoReorderLogged)
            {
                var legacySuppression = Config.SuppressLoadOrderWarning;
                ModLog.Info(legacySuppression
                    ? "检测到皮肤 Mod 排在本 Mod 之前；旧版“不再提示”设置不再生效，将自动调整本 Mod 到所有皮肤 Mod 之前。"
                    : "检测到皮肤 Mod 排在本 Mod 之前，将自动调整本 Mod 到所有皮肤 Mod 之前。");
                _loadOrderAutoReorderLogged = true;
            }

            return shouldShow;
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
                PreparedRuntimeOverlays.Clear();
                RuntimeResourceBundles.Clear();
                CardPortraitCache.Clear();
                ExternalCardProviderIdentityPaths.Clear();
                BaselineRelicAtlasCache.Clear();
                BaselineRelicIconCache.Clear();
                FailedCardPortraitRequests.Clear();
                IsolatedCardOverlayCache.Clear();
                CardCanonicalResourceOwners.Reset();
                CharacterPreviewSelections.Clear();
                _cardLookupCache = new ConditionalWeakTable<CardModel, CardLookup>();
                AncientStyleMethods.Clear();
                MissingAncientStyleMethods.Clear();
                FailedAncientStyleMethods.Clear();
                MountedOverlayCache.Clear();
                MountedScopedRuntimeProviderPacks.Clear();
                MountedLargeRuntimeProviderPacks.Clear();
                RuntimeCanonicalDependencyPaths.Clear();
                WarmedRuntimeProviderPacks.Clear();
                WarmingRuntimeProviderPacks.Clear();
                _runtimePackWarmGeneration++;
                ScopedMonsterSelections.Replace(
                    new Dictionary<string, IReadOnlyCollection<string>>(
                        StringComparer.OrdinalIgnoreCase));
                _runtimeProviderBehaviorScope = null;
                RuntimeProviderScopeLeases.Reset();
                MountedLocalizationFiles.Clear();
                LocalizationStateCache.Clear();
                _mountedLocalizationSignature = null;
                CleanupOldOverlays();
                CleanupPreparedRuntimeOverlayCache();
                // Protocol 9 no longer downloads multiplayer skins. Keep one startup sweep so
                // files left by older releases are removed instead of becoming permanent disk
                // usage after the feature is retired.
                OnlineSkinCache.CleanupStaleSessionsAtStartup();
                var gamePckPath = GamePackLocator.Resolve(OS.GetExecutablePath());
                ModLog.Info($"已定位游戏主资源包：{gamePckPath}");
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
                        ManagedSkinModLoader.ShouldTreatAsGameplayBaseline(mod, loadedMods),
                        mod.path,
                        mod.manifest.hasDll))
                    .ToArray();

                Catalog = SkinCatalog.Build(gamePckPath, mods);
                ManagedCharacterAssetRegistrationGuard.Configure(Catalog);
                Config = LoadConfig();
                _configLoaded = true;
                SanitizeSelections();
                MountOverlay(Catalog.Groups.Select(group => group.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
                Config.Save(ConfigPath);
                // 仅在完整成功后才标记已初始化，失败时允许后续调用重试而不是整个会话失效。
                _initialized = true;
                ModLog.Info(
                    $"发现 {Catalog.Groups.Count} 个生物外观组和 {Catalog.CardGroups.Count} 个卡牌外观组。" +
                    "角色、怪物、先古之民与卡牌选项已接入对应界面。");
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
            if (Catalog == null || _cardGroupsInitialized)
            {
                return;
            }

            try
            {
                FrameworkCompatibilityLayer.SynchronizeSelections(
                    Catalog,
                    GetVisualSelections());
                InitializeMonsterSkinCategoriesAfterModels();
                var cards = ModelDb.AllCards.ToArray();
                var entries = cards.Select(card => new CardCatalogEntry(
                        card.GetType().Name,
                        card.PortraitPath,
                        GetCardPoolGroupId(card),
                        GetCardCatalogGroupId(card),
                        GetCardFilterGroupId(card)))
                    .ToArray();

                Catalog.FinalizeCardGroups(entries);
                _cardLookupCache = new ConditionalWeakTable<CardModel, CardLookup>();
                CardCoverageCache.Clear();
                MigrateStoredCardSelections();
                SanitizeCardSelections();
                MigrateLegacyCardSkinPresets();
                SanitizeCardSkinPresets();
                MountCardOverlay(Catalog.CardGroups
                    .Select(group => group.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
                Config.Save(ConfigPath);
                _cardGroupsInitialized = true;
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

    private static void InitializeMonsterSkinCategoriesAfterModels()
    {
        var catalog = Catalog!;
        var registered = 0;
        foreach (var act in ModelDb.Acts)
        {
            var groupIds = ResolveMonsterSkinGroupIds(catalog, act.AllMonsters);
            if (RegisterMonsterSkinCategory(
                    "act:" + act.Id.Entry.ToLowerInvariant(),
                    groupIds))
            {
                registered++;
            }
        }

        var eventEncounters = typeof(ModelDb).GetProperty("EventEncounters")?.GetValue(null)
            as IEnumerable<EncounterModel>;
        if (eventEncounters != null && RegisterMonsterSkinCategory(
                "events",
                ResolveMonsterSkinGroupIds(
                    catalog,
                    eventEncounters.SelectMany(encounter => encounter.AllPossibleMonsters))))
        {
            registered++;
        }

        if (registered > 0)
        {
            ModLog.Info($"已按怪物图鉴登记 {registered} 个地区的怪物皮肤优先级。");
        }
    }

    private static IReadOnlyList<string> ResolveMonsterSkinGroupIds(
        SkinCatalog catalog,
        IEnumerable<MonsterModel> monsters) =>
        monsters
            .Select(monster => catalog.ResolveManagedMonsterGroupId(monster.Id.Entry) ??
                               catalog.ResolveManagedMonsterGroupId(monster.GetType().Name))
            .Where(groupId => !string.IsNullOrWhiteSpace(groupId))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Builds the selected character-preview packs while the game's own startup loading screen is
    /// still active. No Godot resources are decoded here; the expensive PCK graph walk and write
    /// are simply moved out of the first character click and the result is reused for the session.
    /// </summary>
    public static void PrepareSelectedCharacterPreviews()
    {
        lock (Sync)
        {
            var catalog = Catalog;
            if (catalog == null)
            {
                return;
            }

            var started = Stopwatch.GetTimestamp();
            var prepared = 0;
            var preparedRelicBundles = 0;
            foreach (var character in ModelDb.AllCharacters)
            {
                var groupId = character.Id.Entry.ToLowerInvariant();
                var group = catalog.Groups.FirstOrDefault(candidate =>
                    candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
                if (group == null)
                {
                    continue;
                }

                var selection = Config.GetSelection(group.Id);
                if (selection.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) ||
                    (catalog.IsRuntimeProviderOption(group.Id, selection) &&
                     !catalog.IsResourceBackedOption(group.Id, selection) &&
                     catalog.GetRuntimeImagePath(group.Id, selection) != null))
                {
                    continue;
                }

                var paths = CharacterSelectResourcePaths(groupId);
                _ = GetOrPrepareRuntimeOverlay(
                    catalog,
                    group.Id,
                    selection,
                    paths,
                    includeProviderDependencies: true);
                prepared++;

                var selected = group.Options.FirstOrDefault(option => option.Id.Equals(
                    selection,
                    StringComparison.OrdinalIgnoreCase));
                if (selected != null)
                {
                    var relicPaths = catalog.GetProviderRelicSpritePaths(selected);
                    if (relicPaths.Count > 0)
                    {
                        _ = GetOrPrepareRuntimeOverlay(
                            catalog,
                            group.Id,
                            selection,
                            relicPaths,
                            includeProviderDependencies: false,
                            isolateRelicCanonicalPaths: true);
                        preparedRelicBundles++;
                    }
                }
            }

            if (prepared > 0)
            {
                ModLog.Info(
                    $"已在启动阶段准备 {prepared} 个角色的选角资源包，" +
                    $"其中 {preparedRelicBundles} 套包含完整遗物图集；" +
                    $"耗时={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms。");
            }
        }
    }

    public static void FocusRuntimeProviderBehaviorsOnCharacters(IEnumerable<string> groupIds)
        => FocusRuntimeProviderBehaviorsOnGroups(
            groupIds,
            runEnvironmentProviderIds: [],
            reason: "角色预览");

    public static long FocusRuntimeProviderBehaviorsOnGroups(
        IEnumerable<string> groupIds,
        IReadOnlyCollection<string> runEnvironmentProviderIds,
        string reason)
    {
        lock (Sync)
        {
            var nextScope = groupIds
                .Where(groupId => !string.IsNullOrWhiteSpace(groupId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nextRunEnvironmentProviders = runEnvironmentProviderIds
                .Where(providerId => !string.IsNullOrWhiteSpace(providerId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_runtimeProviderBehaviorScope is { } currentScope &&
                currentScope.RunEnvironmentProviderIds.ToHashSet(
                    StringComparer.OrdinalIgnoreCase).SetEquals(nextRunEnvironmentProviders) &&
                currentScope.VisibleGroupIds.ToHashSet(
                    StringComparer.OrdinalIgnoreCase).SetEquals(nextScope))
            {
                return RuntimeProviderScopeLeases.Current;
            }

            _runtimeProviderBehaviorScope = new RuntimeProviderScope(
                nextScope,
                nextRunEnvironmentProviders);
            var scopeLease = RuntimeProviderScopeLeases.Claim();
            var catalog = Catalog;
            if (catalog == null)
            {
                return scopeLease;
            }

            var activeProviders = GetActiveRuntimeProviders(catalog);
            ManagedSkinModLoader.ConfigureRunEnvironmentProviders(nextRunEnvironmentProviders);
            ManagedSkinModLoader.DeactivateProvidersExcept(activeProviders);
            EnsureScopedRuntimeProviderResourcesMounted(catalog, activeProviders);
            foreach (var providerId in activeProviders.Where(
                         catalog.ProviderUsesManagedGodotScripts))
            {
                ManagedSkinModLoader.EnsureProviderGodotScripts(providerId);
            }
            foreach (var providerId in activeProviders.Where(
                         catalog.ProviderUsesScopedMonsterRuntime))
            {
                ManagedSkinModLoader.EnsureScopedMonsterSelectionRouter(providerId);
            }
            ManagedSkinModLoader.ActivateSelectedProviders(activeProviders);
            // Provider initializers can write character paths into a framework-owned registry.
            // Suppress and clear those writes after every activation so deselected providers
            // cannot keep controlling combat, merchant, rest-site or UI scenes indirectly.
            ManagedCharacterAssetRegistrationGuard.SuppressCurrentRegistrations();
            ScheduleRuntimeProviderPackWarm(catalog, activeProviders, reason);
            ModLog.Info(
                $"已按{reason}将皮肤代码行为收窄到 {nextScope.Count} 个可见外观组；" +
                $"保留 {activeProviders.Count} 个当前场景需要的 DLL 皮肤提供者。");
            return scopeLease;
        }
    }

    public static bool TryFocusRuntimeProviderBehaviorsOnGroups(
        long expectedScopeLease,
        IEnumerable<string> groupIds,
        IReadOnlyCollection<string> runEnvironmentProviderIds,
        string reason,
        out long scopeLease)
    {
        lock (Sync)
        {
            if (!RuntimeProviderScopeLeases.IsCurrent(expectedScopeLease))
            {
                scopeLease = RuntimeProviderScopeLeases.Current;
                return false;
            }

            scopeLease = FocusRuntimeProviderBehaviorsOnGroups(
                groupIds,
                runEnvironmentProviderIds,
                reason);
            return true;
        }
    }

    public static IReadOnlyList<string> GetSelectionResourcePackPaths(
        string groupId,
        string optionId)
    {
        lock (Sync)
        {
            var group = Catalog?.Groups.FirstOrDefault(candidate =>
                candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            var option = group?.Options.FirstOrDefault(candidate =>
                candidate.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase));
            if (option == null || !option.IsRuntimeProvider)
            {
                return [];
            }

            return Catalog!.GetProviderResourcePackPaths(option.EffectiveProviderId)
                .Select(System.IO.Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .ToArray();
        }
    }

    public static async Task WarmResourcePackFilesAsync(
        IReadOnlyCollection<string> resourcePackPaths,
        Action<double>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        var paths = resourcePackPaths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var totalBytes = paths.Sum(path => new FileInfo(path).Length);
        if (totalBytes <= 0)
        {
            reportProgress?.Invoke(1d);
            return;
        }

        var completedBytes = 0L;
        var buffer = GC.AllocateUninitializedArray<byte>(1024 * 1024);
        foreach (var path in paths)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                System.IO.FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                completedBytes += read;
                reportProgress?.Invoke(Math.Clamp((double)completedBytes / totalBytes, 0d, 1d));
            }
        }

        reportProgress?.Invoke(1d);
    }

    private static void ScheduleRuntimeProviderPackWarm(
        SkinCatalog catalog,
        IEnumerable<string> activeProviders,
        string reason)
    {
        var generation = ++_runtimePackWarmGeneration;
        var paths = new List<string>();
        foreach (var path in activeProviders
                     .SelectMany(catalog.GetProviderResourcePackPaths)
                     .Select(System.IO.Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var alreadyWarmed = WarmedRuntimeProviderPacks.Contains(path) ||
                                    WarmingRuntimeProviderPacks.Contains(path);
                if (!File.Exists(path) ||
                    !RuntimePackWarmPolicy.ShouldWarm(
                        new FileInfo(path).Length,
                        alreadyWarmed))
                {
                    continue;
                }

                WarmingRuntimeProviderPacks.Add(path);
                paths.Add(path);
            }
            catch (Exception exception)
            {
                ModLog.Info(
                    $"跳过无法检查的当前皮肤资源包 {System.IO.Path.GetFileName(path)}：" +
                    exception.GetBaseException().Message);
            }
        }

        if (paths.Count > 0)
        {
            _ = WarmRuntimeProviderPacksAsync(paths, reason, generation);
        }
    }

    private static async Task WarmRuntimeProviderPacksAsync(
        IReadOnlyCollection<string> paths,
        string reason,
        int generation)
    {
        var started = Stopwatch.GetTimestamp();
        await RuntimePackWarmGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (Sync)
            {
                if (generation != _runtimePackWarmGeneration)
                {
                    foreach (var path in paths)
                    {
                        WarmingRuntimeProviderPacks.Remove(path);
                    }
                    return;
                }
            }

            await WarmResourcePackFilesAsync(paths).ConfigureAwait(false);
            lock (Sync)
            {
                foreach (var path in paths)
                {
                    WarmingRuntimeProviderPacks.Remove(path);
                    WarmedRuntimeProviderPacks.Add(path);
                }
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed >= TimeSpan.FromMilliseconds(50))
            {
                ModLog.Info(
                    $"已后台预读{reason}所需的 {paths.Count} 个当前皮肤资源包；" +
                    $"耗时={elapsed.TotalMilliseconds:F1} ms。");
            }
        }
        catch (Exception exception)
        {
            lock (Sync)
            {
                foreach (var path in paths)
                {
                    WarmingRuntimeProviderPacks.Remove(path);
                }
            }

            ModLog.Info(
                $"后台预读{reason}皮肤资源未完成，将在实际加载时继续：" +
                exception.GetBaseException().Message);
        }
        finally
        {
            RuntimePackWarmGate.Release();
        }
    }

    public static bool ApplySelection(string groupId, string optionId)
    {
        // The category entry is an instruction, not a resource provider. Keep this in the
        // shared selection path so queued in-run choices retain their follow-category state.
        if (optionId.Equals(InheritMonsterSelectionId, StringComparison.OrdinalIgnoreCase))
        {
            return FollowMonsterCategoryPriority(groupId);
        }

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
                 group.Options.All(option =>
                     !option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))))
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
            var previousVisualProviderPriority = Config.VisualProviderPriority.ToList();
            var previousFollowingGroups = Config.MonsterGroupsFollowingCategory.ToList();
            var previousManualGroups = Config.MonsterGroupsWithManualSelection.ToList();
            var affectedGroups = updates.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                Config.MonsterGroupsFollowingCategory.RemoveAll(candidate =>
                    affectedGroups.Contains(candidate));
                foreach (var affectedGroup in affectedGroups.Where(affectedGroup =>
                             Config.MonsterSkinCategoryGroups.Values.Any(groupIds =>
                                 groupIds.Contains(affectedGroup, StringComparer.OrdinalIgnoreCase)) &&
                             !Config.MonsterGroupsWithManualSelection.Contains(
                                 affectedGroup,
                                 StringComparer.OrdinalIgnoreCase)))
                {
                    Config.MonsterGroupsWithManualSelection.Add(affectedGroup);
                }

                foreach (var update in updates)
                {
                    Config.Selections[update.Key] = update.Value;
                    ClearRuntimeResourceCache(update.Key);
                }

                UpdateVisualProviderPriority(groupId, optionId);
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
                Config.VisualProviderPriority = previousVisualProviderPriority;
                Config.MonsterGroupsFollowingCategory = previousFollowingGroups;
                Config.MonsterGroupsWithManualSelection = previousManualGroups;

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
                 group.Options.All(option =>
                     !option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))))
            {
                LastError = $"未知的卡牌皮肤选择：{groupId}/{optionId}";
                return false;
            }

            var entries = GetCardPriorityEntriesInternal(group)
                .Select(entry => entry with
                {
                    Enabled = !optionId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) &&
                              entry.OptionId.Equals(optionId, StringComparison.OrdinalIgnoreCase)
                })
                .OrderByDescending(entry => entry.Enabled)
                .ToArray();
            return ApplyCardPriority(groupId, entries);
        }
    }

    // 以下读取不持锁：所有写操作都发生在 Godot 主线程，与 UI 读取同线程；
    // LastError 用 volatile 保证异常路径下的可见性。
    public static string GetCardSelection(string groupId) =>
        Config.GetSelection(CardSelectionKey(groupId));

    public static IReadOnlyList<CardPriorityOptionState> GetCardPriorityOptions(string groupId)
    {
        lock (Sync)
        {
            var group = Catalog?.CardGroups.FirstOrDefault(candidate =>
                candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                return [];
            }

            var entries = GetCardPriorityEntriesInternal(group);
            var coverage = GetCardCoverage(group);
            return entries.Select((entry, colorIndex) =>
                {
                    var option = group.Options.First(candidate =>
                        candidate.Id.Equals(entry.OptionId, StringComparison.OrdinalIgnoreCase));
                    return new CardPriorityOptionState(
                        option.Id,
                        option.Name,
                        entry.Enabled,
                        colorIndex,
                        coverage.ByOption.GetValueOrDefault(entry.OptionId),
                        coverage.TotalCards);
                })
                .ToArray();
        }
    }

    public static IReadOnlyList<CardSkinSourceState> GetCardSkinSources(CardModel card)
    {
        lock (Sync)
        {
            var lookup = GetCardLookup(card);
            if (lookup.Group == null)
            {
                return [];
            }

            var current = GetEffectiveCardSelection(card, lookup);
            return GetCardPriorityEntriesInternal(lookup.Group)
                .Select((entry, colorIndex) => (Entry: entry, ColorIndex: colorIndex))
                .Where(pair =>
                    lookup.OptionsById.ContainsKey(pair.Entry.OptionId))
                .Select(pair =>
                {
                    var option = lookup.OptionsById[pair.Entry.OptionId].Option;
                    return new CardSkinSourceState(
                        option.Id,
                        option.Name,
                        pair.Entry.Enabled,
                        pair.ColorIndex,
                        option.Id.Equals(current, StringComparison.OrdinalIgnoreCase));
                })
                .ToArray();
        }
    }

    public static int GetCardSkinSourceCount(CardModel card) =>
        GetCardSkinSources(card).Count;

    public static IReadOnlyList<CardSkinPresetState> GetCardSkinPresets(string groupId)
    {
        lock (Sync)
        {
            return Config.CardSkinPresets
                .Where(preset => preset.CategoryId?.Equals(
                    groupId,
                    StringComparison.OrdinalIgnoreCase) == true)
                .Select(preset => new CardSkinPresetState(
                    preset.Name,
                    preset.Name.Equals(
                        GetActiveCardSkinPreset(groupId),
                        StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
    }

    public static bool CreateCardSkinPreset(string groupId, string name)
    {
        lock (Sync)
        {
            if (!IsKnownCardGroup(groupId))
            {
                LastError = $"未知的卡牌皮肤分类：{groupId}";
                return false;
            }

            var normalizedName = NormalizeCardSkinPresetName(name);
            if (normalizedName == null)
            {
                return false;
            }

            if (FindCardSkinPresetIndex(groupId, normalizedName) >= 0)
            {
                LastError = $"卡图预设已存在：{normalizedName}";
                return false;
            }

            var previousActivePreset = GetActiveCardSkinPreset(groupId);
            var preset = CaptureCurrentCardSkinPreset(groupId, normalizedName);
            try
            {
                Config.CardSkinPresets.Add(preset);
                SetActiveCardSkinPreset(groupId, preset.Name);
                Config.Save(ConfigPath);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                Config.CardSkinPresets.Remove(preset);
                SetActiveCardSkinPreset(groupId, previousActivePreset);
                LastError = exception.Message;
                ModLog.Error($"保存卡图预设 {normalizedName} 失败：{exception}");
                return false;
            }
        }
    }

    public static bool OverwriteCardSkinPreset(string groupId, string name)
    {
        lock (Sync)
        {
            var index = FindCardSkinPresetIndex(groupId, name);
            if (index < 0)
            {
                LastError = $"找不到卡图预设：{name}";
                return false;
            }

            var previous = CloneCardSkinPreset(Config.CardSkinPresets[index]);
            var previousActivePreset = GetActiveCardSkinPreset(groupId);
            var replacement = CaptureCurrentCardSkinPreset(groupId, previous.Name);
            try
            {
                Config.CardSkinPresets[index] = replacement;
                SetActiveCardSkinPreset(groupId, replacement.Name);
                Config.Save(ConfigPath);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                Config.CardSkinPresets[index] = previous;
                SetActiveCardSkinPreset(groupId, previousActivePreset);
                LastError = exception.Message;
                ModLog.Error($"覆盖卡图预设 {previous.Name} 失败：{exception}");
                return false;
            }
        }
    }

    public static bool RenameCardSkinPreset(string groupId, string currentName, string newName)
    {
        lock (Sync)
        {
            var index = FindCardSkinPresetIndex(groupId, currentName);
            var normalizedName = NormalizeCardSkinPresetName(newName);
            if (index < 0 || normalizedName == null)
            {
                if (index < 0)
                {
                    LastError = $"找不到卡图预设：{currentName}";
                }

                return false;
            }

            var duplicateIndex = FindCardSkinPresetIndex(groupId, normalizedName);
            if (duplicateIndex >= 0 && duplicateIndex != index)
            {
                LastError = $"卡图预设已存在：{normalizedName}";
                return false;
            }

            var previousName = Config.CardSkinPresets[index].Name;
            var previousActivePreset = GetActiveCardSkinPreset(groupId);
            try
            {
                Config.CardSkinPresets[index].Name = normalizedName;
                if (previousName.Equals(previousActivePreset, StringComparison.OrdinalIgnoreCase))
                {
                    SetActiveCardSkinPreset(groupId, normalizedName);
                }

                Config.Save(ConfigPath);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                Config.CardSkinPresets[index].Name = previousName;
                SetActiveCardSkinPreset(groupId, previousActivePreset);
                LastError = exception.Message;
                ModLog.Error($"重命名卡图预设 {previousName} 失败：{exception}");
                return false;
            }
        }
    }

    public static bool DeleteCardSkinPreset(string groupId, string name)
    {
        lock (Sync)
        {
            var index = FindCardSkinPresetIndex(groupId, name);
            if (index < 0)
            {
                LastError = $"找不到卡图预设：{name}";
                return false;
            }

            var preset = Config.CardSkinPresets[index];
            var previousActivePreset = GetActiveCardSkinPreset(groupId);
            try
            {
                Config.CardSkinPresets.RemoveAt(index);
                if (preset.Name.Equals(
                        previousActivePreset,
                        StringComparison.OrdinalIgnoreCase))
                {
                    SetActiveCardSkinPreset(groupId, null);
                }

                Config.Save(ConfigPath);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                Config.CardSkinPresets.Insert(index, preset);
                SetActiveCardSkinPreset(groupId, previousActivePreset);
                LastError = exception.Message;
                ModLog.Error($"删除卡图预设 {preset.Name} 失败：{exception}");
                return false;
            }
        }
    }

    public static bool ApplyCardSkinPreset(string groupId, string name)
    {
        lock (Sync)
        {
            var index = FindCardSkinPresetIndex(groupId, name);
            var catalog = Catalog;
            var group = catalog?.CardGroups.FirstOrDefault(candidate => candidate.Id.Equals(
                groupId,
                StringComparison.OrdinalIgnoreCase));
            if (index < 0 || catalog == null || group == null)
            {
                LastError = index < 0
                    ? $"找不到卡图预设：{name}"
                    : catalog == null ? "皮肤目录尚未初始化。" : $"未知的卡牌皮肤分类：{groupId}";
                return false;
            }

            var preset = Config.CardSkinPresets[index];
            var hadPreviousPriority = Config.CardSkinPriorities.TryGetValue(groupId, out var previousPriority);
            var previousSelections = CaptureCardSelectionsForGroup(groupId);
            var previousActivePreset = GetActiveCardSkinPreset(groupId);
            var previousDefaultsVersion = Config.CardPriorityDefaultsVersion;
            try
            {
                if (preset.CardSkinPriorities.TryGetValue(groupId, out var requestedPriority))
                {
                    Config.CardSkinPriorities[groupId] = requestedPriority.ToList();
                }
                else
                {
                    Config.CardSkinPriorities.Remove(groupId);
                }

                ReplaceCardSelectionsForGroup(groupId, preset.Selections);
                Config.CardPriorityDefaultsVersion = 1;
                SetActiveCardSkinPreset(groupId, preset.Name);
                GetCardPriorityEntriesInternal(group);
                CardPreviewSelections.Clear();
                ClearCardPortraitCache(groupId);

                MountCardOverlay(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupId });
                Config.Save(ConfigPath);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                if (hadPreviousPriority)
                {
                    Config.CardSkinPriorities[groupId] = previousPriority!;
                }
                else
                {
                    Config.CardSkinPriorities.Remove(groupId);
                }

                ReplaceCardSelectionsForGroup(groupId, previousSelections);
                SetActiveCardSkinPreset(groupId, previousActivePreset);
                Config.CardPriorityDefaultsVersion = previousDefaultsVersion;
                CardPreviewSelections.Clear();
                ClearCardPortraitCache(groupId);

                try
                {
                    MountCardOverlay(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupId });
                }
                catch (Exception restoreException)
                {
                    ModLog.Error("恢复卡图预设切换前的资源失败：" + restoreException);
                }

                LastError = exception.Message;
                ModLog.Error($"应用卡图预设 {preset.Name} 失败：{exception}");
                return false;
            }
        }
    }

    public static bool SetCardPriorityEnabled(string groupId, string optionId, bool enabled)
    {
        lock (Sync)
        {
            var group = Catalog?.CardGroups.FirstOrDefault(candidate =>
                candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                LastError = $"未知的卡牌皮肤分类：{groupId}";
                return false;
            }

            var entries = GetCardPriorityEntriesInternal(group)
                .Select(entry => entry.OptionId.Equals(optionId, StringComparison.OrdinalIgnoreCase)
                    ? entry with { Enabled = enabled }
                    : entry)
                .ToArray();
            return ApplyCardPriority(groupId, entries);
        }
    }

    public static bool MoveCardPriority(string groupId, string optionId, int offset)
    {
        lock (Sync)
        {
            var group = Catalog?.CardGroups.FirstOrDefault(candidate =>
                candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                LastError = $"未知的卡牌皮肤分类：{groupId}";
                return false;
            }

            var entries = GetCardPriorityEntriesInternal(group).ToList();
            var index = entries.FindIndex(entry =>
                entry.OptionId.Equals(optionId, StringComparison.OrdinalIgnoreCase));
            var target = Math.Clamp(index + offset, 0, entries.Count - 1);
            if (index < 0 || target == index)
            {
                return index >= 0;
            }

            var moved = entries[index];
            entries.RemoveAt(index);
            entries.Insert(target, moved);
            return ApplyCardPriority(groupId, entries);
        }
    }

    private static bool ApplyCardPriority(
        string groupId,
        IReadOnlyList<CardSkinPriorityEntry> requestedEntries)
    {
        var group = Catalog?.CardGroups.FirstOrDefault(candidate =>
            candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            LastError = $"未知的卡牌皮肤分类：{groupId}";
            return false;
        }

        var knownIds = group.Options.Select(option => option.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownEntries = requestedEntries
            .Where(entry => knownIds.Contains(entry.OptionId))
            .DistinctBy(entry => entry.OptionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var option in group.Options.Where(option => knownEntries.All(entry =>
                     !entry.OptionId.Equals(option.Id, StringComparison.OrdinalIgnoreCase))))
        {
            knownEntries.Add(new CardSkinPriorityEntry(option.Id, Enabled: true));
        }

        var previousEntries = Config.CardSkinPriorities.TryGetValue(group.Id, out var configured)
            ? configured.ToList()
            : null;
        var storedEntries = MergeKnownCardPriorityEntries(
            previousEntries ?? [],
            knownEntries,
            knownIds);
        var selectionKey = CardSelectionKey(group.Id);
        var hadPreviousSelection = Config.Selections.TryGetValue(selectionKey, out var previousSelection);
        var previousActivePreset = GetActiveCardSkinPreset(group.Id);
        try
        {
            Config.CardSkinPriorities[group.Id] = storedEntries;
            Config.Selections[selectionKey] = knownEntries.FirstOrDefault(entry => entry.Enabled)?.OptionId ??
                                             SkinCatalog.BaseOptionId;
            SetActiveCardSkinPreset(group.Id, null);
            ClearCardPortraitCache(group.Id);
            MountCardOverlay(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Id });
            Config.Save(ConfigPath);
            LastError = null;
            return true;
        }
        catch (Exception exception)
        {
            if (previousEntries == null)
            {
                Config.CardSkinPriorities.Remove(group.Id);
            }
            else
            {
                Config.CardSkinPriorities[group.Id] = previousEntries;
            }

            RestoreSelection(selectionKey, previousSelection, hadPreviousSelection);
            SetActiveCardSkinPreset(group.Id, previousActivePreset);
            ClearCardPortraitCache(group.Id);
            TryRestoreOverlay(group.Id, cardOverlay: true);
            LastError = exception.Message;
            ModLog.Error($"调整 {group.Id} 卡牌皮肤优先级失败：{exception}");
            return false;
        }
    }

    public static bool RegisterMonsterSkinCategory(
        string categoryId,
        IEnumerable<string> groupIds)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            if (catalog == null || string.IsNullOrWhiteSpace(categoryId))
            {
                return false;
            }

            var knownGroupIds = groupIds
                .Where(groupId => !string.IsNullOrWhiteSpace(groupId))
                .Where(groupId => catalog.Groups.Any(group =>
                    group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase) &&
                    group.Options.Count > 0))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (knownGroupIds.Count == 0)
            {
                return false;
            }

            if (Config.MonsterSkinCategoryGroups.TryGetValue(categoryId, out var current) &&
                current.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(knownGroupIds))
            {
                return true;
            }

            return ChangeMonsterPriorityConfiguration(
                categoryId,
                () => Config.MonsterSkinCategoryGroups[categoryId] = knownGroupIds,
                adoptUnconfiguredGroups: true);
        }
    }

    public static IReadOnlyList<MonsterPriorityOptionState> GetMonsterPriorityOptions(string categoryId)
    {
        lock (Sync)
        {
            var options = GetMonsterCategoryOptionsInternal(categoryId);
            if (options.Count == 0)
            {
                return [];
            }

            var entries = GetMonsterPriorityEntriesInternal(categoryId);
            return entries.Select((entry, colorIndex) =>
                {
                    var option = options.First(candidate => candidate.OptionId.Equals(
                        entry.OptionId,
                        StringComparison.OrdinalIgnoreCase));
                    return new MonsterPriorityOptionState(
                        option.OptionId,
                        option.Name,
                        entry.Enabled,
                        colorIndex,
                        option.Coverage,
                        option.TotalMonsters);
                })
                .ToArray();
        }
    }

    public static IReadOnlySet<string> GetMonsterRunEnvironmentProviders(
        string categoryId,
        IEnumerable<string> groupIds,
        IReadOnlySet<string>? activeCombatGroupIds = null)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            if (catalog == null)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var groups = groupIds
                .Where(groupId => !string.IsNullOrWhiteSpace(groupId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(groupId => catalog.Groups.FirstOrDefault(group =>
                    group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase)))
                .Where(group => group != null)
                .Cast<SkinGroup>()
                .ToArray();
            var options = groups
                .SelectMany(group => group.Options)
                .DistinctBy(option => option.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var knownOptionIds = options
                .Select(option => option.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var priorities = Config.MonsterSkinPriorities.TryGetValue(categoryId, out var configured)
                ? configured
                    .Where(entry => knownOptionIds.Contains(entry.OptionId))
                    .DistinctBy(entry => entry.OptionId, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
            foreach (var option in options.Where(option => priorities.All(entry =>
                         !entry.OptionId.Equals(option.Id, StringComparison.OrdinalIgnoreCase))))
            {
                priorities.Add(new MonsterSkinPriorityEntry(option.Id, Enabled: true));
            }

            var activeProviderIds = activeCombatGroupIds?
                .Where(groupId => !string.IsNullOrWhiteSpace(groupId))
                .Select(groupId => catalog.ResolveVisualProviderId(GetVisualSelection(groupId)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var manuallySelectedActiveProviderIds = activeCombatGroupIds?
                .Where(groupId => Config.MonsterGroupsWithManualSelection.Contains(
                    groupId,
                    StringComparer.OrdinalIgnoreCase))
                .Select(groupId => catalog.ResolveVisualProviderId(GetVisualSelection(groupId)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ??
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var candidates = priorities.Select(entry =>
            {
                var providerId = catalog.ResolveVisualProviderId(entry.OptionId);
                return new RuntimeProviderPriorityCandidate(
                    providerId,
                    entry.Enabled || manuallySelectedActiveProviderIds.Contains(providerId),
                    catalog.ProviderUsesScopedMonsterRuntime(providerId),
                    activeProviderIds == null || activeProviderIds.Contains(providerId));
            });
            return RuntimeProviderScopePolicy.SelectRunEnvironmentProviders(candidates);
        }
    }

    public static bool SetMonsterPriorityOptionEnabled(
        string categoryId,
        string optionId,
        bool enabled)
    {
        lock (Sync)
        {
            var entries = GetMonsterPriorityEntriesInternal(categoryId)
                .Select(entry => entry.OptionId.Equals(optionId, StringComparison.OrdinalIgnoreCase)
                    ? entry with { Enabled = enabled }
                    : entry)
                .ToList();
            if (entries.All(entry => !entry.OptionId.Equals(optionId, StringComparison.OrdinalIgnoreCase)))
            {
                LastError = $"未知的怪物皮肤选项：{categoryId}/{optionId}";
                return false;
            }

            return ChangeMonsterPriorityConfiguration(
                categoryId,
                () => Config.MonsterSkinPriorities[categoryId] = entries,
                adoptUnconfiguredGroups: false);
        }
    }

    public static bool MoveMonsterPriority(string categoryId, string optionId, int offset)
    {
        lock (Sync)
        {
            var entries = GetMonsterPriorityEntriesInternal(categoryId).ToList();
            var index = entries.FindIndex(entry =>
                entry.OptionId.Equals(optionId, StringComparison.OrdinalIgnoreCase));
            var target = Math.Clamp(index + offset, 0, entries.Count - 1);
            if (index < 0 || target == index)
            {
                return index >= 0;
            }

            var moved = entries[index];
            entries.RemoveAt(index);
            entries.Insert(target, moved);
            return ChangeMonsterPriorityConfiguration(
                categoryId,
                () => Config.MonsterSkinPriorities[categoryId] = entries,
                adoptUnconfiguredGroups: false);
        }
    }

    public static bool HasMonsterSkinCategory(string groupId) =>
        Config.MonsterSkinCategoryGroups.Values.Any(groupIds =>
            groupIds.Contains(groupId, StringComparer.OrdinalIgnoreCase));

    public static string GetMonsterOverrideSelection(string groupId) =>
        Config.MonsterGroupsFollowingCategory.Contains(groupId, StringComparer.OrdinalIgnoreCase)
            ? InheritMonsterSelectionId
            : Config.GetSelection(groupId);

    public static bool FollowMonsterCategoryPriority(string groupId)
    {
        lock (Sync)
        {
            var categoryId = Config.MonsterSkinCategoryGroups
                .FirstOrDefault(pair => pair.Value.Contains(groupId, StringComparer.OrdinalIgnoreCase))
                .Key;
            if (string.IsNullOrWhiteSpace(categoryId))
            {
                LastError = $"怪物 {groupId} 不属于已登记的图鉴分类。";
                return false;
            }

            return ChangeMonsterPriorityConfiguration(
                categoryId,
                () =>
                {
                    Config.MonsterGroupsWithManualSelection.RemoveAll(candidate =>
                        candidate.Equals(groupId, StringComparison.OrdinalIgnoreCase));
                    if (!Config.MonsterGroupsFollowingCategory.Contains(
                            groupId,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        Config.MonsterGroupsFollowingCategory.Add(groupId);
                    }
                },
                adoptUnconfiguredGroups: false);
        }
    }

    private static bool ChangeMonsterPriorityConfiguration(
        string categoryId,
        Action mutation,
        bool adoptUnconfiguredGroups)
    {
        var previousSelections = new Dictionary<string, string>(
            Config.Selections,
            StringComparer.OrdinalIgnoreCase);
        var previousPriorities = Config.MonsterSkinPriorities.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
        var previousCategories = Config.MonsterSkinCategoryGroups.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
        var previousEnabledCategories = Config.EnabledMonsterSkinPriorityCategories.ToList();
        var previousFollowingGroups = Config.MonsterGroupsFollowingCategory.ToList();
        var previousManualGroups = Config.MonsterGroupsWithManualSelection.ToList();
        var previousVisualProviderPriority = Config.VisualProviderPriority.ToList();
        var affectedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            mutation();
            if (!Config.MonsterSkinCategoryGroups.TryGetValue(categoryId, out var categoryGroups))
            {
                throw new InvalidOperationException($"未知的怪物图鉴分类：{categoryId}");
            }

            if (adoptUnconfiguredGroups)
            {
                foreach (var groupId in categoryGroups.Where(groupId =>
                             !Config.MonsterGroupsWithManualSelection.Contains(
                                 groupId,
                                 StringComparer.OrdinalIgnoreCase)))
                {
                    if (!Config.MonsterGroupsFollowingCategory.Contains(
                            groupId,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        Config.MonsterGroupsFollowingCategory.Add(groupId);
                    }
                }
            }

            _ = GetMonsterPriorityEntriesInternal(categoryId);
            affectedGroups = ApplyMonsterCategoryPriorityToSelections(categoryId);
            foreach (var groupId in affectedGroups)
            {
                ClearRuntimeResourceCache(groupId);
                UpdateVisualProviderPriority(groupId, Config.GetSelection(groupId));
            }

            if (affectedGroups.Count > 0)
            {
                MountOverlay(affectedGroups);
            }

            Config.Save(ConfigPath);
            LastError = null;
            return true;
        }
        catch (Exception exception)
        {
            var restoreGroups = Config.Selections.Keys
                .Union(previousSelections.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(groupId => !string.Equals(
                    Config.Selections.GetValueOrDefault(groupId),
                    previousSelections.GetValueOrDefault(groupId),
                    StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            restoreGroups.UnionWith(affectedGroups);
            Config.Selections = previousSelections;
            Config.MonsterSkinPriorities = previousPriorities;
            Config.MonsterSkinCategoryGroups = previousCategories;
            Config.EnabledMonsterSkinPriorityCategories = previousEnabledCategories;
            Config.MonsterGroupsFollowingCategory = previousFollowingGroups;
            Config.MonsterGroupsWithManualSelection = previousManualGroups;
            Config.VisualProviderPriority = previousVisualProviderPriority;
            foreach (var groupId in restoreGroups)
            {
                ClearRuntimeResourceCache(groupId);
            }

            if (restoreGroups.Count > 0)
            {
                TryRestoreOverlay(restoreGroups, cardOverlay: false);
            }

            LastError = exception.Message;
            ModLog.Error($"调整 {categoryId} 怪物皮肤优先级失败：{exception}");
            return false;
        }
    }

    public static bool ShouldDriveManagedCharacterAnimations(string groupId)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            if (catalog == null)
            {
                return false;
            }

            var selectedId = GetVisualSelection(groupId);
            return catalog.IsRuntimeProviderOption(groupId, selectedId) &&
                   catalog.ProviderUsesManagedCharacterScene(groupId, selectedId);
        }
    }

    public static IReadOnlyList<CardSkinOption> GetCardOptions(CardModel card)
    {
        lock (Sync)
        {
            return GetCardLookup(card).Options;
        }
    }

    public static string? GetCardOptionName(string optionId)
    {
        lock (Sync)
        {
            return Catalog?.CardGroups
                .SelectMany(group => group.Options)
                .FirstOrDefault(option => option.Id.Equals(
                    optionId,
                    StringComparison.OrdinalIgnoreCase))
                ?.Name;
        }
    }

    public static bool HasCardSkin(CardModel card)
    {
        lock (Sync)
        {
            return GetCardLookup(card).Options.Count > 0;
        }
    }

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
            var lookup = GetCardLookup(card);
            return GetEffectiveCardSelection(card, lookup);
        }
    }

    public static void WithCardPreviewSelection(
        CardModel card,
        string selection,
        Action previewAction)
    {
        var key = IndividualCardSelectionKey(card);
        string? previous = null;
        bool hadPrevious;
        lock (Sync)
        {
            hadPrevious = CardPreviewSelections.TryGetValue(key, out previous);
            CardPreviewSelections[key] = selection;
        }

        try
        {
            previewAction();
        }
        finally
        {
            lock (Sync)
            {
                if (hadPrevious)
                {
                    CardPreviewSelections[key] = previous!;
                }
                else
                {
                    CardPreviewSelections.Remove(key);
                }
            }
        }
    }

    private static string GetEffectiveCardSelection(
        CardModel card,
        CardLookup lookup)
    {
        var cardSelectionKey = IndividualCardSelectionKey(card);
        var individual = CardPreviewSelections.TryGetValue(cardSelectionKey, out var previewSelection)
            ? previewSelection
            : Config.Selections.GetValueOrDefault(cardSelectionKey, InheritCardSelectionId);
        if (individual.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase))
        {
            return individual;
        }

        if (lookup.OptionsById.ContainsKey(individual))
        {
            return individual;
        }

        if (lookup.Group == null)
        {
            return SkinCatalog.BaseOptionId;
        }

        foreach (var entry in GetCardPriorityEntriesInternal(lookup.Group).Where(entry => entry.Enabled))
        {
            if (lookup.OptionsById.TryGetValue(entry.OptionId, out var option))
            {
                return option.Option.Id;
            }
        }

        return SkinCatalog.BaseOptionId;
    }

    public static CardPresentationDefinition? GetCardPresentation(CardModel card)
    {
        lock (Sync)
        {
            var lookup = GetCardLookup(card);
            var selection = GetEffectiveCardSelection(card, lookup);
            return lookup.OptionsById.TryGetValue(selection, out var option)
                ? option.Option.CardPresentations.GetValueOrDefault(lookup.CardType)
                : null;
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
            var lookup = GetCardLookup(card);
            var selection = GetEffectiveCardSelection(card, lookup);
            lookup.OptionsById.TryGetValue(selection, out var optionLookup);
            var option = optionLookup?.Option;
            var cacheKey =
                $"card-presentation:{lookup.GroupId}:{typeof(T).FullName}:{selection}:{resourcePath}";
            if (RuntimeResourceCache.TryGetValue(cacheKey, out var cached) &&
                cached is T typedCached &&
                GodotObject.IsInstanceValid(typedCached))
            {
                return typedCached;
            }

            T? resource = null;
            if (lookup.Group != null)
            {
                foreach (var useSelectedProvider in new[] { true, false })
                {
                    if (useSelectedProvider && option?.ProviderRootPath == null)
                    {
                        continue;
                    }

                    try
                    {
                        var providerPaths = useSelectedProvider && option != null
                            ? option.CardPresentations.Values
                                .SelectMany(presentation => presentation.ResourcePaths)
                                .Append(resourcePath)
                            : [resourcePath];
                        var overlay = EnsureIsolatedCardOverlay(
                            lookup.Group.Id,
                            selection,
                            useSelectedProvider,
                            providerPaths);
                        if (overlay.ResourcePaths.TryGetValue(resourcePath, out var isolatedPath))
                        {
                            resource = ResourceLoader.Load<T>(
                                isolatedPath,
                                null,
                                overlay.CanReuseExternalDependencies
                                    ? ResourceLoader.CacheMode.Ignore
                                    : ResourceLoader.CacheMode.IgnoreDeep);
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
            var lookup = GetCardLookup(card);
            var group = lookup.Group;
            if (group == null)
            {
                LastError = $"没有找到卡牌 {card.Id} 的皮肤分类。";
                return false;
            }

            if (!optionId.Equals(InheritCardSelectionId, StringComparison.OrdinalIgnoreCase) &&
                !optionId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) &&
                !lookup.OptionsById.ContainsKey(optionId))
            {
                LastError = $"未知的单卡皮肤选择：{card.Id}/{optionId}";
                return false;
            }

            var key = IndividualCardSelectionKey(card);
            var hadPrevious = Config.Selections.TryGetValue(key, out var previous);
            var previousActivePreset = GetActiveCardSkinPreset(group.Id);
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

                SetActiveCardSkinPreset(group.Id, null);

                ClearCardPortraitCache(group.Id);
                MountCardOverlay(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Id });
                Config.Save(ConfigPath);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                RestoreSelection(key, previous, hadPrevious);
                SetActiveCardSkinPreset(group.Id, previousActivePreset);
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
            var request = ResolveCardPortraitRequest(card);
            if (request == null)
            {
                return;
            }

            var portrait = GetOrLoadCardPortrait(request);
            if (portrait != null)
            {
                result = portrait;
            }
        }
    }

    public static bool TryAssignExternalCardProviderIdentity(
        CardModel card,
        Texture2D texture)
    {
        lock (Sync)
        {
            var request = ResolveCardPortraitRequest(card);
            if (request == null ||
                !CardPortraitCache.TryGetValue(request.CacheKey, out var managedTexture) ||
                !GodotObject.IsInstanceValid(managedTexture) ||
                managedTexture.GetInstanceId() != texture.GetInstanceId())
            {
                return false;
            }

            if (ExternalCardProviderIdentityPolicy.NeedsSyntheticPath(
                    managerAvailable: true,
                    isManagedTexture: true,
                    texture.ResourcePath))
            {
                if (!ExternalCardProviderIdentityPaths.TryGetValue(
                        request.CacheKey,
                        out var providerPath))
                {
                    providerPath = ExternalCardProviderIdentityPolicy.BuildSyntheticPath(
                        card.Id.ToString(),
                        request.CacheKey);
                    ExternalCardProviderIdentityPaths[request.CacheKey] = providerPath;
                }

                texture.ResourcePath = providerPath;
                return !string.IsNullOrWhiteSpace(texture.ResourcePath);
            }

            return false;
        }
    }

    public static void PreloadCardPortraits(IEnumerable<CardModel> cards)
    {
        lock (Sync)
        {
            var requests = cards
                .Select(ResolveCardPortraitRequest)
                .Where(request => request != null)
                .Cast<CardPortraitRequest>()
                .DistinctBy(request => request.CacheKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var batch in requests.GroupBy(
                         request => request.OverlayKey,
                         StringComparer.OrdinalIgnoreCase))
            {
                var sample = batch.First();
                try
                {
                    EnsureIsolatedCardOverlay(
                        sample.GroupId,
                        sample.Selection,
                        sample.UseSelectedProvider,
                        batch.Select(request => request.ResourcePath));
                }
                catch
                {
                    // Individual portrait loads keep their existing fallback behavior and error
                    // handling; preloading is only a latency optimization.
                }
            }

            foreach (var request in requests)
            {
                try
                {
                    GetOrLoadCardPortrait(request);
                }
                catch
                {
                    // Keep the grid usable when one optional provider resource is malformed.
                    // The normal portrait getter retains the established per-card fallback path.
                }
            }
        }
    }

    private static CardPortraitRequest? ResolveCardPortraitRequest(CardModel card)
    {
        var lookup = GetCardLookup(card);
        if (lookup.Group == null)
        {
            return null;
        }

        var selection = GetEffectiveCardSelection(card, lookup);
        if (selection.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase))
        {
            return lookup.Options.Count == 0
                ? null
                : new CardPortraitRequest(
                    lookup.GroupId,
                    selection,
                    card.PortraitPath,
                    $"{lookup.GroupId}\n{selection}\npck\n{card.PortraitPath}",
                    UseSelectedProvider: false,
                    WrapAtlas: false);
        }

        if (!lookup.OptionsById.TryGetValue(selection, out var optionLookup))
        {
            return null;
        }

        var configuredPath = optionLookup.Option.GetPortraitPath(
            lookup.CardType,
            IsAncientStyleEnabled(optionLookup.Option, lookup.CardType, card));
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return new CardPortraitRequest(
                lookup.GroupId,
                selection,
                configuredPath,
                $"{lookup.GroupId}\n{selection}\nconfig\n{configuredPath}",
                UseSelectedProvider: true,
                WrapAtlas: true);
        }

        var selectedProviderPath = SelectProviderCardPath(
            optionLookup.MatchedAssetPaths,
            card,
            card.PortraitPath);
        return selectedProviderPath == null
            // The winning provider may own only the frame/layout. Missing layers belong to the
            // game baseline, never to the next provider in the priority list. Load an isolated
            // baseline portrait explicitly so a previously mounted lower-priority card atlas
            // cannot leak into this card.
            ? new CardPortraitRequest(
                lookup.GroupId,
                SkinCatalog.BaseOptionId,
                card.PortraitPath,
                $"{lookup.GroupId}\n{selection}\nbase-fallback\n{card.PortraitPath}",
                UseSelectedProvider: false,
                WrapAtlas: false)
            : new CardPortraitRequest(
                lookup.GroupId,
                selection,
                selectedProviderPath,
                $"{lookup.GroupId}\n{selection}\npck\n{selectedProviderPath}",
                UseSelectedProvider: true,
                WrapAtlas: false);
    }

    private static Texture2D? GetOrLoadCardPortrait(CardPortraitRequest request)
    {
        if (CardPortraitCache.TryGetValue(request.CacheKey, out var cached) &&
            GodotObject.IsInstanceValid(cached))
        {
            return cached;
        }

        if (FailedCardPortraitRequests.Contains(request.CacheKey))
        {
            return null;
        }

        try
        {
            var loaded = LoadIsolatedCardPortrait(
                request.GroupId,
                request.Selection,
                request.ResourcePath,
                request.UseSelectedProvider);
            if (loaded == null)
            {
                throw new InvalidOperationException("资源包未返回可用卡牌贴图。");
            }

            // ResourceLoader normally returns a CompressedTexture2D for imported card images;
            // those need an AtlasTexture wrapper for the card model's atlas-based path.  The
            // raw-image fallback above creates a real ImageTexture, which is already a complete
            // canvas.  Wrapping it as an AtlasTexture changes the texture-size metadata used by
            // NCard and makes exported portraits appear horizontally compressed, so preserve
            // the original ImageTexture shape.
            var portrait = request.WrapAtlas &&
                           loaded is not AtlasTexture &&
                           loaded is not ImageTexture
                ? new AtlasTexture
                {
                    Atlas = loaded,
                    Region = new Rect2(0, 0, loaded.GetWidth(), loaded.GetHeight())
                }
                : loaded;
            if (CardPortraitCache.Set(request.CacheKey, portrait, out var evicted))
            {
                // Existing NCard nodes keep their own texture reference. Dropping only the cache
                // reference lets Godot release high-resolution portraits after those nodes leave
                // the tree, instead of retaining every card ever viewed for the whole session.
                ExternalCardProviderIdentityPaths.Remove(evicted.Key);
            }
            return portrait;
        }
        catch (Exception exception)
        {
            if (FailedCardPortraitRequests.Add(request.CacheKey))
            {
                ModLog.Warn(
                    $"卡牌皮肤资源不可用，已回退原版卡图：{request.GroupId}/{request.Selection}/" +
                    $"{request.ResourcePath}；原因={exception.GetBaseException().Message}");
            }

            // Returning null leaves the original CardModel.Portrait value untouched. This is
            // especially important for Skill Potion/card-choice overlays: an optional skin
            // failure must not abort NCard construction and leave the action queue waiting for a
            // choice that can no longer be shown.
            return null;
        }
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

    public static bool CardBelongsToGroup(CardModel card, string groupId)
    {
        lock (Sync)
        {
            return GetCardLookup(card).GroupId.Equals(
                groupId,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static CardLookup GetCardLookup(CardModel card) =>
        _cardLookupCache.GetValue(card, ResolveCardLookup);

    private static CardLookup ResolveCardLookup(CardModel card)
    {
        var poolGroupId = GetCardPoolGroupId(card);
        var filterGroupId = GetCardFilterGroupId(card);
        string groupId;
        if (!filterGroupId.Equals(poolGroupId, StringComparison.OrdinalIgnoreCase) &&
            CardGroupAffectsCardUncached(filterGroupId, card))
        {
            groupId = filterGroupId;
        }
        else
        {
            var cardType = card.GetType().Name;
            var configuredGroup = Catalog?.CardGroups.FirstOrDefault(group =>
                group.Options.Any(option =>
                    option.NormalPortraits.ContainsKey(cardType) ||
                    option.AncientPortraits.ContainsKey(cardType)));
            if (configuredGroup != null)
            {
                groupId = configuredGroup.Id;
            }
            else if (CardGroupAffectsCardUncached(poolGroupId, card))
            {
                groupId = poolGroupId;
            }
            else
            {
                var catalogGroupId = GetCardCatalogGroupId(card);
                groupId = CardGroupAffectsCardUncached(catalogGroupId, card)
                    ? catalogGroupId
                    : poolGroupId;
            }
        }

        var group = Catalog?.CardGroups.FirstOrDefault(candidate =>
            candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        var options = group?.Options
            .Select(option => BuildCardOptionLookup(option, card))
            .Where(option => option != null)
            .Cast<CardOptionLookup>()
            .ToArray() ?? [];
        return new CardLookup(
            groupId,
            card.GetType().Name,
            group,
            options.Select(option => option.Option).ToArray(),
            options.ToDictionary(
                option => option.Option.Id,
                StringComparer.OrdinalIgnoreCase));
    }

    private static bool CardGroupAffectsCardUncached(string groupId, CardModel card)
    {
        var group = Catalog?.CardGroups.FirstOrDefault(group =>
            group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            return false;
        }

        return group.Options.Any(option => CardOptionAffectsCardUncached(option, card));
    }

    private static CardSkinGroup? GetCardGroup(CardModel card)
        => GetCardLookup(card).Group;

    private static CardOptionLookup? BuildCardOptionLookup(
        CardSkinOption option,
        CardModel card)
    {
        var cardType = card.GetType().Name;
        if (option.NormalPortraits.ContainsKey(cardType) ||
            option.AncientPortraits.ContainsKey(cardType))
        {
            return new CardOptionLookup(option, []);
        }

        var matchedAssetPaths = option.Assets.Keys
            .Where(assetPath => CardArtMatches(assetPath, card))
            .ToArray();
        return option.CardPresentations.ContainsKey(cardType) || matchedAssetPaths.Length > 0
            ? new CardOptionLookup(option, matchedAssetPaths)
            : null;
    }

    private static bool CardOptionAffectsCardUncached(CardSkinOption option, CardModel card) =>
        BuildCardOptionLookup(option, card) != null;

    private static CardCoverageState GetCardCoverage(CardSkinGroup group)
    {
        if (CardCoverageCache.TryGetValue(group.Id, out var cached))
        {
            return cached;
        }

        var cards = ModelDb.AllCards
            .Select(card => GetCardLookup(card))
            .Where(lookup => lookup.GroupId.Equals(group.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var byOption = group.Options.ToDictionary(
            option => option.Id,
            option => cards.Count(lookup =>
                lookup.OptionsById.ContainsKey(option.Id)),
            StringComparer.OrdinalIgnoreCase);
        cached = new CardCoverageState(cards.Length, byOption);
        CardCoverageCache[group.Id] = cached;
        return cached;
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

    private static Texture2D? LoadIsolatedCardPortrait(
        string groupId,
        string selection,
        string resourcePath,
        bool useSelectedProvider)
    {
        // Raw raster entries are not Godot resources. Calling ResourceLoader for them first emits
        // two engine errors per card ("No loader found" + "Error loading resource") before the
        // direct-image fallback gets a chance to run. Large card-art packs can therefore flood
        // the log and stall the card library even though their bytes are perfectly valid. Resolve
        // direct provider images first; imported .ctex/.tres resources still take the overlay path
        // below because TryReadCardImageBytes deliberately accepts only raster source files.
        var catalog = Catalog;
        if (catalog != null && catalog.TryReadCardImageBytes(
                groupId,
                selection,
                resourcePath,
                useSelectedProvider,
                out var directBytes))
        {
            using var image = LoadRasterImageFromBuffer(directBytes, resourcePath);
            return ImageTexture.CreateFromImage(image);
        }

        var overlay = EnsureIsolatedCardOverlay(
            groupId,
            selection,
            useSelectedProvider,
            [resourcePath]);
        if (!overlay.ResourcePaths.TryGetValue(resourcePath, out var isolatedPath))
        {
            return null;
        }

        var loaded = ResourceLoader.Load<Texture2D>(
            isolatedPath,
            null,
            overlay.CanReuseExternalDependencies
                ? ResourceLoader.CacheMode.Ignore
                : ResourceLoader.CacheMode.IgnoreDeep);
        if (loaded != null)
        {
            return loaded;
        }

        // Some exported card providers ship raw raster files without Godot's imported
        // .import/.ctex pair.  The generated PCK is still useful for the normal resource path,
        // but ResourceLoader cannot decode that raw entry.  Read the selected provider's source
        // bytes directly and create an in-memory texture instead of falling back to the game
        // portrait.  This is deliberately provider-agnostic.
        return null;
    }

    private static Image LoadRasterImageFromBuffer(byte[] bytes, string imagePath)
    {
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException($"独立卡牌图片为空：{imagePath}");
        }

        var image = new Image();
        Error error;
        if (HasPrefix(bytes, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))
        {
            error = image.LoadPngFromBuffer(bytes);
        }
        else if (HasPrefix(bytes, [0xff, 0xd8, 0xff]))
        {
            error = image.LoadJpgFromBuffer(bytes);
        }
        else if (bytes.Length >= 12 &&
                 HasPrefix(bytes, [(byte)'R', (byte)'I', (byte)'F', (byte)'F']) &&
                 bytes.AsSpan(8, 4).SequenceEqual([(byte)'W', (byte)'E', (byte)'B', (byte)'P']))
        {
            error = image.LoadWebpFromBuffer(bytes);
        }
        else
        {
            image.Dispose();
            throw new InvalidOperationException($"无法识别独立卡牌图片格式：{imagePath}");
        }

        if (error != Error.Ok || image.IsEmpty())
        {
            image.Dispose();
            throw new InvalidOperationException($"无法解码独立卡牌图片（{error}）：{imagePath}");
        }

        return image;

        static bool HasPrefix(byte[] value, ReadOnlySpan<byte> prefix) =>
            value.Length >= prefix.Length && value.AsSpan(0, prefix.Length).SequenceEqual(prefix);
    }

    private static IsolatedCardOverlayState EnsureIsolatedCardOverlay(
        string groupId,
        string selection,
        bool useSelectedProvider,
        IEnumerable<string> resourcePaths)
    {
        var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
        var sourceName = useSelectedProvider ? "provider" : "base";
        // Every unskinned card comes from the same game resource set. Share one baseline
        // namespace across card groups so switching categories does not upload another copy of
        // the game's giant card atlas to the GPU.
        var cacheKey = useSelectedProvider
            ? $"{groupId}\n{selection}\n{sourceName}"
            : $"{SkinCatalog.BaseOptionId}\n{sourceName}";
        if (!IsolatedCardOverlayCache.TryGetValue(cacheKey, out var state))
        {
            state = new IsolatedCardOverlayState(cacheKey);
            IsolatedCardOverlayCache.Add(cacheKey, state);
        }

        ReactivateIsolatedCardOverlayIfNeeded(state);

        var missingPaths = resourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !state.ResourcePaths.ContainsKey(path) &&
                           !state.UnavailablePaths.Contains(path))
            .ToArray();
        if (missingPaths.Length == 0)
        {
            return state;
        }

        RuntimeResourceOverlay overlay;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var generation = ++_overlayGeneration;
            state.AliasToken ??=
                $"{_sessionId}/{generation:D3}_card_{sourceName}";
            overlay = catalog.BuildIsolatedCardResources(
                groupId,
                selection,
                missingPaths,
                useSelectedProvider,
                state.AliasToken,
                state.ResourcePaths);
            var newFiles = overlay.Files
                .Where(pair => !state.MountedFilePaths.Contains(pair.Key))
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase);
            if (newFiles.Count == 0)
            {
                throw new InvalidOperationException("独立卡牌资源包没有产生新的可挂载文件。");
            }
            var overlayPath = System.IO.Path.Combine(
                OS.GetUserDataDir(),
                $"sts2_skin_overlay_{_sessionId}_{generation:D3}_card_{sourceName}.pck");
            PckArchive.Write(overlayPath, newFiles);
            if (!ProjectSettings.LoadResourcePack(overlayPath, replaceFiles: true))
            {
                throw new InvalidOperationException("Godot 拒绝加载批量独立卡牌资源包。");
            }
            state.OverlayPaths.Add(overlayPath);
            state.MountedFilePaths.UnionWith(newFiles.Keys);
            state.CanonicalRedirectPaths.UnionWith(overlay.CanonicalDependencyPaths);
            state.CanReuseExternalDependencies &= overlay.CanReuseExternalDependencies;
            CardCanonicalResourceOwners.MarkActivated(
                state.OwnerId,
                overlay.CanonicalDependencyPaths);
            stopwatch.Stop();
            if (missingPaths.Length > 1 || stopwatch.ElapsedMilliseconds >= 100)
            {
                ModLog.Info(
                    $"已批量隔离 {groupId}/{selection} 的 {overlay.ResourcePaths.Count} 个卡牌资源" +
                    $"（{sourceName}，请求 {missingPaths.Length} 个），耗时={stopwatch.Elapsed.TotalMilliseconds:F1} ms。");
            }
        }
        catch
        {
            // A batch can fail because one provider resource is malformed. Do not poison every
            // other path in the batch; a later single-card request can still isolate the good ones.
            throw;
        }

        foreach (var pair in overlay.ResourcePaths)
        {
            state.ResourcePaths[pair.Key] = pair.Value;
        }
        foreach (var path in missingPaths)
        {
            if (!overlay.ResourcePaths.ContainsKey(path))
            {
                state.UnavailablePaths.Add(path);
            }
        }

        return state;
    }

    private static void ReactivateIsolatedCardOverlayIfNeeded(IsolatedCardOverlayState state)
    {
        if (state.OverlayPaths.Count == 0 ||
            !CardCanonicalResourceOwners.RequiresActivation(
                state.OwnerId,
                state.CanonicalRedirectPaths))
        {
            return;
        }

        foreach (var overlayPath in state.OverlayPaths)
        {
            if (!File.Exists(overlayPath) ||
                !ProjectSettings.LoadResourcePack(overlayPath, replaceFiles: true))
            {
                throw new InvalidOperationException(
                    "Godot 拒绝重新激活已缓存的独立卡牌资源包。");
            }
        }

        CardCanonicalResourceOwners.MarkActivated(
            state.OwnerId,
            state.CanonicalRedirectPaths);
    }

    private static string? SelectProviderCardPath(
        IReadOnlyList<string> paths,
        CardModel card,
        string originalPath)
    {
        string? selected = null;
        var selectedScore = int.MinValue;
        foreach (var path in paths)
        {
            var score = CardArtSelectionScore(path, card, originalPath);
            if (score > selectedScore ||
                (score == selectedScore &&
                 string.Compare(path, selected, StringComparison.OrdinalIgnoreCase) < 0))
            {
                selected = path;
                selectedScore = score;
            }
        }

        return selected;
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

    private static bool IsAncientStyleEnabled(
        CardSkinOption option,
        string cardType,
        CardModel card)
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
                .Select(AncientStyleMethodPolicy.Find)
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
            // Exported card projects commonly declare both image and ancientImage for every
            // card.  Without their optional ConfigHelper, ancientImage belongs to the game's
            // Ancient layout only; using it for every normal card makes a 375x527 portrait get
            // fitted into the normal 375x285 slot and visibly narrows the artwork. A provider
            // presentation that explicitly requests the Ancient layout is different: choosing
            // the normal/base image there would produce an Ancient frame with vanilla art.
            var requestsAncientLayout = option.CardPresentations
                .GetValueOrDefault(cardType)?.UseAncientLayout == true;
            return AncientStyleMethodPolicy.ResolveWithoutProviderMethod(
                card.Rarity == CardRarity.Ancient,
                requestsAncientLayout);
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
                ModLog.Warn($"读取 {option.Id} 的先古卡图样式设置失败：{exception.Message}");
            }

            return true;
        }
    }

    public static PackedScene LoadRuntimeScene(string groupId, string scenePath)
    {
        var resourcePaths = RuntimeSceneResourcePaths(groupId, scenePath);
        return LoadRuntimeResources(
                   groupId,
                   resourcePaths,
                   includeProviderDependencies: true)
               .GetValueOrDefault(scenePath) as PackedScene ??
               throw new InvalidOperationException($"独立皮肤资源不是场景：{scenePath}");
    }

    public static PackedScene GetOrLoadRuntimeScene(string groupId, string scenePath)
    {
        var resourcePaths = RuntimeSceneResourcePaths(groupId, scenePath);
        return LoadRuntimeResources(
                   groupId,
                   resourcePaths,
                   includeProviderDependencies: true)
               .GetValueOrDefault(scenePath) as PackedScene ??
               throw new InvalidOperationException($"独立皮肤资源不是场景：{scenePath}");
    }

    /// <summary>
    /// Loads and instantiates a scene while its per-selection overlay is still mounted.
    /// PackedScene keeps some binary external-resource references lazy; returning the scene and
    /// instantiating it after <see cref="WithRuntimeResources{T}"/> restores the canonical packs
    /// can therefore bind a remote player's model to the other player's currently mounted skin.
    /// </summary>
    public static T InstantiateRuntimeScene<T>(
        string groupId,
        string scenePath,
        Action? beforeInstantiate = null)
        where T : Node
    {
        var resourcePaths = RuntimeSceneResourcePaths(groupId, scenePath);
        return WithRuntimeResources(
            groupId,
            resourcePaths,
            resources =>
            {
                beforeInstantiate?.Invoke();
                var scene = resources.GetValueOrDefault(scenePath) as PackedScene ??
                            throw new InvalidOperationException($"独立皮肤资源不是场景：{scenePath}");
                return scene.Instantiate<T>(PackedScene.GenEditState.Disabled);
            },
            includeProviderDependencies: true);
    }

    public static bool TryInstantiateSelectedCharacterCreatureVisuals(
        string groupId,
        string scenePath,
        Func<NCreatureVisuals, NCreatureVisuals>? configureVisuals,
        out NCreatureVisuals visuals)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            var selection = GetVisualSelection(groupId);
            var option = catalog?.Groups.FirstOrDefault(group =>
                    group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
                .Options.FirstOrDefault(candidate =>
                    candidate.Id.Equals(selection, StringComparison.OrdinalIgnoreCase));
            var hasManagedCombatScene = option?.FrameworkContract?
                                            .CharacterResources.ContainsKey("CombatVisual") == true ||
                                        option?.Assets.Keys.Any(path =>
                                            path.Equals(scenePath, StringComparison.OrdinalIgnoreCase)) == true;
            var hasManagedCombatDependencies = option != null &&
                                               CharacterCombatSceneInstantiationPolicy
                                                   .HasManagedCombatDependencies(
                                                       scenePath,
                                                       option.Assets.Keys);
            var isBaseSelection = option == null || option.Id.Equals(
                SkinCatalog.BaseOptionId,
                StringComparison.OrdinalIgnoreCase);
            if (!CharacterCombatSceneInstantiationPolicy.ShouldUseManagedFactory(
                    isBaseSelection,
                    hasManagedCombatScene,
                    hasManagedCombatDependencies))
            {
                visuals = null!;
                return false;
            }
        }

        visuals = InstantiateManagedCharacterCreatureVisuals(
            groupId,
            scenePath,
            configureVisuals);
        return true;
    }

    public static NCreatureVisuals InstantiateManagedCharacterCreatureVisuals(
        string groupId,
        string scenePath,
        Func<NCreatureVisuals, NCreatureVisuals>? configureVisuals = null)
    {
        var resourcePaths = RuntimeSceneResourcePaths(groupId, scenePath);
        return WithRuntimeResources(
            groupId,
            resourcePaths,
            resources =>
            {
                var scene = resources.GetValueOrDefault(scenePath) as PackedScene ??
                            throw new InvalidOperationException(
                                $"角色皮肤资源不是场景：{scenePath}");
                var visuals = FrameworkCreatureSceneFactory.Create(scene);
                return configureVisuals?.Invoke(visuals) ?? visuals;
            },
            includeProviderDependencies: true,
            takeOverCanonicalPaths: true);
    }

    /// <summary>
    /// Mounts a selected visual's private resource overlay for a short-lived Godot lifecycle
    /// callback (for example NCreature._Ready).  Unlike <see cref="WithRuntimeResources{T}"/>,
    /// this does not load a root resource; it keeps canonical external references visible while
    /// the already-instantiated scene is being attached to the tree, then restores the local
    /// selection when the callback finishes.
    /// </summary>
    public static IDisposable BeginRuntimeResourceScope(string groupId, string scenePath)
    {
        var resourcePaths = RuntimeSceneResourcePaths(groupId, scenePath);
        lock (Sync)
        {
            var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
            var selection = GetVisualSelection(groupId);
            var prepared = GetOrPrepareRuntimeOverlay(
                catalog,
                groupId,
                selection,
                resourcePaths,
                includeProviderDependencies: true);
            MountContextualFullRuntimeProviderPacks(catalog, selection);
            if (prepared.OverlayPath != null &&
                !ProjectSettings.LoadResourcePack(prepared.OverlayPath, replaceFiles: true))
            {
                throw new InvalidOperationException("Godot 拒绝加载已准备的独立皮肤场景资源包。");
            }

            if (prepared.CanonicalDependencyPaths.Count > 0)
            {
                if (!RuntimeCanonicalDependencyPaths.TryGetValue(groupId, out var trackedPaths))
                {
                    trackedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    RuntimeCanonicalDependencyPaths[groupId] = trackedPaths;
                }

                trackedPaths.UnionWith(prepared.CanonicalDependencyPaths);
            }

            // A remote provider is not necessarily selected in the local config, so its Godot
            // scripts may not have been registered by the normal global mount yet.  Register
            // them after the private files are visible, before any _Ready callback can resolve
            // script-backed nodes.
            if (catalog.IsRuntimeProviderOption(groupId, selection) &&
                catalog.ProviderUsesManagedGodotScripts(selection))
            {
                ManagedSkinModLoader.EnsureProviderGodotScripts(
                    catalog.ResolveVisualProviderId(selection));
            }

            var restoreGroups = prepared.RestoreGroups
                .Append(groupId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new MountedRuntimeResourceScope(restoreGroups);
        }
    }

    private static IReadOnlyList<string> RuntimeSceneResourcePaths(string groupId, string scenePath)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            var selection = GetVisualSelection(groupId);
            var mode = catalog?.GetRuntimeMonsterVisualMode(groupId, selection);
            IEnumerable<string> modeResourcePaths = catalog != null &&
                                                    CanReuseMountedLargeProviderPack(
                                                        catalog,
                                                        groupId,
                                                        selection)
                ? []
                : mode?.ResourcePaths ?? [];
            return new[] { scenePath }
                .Concat(modeResourcePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private static bool CanReuseMountedLargeProviderPack(
        SkinCatalog catalog,
        string groupId,
        string selection)
    {
        if (!Config.GetSelection(groupId).Equals(
                selection,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var option = catalog.Groups.FirstOrDefault(group =>
                group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(option =>
                option.Id.Equals(selection, StringComparison.OrdinalIgnoreCase));
        return option != null && catalog.GetProviderResourcePackPaths(option.EffectiveProviderId)
            .Select(System.IO.Path.GetFullPath)
            .Any(MountedLargeRuntimeProviderPacks.ContainsKey);
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

    public static Resource GetOrLoadCharacterIconResource(
        string groupId,
        string resourcePath,
        bool includeProviderDependencies = false)
    {
        lock (Sync)
        {
            var selection = GetVisualSelection(groupId);
            var cacheKey = RuntimeResourceKey(groupId, selection, resourcePath);
            if (RuntimeResourceCache.TryGetValue(cacheKey, out var cached) &&
                GodotObject.IsInstanceValid(cached))
            {
                return cached;
            }

            RuntimeResourceCache.Remove(cacheKey);
            return LoadRuntimeResourcesForSelection(
                groupId,
                selection,
                [resourcePath],
                includeProviderDependencies)[resourcePath];
        }
    }

    public static T WithCharacterIconResource<T>(
        string groupId,
        string resourcePath,
        Func<Resource, T> callback,
        bool includeProviderDependencies = false)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (Sync)
        {
            var selection = GetVisualSelection(groupId);
            return WithRuntimeResourcesForSelection(
                groupId,
                selection,
                [resourcePath],
                resources => callback(resources[resourcePath]),
                includeProviderDependencies,
                takeOverCanonicalPaths: false);
        }
    }

    public static Texture2D? GetRelicIconOverride(string resourcePath)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            var groupId = catalog?.FindSelectedRelicIconGroup(
                resourcePath,
                Config.Selections,
                Config.VisualProviderPriority);
            if (catalog == null)
            {
                return null;
            }

            if (groupId == null)
            {
                return GetBaselineRelicIcon(catalog, resourcePath);
            }

            var cacheKey = RuntimeResourceKey(groupId, resourcePath);
            if (!RuntimeResourceCache.TryGetValue(cacheKey, out var cached) ||
                !GodotObject.IsInstanceValid(cached))
            {
                LoadSelectedRelicBundle(catalog, groupId);
                cached = RuntimeResourceCache.GetValueOrDefault(cacheKey);
            }

            return cached as Texture2D ?? throw new InvalidOperationException(
                $"隔离的遗物图标不是贴图：{resourcePath}");
        }
    }

    private static Texture2D? GetBaselineRelicIcon(SkinCatalog catalog, string resourcePath)
    {
        if (BaselineRelicIconCache.TryGetValue(resourcePath, out var cached) &&
            GodotObject.IsInstanceValid(cached))
        {
            return cached;
        }

        BaselineRelicIconCache.Remove(resourcePath);
        var ownerGroupId = catalog.FindRelicIconOwnerGroup(resourcePath);
        if (ownerGroupId == null ||
            !catalog.TryGetBaselineRelicTextureDefinition(resourcePath, out var definition))
        {
            return null;
        }

        if (!BaselineRelicAtlasCache.TryGetValue(definition.AtlasPath, out var atlas) ||
            !GodotObject.IsInstanceValid(atlas))
        {
            BaselineRelicAtlasCache.Remove(definition.AtlasPath);
            var prepared = GetOrPrepareRuntimeOverlay(
                catalog,
                ownerGroupId,
                SkinCatalog.BaseOptionId,
                [definition.AtlasPath],
                includeProviderDependencies: false,
                isolateRelicCanonicalPaths: true);
            if (prepared.OverlayPath != null &&
                !ProjectSettings.LoadResourcePack(prepared.OverlayPath, replaceFiles: true))
            {
                throw new InvalidOperationException("Godot 拒绝加载原版遗物私有图集。");
            }

            if (!prepared.ResourcePaths.TryGetValue(definition.AtlasPath, out var atlasAlias))
            {
                throw new InvalidOperationException($"无法隔离原版遗物图集：{definition.AtlasPath}");
            }

            atlas = ResourceLoader.Load<Texture2D>(
                        atlasAlias,
                        null,
                        ResourceLoader.CacheMode.IgnoreDeep) ??
                    throw new InvalidOperationException($"无法加载原版遗物私有图集：{atlasAlias}");
            BaselineRelicAtlasCache[definition.AtlasPath] = atlas;
        }

        var result = new AtlasTexture
        {
            Atlas = atlas,
            Region = new Rect2(
                definition.Region.X,
                definition.Region.Y,
                definition.Region.Width,
                definition.Region.Height),
            Margin = new Rect2(
                definition.Margin.X,
                definition.Margin.Y,
                definition.Margin.Width,
                definition.Margin.Height),
            FilterClip = definition.FilterClip
        };
        BaselineRelicIconCache[resourcePath] = result;
        return result;
    }

    private static void LoadSelectedRelicBundle(SkinCatalog catalog, string groupId)
    {
        var group = catalog.Groups.First(group => group.Id.Equals(
            groupId,
            StringComparison.OrdinalIgnoreCase));
        var selection = Config.GetSelection(groupId);
        var selected = group.Options.First(option => option.Id.Equals(
            selection,
            StringComparison.OrdinalIgnoreCase));
        var relicPaths = catalog.GetProviderRelicSpritePaths(selected);
        if (relicPaths.Count == 0)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var prepared = GetOrPrepareRuntimeOverlay(
            catalog,
            groupId,
            selection,
            relicPaths,
            includeProviderDependencies: false,
            isolateRelicCanonicalPaths: true);

        if (prepared.OverlayPath != null &&
            !ProjectSettings.LoadResourcePack(prepared.OverlayPath, replaceFiles: true))
        {
            throw new InvalidOperationException("Godot 拒绝加载遗物皮肤资源包。");
        }

        var loaded = new Dictionary<string, Resource>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var providerAtlases = prepared.ResourcePaths
                .Where(pair => SkinCatalog.IsRelicAtlasTexturePath(pair.Key))
                .ToDictionary(
                    pair => pair.Key,
                    pair => ResourceLoader.Load<Texture2D>(
                                pair.Value,
                                null,
                                ResourceLoader.CacheMode.IgnoreDeep) ??
                            throw new InvalidOperationException(
                                $"无法加载遗物皮肤私有图集：{pair.Value}"),
                    StringComparer.OrdinalIgnoreCase);
            var normalAtlas = providerAtlases.FirstOrDefault(pair =>
                !pair.Key.Contains("relic_outline_atlas", StringComparison.OrdinalIgnoreCase)).Value;
            var outlineAtlas = providerAtlases.FirstOrDefault(pair =>
                pair.Key.Contains("relic_outline_atlas", StringComparison.OrdinalIgnoreCase)).Value;
            var needsNormalAtlas = relicPaths.Any(path => !path.Contains(
                "relic_outline_atlas",
                StringComparison.OrdinalIgnoreCase));
            var needsOutlineAtlas = relicPaths.Any(path => path.Contains(
                "relic_outline_atlas",
                StringComparison.OrdinalIgnoreCase));
            if ((needsNormalAtlas && normalAtlas == null) ||
                (needsOutlineAtlas && outlineAtlas == null))
            {
                throw new InvalidOperationException("遗物皮肤缺少其切片引用的图集。");
            }

            foreach (var relicPath in relicPaths)
            {
                if (!prepared.ResourcePaths.TryGetValue(relicPath, out var alias))
                {
                    continue;
                }

                var texture = ResourceLoader.Load<AtlasTexture>(
                                  alias,
                                  null,
                                  ResourceLoader.CacheMode.Ignore) ??
                              throw new InvalidOperationException(
                                  $"无法加载遗物皮肤切片：{alias}");
                texture.Atlas = relicPath.Contains(
                    "relic_outline_atlas",
                    StringComparison.OrdinalIgnoreCase)
                    ? outlineAtlas!
                    : normalAtlas!;
                loaded[relicPath] = texture;
            }
        }
        finally
        {
            var restoreGroups = prepared.RestoreGroups
                .Append(groupId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            MountOverlay(restoreGroups);
        }

        foreach (var resource in loaded)
        {
            RuntimeResourceCache[RuntimeResourceKey(groupId, resource.Key)] = resource.Value;
        }

        ModLog.Info(
            $"已一次性加载 {groupId} 的 {loaded.Count} 个遗物皮肤切片；" +
            $"运行包={prepared.FileCount} 个文件/{prepared.FileSize / 1024d:F1} KiB，" +
            $"耗时={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms。");
    }

    public static bool IsRuntimeProviderSelected(string groupId)
    {
        lock (Sync)
        {
            return Catalog?.IsRuntimeProviderOption(groupId, GetVisualSelection(groupId)) == true;
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

            var selection = GetVisualSelection(groupId);
            if (!Catalog.TryGetVisualProviderId(groupId, selection, out var providerId))
            {
                return null;
            }

            return Catalog.IsRuntimeProviderOption(groupId, selection) &&
                   Catalog.ProviderUsesFullRuntime(providerId) &&
                   Catalog.IsFullRuntimeProviderFullySelected(providerId, GetVisualSelections())
                ? providerId
                : null;
        }
    }

    public static string? GetSelectedRuntimeProvider(string groupId)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            if (catalog == null)
            {
                return null;
            }

            var selection = GetVisualSelection(groupId);
            return catalog.IsRuntimeProviderOption(groupId, selection) &&
                   catalog.TryGetVisualProviderId(groupId, selection, out var providerId)
                ? providerId
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
            var selection = GetVisualSelection(groupId);
            var catalog = Catalog;
            providerId = catalog?.IsRuntimeProviderOption(groupId, selection) == true &&
                         (!catalog.ProviderUsesFullRuntime(selection) ||
                          catalog.IsFullRuntimeProviderFullySelected(selection, GetVisualSelections()))
                ? selection
                : null;
        }

        if (providerId != null)
        {
            ManagedSkinModLoader.ApplySelectedVisualPostfix(providerId, model, ref visuals);
        }
    }

    public static bool IsScopedMonsterRuntimeProviderSelected(
        string providerId,
        string monsterId) => ScopedMonsterSelections.IsSelected(providerId, monsterId);

    internal static void ReleaseTransientRuntimeResources(IEnumerable<string> groupIds)
    {
        lock (Sync)
        {
            var releasedResources = 0;
            var releasedBundles = 0;
            foreach (var groupId in groupIds
                         .Where(groupId => !string.IsNullOrWhiteSpace(groupId))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var prefix = groupId + "\n";
                foreach (var key in RuntimeResourceCache.Keys
                             .Where(key => key.StartsWith(
                                 prefix,
                                 StringComparison.OrdinalIgnoreCase))
                             .ToArray())
                {
                    if (RuntimeResourceCache.Remove(key))
                    {
                        releasedResources++;
                    }
                }

                foreach (var key in RuntimeResourceBundles.Keys
                             .Where(key => key.StartsWith(
                                 prefix,
                                 StringComparison.OrdinalIgnoreCase))
                             .ToArray())
                {
                    if (RuntimeResourceBundles.Remove(key))
                    {
                        releasedBundles++;
                    }
                }
            }

            if (releasedResources > 0 || releasedBundles > 0)
            {
                ModLog.Info(
                    $"战斗房间退出后已释放 {releasedResources} 个怪物资源引用/" +
                    $"{releasedBundles} 个资源包缓存；已生成的运行包仍保留供下次快速复用。");
            }
        }
    }

    public static void ApplyScopedMonsterRuntimeProviderSelection(
        string providerId,
        string monsterId,
        bool enabled)
    {
        string? groupId;
        string currentSelection;
        lock (Sync)
        {
            var catalog = Catalog;
            if (catalog == null || !catalog.ProviderUsesScopedMonsterRuntime(providerId))
            {
                return;
            }

            groupId = catalog.ResolveManagedMonsterGroupId(monsterId);
            if (groupId == null)
            {
                return;
            }

            currentSelection = Config.GetSelection(groupId);
        }

        if (enabled)
        {
            if (!currentSelection.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            {
                ApplySelection(groupId, providerId);
            }
        }
        else if (currentSelection.Equals(providerId, StringComparison.OrdinalIgnoreCase))
        {
            ApplySelection(groupId, SkinCatalog.BaseOptionId);
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

            var selection = GetVisualSelection(groupId);
            return Catalog.IsRuntimeProviderOption(groupId, selection) &&
                   !Catalog.IsResourceBackedOption(groupId, selection) &&
                   Catalog.GetRuntimeImagePath(groupId, selection) != null;
        }
    }

    public static bool ShouldDeferCharacterIconResourceToExternalRuntime(
        string groupId,
        string resourcePath)
    {
        lock (Sync)
        {
            if (Catalog == null)
            {
                return false;
            }

            var visualSelection = GetVisualSelection(groupId);
            return Catalog.IsRuntimeProviderOption(groupId, visualSelection) &&
                   !Catalog.IsResourceBackedOption(groupId, visualSelection) &&
                   Catalog.GetRuntimeImagePath(groupId, visualSelection) != null;
        }
    }

    public static bool IsInteractiveRuntimeProviderSelected(string groupId)
    {
        lock (Sync)
        {
            if (Catalog == null)
            {
                return false;
            }

            var selection = GetVisualSelection(groupId);
            return Catalog.IsRuntimeProviderOption(groupId, selection) &&
                   Catalog.ProviderUsesInteractiveRuntime(selection);
        }
    }

    public static bool IsManagedResourceOptionSelected(string groupId)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            return catalog != null &&
                   catalog.IsResourceBackedOption(groupId, GetVisualSelection(groupId));
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

    public static float SetSelectedMonsterScale(
        string groupId,
        float scale,
        bool save = true)
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

            if (save)
            {
                Config.Save(ConfigPath);
            }

            return normalized;
        }
    }

    public static CharacterCombatTransform GetCharacterCombatTransform(
        string groupId,
        string? optionId = null)
    {
        lock (Sync)
        {
            var selectedOptionId = optionId ?? Config.GetSelection(groupId);
            if (!Config.CharacterCombatTransforms.TryGetValue(groupId, out var options) ||
                !options.TryGetValue(selectedOptionId, out var value))
            {
                return new CharacterCombatTransform();
            }

            return NormalizeCharacterCombatTransform(value);
        }
    }

    internal static IReadOnlyDictionary<string, CharacterCombatTransform>
        GetSessionCharacterCombatTransforms(string groupId, string optionId)
    {
        lock (Sync)
        {
            var result = new Dictionary<string, CharacterCombatTransform>(
                StringComparer.OrdinalIgnoreCase)
            {
                [groupId] = GetCharacterCombatTransform(groupId, optionId)
            };
            var companionPrefix = groupId + "::companion::";
            foreach (var pair in Config.CharacterCombatTransforms.Where(pair =>
                         pair.Key.StartsWith(companionPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                if (pair.Value.TryGetValue(optionId, out var value))
                {
                    result[pair.Key] = NormalizeCharacterCombatTransform(value);
                }
            }
            return result;
        }
    }

    public static CharacterCombatTransform SetCharacterCombatTransform(
        string groupId,
        string optionId,
        CharacterCombatTransform value,
        bool save = true)
    {
        lock (Sync)
        {
            var normalized = NormalizeCharacterCombatTransform(value);
            if (IsDefaultCharacterCombatTransform(normalized))
            {
                if (Config.CharacterCombatTransforms.TryGetValue(groupId, out var existing))
                {
                    existing.Remove(optionId);
                    if (existing.Count == 0)
                    {
                        Config.CharacterCombatTransforms.Remove(groupId);
                    }
                }
            }
            else
            {
                if (!Config.CharacterCombatTransforms.TryGetValue(groupId, out var options))
                {
                    options = new Dictionary<string, CharacterCombatTransform>(StringComparer.OrdinalIgnoreCase);
                    Config.CharacterCombatTransforms[groupId] = options;
                }

                options[optionId] = normalized;
            }

            if (save)
            {
                Config.Save(ConfigPath);
            }

            return normalized;
        }
    }

    internal static CharacterCombatTransform NormalizeCharacterCombatTransform(
        CharacterCombatTransform value) =>
        new CharacterCombatTransform(
            Mathf.Clamp(
                Mathf.Round(value.Scale / CharacterScaleStep) * CharacterScaleStep,
                MinimumCharacterScale,
                MaximumCharacterScale),
            Mathf.Clamp(
                Mathf.Round(value.OffsetX / CharacterOffsetStep) * CharacterOffsetStep,
                MinimumCharacterOffset,
                MaximumCharacterOffset),
            Mathf.Clamp(
                Mathf.Round(value.OffsetY / CharacterOffsetStep) * CharacterOffsetStep,
                MinimumCharacterOffset,
                MaximumCharacterOffset))
        {
            HealthBarScale = Mathf.Clamp(
                Mathf.Round(value.HealthBarScale / CharacterScaleStep) * CharacterScaleStep,
                MinimumCharacterScale,
                MaximumCharacterScale),
            HealthBarOffsetX = Mathf.Clamp(
                Mathf.Round(value.HealthBarOffsetX / CharacterOffsetStep) * CharacterOffsetStep,
                MinimumCharacterOffset,
                MaximumCharacterOffset),
            HealthBarOffsetY = Mathf.Clamp(
                Mathf.Round(value.HealthBarOffsetY / CharacterOffsetStep) * CharacterOffsetStep,
                MinimumCharacterOffset,
                MaximumCharacterOffset),
            HealthBarFollowsModelScale = value.HealthBarFollowsModelScale,
            HealthBarFollowsModelMovement = value.HealthBarFollowsModelMovement,
            IntentScale = Mathf.Clamp(
                Mathf.Round(value.IntentScale / CharacterScaleStep) * CharacterScaleStep,
                MinimumCharacterScale,
                MaximumCharacterScale),
            IntentOffsetX = Mathf.Clamp(
                Mathf.Round(value.IntentOffsetX / CharacterOffsetStep) * CharacterOffsetStep,
                MinimumCharacterOffset,
                MaximumCharacterOffset),
            IntentOffsetY = Mathf.Clamp(
                Mathf.Round(value.IntentOffsetY / CharacterOffsetStep) * CharacterOffsetStep,
                MinimumCharacterOffset,
                MaximumCharacterOffset),
            IntentFollowsModelScale = value.IntentFollowsModelScale,
            IntentFollowsModelMovement = value.IntentFollowsModelMovement,
            SelectionReticleScale = Mathf.Clamp(
                Mathf.Round(value.SelectionReticleScale / CharacterScaleStep) * CharacterScaleStep,
                MinimumCharacterScale,
                MaximumCharacterScale),
            SelectionReticleOffsetX = Mathf.Clamp(
                Mathf.Round(value.SelectionReticleOffsetX / CharacterOffsetStep) * CharacterOffsetStep,
                MinimumCharacterOffset,
                MaximumCharacterOffset),
            SelectionReticleOffsetY = Mathf.Clamp(
                Mathf.Round(value.SelectionReticleOffsetY / CharacterOffsetStep) * CharacterOffsetStep,
                MinimumCharacterOffset,
                MaximumCharacterOffset),
            SelectionReticleFollowsModelScale = value.SelectionReticleFollowsModelScale,
            SelectionReticleFollowsModelMovement = value.SelectionReticleFollowsModelMovement
        };

    private static bool IsDefaultCharacterCombatTransform(CharacterCombatTransform value) =>
        Mathf.IsEqualApprox(value.Scale, 1f) &&
        Mathf.IsZeroApprox(value.OffsetX) &&
        Mathf.IsZeroApprox(value.OffsetY) &&
        Mathf.IsEqualApprox(value.HealthBarScale, 1f) &&
        Mathf.IsZeroApprox(value.HealthBarOffsetX) &&
        Mathf.IsZeroApprox(value.HealthBarOffsetY) &&
        !value.HealthBarFollowsModelScale &&
        value.HealthBarFollowsModelMovement &&
        Mathf.IsEqualApprox(value.IntentScale, 1f) &&
        Mathf.IsZeroApprox(value.IntentOffsetX) &&
        Mathf.IsZeroApprox(value.IntentOffsetY) &&
        !value.IntentFollowsModelScale &&
        value.IntentFollowsModelMovement &&
        Mathf.IsEqualApprox(value.SelectionReticleScale, 1f) &&
        Mathf.IsZeroApprox(value.SelectionReticleOffsetX) &&
        Mathf.IsZeroApprox(value.SelectionReticleOffsetY) &&
        value.SelectionReticleFollowsModelScale &&
        value.SelectionReticleFollowsModelMovement;

    public static Texture2D GetSelectedRuntimeImageTexture(string groupId)
    {
        lock (Sync)
        {
            var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
            var selection = GetVisualSelection(groupId);
            var imagePath = catalog.GetRuntimeImagePath(groupId, selection) ??
                            throw new InvalidOperationException($"{groupId}/{selection} 没有独立图片资源。");
            var cacheKey = RuntimeResourceKey(groupId, "external-image:" + imagePath);
            if (RuntimeResourceCache.TryGetValue(cacheKey, out var cached) &&
                GodotObject.IsInstanceValid(cached) && cached is Texture2D cachedTexture)
            {
                return cachedTexture;
            }

            var image = LoadRuntimeImage(imagePath);
            var texture = ImageTexture.CreateFromImage(image);
            RuntimeResourceCache[cacheKey] = texture;
            return texture;
        }
    }

    private static Image LoadRuntimeImage(string imagePath)
    {
        var bytes = File.ReadAllBytes(imagePath);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException($"独立皮肤图片为空：{imagePath}");
        }

        var image = new Image();
        Error error;
        if (HasPrefix(bytes, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))
        {
            error = image.LoadPngFromBuffer(bytes);
        }
        else if (HasPrefix(bytes, [0xff, 0xd8, 0xff]))
        {
            error = image.LoadJpgFromBuffer(bytes);
        }
        else if (bytes.Length >= 12 &&
                 HasPrefix(bytes, [(byte)'R', (byte)'I', (byte)'F', (byte)'F']) &&
                 bytes.AsSpan(8, 4).SequenceEqual([(byte)'W', (byte)'E', (byte)'B', (byte)'P']))
        {
            error = image.LoadWebpFromBuffer(bytes);
        }
        else
        {
            image.Dispose();
            image = Image.LoadFromFile(imagePath);
            if (image == null || image.IsEmpty())
            {
                throw new InvalidOperationException($"无法识别独立皮肤图片格式：{imagePath}");
            }

            return image;
        }

        if (error != Error.Ok || image.IsEmpty())
        {
            image.Dispose();
            throw new InvalidOperationException(
                $"无法解码独立皮肤图片（{error}）：{imagePath}");
        }

        return image;

        static bool HasPrefix(byte[] value, ReadOnlySpan<byte> prefix) =>
            value.Length >= prefix.Length && value.AsSpan(0, prefix.Length).SequenceEqual(prefix);
    }

    public static RuntimeMonsterVisualMode? GetSelectedRuntimeMonsterVisualMode(string groupId)
    {
        lock (Sync)
        {
            return Catalog?.GetRuntimeMonsterVisualMode(groupId, GetVisualSelection(groupId));
        }
    }

    public static AncientLayeredImageTextures? GetSelectedAncientLayeredImageTextures(string groupId)
    {
        lock (Sync)
        {
            var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
            var paths = catalog.GetAncientLayeredImagePaths(
                groupId,
                GetVisualSelection(groupId));
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
                throw new InvalidOperationException($"先古图层资源不是贴图：{path}");
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
        var resources = LoadRuntimeResources(
            groupId,
            scenePaths,
            includeProviderDependencies: true);
        // Runtime resource loading deliberately returns the requested roots together with every
        // discovered dependency so native resources (for example Spine skeleton data) can be
        // rebound after a hot swap. Only the caller-requested roots are scenes; attempting to cast
        // the dependency textures/materials as PackedScene makes every resource-backed preview
        // fail while image-only providers appear to work.
        return scenePaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                scenePath => scenePath,
                scenePath => resources.GetValueOrDefault(scenePath) as PackedScene ??
                             throw new InvalidOperationException(
                                 $"独立皮肤资源不是场景：{scenePath}"),
                StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, Resource> LoadRuntimeResources(
        string groupId,
        IReadOnlyCollection<string> resourcePaths,
        bool includeProviderDependencies = false)
    {
        lock (Sync)
        {
            return LoadRuntimeResourcesForSelection(
                groupId,
                GetVisualSelection(groupId),
                resourcePaths,
                includeProviderDependencies);
        }
    }

    private static IReadOnlyDictionary<string, Resource> LoadRuntimeResourcesForSelection(
        string groupId,
        string selection,
        IReadOnlyCollection<string> resourcePaths,
        bool includeProviderDependencies)
    {
        _ = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
        if (TryGetCachedRuntimeResources(
                groupId,
                selection,
                resourcePaths,
                out var cached))
        {
            return cached;
        }

        // Keep the fast cache path above, but do the first load through the callback form so
        // callers that need to instantiate a PackedScene can run that instantiation while
        // the temporary dependency pack is still mounted.
        return WithRuntimeResourcesForSelection(
            groupId,
            selection,
            resourcePaths,
            resources => resources,
            includeProviderDependencies,
            takeOverCanonicalPaths: false);
    }

    /// <summary>
    /// Loads a selected skin's resources and executes <paramref name="callback">callback</paramref>
    /// before the temporary resource overlay is restored.  A PackedScene is not fully resolved
    /// when ResourceLoader.Load returns: its external resources are often looked up when the
    /// scene is instantiated.  Keeping this operation inside the mount scope prevents a hot
    /// character-select switch from binding a skeleton or layout resource from the previously
    /// selected skin.
    /// </summary>
    public static T WithRuntimeResources<T>(
        string groupId,
        IReadOnlyCollection<string> resourcePaths,
        Func<IReadOnlyDictionary<string, Resource>, T> callback,
        bool includeProviderDependencies = false,
        bool takeOverCanonicalPaths = false)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (Sync)
        {
            var selection = GetVisualSelection(groupId);
            return WithRuntimeResourcesForSelection(
                groupId,
                selection,
                resourcePaths,
                callback,
                includeProviderDependencies,
                takeOverCanonicalPaths);
        }
    }

    private static T WithRuntimeResourcesForSelection<T>(
        string groupId,
        string selection,
        IReadOnlyCollection<string> resourcePaths,
        Func<IReadOnlyDictionary<string, Resource>, T> callback,
        bool includeProviderDependencies,
        bool takeOverCanonicalPaths)
    {
        var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
        var loadStarted = Stopwatch.GetTimestamp();
        var prepared = GetOrPrepareRuntimeOverlay(
            catalog,
            groupId,
            selection,
            resourcePaths,
            includeProviderDependencies);
        if (prepared.OverlayPath != null &&
            !ProjectSettings.LoadResourcePack(prepared.OverlayPath, replaceFiles: true))
        {
            throw new InvalidOperationException("Godot 拒绝加载已准备的独立皮肤场景资源包。");
        }

        if (prepared.CanonicalDependencyPaths.Count > 0)
        {
            if (!RuntimeCanonicalDependencyPaths.TryGetValue(groupId, out var trackedPaths))
            {
                trackedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                RuntimeCanonicalDependencyPaths[groupId] = trackedPaths;
            }

            trackedPaths.UnionWith(prepared.CanonicalDependencyPaths);
        }

        var reused = TryGetRuntimeResourceBundle(prepared.Key, out var resources);
        if (!reused)
        {
            resources = new Dictionary<string, Resource>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in prepared.ResourcePaths)
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
                RuntimeResourceCache[
                    RuntimeResourceKey(groupId, selection, pair.Key)] = resource;
            }

            RuntimeResourceBundles[prepared.Key] = new RuntimeResourceBundleState(resources);
        }

        T callbackResult;
        IDisposable? canonicalOwnership = null;
        try
        {
            if (takeOverCanonicalPaths)
            {
                canonicalOwnership = BeginCanonicalRuntimeResourceOwnership(
                    groupId,
                    prepared.ResourcePaths,
                    resources);
            }

            callbackResult = callback(resources);
        }
        finally
        {
            canonicalOwnership?.Dispose();
            if (prepared.RestoreGroups.Count > 0)
            {
                // Binary resources cannot rewrite all of their internal paths. Restore only
                // the other catalog groups that the temporary dependency pack actually
                // touched; rebuilding every skin group here caused a full localization reload
                // on the first character click.
                MountOverlay(prepared.RestoreGroups);
            }
        }

        var elapsedMs = Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds;
        ModLog.Info(
            $"已{(reused ? "复用" : "加载")} {groupId} 的 {resources.Count} 个独立资源；" +
            $"运行包={prepared.FileCount} 个文件/{prepared.FileSize / 1024d:F1} KiB，" +
            $"耗时={elapsedMs:F1} ms：{prepared.AliasToken}");
        return callbackResult;
    }

    private static IDisposable BeginCanonicalRuntimeResourceOwnership(
        string groupId,
        IReadOnlyDictionary<string, string> resourcePaths,
        IReadOnlyDictionary<string, Resource> resources)
    {
        var restoreOwnership =
            CharacterCombatSceneInstantiationPolicy.ShouldRestoreCanonicalOwnership(
                MultiplayerSkinSync.GetScopedSelection(groupId),
                Config.GetSelection(groupId));
        var ownedResources = new List<CanonicalRuntimeResourceOwnershipEntry>(resources.Count);
        foreach (var pair in resources)
        {
            var canonicalPath = pair.Key;
            var selectedResource = pair.Value;
            Resource? previousResource = null;
            if (ResourceLoader.HasCached(canonicalPath))
            {
                var cached = ResourceLoader.Load<Resource>(
                    canonicalPath,
                    null,
                    ResourceLoader.CacheMode.Reuse);
                if (cached != null && !ReferenceEquals(cached, selectedResource))
                {
                    previousResource = cached;
                }
            }

            var aliasPath = resourcePaths.GetValueOrDefault(canonicalPath);
            if (string.IsNullOrWhiteSpace(aliasPath))
            {
                aliasPath = selectedResource.ResourcePath;
            }

            selectedResource.TakeOverPath(canonicalPath);
            ownedResources.Add(new CanonicalRuntimeResourceOwnershipEntry(
                canonicalPath,
                aliasPath,
                selectedResource,
                previousResource));
        }

        return restoreOwnership
            ? new CanonicalRuntimeResourceOwnershipScope(ownedResources)
            : NoopDisposable.Instance;
    }

    private static PreparedRuntimeOverlay GetOrPrepareRuntimeOverlay(
        SkinCatalog catalog,
        string groupId,
        string selection,
        IReadOnlyCollection<string> resourcePaths,
        bool includeProviderDependencies,
        bool isolateRelicCanonicalPaths = false)
    {
        var normalizedPaths = resourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var reuseMountedPrivateDependencies =
            !isolateRelicCanonicalPaths &&
            catalog.IsRuntimeProviderOption(groupId, selection) &&
            catalog.ProviderRequiresCoherentRuntimePackage(selection) &&
            Config.GetSelection(groupId).Equals(
                selection,
                StringComparison.OrdinalIgnoreCase);
        var key = RuntimeOverlayKey(
            groupId,
            selection,
            normalizedPaths,
            includeProviderDependencies,
            reuseMountedPrivateDependencies,
            isolateRelicCanonicalPaths);
        if (PreparedRuntimeOverlays.TryGetValue(key, out var cached) &&
            (cached.OverlayPath == null || File.Exists(cached.OverlayPath)))
        {
            return cached;
        }

        RuntimeResourceBundles.Remove(key);

        var generation = ++_overlayGeneration;
        var aliasToken = $"{_sessionId}/{generation:D3}";
        var overlay = isolateRelicCanonicalPaths
            ? catalog.BuildIsolatedRelicResourceOverlay(
                groupId,
                selection,
                normalizedPaths,
                aliasToken)
            : catalog.BuildRuntimeResourceOverlay(
                groupId,
                selection,
                normalizedPaths,
                aliasToken,
                includeProviderDependencies,
                reuseMountedPrivateDependencies);
        var restoreGroups = catalog.GetRuntimeDependencyRestoreGroups(
            groupId,
            overlay.CanonicalDependencyPaths);
        string? overlayPath = null;
        long overlaySize = 0;
        if (overlay.Files.Count > 0)
        {
            var directory = PreparedRuntimeOverlayDirectory();
            Directory.CreateDirectory(directory);
            overlayPath = System.IO.Path.Combine(directory, $"{generation:D3}.pck");
            PckArchive.Write(overlayPath, overlay.Files);
            overlaySize = new FileInfo(overlayPath).Length;
        }

        var prepared = new PreparedRuntimeOverlay(
            key,
            aliasToken,
            overlayPath,
            new Dictionary<string, string>(
                overlay.ResourcePaths,
                StringComparer.OrdinalIgnoreCase),
            overlay.CanonicalDependencyPaths.ToHashSet(StringComparer.OrdinalIgnoreCase),
            restoreGroups.ToHashSet(StringComparer.OrdinalIgnoreCase),
            overlay.Files.Count,
            overlaySize);
        PreparedRuntimeOverlays[key] = prepared;
        return prepared;
    }

    private static bool TryGetRuntimeResourceBundle(
        string key,
        out Dictionary<string, Resource> resources)
    {
        if (RuntimeResourceBundles.TryGetValue(key, out var bundle) &&
            bundle.Resources.Values.All(resource =>
                GodotObject.IsInstanceValid(resource)))
        {
            resources = bundle.Resources;
            return true;
        }

        RuntimeResourceBundles.Remove(key);
        resources = null!;
        return false;
    }

    private static bool TryGetCachedRuntimeResources(
        string groupId,
        string selection,
        IReadOnlyCollection<string> resourcePaths,
        out IReadOnlyDictionary<string, Resource> resources)
    {
        var loaded = new Dictionary<string, Resource>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourcePath in resourcePaths)
        {
            var cacheKey = RuntimeResourceKey(groupId, selection, resourcePath);
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
        var totalStarted = Stopwatch.GetTimestamp();
        var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
        RefreshScopedMonsterSelectionSnapshot(catalog);
        var effectiveSelections = GetVisualSelections();
        FrameworkCompatibilityLayer.SynchronizeSelections(catalog, effectiveSelections);
        var activeRuntimeProviders = GetActiveRuntimeProviders(catalog);
        ManagedSkinModLoader.ConfigureRunEnvironmentProviders(
            _runtimeProviderBehaviorScope?.RunEnvironmentProviderIds ?? []);
        // Provider callbacks must be gone before a baseline replacement pack is mounted. Otherwise
        // a stale AssetCache/TakeOverPath callback can immediately reclaim the path being restored.
        ManagedSkinModLoader.DeactivateProvidersExcept(activeRuntimeProviders);
        EnsureScopedRuntimeProviderResourcesMounted(catalog, activeRuntimeProviders);
        var largeProviderMountPlan = MountSelectedLargeRuntimeProviderPacks(catalog, groups);
        var promotedPackResourcePaths = largeProviderMountPlan.PromotedPackPaths
            .Where(MountedLargeRuntimeProviderPacks.ContainsKey)
            .SelectMany(path => MountedLargeRuntimeProviderPacks[path].ResourcePaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var promotedPackRestoreGroups = catalog.GetRuntimeDependencyRestoreGroups(
            string.Empty,
            promotedPackResourcePaths);
        var overlayGroups = groups
            .Union(promotedPackRestoreGroups)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var buildStarted = Stopwatch.GetTimestamp();
        var staleCanonicalPaths = groups
            .Where(RuntimeCanonicalDependencyPaths.ContainsKey)
            .SelectMany(group => RuntimeCanonicalDependencyPaths[group])
            .Concat(promotedPackResourcePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var files = catalog.BuildBaselineDependencyOverlay(staleCanonicalPaths);
        var selectedOverlay = catalog.BuildOverlay(effectiveSelections, overlayGroups);
        // The selected complete PCK is already mounted directly below this correction overlay.
        // A vanilla canonical resource in the correction overlay would hide a selected provider's
        // <canonical path>.remap and produce a mixed skin (for example, a selected merchant body
        // with the vanilla merchant hand). Remove only canonical paths whose provider remap is in
        // the selected overlay. Other files from a multi-group PCK must stay on the baseline.
        var selectedProviderOverlayPaths = selectedOverlay
            .Where(pair =>
                largeProviderMountPlan.PromotedPackPaths.Contains(pair.Value.Archive.Path))
            .Select(pair => pair.Key);
        foreach (var ownedPath in
                 PromotedPackOverlayPolicy.FindBaselinePathsShadowingSelectedRemaps(
                     files.Keys,
                     selectedProviderOverlayPaths))
        {
            files.Remove(ownedPath);
        }

        foreach (var selectedFile in selectedOverlay)
        {
            if (largeProviderMountPlan.PromotedPackPaths.Contains(selectedFile.Value.Archive.Path) &&
                selectedFile.Key.Equals(
                    selectedFile.Value.Path,
                    StringComparison.OrdinalIgnoreCase) &&
                IsDirectProviderPayloadPath(selectedFile.Key))
            {
                // Imported payloads make up almost all of a large animated PCK. The selected pack
                // was just remounted at highest priority, so copying the identical payload into the
                // correction overlay only duplicates it. Keep scenes, imports, localization and
                // mapped/takeover paths in the small overlay so normal refresh and ownership logic
                // still observes them.
                files.Remove(selectedFile.Key);
                continue;
            }

            // Baseline dependency restoration must happen before the current selection is applied.
            // The current provider therefore remains authoritative for paths it still owns.
            files[selectedFile.Key] = selectedFile.Value;
        }
        var buildElapsed = Stopwatch.GetElapsedTime(buildStarted);
        var mountStarted = Stopwatch.GetTimestamp();
        MountArchiveOverlay(files, "visual", "Godot 拒绝加载生成的皮肤资源包。");
        foreach (var group in groups)
        {
            RuntimeCanonicalDependencyPaths.Remove(group);
        }
        var mountElapsed = Stopwatch.GetElapsedTime(mountStarted);
        var localizationStarted = Stopwatch.GetTimestamp();
        RefreshLocalizationIfNeeded(files);
        var localizationElapsed = Stopwatch.GetElapsedTime(localizationStarted);

        // Register scripts and run third-party initializers only after every private scene, atlas,
        // imported payload and frame directory is visible at its original res:// path. Static
        // resource fields in provider assemblies are often initialized on their first type access.
        foreach (var group in catalog.Groups.Where(group => groups.Contains(group.Id)))
        {
            var selectedId = effectiveSelections.GetValueOrDefault(
                group.Id,
                SkinCatalog.BaseOptionId);
            if (catalog.IsRuntimeProviderOption(group.Id, selectedId) &&
                catalog.ProviderUsesManagedGodotScripts(selectedId))
            {
                // Register before a private PackedScene is instantiated. The compatibility patch
                // on Godot's path map makes this operation idempotent when the provider initializer
                // also calls LookupScriptsInAssembly (a common pattern in complete character packs).
                ManagedSkinModLoader.EnsureProviderGodotScripts(
                    catalog.ResolveVisualProviderId(selectedId));
            }
        }

        // A remote player may use a managed character provider that is not selected in our
        // persistent config. Its scripts still need registering before that player's isolated
        // scene is instantiated; registering is idempotent and does not choose the provider for
        // any local character.
        foreach (var providerId in activeRuntimeProviders.Where(
                     catalog.ProviderUsesManagedGodotScripts))
        {
            ManagedSkinModLoader.EnsureProviderGodotScripts(providerId);
        }

        foreach (var providerId in activeRuntimeProviders.Where(
                     catalog.ProviderUsesScopedMonsterRuntime))
        {
            ManagedSkinModLoader.EnsureScopedMonsterSelectionRouter(providerId);
        }
        // Install the per-monster selection router before invoking a provider initializer. Some
        // providers evaluate IsEnabled(profile) while constructing their runtime services; doing
        // this afterwards can leave a disabled profile cached as active (or vice versa) until the
        // next game launch.
        ManagedSkinModLoader.ActivateSelectedProviders(activeRuntimeProviders);
        ScheduleRuntimeProviderPackWarm(catalog, activeRuntimeProviders, "当前场景");
        ModLog.Info(
            $"已挂载 {groups.Count} 个外观分组/{files.Count} 个文件；" +
            $"目录={buildElapsed.TotalMilliseconds:F1} ms，" +
            $"资源包={mountElapsed.TotalMilliseconds:F1} ms，" +
            $"本地化={localizationElapsed.TotalMilliseconds:F1} ms，" +
            $"总计={Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds:F1} ms。");
    }

    private static void RefreshScopedMonsterSelectionSnapshot(SkinCatalog catalog)
    {
        var selectedMonsterIdsByProvider = new Dictionary<string, IReadOnlyCollection<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var providerId in catalog.GetSelectedScopedMonsterRuntimeProviders(
                     Config.Selections))
        {
            selectedMonsterIdsByProvider[providerId] = catalog
                .GetRuntimeProviderGroups(providerId)
                .Where(groupId => catalog.ResolveVisualProviderId(Config.GetSelection(groupId))
                    .Equals(providerId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        ScopedMonsterSelections.Replace(selectedMonsterIdsByProvider);
    }

    private sealed class MountedRuntimeResourceScope(IReadOnlySet<string> restoreGroups) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (Sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    MountOverlay(restoreGroups);
                }
                catch (Exception exception)
                {
                    ModLog.Error($"恢复临时运行资源覆盖失败：{exception}");
                }
            }
        }
    }

    private sealed record CanonicalRuntimeResourceOwnershipEntry(
        string CanonicalPath,
        string AliasPath,
        Resource SelectedResource,
        Resource? PreviousResource);

    private sealed class CanonicalRuntimeResourceOwnershipScope(
        IReadOnlyList<CanonicalRuntimeResourceOwnershipEntry> entries) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                var entry = entries[index];
                try
                {
                    if (GodotObject.IsInstanceValid(entry.SelectedResource))
                    {
                        entry.SelectedResource.TakeOverPath(entry.AliasPath);
                    }

                    if (entry.PreviousResource != null &&
                        GodotObject.IsInstanceValid(entry.PreviousResource))
                    {
                        entry.PreviousResource.TakeOverPath(entry.CanonicalPath);
                    }
                }
                catch (Exception exception)
                {
                    ModLog.Warn(
                        $"恢复角色规范资源 {entry.CanonicalPath} 失败：{exception.Message}");
                }
            }
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private static void MountContextualFullRuntimeProviderPacks(
        SkinCatalog catalog,
        string selection)
    {
        if (!catalog.ProviderUsesFullRuntime(selection) ||
            catalog.IsFullRuntimeProviderFullySelected(selection, Config.Selections))
        {
            return;
        }

        // A multiplayer selection can differ from this client's persistent character selection.
        // Full-runtime character scenes often reference hundreds of imported frames through
        // prefix-compressed binary paths. The small alias overlay cannot discover or reproduce
        // that import map, so mount the already-installed provider PCK at full priority for the
        // lifetime of this creature-creation scope. The generated alias is mounted immediately
        // afterwards and remains the authoritative entry scene; MountedRuntimeResourceScope then
        // restores this client's configured provider/baseline resources.
        foreach (var resourcePackPath in catalog.GetProviderResourcePackPaths(selection))
        {
            var normalizedPath = System.IO.Path.GetFullPath(resourcePackPath);
            if (!ProjectSettings.LoadResourcePack(normalizedPath, replaceFiles: true))
            {
                throw new InvalidOperationException(
                    $"无法为联机玩家挂载 {selection} 的完整运行时资源包。");
            }

            ModLog.Info($"已为联机玩家临时挂载 {selection} 的完整运行时资源。");
        }
    }

    private static void EnsureScopedRuntimeProviderResourcesMounted(
        SkinCatalog catalog,
        IEnumerable<string> activeRuntimeProviders)
    {
        foreach (var providerId in activeRuntimeProviders
                     .Where(catalog.ProviderUsesScopedMonsterRuntime)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var resourcePackPath in catalog.GetProviderResourcePackPaths(providerId))
            {
                var normalizedPath = System.IO.Path.GetFullPath(resourcePackPath);
                if (!MountedScopedRuntimeProviderPacks.Add(normalizedPath))
                {
                    continue;
                }

                // A scoped DLL can resolve effects, sounds or encounter backgrounds dynamically;
                // those paths cannot be discovered by walking references from the selected
                // skeleton alone. Mount its original PCK as a low-priority dependency namespace.
                // Canonical game paths remain owned by the game/current generated overlay because
                // replaceFiles is false, while provider-private res:// paths become available.
                if (!ProjectSettings.LoadResourcePack(normalizedPath, replaceFiles: false))
                {
                    MountedScopedRuntimeProviderPacks.Remove(normalizedPath);
                    throw new InvalidOperationException(
                        $"无法挂载 {providerId} 的运行时依赖资源包。");
                }

                ModLog.Info($"已低优先级挂载 {providerId} 的完整运行时资源。");
            }
        }
    }

    private static LargeProviderMountPlan MountSelectedLargeRuntimeProviderPacks(
        SkinCatalog catalog,
        IReadOnlySet<string> affectedGroups)
    {
        var selectedOptions = catalog.Groups
            .Select(group =>
            {
                var selection = GetVisualSelection(group.Id);
                var option = group.Options.FirstOrDefault(option =>
                    option.Id.Equals(selection, StringComparison.OrdinalIgnoreCase) &&
                    option.IsRuntimeProvider);
                return (GroupId: group.Id, Option: option);
            })
            .Where(selection => selection.Option != null)
            .ToArray();
        var directProviderPacks = selectedOptions
            .SelectMany(selection =>
            {
                var providerId = selection.Option!.EffectiveProviderId;
                var requiresCoherentPackage =
                    catalog.ProviderRequiresCoherentRuntimePackage(providerId);
                return catalog.GetProviderResourcePackPaths(providerId)
                    .Select(System.IO.Path.GetFullPath)
                    .Where(path =>
                        requiresCoherentPackage ||
                        new FileInfo(path).Length >= DirectRuntimeProviderPackThresholdBytes)
                    .Select(path => (selection.GroupId, Path: path));
            })
            .ToArray();
        var selectedPackPaths = directProviderPacks
            .Select(pack => pack.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var promotedPackPaths = directProviderPacks
            .Where(pack => affectedGroups.Contains(pack.GroupId))
            .Select(pack => pack.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var packPath in selectedPackPaths)
        {
            var firstMount = !MountedLargeRuntimeProviderPacks.ContainsKey(packPath);
            if (!MountedLargeRuntimeProviderPacks.ContainsKey(packPath))
            {
                using var archive = PckArchive.Open(packPath);
                MountedLargeRuntimeProviderPacks[packPath] = new MountedProviderPackState(
                    archive.Paths.ToArray());
                promotedPackPaths.Add(packPath);
            }

            // The correction overlay already restores every selected canonical path. Re-loading
            // every unrelated 50-200 MiB character pack on each dropdown change was therefore
            // pure I/O and could freeze the menu for tens of seconds. Promote only a newly seen
            // pack or the provider belonging to the group that actually changed.
            if (!firstMount && !promotedPackPaths.Contains(packPath))
            {
                continue;
            }

            // Move the affected pack to the top of Godot's pack stack. A generated correction
            // overlay is mounted immediately afterwards to restore every card/game path that this
            // visual selection does not own.
            if (!ProjectSettings.LoadResourcePack(packPath, replaceFiles: true))
            {
                throw new InvalidOperationException(
                    $"无法直接挂载完整外观资源包：{System.IO.Path.GetFileName(packPath)}");
            }

            ModLog.Info(
                $"已直接复用完整外观资源包 {System.IO.Path.GetFileName(packPath)}，" +
                "后续仅生成资源校正包。");
        }

        return new LargeProviderMountPlan(promotedPackPaths);
    }

    private static bool IsDirectProviderPayloadPath(string path) =>
        path.StartsWith("res://.godot/imported/", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> GetActiveRuntimeProviders(SkinCatalog catalog)
    {
        var localSelections = GetVisualSelections();
        var selectionSets = new List<IReadOnlyDictionary<string, string>> { localSelections };
        selectionSets.AddRange(MultiplayerSkinSync.GetAvailableSelectionMaps());
        var selectedProviders = catalog.GetFullySelectedFullRuntimeProviders(localSelections)
            .Union(
                catalog.GetSelectedInteractiveRuntimeProviders(localSelections),
                StringComparer.OrdinalIgnoreCase)
            .Union(
                catalog.GetSelectedScopedMonsterRuntimeProviders(localSelections),
                StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var selections in selectionSets.Skip(1))
        {
            selectedProviders.UnionWith(catalog.GetFullySelectedFullRuntimeProviders(selections));
            selectedProviders.UnionWith(catalog.GetSelectedInteractiveRuntimeProviders(selections));
            selectedProviders.UnionWith(catalog.GetSelectedScopedMonsterRuntimeProviders(selections));
        }
        if (_runtimeProviderBehaviorScope is { } scope)
        {
            selectedProviders.UnionWith(scope.RunEnvironmentProviderIds);
        }
        var candidates = selectedProviders.Select(providerId => new RuntimeProviderCandidate(
            providerId,
            catalog.GetRuntimeProviderGroups(providerId),
            catalog.ProviderUsesScopedMonsterRuntime(providerId)));
        return RuntimeProviderScopePolicy.SelectActiveProviders(
            candidates,
            _runtimeProviderBehaviorScope).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void RefreshLocalizationIfNeeded(
        IReadOnlyDictionary<string, ResourceFile> mountedFiles)
    {
        foreach (var file in mountedFiles.Where(pair =>
                     pair.Key.Contains("/localization/", StringComparison.OrdinalIgnoreCase)))
        {
            MountedLocalizationFiles[file.Key] = file.Value;
        }

        // A deselection can mount no language files at all. It still changes which provider
        // tables the game is allowed to merge, so never skip this check for an empty file delta.
        var signature = BuildActiveLocalizationSignature();

        try
        {
            var manager = LocManager.Instance;
            if (manager == null || string.IsNullOrWhiteSpace(manager.Language))
            {
                // During boot LocManager initializes after Mod PCK mounting and reads the
                // selected files itself. Only an in-session switch needs an explicit reload.
                _mountedLocalizationSignature = signature;
                return;
            }

            if (string.Equals(_mountedLocalizationSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            var language = manager.Language;
            if (_mountedLocalizationSignature != null)
            {
                CaptureLocalizationState(manager, _mountedLocalizationSignature, language);
            }

            var cacheKey = LocalizationCacheKey(signature, language);
            if (LocalizationStateCache.TryGetValue(cacheKey, out var cached) &&
                SetLanguageInternalMethod != null)
            {
                SetLanguageInternalMethod.Invoke(
                    manager,
                    [language, cached.Tables, cached.OverridesActive, cached.ValidationErrors.ToList()]);
                _mountedLocalizationSignature = signature;
                ModLog.Info($"已复用 {language} 本地化状态缓存。");
                return;
            }

            manager.SetLanguage(language);
            _mountedLocalizationSignature = signature;
            CaptureLocalizationState(manager, signature, language);
            ModLog.Info($"已刷新 {language} 本地化缓存。");
        }
        catch (Exception exception)
        {
            // A broken optional translation must not make an otherwise valid visual switch fail.
            ModLog.Warn("刷新皮肤本地化缓存失败：" + exception.GetBaseException().Message);
        }
    }

    private static string BuildActiveLocalizationSignature()
    {
        var catalog = Catalog;
        if (catalog == null)
        {
            return string.Empty;
        }

        var visualSelections = GetVisualSelections();
        var activePaths = catalog.FilterModdedLocalizationTables(
                MountedLocalizationFiles.Keys,
                visualSelections)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeFiles = MountedLocalizationFiles
            .Where(pair => activePaths.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return BuildOverlaySignature(activeFiles, "localization") + "\n" + string.Join(
            "\n",
            catalog.GetSelectedLocalizationProviderIds(visualSelections)
                .Order(StringComparer.OrdinalIgnoreCase));
    }

    public static IEnumerable<string> FilterModdedLocalizationTables(
        IEnumerable<string> localizationPaths)
    {
        lock (Sync)
        {
            return Catalog?.FilterModdedLocalizationTables(localizationPaths, GetVisualSelections()) ??
                   localizationPaths.ToArray();
        }
    }

    private static void CaptureLocalizationState(
        LocManager manager,
        string signature,
        string language)
    {
        if (LocTablesField?.GetValue(manager) is not Dictionary<string, LocTable> tables)
        {
            return;
        }

        LocalizationStateCache[LocalizationCacheKey(signature, language)] = new LocalizationCacheState(
            tables,
            manager.OverridesActive,
            manager.ValidationErrors.ToList());
    }

    private static string LocalizationCacheKey(string signature, string language) =>
        language + "\n" + signature;

    private static void MountCardOverlay(IReadOnlySet<string> groups)
    {
        var catalog = Catalog ?? throw new InvalidOperationException("皮肤目录尚未初始化。");
        var priorityStacks = catalog.CardGroups.ToDictionary(
            group => group.Id,
            group => (IReadOnlyList<string>)GetCardPriorityEntriesInternal(group)
                .Where(entry => entry.Enabled)
                .Select(entry => entry.OptionId)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var files = catalog.BuildCardOverlay(Config.Selections, priorityStacks, groups);
        if (files.Count == 0)
        {
            return;
        }

        MountArchiveOverlay(files, "cards", "Godot 拒绝加载生成的卡牌皮肤资源包。");
        // The baseline card pack intentionally reclaims canonical source/import/remap paths.
        // Cached binary AtlasTexture overlays must therefore reacquire only the bridge paths
        // they actually depend on when the corresponding skin is requested again.
        CardCanonicalResourceOwners.Reset();
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
            overlayPath = TryGetCompleteSourceArchive(files);
            if (overlayPath == null)
            {
                overlayPath = System.IO.Path.Combine(
                    OS.GetUserDataDir(),
                    $"sts2_skin_overlay_{_sessionId}_{++_overlayGeneration:D3}_{category}.pck");
                var sources = files.ToDictionary(
                    pair => pair.Key,
                    pair => (pair.Value.Archive, pair.Value.Path),
                    StringComparer.OrdinalIgnoreCase);
                PckArchive.WriteFromArchives(overlayPath, sources);
            }

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

    private static string? TryGetCompleteSourceArchive(
        IReadOnlyDictionary<string, ResourceFile> files)
    {
        var archive = files.Values.FirstOrDefault()?.Archive;
        if (archive == null ||
            files.Values.Any(file => !ReferenceEquals(file.Archive, archive)) ||
            !File.Exists(archive.Path))
        {
            return null;
        }

        var archivePaths = archive.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (files.Count != archivePaths.Count ||
            files.Any(pair =>
                !ReferenceEquals(pair.Value.Archive, archive) ||
                !pair.Key.Equals(pair.Value.Path, StringComparison.OrdinalIgnoreCase)) ||
            !archivePaths.SetEquals(files.Keys))
        {
            return null;
        }

        // Mounting the original archive is equivalent to mounting an exact byte-for-byte copy.
        // Baseline overlays still restore any canonical paths after deselection; private provider
        // namespaces are inert once their DLL callbacks are deactivated.
        return archive.Path;
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

    private sealed record CardLookup(
        string GroupId,
        string CardType,
        CardSkinGroup? Group,
        IReadOnlyList<CardSkinOption> Options,
        IReadOnlyDictionary<string, CardOptionLookup> OptionsById);

    private sealed record CardOptionLookup(
        CardSkinOption Option,
        IReadOnlyList<string> MatchedAssetPaths);

    private sealed record CardCoverageState(
        int TotalCards,
        IReadOnlyDictionary<string, int> ByOption);

    private sealed record CardPortraitRequest(
        string GroupId,
        string Selection,
        string ResourcePath,
        string CacheKey,
        bool UseSelectedProvider,
        bool WrapAtlas)
    {
        public string OverlayKey =>
            $"{GroupId}\n{Selection}\n{(UseSelectedProvider ? "provider" : "base")}";
    }

    private sealed record PreparedRuntimeOverlay(
        string Key,
        string AliasToken,
        string? OverlayPath,
        IReadOnlyDictionary<string, string> ResourcePaths,
        IReadOnlySet<string> CanonicalDependencyPaths,
        IReadOnlySet<string> RestoreGroups,
        int FileCount,
        long FileSize);

    private sealed record RuntimeResourceBundleState(
        Dictionary<string, Resource> Resources);

    private sealed class IsolatedCardOverlayState(string ownerId)
    {
        public string OwnerId { get; } = ownerId;
        public string? AliasToken { get; set; }
        public Dictionary<string, string> ResourcePaths { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> UnavailablePaths { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<string> OverlayPaths { get; } = [];
        public HashSet<string> MountedFilePaths { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> CanonicalRedirectPaths { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public bool CanReuseExternalDependencies { get; set; } = true;
    }

    private sealed record LocalizationCacheState(
        Dictionary<string, LocTable> Tables,
        bool OverridesActive,
        IReadOnlyList<LocValidationError> ValidationErrors);

    private static void AppendSignaturePart(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static void ClearRuntimeResourceCache(string groupId)
    {
        var prefix = groupId + "\n";
        RestoreCachedRelicTexturesToBaseline(groupId, prefix);
        foreach (var key in RuntimeResourceCache.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            RuntimeResourceCache.Remove(key);
        }
        foreach (var key in PreparedRuntimeOverlays.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            PreparedRuntimeOverlays.Remove(key);
            RuntimeResourceBundles.Remove(key);
        }
    }

    private static void RestoreCachedRelicTexturesToBaseline(string groupId, string cachePrefix)
    {
        if (Catalog == null)
        {
            return;
        }

        var restored = 0;
        foreach (var pair in RuntimeResourceCache.Where(pair =>
                     pair.Key.StartsWith(cachePrefix, StringComparison.OrdinalIgnoreCase) &&
                     pair.Value is AtlasTexture))
        {
            var pathStart = pair.Key.IndexOf('\n', cachePrefix.Length);
            if (pathStart < 0 || pathStart + 1 >= pair.Key.Length)
            {
                continue;
            }

            var resourcePath = pair.Key[(pathStart + 1)..];
            if (!SkinCatalog.IsRelicAtlasSpritePath(resourcePath) ||
                pair.Value is not AtlasTexture existing)
            {
                continue;
            }

            try
            {
                if (GetBaselineRelicIcon(Catalog, resourcePath) is not AtlasTexture baseline)
                {
                    continue;
                }

                // TextureRect nodes can keep the old AtlasTexture object after the character
                // selection changes. Mutate that still-referenced object back to the game's
                // atlas and coordinates before dropping our cache entry; merely remounting the
                // original PCK cannot update an already constructed Godot Resource.
                existing.Atlas = baseline.Atlas;
                existing.Region = baseline.Region;
                existing.Margin = baseline.Margin;
                existing.FilterClip = baseline.FilterClip;
                existing.EmitChanged();
                restored++;
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"回退 {resourcePath} 的遗物图集缓存失败，将在下次创建图标时恢复：" +
                    exception.GetBaseException().Message);
            }
        }

        if (restored > 0)
        {
            ModLog.Info($"已将 {groupId} 的 {restored} 个存量遗物图标恢复到游戏图集。");
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
            ExternalCardProviderIdentityPaths.Remove(key);
        }

        FailedCardPortraitRequests.RemoveWhere(key =>
            key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string CardSelectionKey(string groupId) => "cards:" + groupId;

    private static string IndividualCardSelectionKey(CardModel card) =>
        "cards:item:" + card.Id.ToString().ToLowerInvariant();

    private static string? NormalizeCardSkinPresetName(string name)
    {
        var normalized = name.Trim();
        if (normalized.Length == 0 || normalized.Any(char.IsControl))
        {
            LastError = "卡图预设名称不能为空。";
            return null;
        }

        if (normalized.Length > CardSkinPresetNameMaxLength)
        {
            LastError = $"卡图预设名称不能超过 {CardSkinPresetNameMaxLength} 个字符。";
            return null;
        }

        return normalized;
    }

    private static int FindCardSkinPresetIndex(string groupId, string name) =>
        Config.CardSkinPresets.FindIndex(preset =>
            preset.CategoryId?.Equals(groupId, StringComparison.OrdinalIgnoreCase) == true &&
            preset.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsKnownCardGroup(string groupId) =>
        !string.IsNullOrWhiteSpace(groupId) &&
        Catalog?.CardGroups.Any(group => group.Id.Equals(
            groupId,
            StringComparison.OrdinalIgnoreCase)) == true;

    private static string? GetActiveCardSkinPreset(string groupId) =>
        Config.ActiveCardSkinPresets.GetValueOrDefault(groupId);

    private static void SetActiveCardSkinPreset(string groupId, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Config.ActiveCardSkinPresets.Remove(groupId);
            return;
        }

        Config.ActiveCardSkinPresets[groupId] = name;
    }

    private static CardSkinPreset CaptureCurrentCardSkinPreset(string groupId, string name)
    {
        var group = Catalog?.CardGroups.FirstOrDefault(candidate => candidate.Id.Equals(
            groupId,
            StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            throw new InvalidOperationException($"未知的卡牌皮肤分类：{groupId}");
        }

        GetCardPriorityEntriesInternal(group);

        return new CardSkinPreset
        {
            Name = name,
            CategoryId = group.Id,
            CardSkinPriorities = new Dictionary<string, List<CardSkinPriorityEntry>>(
                StringComparer.OrdinalIgnoreCase)
            {
                [group.Id] = Config.CardSkinPriorities.TryGetValue(group.Id, out var entries)
                    ? entries.ToList()
                    : []
            },
            Selections = CaptureCardSelectionsForGroup(group.Id)
        };
    }

    private static CardSkinPreset CloneCardSkinPreset(CardSkinPreset preset) => new()
    {
        Name = preset.Name,
        CategoryId = preset.CategoryId,
        CardSkinPriorities = CloneCardSkinPriorities(preset.CardSkinPriorities),
        Selections = new Dictionary<string, string>(
            preset.Selections,
            StringComparer.OrdinalIgnoreCase)
    };

    private static Dictionary<string, List<CardSkinPriorityEntry>> CloneCardSkinPriorities(
        IReadOnlyDictionary<string, List<CardSkinPriorityEntry>> priorities) =>
        priorities.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> CaptureCardSelections() =>
        Config.Selections
            .Where(pair => pair.Key.StartsWith("cards:", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> CaptureCardSelectionsForGroup(string groupId) =>
        CaptureCardSelections()
            .Where(pair => CardSelectionBelongsToGroup(pair.Key, groupId))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

    private static void ReplaceCardSelections(IReadOnlyDictionary<string, string> selections)
    {
        foreach (var key in Config.Selections.Keys
                     .Where(key => key.StartsWith("cards:", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            Config.Selections.Remove(key);
        }

        foreach (var pair in selections.Where(pair =>
                     pair.Key.StartsWith("cards:", StringComparison.OrdinalIgnoreCase)))
        {
            Config.Selections[pair.Key] = pair.Value;
        }
    }

    private static void ReplaceCardSelectionsForGroup(
        string groupId,
        IReadOnlyDictionary<string, string> selections)
    {
        foreach (var key in Config.Selections.Keys
                     .Where(key => CardSelectionBelongsToGroup(key, groupId))
                     .ToArray())
        {
            Config.Selections.Remove(key);
        }

        foreach (var pair in selections.Where(pair =>
                     pair.Key.StartsWith("cards:", StringComparison.OrdinalIgnoreCase) &&
                     CardSelectionBelongsToGroup(pair.Key, groupId)))
        {
            Config.Selections[pair.Key] = pair.Value;
        }
    }

    private static bool CardSelectionBelongsToGroup(string key, string groupId)
    {
        if (key.Equals(CardSelectionKey(groupId), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string prefix = "cards:item:";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ModelDb.AllCards.Any(card =>
            IndividualCardSelectionKey(card).Equals(key, StringComparison.OrdinalIgnoreCase) &&
            GetCardLookup(card).GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase));
    }

    private static void MigrateLegacyCardSkinPresets()
    {
        if (Catalog == null || Catalog.CardGroups.Count == 0)
        {
            if (Catalog?.CardGroups.Count == 0 && Config.CardSkinPresets.Any(preset =>
                    string.IsNullOrWhiteSpace(preset.CategoryId)))
            {
                ModLog.Warn("当前没有可用的卡牌皮肤分类，暂不拆分旧版卡图预设，以免丢失设置。");
            }

            return;
        }

        var legacyPresets = Config.CardSkinPresets
            .Where(preset => string.IsNullOrWhiteSpace(preset.CategoryId))
            .ToArray();
        if (legacyPresets.Length == 0)
        {
            return;
        }

        var migrated = Config.CardSkinPresets
            .Where(preset => !string.IsNullOrWhiteSpace(preset.CategoryId))
            .ToList();
        var migratedCount = 0;
        foreach (var legacy in legacyPresets)
        {
            foreach (var group in Catalog.CardGroups)
            {
                var name = BuildMigratedPresetName(group.DisplayName, legacy.Name);
                var disambiguator = 2;
                while (migrated.Any(preset =>
                           preset.CategoryId?.Equals(group.Id, StringComparison.OrdinalIgnoreCase) == true &&
                           preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    name = BuildMigratedPresetName(
                        group.DisplayName,
                        $"{legacy.Name}-{disambiguator++}");
                }

                var priority = legacy.CardSkinPriorities.TryGetValue(group.Id, out var entries)
                    ? entries.ToList()
                    : GetCardPriorityEntriesInternal(group).ToList();
                migrated.Add(new CardSkinPreset
                {
                    Name = name,
                    CategoryId = group.Id,
                    CardSkinPriorities = new Dictionary<string, List<CardSkinPriorityEntry>>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [group.Id] = priority
                    },
                    Selections = legacy.Selections
                        .Where(pair => CardSelectionBelongsToGroup(pair.Key, group.Id))
                        .ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.OrdinalIgnoreCase)
                });
                migratedCount++;

                if (legacy.Name.Equals(
                        Config.ActiveCardSkinPreset,
                        StringComparison.OrdinalIgnoreCase))
                {
                    SetActiveCardSkinPreset(group.Id, name);
                }
            }
        }

        Config.CardSkinPresets = migrated;
        // The old value represented a whole-deck preset. Once split, only the per-category map
        // is authoritative and the legacy value must not make another category appear active.
        Config.ActiveCardSkinPreset = null;
        ModLog.Info($"已将 {legacyPresets.Length} 个整套卡组预设拆分为 {migratedCount} 个分类预设。");
    }

    private static string BuildMigratedPresetName(string categoryName, string presetName)
    {
        var prefix = string.IsNullOrWhiteSpace(categoryName) ? "卡牌" : categoryName.Trim();
        // Migration names intentionally keep the complete original name. The 40-character input
        // limit applies to newly created presets; trimming here would make two old presets
        // indistinguishable and could silently discard one during collision handling.
        return $"{prefix}-{presetName}";
    }

    private static string RuntimeResourceKey(string groupId, string resourcePath) =>
        RuntimeResourceKey(groupId, GetVisualSelection(groupId), resourcePath);

    private static string RuntimeResourceKey(
        string groupId,
        string selection,
        string resourcePath) =>
        groupId + "\n" + selection + "\n" + resourcePath;

    private static string RuntimeOverlayKey(
        string groupId,
        string selection,
        IReadOnlyCollection<string> resourcePaths,
        bool includeProviderDependencies,
        bool reuseMountedPrivateDependencies,
        bool isolateRelicCanonicalPaths = false) =>
        groupId + "\n" + selection + "\n" + includeProviderDependencies + "\n" +
        reuseMountedPrivateDependencies + "\n" + isolateRelicCanonicalPaths + "\n" +
        string.Join("\n", resourcePaths);

    private static string[] CharacterSelectResourcePaths(string characterId) =>
    [
        $"res://scenes/screens/char_select/char_select_bg_{characterId}.tscn",
        $"res://images/packed/character_select/char_select_{characterId}.png",
        $"res://images/packed/character_select/char_select_{characterId}_locked.png"
    ];

    private static string PreparedRuntimeOverlayDirectory() =>
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Gurio.SkinChanger",
            "runtime",
            _sessionId);

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

    private static CharacterSkinComposition CloneCharacterSkinComposition(
        CharacterSkinComposition source) =>
        new()
        {
            Id = source.Id,
            GroupId = source.GroupId,
            Name = source.Name,
            SourceOptionIds = source.SourceOptionIds.ToList(),
            HideSources = source.HideSources
        };

    private static void MigrateLegacyCharacterIconSelections()
    {
        if (Catalog == null || Config.CharacterIconSelections.Count == 0)
        {
            return;
        }

        foreach (var pair in Config.CharacterIconSelections.ToArray())
        {
            Config.CharacterIconSelections.Remove(pair.Key);
            if (pair.Value.Equals(
                    LegacyFollowCharacterSkinIconSelectionId,
                    StringComparison.OrdinalIgnoreCase) ||
                pair.Value.Equals(
                    SkinCatalog.BaseOptionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var iconOptionId = Catalog.ResolveStoredVisualSelectionId(pair.Key, pair.Value);
            var rawOptions = Catalog.GetRawCharacterOptions(pair.Key);
            if (rawOptions.All(option => !option.Id.Equals(
                    iconOptionId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                ModLog.Info($"旧头像来源 {pair.Key}/{pair.Value} 已不存在，已忽略。 ");
                continue;
            }

            var sources = new List<string> { iconOptionId };
            var selected = Config.GetSelection(pair.Key);
            if (!selected.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase))
            {
                sources.AddRange(Catalog.GetCompositionSourceOptionIds(pair.Key, selected));
            }
            sources = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var sourceNames = sources.Select(sourceId =>
                    rawOptions.FirstOrDefault(option => option.Id.Equals(
                        sourceId,
                        StringComparison.OrdinalIgnoreCase))?.Name)
                .Where(sourceName => !string.IsNullOrWhiteSpace(sourceName))
                .Cast<string>();
            var name = CharacterSkinCompositionPolicy.UniqueName(
                string.Join(" + ", sourceNames),
                Config.CharacterSkinCompositions
                    .Where(composition => composition.GroupId.Equals(
                        pair.Key,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(composition => composition.Name),
                ModLocalization.Get(ModText.CombinedSkinDefaultName));
            var composition = new CharacterSkinComposition
            {
                Id = CharacterSkinCompositionPolicy.CreateId(),
                GroupId = pair.Key,
                Name = name,
                SourceOptionIds = sources
            };
            Config.CharacterSkinCompositions.Add(composition);
            Config.Selections[pair.Key] = composition.Id;
            ModLog.Info($"已将 {pair.Key} 的旧头像来源迁移为合并皮肤“{name}”。");
        }
    }

    private static void SanitizeSelections()
    {
        Config.CharacterSkinCompositions = CharacterSkinCompositionPolicy.Normalize(
            Config.CharacterSkinCompositions);
        Catalog!.SynchronizeCharacterSkinCompositions(Config.CharacterSkinCompositions);
        MigrateLegacyCharacterIconSelections();
        Catalog.SynchronizeCharacterSkinCompositions(Config.CharacterSkinCompositions);

        var migratedVisualSelectionCount = 0;
        foreach (var group in Catalog!.Groups)
        {
            if (!Config.Selections.TryGetValue(group.Id, out var storedSelection))
            {
                continue;
            }

            var resolvedSelection = Catalog.ResolveStoredVisualSelectionId(
                group.Id,
                storedSelection);
            if (resolvedSelection.Equals(storedSelection, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Config.Selections[group.Id] = resolvedSelection;
            migratedVisualSelectionCount++;
        }

        if (migratedVisualSelectionCount > 0)
        {
            ModLog.Info(
                $"已把 {migratedVisualSelectionCount} 项旧外观设置迁移到具体的同 ID 差分包。");
        }

        if (Config.VisualSelectionDefaultsVersion < 1)
        {
            // The old default picked the first discovered Mod option for every new group. A
            // multi-monster DLL could therefore be saved as a misleading partial bundle: the UI
            // said that provider was selected, but its runtime was intentionally not started.
            // Only clear those legacy partial selections. Complete and independently saved
            // selections remain untouched.
            var legacyScopedProviders = Catalog!.Groups
                .Select(group => Config.GetSelection(group.Id))
                .Where(Catalog.ProviderUsesScopedMonsterRuntime)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var providerId in legacyScopedProviders)
            {
                var ownedGroups = Catalog.GetScopedMonsterRuntimeProviderGroups(providerId);
                var selectedGroups = ownedGroups.Where(groupId =>
                    Config.GetSelection(groupId).Equals(providerId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (selectedGroups.Length == 0 || selectedGroups.Length == ownedGroups.Count)
                {
                    continue;
                }

                foreach (var groupId in selectedGroups)
                {
                    Config.Selections[groupId] = SkinCatalog.BaseOptionId;
                }

                ModLog.Info(
                    $"已清理 {providerId} 的 {selectedGroups.Length} 个旧版自动外观选择；" +
                    "这些组现在默认使用游戏原版，玩家重新选择后可按怪物独立生效。");
            }

            Config.VisualSelectionDefaultsVersion = 1;
        }

        foreach (var group in Catalog!.Groups)
        {
            if (!Config.Selections.ContainsKey(group.Id))
            {
                Config.Selections[group.Id] = SkinCatalog.BaseOptionId;
            }
        }

        SanitizeMonsterSkinPriorities();

        // A full-runtime provider is safe only as one coherent selection transaction. Never leave
        // a partial provider displayed as selected while its callbacks are deliberately inactive,
        // and never complete it by silently overwriting another explicit group choice.
        var incompleteProviders = Catalog.Groups
            .Select(group => Config.GetSelection(group.Id))
            .Select(Catalog.ResolveVisualProviderId)
            .Where(Catalog.ProviderUsesFullRuntime)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(providerId =>
                !Catalog.IsFullRuntimeProviderFullySelected(providerId, Config.Selections))
            .ToArray();
        foreach (var providerId in incompleteProviders)
        {
            var ownedGroups = Catalog.GetFullRuntimeProviderGroups(providerId);
            var resetCount = 0;
            foreach (var ownedGroupId in ownedGroups.Where(ownedGroupId =>
                         Catalog.SelectionUsesVisualProvider(
                             ownedGroupId,
                             Config.GetSelection(ownedGroupId),
                             providerId)))
            {
                Config.Selections[ownedGroupId] = SkinCatalog.BaseOptionId;
                resetCount++;
            }

            if (resetCount > 0)
            {
                ModLog.Info(
                    $"已将 {providerId} 的 {resetCount} 个不完整联动选择恢复为游戏原版，" +
                    "避免界面显示已选但实际运行时未启用。");
            }
        }

        SanitizeCardSelections();
        SanitizeVisualProviderPriority();
    }

    private static void UpdateVisualProviderPriority(string groupId, string optionId)
    {
        var requestedProviderIds = Catalog!.GetSelectionProviderIds(groupId, optionId);
        SanitizeVisualProviderPriority();
        if (requestedProviderIds.Count == 0)
        {
            return;
        }

        Config.VisualProviderPriority.RemoveAll(providerId =>
            requestedProviderIds.Contains(providerId, StringComparer.OrdinalIgnoreCase));
        Config.VisualProviderPriority.AddRange(
            CharacterSkinCompositionPolicy.BuildProviderPriority(requestedProviderIds));
    }

    private static void SanitizeVisualProviderPriority()
    {
        var selectedProviderIds = Catalog!.Groups
            .SelectMany(group => Catalog.GetSelectionProviderIds(
                group.Id,
                Config.GetSelection(group.Id)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Config.VisualProviderPriority = Config.VisualProviderPriority
            .Select(providerId => Catalog.ResolveStoredProviderId(
                providerId,
                selectedProviderIds))
            .Where(selectedProviderIds.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var providerId in selectedProviderIds
                     .OrderBy(providerId => providerId, StringComparer.OrdinalIgnoreCase))
        {
            if (!Config.VisualProviderPriority.Contains(
                    providerId,
                    StringComparer.OrdinalIgnoreCase))
            {
                Config.VisualProviderPriority.Insert(0, providerId);
            }
        }
    }

    private static void SanitizeCardSelections()
    {
        var enableAllByDefault = Config.CardPriorityDefaultsVersion < 1;
        foreach (var group in Catalog!.CardGroups)
        {
            GetCardPriorityEntriesInternal(group, enableAllByDefault);
        }

        Config.CardPriorityDefaultsVersion = 1;
    }

    private static void MigrateStoredCardSelections()
    {
        var catalog = Catalog;
        if (catalog == null)
        {
            return;
        }

        var groupsById = catalog.CardGroups.ToDictionary(
            group => group.Id,
            StringComparer.OrdinalIgnoreCase);
        var individualGroupIds = ModelDb.AllCards
            .GroupBy(IndividualCardSelectionKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => GetCardLookup(group.First()).GroupId,
                StringComparer.OrdinalIgnoreCase);
        var migratedCount = 0;
        foreach (var pair in Config.Selections
                     .Where(pair => pair.Key.StartsWith("cards:", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            string? groupId = null;
            if (individualGroupIds.TryGetValue(pair.Key, out var individualGroupId))
            {
                groupId = individualGroupId;
            }
            else if (!pair.Key.StartsWith("cards:item:", StringComparison.OrdinalIgnoreCase))
            {
                var categoryId = pair.Key["cards:".Length..];
                if (groupsById.ContainsKey(categoryId))
                {
                    groupId = categoryId;
                }
            }

            if (groupId == null ||
                pair.Value.Equals(InheritCardSelectionId, StringComparison.OrdinalIgnoreCase) ||
                pair.Value.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolvedSelection = catalog.ResolveStoredCardSelectionId(groupId, pair.Value);
            if (resolvedSelection.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Config.Selections[pair.Key] = resolvedSelection;
            migratedCount++;
        }

        if (migratedCount > 0)
        {
            ModLog.Info($"已把 {migratedCount} 项旧单卡设置迁移到具体的同 ID 差分包。");
        }
    }

    private static void SanitizeCardSkinPresets()
    {
        var catalog = Catalog;
        if (catalog == null)
        {
            return;
        }

        var selectionKeysByGroup = catalog.CardGroups.ToDictionary(
            group => group.Id,
            group => new HashSet<string>(
                [CardSelectionKey(group.Id)],
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        foreach (var card in ModelDb.AllCards)
        {
            var lookup = GetCardLookup(card);
            if (selectionKeysByGroup.TryGetValue(lookup.GroupId, out var keys))
            {
                keys.Add(IndividualCardSelectionKey(card));
            }
        }

        foreach (var preset in Config.CardSkinPresets.Where(preset =>
                     !string.IsNullOrWhiteSpace(preset.CategoryId)))
        {
            var group = catalog.CardGroups.FirstOrDefault(candidate => candidate.Id.Equals(
                preset.CategoryId,
                StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                continue;
            }

            if (preset.CardSkinPriorities.TryGetValue(group.Id, out var priorityEntries))
            {
                preset.CardSkinPriorities[group.Id] = priorityEntries
                    .Select(entry => entry with
                    {
                        OptionId = catalog.ResolveStoredCardSelectionId(
                            group.Id,
                            entry.OptionId)
                    })
                    .DistinctBy(entry => entry.OptionId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var selectionKeys = selectionKeysByGroup[group.Id];
            foreach (var pair in preset.Selections
                         .Where(pair => selectionKeys.Contains(pair.Key))
                         .ToArray())
            {
                if (pair.Value.Equals(InheritCardSelectionId, StringComparison.OrdinalIgnoreCase) ||
                    pair.Value.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                preset.Selections[pair.Key] = catalog.ResolveStoredCardSelectionId(
                    group.Id,
                    pair.Value);
            }
        }
    }

    private static void SanitizeMonsterSkinPriorities()
    {
        var knownGroupIds = Catalog!.Groups
            .Select(group => group.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Config.MonsterSkinCategoryGroups = Config.MonsterSkinCategoryGroups
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .Where(knownGroupIds.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
        var knownCategoryIds = Config.MonsterSkinCategoryGroups.Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var categorizedGroupIds = Config.MonsterSkinCategoryGroups.Values
            .SelectMany(groupIds => groupIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Config.MonsterGroupsWithManualSelection = Config.MonsterGroupsWithManualSelection
            .Where(knownGroupIds.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Config.MonsterGroupsFollowingCategory = Config.MonsterGroupsFollowingCategory
            .Where(categorizedGroupIds.Contains)
            .Where(groupId => !Config.MonsterGroupsWithManualSelection.Contains(
                groupId,
                StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (Config.MonsterPriorityDefaultsVersion < 1)
        {
            foreach (var groupId in categorizedGroupIds.Where(groupId =>
                         !Config.MonsterGroupsFollowingCategory.Contains(
                             groupId,
                             StringComparer.OrdinalIgnoreCase) &&
                         !Config.MonsterGroupsWithManualSelection.Contains(
                             groupId,
                             StringComparer.OrdinalIgnoreCase) &&
                         !Config.GetSelection(groupId).Equals(
                             SkinCatalog.BaseOptionId,
                             StringComparison.OrdinalIgnoreCase)))
            {
                Config.MonsterGroupsWithManualSelection.Add(groupId);
            }

            Config.MonsterPriorityDefaultsVersion = 1;
        }

        if (Config.MonsterPriorityDefaultsVersion < 2)
        {
            foreach (var categoryId in knownCategoryIds)
            {
                var entries = GetMonsterPriorityEntriesInternal(categoryId).ToList();
                if (entries.Count > 0 && entries.All(entry => !entry.Enabled))
                {
                    Config.MonsterSkinPriorities[categoryId] = entries
                        .Select(entry => entry with { Enabled = true })
                        .ToList();
                }
            }

            Config.MonsterPriorityDefaultsVersion = 2;
        }

        Config.EnabledMonsterSkinPriorityCategories.Clear();
        Config.MonsterGroupsFollowingCategory = categorizedGroupIds
            .Where(groupId => !Config.MonsterGroupsWithManualSelection.Contains(
                groupId,
                StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var categoryId in knownCategoryIds)
        {
            _ = GetMonsterPriorityEntriesInternal(categoryId);
            _ = ApplyMonsterCategoryPriorityToSelections(categoryId);
        }
    }

    private static IReadOnlyList<MonsterSkinPriorityEntry> GetMonsterPriorityEntriesInternal(
        string categoryId)
    {
        var options = GetMonsterCategoryOptionsInternal(categoryId);
        var knownIds = options
            .Select(option => option.OptionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var configuredEntries = Config.MonsterSkinPriorities.TryGetValue(categoryId, out var configured)
            ? configured
            : [];
        configuredEntries = configuredEntries
            .Select(entry => entry with
            {
                OptionId = ResolveStoredMonsterPriorityOptionId(categoryId, entry.OptionId)
            })
            .DistinctBy(entry => entry.OptionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var entries = configuredEntries
            .Where(entry => knownIds.Contains(entry.OptionId))
            .DistinctBy(entry => entry.OptionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var option in options.Where(option => entries.All(entry =>
                     !entry.OptionId.Equals(option.OptionId, StringComparison.OrdinalIgnoreCase))))
        {
            entries.Add(new MonsterSkinPriorityEntry(option.OptionId, Enabled: true));
        }

        Config.MonsterSkinPriorities[categoryId] = MergeKnownMonsterPriorityEntries(
            configuredEntries,
            entries,
            knownIds);
        return entries;
    }

    private static string ResolveStoredMonsterPriorityOptionId(
        string categoryId,
        string optionId)
    {
        var catalog = Catalog;
        if (catalog == null ||
            !Config.MonsterSkinCategoryGroups.TryGetValue(categoryId, out var groupIds))
        {
            return optionId;
        }

        foreach (var groupId in groupIds)
        {
            var resolved = catalog.ResolveStoredVisualSelectionId(groupId, optionId);
            if (!resolved.Equals(optionId, StringComparison.OrdinalIgnoreCase))
            {
                return resolved;
            }
        }

        return optionId;
    }

    private static IReadOnlyList<MonsterCategoryOptionState> GetMonsterCategoryOptionsInternal(
        string categoryId)
    {
        var catalog = Catalog;
        if (catalog == null ||
            !Config.MonsterSkinCategoryGroups.TryGetValue(categoryId, out var categoryGroupIds))
        {
            return [];
        }

        var groups = categoryGroupIds
            .Select(groupId => catalog.Groups.FirstOrDefault(group =>
                group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase)))
            .Where(group => group != null)
            .Cast<SkinGroup>()
            .ToArray();
        var optionOrder = groups
            .SelectMany(group => group.Options)
            .DistinctBy(option => option.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return optionOrder.Select(option => new MonsterCategoryOptionState(
                option.Id,
                option.Name,
                groups.Count(group => group.Options.Any(candidate =>
                    candidate.Id.Equals(option.Id, StringComparison.OrdinalIgnoreCase))),
                groups.Length))
            .ToArray();
    }

    private static HashSet<string> ApplyMonsterCategoryPriorityToSelections(string categoryId)
    {
        var catalog = Catalog!;
        if (!Config.MonsterSkinCategoryGroups.TryGetValue(categoryId, out var categoryGroupIds))
        {
            return [];
        }

        var previousSelections = new Dictionary<string, string>(
            Config.Selections,
            StringComparer.OrdinalIgnoreCase);
        var workingSelections = new Dictionary<string, string>(
            Config.Selections,
            StringComparer.OrdinalIgnoreCase);
        var entries = GetMonsterPriorityEntriesInternal(categoryId);
        var managedGroupIds = categoryGroupIds
            .Where(groupId => Config.MonsterGroupsFollowingCategory.Contains(
                groupId,
                StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedFullProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> desiredSelections;
        while (true)
        {
            desiredSelections = managedGroupIds.ToDictionary(
                groupId => groupId,
                groupId =>
                {
                    var group = catalog.Groups.First(candidate =>
                        candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
                    return entries.FirstOrDefault(entry =>
                        entry.Enabled &&
                        !excludedFullProviders.Contains(entry.OptionId) &&
                        group.Options.Any(option => option.Id.Equals(
                            entry.OptionId,
                            StringComparison.OrdinalIgnoreCase)))?.OptionId ??
                        SkinCatalog.BaseOptionId;
                },
                StringComparer.OrdinalIgnoreCase);
            var invalidFullProviders = desiredSelections.Values
                .Where(catalog.ProviderUsesFullRuntime)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(providerId => catalog.GetFullRuntimeProviderGroups(providerId).Any(groupId =>
                    !managedGroupIds.Contains(groupId) ||
                    !desiredSelections.GetValueOrDefault(groupId, SkinCatalog.BaseOptionId)
                        .Equals(providerId, StringComparison.OrdinalIgnoreCase)))
                .Where(excludedFullProviders.Add)
                .ToArray();
            if (invalidFullProviders.Length == 0)
            {
                break;
            }
        }

        var appliedFullProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var groupId in managedGroupIds)
        {
            var desiredOptionId = desiredSelections[groupId];
            if (catalog.ProviderUsesFullRuntime(desiredOptionId) &&
                !appliedFullProviders.Add(desiredOptionId))
            {
                continue;
            }

            foreach (var update in catalog.BuildVisualSelectionTransaction(
                         groupId,
                         desiredOptionId,
                         workingSelections))
            {
                workingSelections[update.Key] = update.Value;
            }
        }

        var affectedGroups = workingSelections.Keys
            .Union(previousSelections.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(groupId => !string.Equals(
                workingSelections.GetValueOrDefault(groupId),
                previousSelections.GetValueOrDefault(groupId),
                StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Config.Selections = workingSelections;
        return affectedGroups;
    }

    private static List<MonsterSkinPriorityEntry> MergeKnownMonsterPriorityEntries(
        IReadOnlyList<MonsterSkinPriorityEntry> existingEntries,
        IReadOnlyList<MonsterSkinPriorityEntry> knownEntries,
        IReadOnlySet<string> knownIds)
    {
        var pendingKnown = new Queue<MonsterSkinPriorityEntry>(knownEntries);
        var result = new List<MonsterSkinPriorityEntry>(
            Math.Max(existingEntries.Count, knownEntries.Count));
        var preservedUnknownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in existingEntries)
        {
            if (knownIds.Contains(existing.OptionId))
            {
                if (pendingKnown.Count > 0)
                {
                    result.Add(pendingKnown.Dequeue());
                }

                continue;
            }

            if (preservedUnknownIds.Add(existing.OptionId))
            {
                result.Add(existing);
            }
        }

        while (pendingKnown.Count > 0)
        {
            result.Add(pendingKnown.Dequeue());
        }

        return result;
    }

    private static IReadOnlyList<CardSkinPriorityEntry> GetCardPriorityEntriesInternal(
        CardSkinGroup group,
        bool enableAllByDefault = false)
    {
        var knownIds = group.Options
            .Select(option => option.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<CardSkinPriorityEntry> entries;
        var configuredEntries = Config.CardSkinPriorities.TryGetValue(group.Id, out var configured)
            ? configured
            : [];
        configuredEntries = configuredEntries
            .Select(entry => entry with
            {
                OptionId = Catalog!.ResolveStoredCardSelectionId(group.Id, entry.OptionId)
            })
            .DistinctBy(entry => entry.OptionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (configuredEntries.Count > 0)
        {
            if (enableAllByDefault)
            {
                configuredEntries = configuredEntries
                    .Select(entry => entry with { Enabled = true })
                    .ToList();
            }

            entries = configuredEntries
                .Where(entry => knownIds.Contains(entry.OptionId))
                .DistinctBy(entry => entry.OptionId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var option in group.Options.Where(option => entries.All(entry =>
                         !entry.OptionId.Equals(option.Id, StringComparison.OrdinalIgnoreCase))))
            {
                entries.Add(new CardSkinPriorityEntry(option.Id, Enabled: true));
            }
        }
        else
        {
            var selectionKey = CardSelectionKey(group.Id);
            var hasLegacySelection = Config.Selections.TryGetValue(selectionKey, out var legacySelection);
            var selectedId = hasLegacySelection
                ? Catalog!.ResolveStoredCardSelectionId(group.Id, legacySelection!)
                : group.Options.FirstOrDefault()?.Id ?? SkinCatalog.BaseOptionId;
            entries = group.Options
                .OrderByDescending(option => option.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
                .Select(option => new CardSkinPriorityEntry(
                    option.Id,
                    Enabled: true))
                .ToList();
        }

        Config.CardSkinPriorities[group.Id] = MergeKnownCardPriorityEntries(
            configuredEntries,
            entries,
            knownIds);
        Config.Selections[CardSelectionKey(group.Id)] =
            entries.FirstOrDefault(entry => entry.Enabled)?.OptionId ?? SkinCatalog.BaseOptionId;
        return entries;
    }

    private static List<CardSkinPriorityEntry> MergeKnownCardPriorityEntries(
        IReadOnlyList<CardSkinPriorityEntry> existingEntries,
        IReadOnlyList<CardSkinPriorityEntry> knownEntries,
        IReadOnlySet<string> knownIds)
    {
        var pendingKnown = new Queue<CardSkinPriorityEntry>(knownEntries);
        var result = new List<CardSkinPriorityEntry>(
            Math.Max(existingEntries.Count, knownEntries.Count));
        var preservedUnknownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in existingEntries)
        {
            if (knownIds.Contains(existing.OptionId))
            {
                if (pendingKnown.Count > 0)
                {
                    result.Add(pendingKnown.Dequeue());
                }

                continue;
            }

            if (preservedUnknownIds.Add(existing.OptionId))
            {
                result.Add(existing);
            }
        }

        while (pendingKnown.Count > 0)
        {
            result.Add(pendingKnown.Dequeue());
        }

        return result;
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

    private static void CleanupPreparedRuntimeOverlayCache()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Gurio.SkinChanger",
            "runtime");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception)
            {
                ModLog.Warn($"无法清理旧角色预览缓存 {directory}：{exception.Message}");
            }
        }
    }
}

internal sealed record MountedProviderPackState(
    IReadOnlyList<string> ResourcePaths);

internal sealed record LargeProviderMountPlan(
    IReadOnlySet<string> PromotedPackPaths);

internal sealed record AncientLayeredImageTextures(
    Texture2D Character,
    Texture2D? BackgroundCover,
    Texture2D? Mask,
    Texture2D? SleepingCharacter);

internal sealed record CardPriorityOptionState(
    string OptionId,
    string Name,
    bool Enabled,
    int ColorIndex,
    int Coverage,
    int TotalCards);

internal sealed record CardSkinPresetState(
    string Name,
    bool Active);

internal sealed record MonsterPriorityOptionState(
    string OptionId,
    string Name,
    bool Enabled,
    int ColorIndex,
    int Coverage,
    int TotalMonsters);

internal sealed record MonsterCategoryOptionState(
    string OptionId,
    string Name,
    int Coverage,
    int TotalMonsters);

internal sealed record CardSkinSourceState(
    string OptionId,
    string Name,
    bool Enabled,
    int ColorIndex,
    bool IsCurrent);
