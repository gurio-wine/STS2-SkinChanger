using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using STS2SkinChanger.Core;
using STS2SkinChanger.Pck;

namespace STS2SkinChanger.Catalog;

internal sealed partial class SkinCatalog : IDisposable
{
    public const string BaseOptionId = "__base__";
    // Game ownership, not provider identity: unrelated creatures from the same skin pack
    // must remain independent. Add other actual owner/companion relationships here.
    private static readonly (string Owner, string Companion)[] CompanionGroups =
        [("necrobinder", "osty")];
    private static readonly JsonSerializerOptions CardReplacementJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly object RuntimeAncientImageCacheSync = new();
    private static readonly Dictionary<string, RuntimeAncientImageCacheEntry> RuntimeAncientImageCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ManagedScriptCountCacheSync = new();
    private static readonly Dictionary<string, ManagedScriptCountCacheEntry> ManagedScriptCountCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly PckArchive _gameArchive;
    private readonly List<PckResourceIndex> _baselineIndexes;
    private readonly List<PckResourceIndex> _cosmeticIndexes;
    private readonly List<SkinGroup> _groups;
    private readonly IReadOnlyList<CardSkinGroup> _configuredCardGroups;
    private readonly IReadOnlyList<CardSkinOption> _pckCardOptions;
    private readonly List<CardSkinGroup> _cardGroups;
    private readonly IReadOnlySet<string> _managedGodotScriptProviders;
    private readonly IReadOnlySet<string> _cosmeticLocalizationProviders;
    private readonly IReadOnlySet<string> _cosmeticLocalizationPaths;
    private readonly IReadOnlyDictionary<string, ResourceFile> _passthroughLocalizationFiles;
    private readonly IReadOnlySet<string> _fullRuntimeProviders;
    private readonly IReadOnlySet<string> _directCharacterRuntimeProviders;
    private readonly IReadOnlySet<string> _scopedMonsterRuntimeProviders;
    private readonly IReadOnlySet<string> _interactiveRuntimeProviders;
    private readonly IReadOnlySet<string> _characterAppearanceGroupIds;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _fullRuntimeProviderGroups;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _scopedMonsterRuntimeProviderGroups;
    private readonly IReadOnlyDictionary<string, string> _resourceGroupIds;
    private readonly IReadOnlyList<ProviderInstanceIdentity> _providerInstanceIdentities;
    private readonly Dictionary<string, IReadOnlyDictionary<string, ResourceFile>>
        _fullRuntimeProviderBaselineOverlays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, ResourceAsset>>
        _providerRelicAssets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlySet<string>>
        _isolatedRelicProviderPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?>
        _relicOwnerGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BaselineRelicTextureDefinition?>
        _baselineRelicTextureDefinitions = new(StringComparer.OrdinalIgnoreCase);

    private SkinCatalog(
        PckArchive gameArchive,
        List<PckResourceIndex> baselineIndexes,
        List<PckResourceIndex> cosmeticIndexes,
        IReadOnlyList<SkinGroup> groups,
        IReadOnlyList<CardSkinGroup> cardGroups,
        IReadOnlyList<CardSkinOption> pckCardOptions,
        IReadOnlyList<SkinModDescriptor> mods)
    {
        _gameArchive = gameArchive;
        _baselineIndexes = baselineIndexes;
        _cosmeticIndexes = cosmeticIndexes;
        _groups = groups.ToList();
        _characterAppearanceGroupIds = baselineIndexes
            .SelectMany(index => index.Assets.Keys)
            .Select(TryGetUnambiguousCharacterGroup)
            .Where(identity => identity != null)
            .Select(identity => identity!.Id)
            .Concat(_groups
                .Where(group => group.Options.Any(IsCharacterAppearanceOption))
                .Select(group => group.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _resourceGroupIds = _groups
            .SelectMany(group => group.Options
                .SelectMany(option => option.Assets.Keys)
                .Select(path => (Path: NormalizeTakeoverPath(path), GroupId: group.Id)))
            .GroupBy(pair => pair.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Path = group.Key,
                GroupIds = group.Select(pair => pair.GroupId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .Where(entry => entry.GroupIds.Length == 1)
            .ToDictionary(
                entry => entry.Path,
                entry => entry.GroupIds[0],
                StringComparer.OrdinalIgnoreCase);
        _configuredCardGroups = cardGroups;
        _pckCardOptions = pckCardOptions;
        _cardGroups = cardGroups.ToList();
        _providerInstanceIdentities = mods
            .Select(mod => new ProviderInstanceIdentity(
                mod.ResourceNamespaceId,
                mod.Id,
                mod.Name))
            .ToArray();
        // External image routers must be merged before runtime-bundle ownership is calculated.
        // Otherwise a DLL that independently supplies several Ancient pictures looks like one
        // inseparable multi-group runtime merely because its PCK also contains per-Ancient icons.
        AddImageRuntimeProviderOptions(mods);
        // Only the character table of an actual character appearance has a skin-owned lifetime.
        // A visual provider can also add events, cards or other gameplay text; those tables must
        // remain mounted regardless of which cosmetic option is selected.
        var characterVisualProviderIds = _groups.SelectMany(group => group.Options)
            .Where(option => !option.IsCharacterIconOnly && IsCharacterAppearanceOption(option))
            .Select(option => option.EffectiveProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var providerLocalizationFiles = cosmeticIndexes
            .SelectMany(index => index.Archive.Paths
                .Where(path => IsProviderLocalizationFile(path, index.Mod.ResourceNamespaceId))
                .Select(path => (Index: index, Path: path)))
            .ToArray();
        _cosmeticLocalizationPaths = providerLocalizationFiles
            .Where(file => characterVisualProviderIds.Contains(file.Index.Mod.Id) &&
                           IsCharacterLocalizationFile(file.Path))
            .Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _cosmeticLocalizationProviders = providerLocalizationFiles
            .Where(file => _cosmeticLocalizationPaths.Contains(file.Path))
            .Select(file => file.Index.Mod.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _passthroughLocalizationFiles = providerLocalizationFiles
            .Where(file => !_cosmeticLocalizationPaths.Contains(file.Path))
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new ResourceFile(group.Last().Index.Archive, group.Last().Path),
                StringComparer.OrdinalIgnoreCase);
        _managedGodotScriptProviders = cosmeticIndexes
            .Where(index => index.Mod.HasDll && CountManagedGodotScripts(index.Archive) > 0)
            .Select(index => index.Mod.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _interactiveRuntimeProviders = cosmeticIndexes
            .Where(index => index.Mod.HasDll &&
                            _managedGodotScriptProviders.Contains(index.Mod.Id) &&
                            ContainsInteractiveScene(index.Archive))
            .Select(index => index.Mod.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visualGroupsByProvider = _groups
            .SelectMany(group => group.Options
                .Where(option => !option.IsCharacterIconOnly)
                .Select(option =>
                    (GroupId: group.Id, ProviderId: option.EffectiveProviderId)))
            .GroupBy(pair => pair.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.GroupId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var cardProviderIds = _pckCardOptions
            .Where(option => option.Assets.Count > 0 || option.CardPresentations.Count > 0)
            .Select(option => option.ProviderId ?? option.Id)
            .Concat(_configuredCardGroups.SelectMany(group => group.Options)
                .Where(option => option.Assets.Count > 0 || option.CardPresentations.Count > 0)
                .Select(option => option.ProviderId ?? option.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var characterVisualGroupsByProvider = _groups
            .SelectMany(group => group.Options
                .Where(OptionOwnsCharacterRuntimeAssets)
                .Select(option => (GroupId: group.Id, ProviderId: option.EffectiveProviderId)))
            .GroupBy(pair => pair.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.GroupId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var singleCharacterBundleGroupsByProvider = characterVisualGroupsByProvider
            .Where(pair => pair.Value
                .Select(CharacterRuntimeFamilyId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == 1)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        var independentManagedAncientProviders = visualGroupsByProvider
            .Where(pair => pair.Value.All(KnownAncientIds.Contains))
            .Where(pair => pair.Value.All(groupId =>
                _groups.First(group => group.Id.Equals(
                        groupId,
                        StringComparison.OrdinalIgnoreCase))
                    .Options.Any(option =>
                        option.EffectiveProviderId.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) &&
                        (option.RuntimeImagePath != null ||
                         OptionUsesManagedAncientLayers(groupId, option)))))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _scopedMonsterRuntimeProviderGroups = visualGroupsByProvider
            .Where(pair => pair.Value.All(groupId =>
                _groups.First(group => group.Id.Equals(
                        groupId,
                        StringComparison.OrdinalIgnoreCase))
                    .Options.Any(option =>
                        option.EffectiveProviderId.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) &&
                        option.IsManagedMonsterRuntimeProfile)))
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.OrdinalIgnoreCase);
        _scopedMonsterRuntimeProviders = _scopedMonsterRuntimeProviderGroups.Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A direct character runtime provider exposes one independently addressable target
        // per character (for example a Spine registry keyed by Ironclad/Silent/...).  It is not
        // an inseparable all-character bundle: selecting it for one group must not rewrite every
        // other character's selection.  Keep this marker separate from IsRuntimeProvider because
        // monster and full-package runtime options use the same flag for different semantics.
        _directCharacterRuntimeProviders = _groups
            .SelectMany(group => group.Options)
            .Where(option => option.IsDirectCharacterRuntimeProvider)
            .Select(option => option.EffectiveProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Card ownership and runtime behaviour are two independent concerns. A character skin can
        // bundle selectable card art while still relying on its DLL for voice, character-select
        // animation and battle presentation nodes. Keep such a provider's character bundle active,
        // but do not turn unrelated Ancient/monster groups from a mixed card pack into a linked
        // full-runtime selection.
        _fullRuntimeProviders = cosmeticIndexes
            .Where(index => index.Mod.HasDll)
            .Select(index => index.Mod.Id)
            .Where(providerId =>
                visualGroupsByProvider.ContainsKey(providerId) &&
                !_directCharacterRuntimeProviders.Contains(providerId) &&
                !independentManagedAncientProviders.Contains(providerId) &&
                !_scopedMonsterRuntimeProviders.Contains(providerId) &&
                (!cardProviderIds.Contains(providerId) ||
                 singleCharacterBundleGroupsByProvider.ContainsKey(providerId)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _fullRuntimeProviderGroups = visualGroupsByProvider
            .Where(pair => _fullRuntimeProviders.Contains(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)(cardProviderIds.Contains(pair.Key)
                    ? singleCharacterBundleGroupsByProvider[pair.Key]
                    : pair.Value),
                StringComparer.OrdinalIgnoreCase);

        static bool OptionOwnsCharacterRuntimeAssets(SkinOption option) =>
            !option.IsCharacterIconOnly && option.Assets.Keys.Any(path =>
            {
                var canonicalPath = NormalizeTakeoverPath(path);
                return CharacterPathRegex().IsMatch(canonicalPath) ||
                       CharacterSelectSceneRegex().IsMatch(canonicalPath) ||
                       MerchantCharacterSceneRegex().IsMatch(canonicalPath) ||
                       RestSiteCharacterSceneRegex().IsMatch(canonicalPath) ||
                       CharacterSelectIconRegex().IsMatch(canonicalPath) ||
                       CharacterUiTextureRegex().IsMatch(canonicalPath) ||
                       CharacterIconSceneRegex().IsMatch(canonicalPath) ||
                       CharacterMapMarkerRegex().IsMatch(canonicalPath);
            });

        static bool OptionUsesManagedAncientLayers(string groupId, SkinOption option) =>
            option.Assets.Keys.Any(path =>
            {
                var match = AncientLayerImageRegex().Match(NormalizeTakeoverPath(path));
                return match.Success &&
                       match.Groups["id"].Value.Equals(
                           groupId,
                           StringComparison.OrdinalIgnoreCase) &&
                       match.Groups["kind"].Value.Equals(
                           "character",
                           StringComparison.OrdinalIgnoreCase);
            });

        static string CharacterRuntimeFamilyId(string groupId) =>
            groupId.EndsWith("_b", StringComparison.OrdinalIgnoreCase)
                ? groupId[..^2]
                : groupId;
    }

    public IReadOnlyList<SkinGroup> Groups => _groups;
    public IReadOnlyList<CardSkinGroup> CardGroups => _cardGroups;
    public IReadOnlyList<CardSkinOption> PckCardOptions => _pckCardOptions;

    public IReadOnlyList<SkinOption> GetRawCharacterOptions(string groupId)
    {
        var group = _groups.FirstOrDefault(candidate => candidate.Id.Equals(
            groupId,
            StringComparison.OrdinalIgnoreCase));
        return group == null || !IsCharacterAppearanceGroup(groupId)
            ? []
            : group.Options.Where(option => !option.IsComposition).ToArray();
    }

    public IReadOnlyList<string> GetCompositionSourceOptionIds(
        string groupId,
        string optionId)
    {
        if (optionId.Equals(BaseOptionId, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var option = _groups.FirstOrDefault(group => group.Id.Equals(
                groupId,
                StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(candidate => candidate.Id.Equals(
                optionId,
                StringComparison.OrdinalIgnoreCase));
        return option == null
            ? []
            : option.IsComposition
                ? option.CompositionSourceOptionIds
                : [option.Id];
    }

    public IReadOnlyList<string> GetSelectionProviderIds(
        string groupId,
        string optionId)
    {
        if (optionId.Equals(BaseOptionId, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var option = _groups.FirstOrDefault(group => group.Id.Equals(
                groupId,
                StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(candidate => candidate.Id.Equals(
                optionId,
                StringComparison.OrdinalIgnoreCase));
        return option == null
            ? []
            : option.IsComposition
                ? option.CompositionSourceProviderIds
                : [option.EffectiveProviderId];
    }

    public void SynchronizeCharacterSkinCompositions(
        IReadOnlyList<CharacterSkinComposition> compositions)
    {
        foreach (var group in _groups)
        {
            group.Options.RemoveAll(option => option.IsComposition && !option.IsSessionComposition);
        }

        foreach (var composition in compositions)
        {
            var group = _groups.FirstOrDefault(candidate => candidate.Id.Equals(
                composition.GroupId,
                StringComparison.OrdinalIgnoreCase));
            if (group == null || !IsCharacterAppearanceGroup(group.Id) ||
                !TryBuildCompositionOption(group, composition, session: false, out var option))
            {
                continue;
            }

            group.Options.Add(option);
        }

        // Rebuilding saved compositions also rebuilds their derived companion choices.
        // Otherwise editing an unrelated composition could remove the active pet option.
        foreach (var (ownerId, companionId) in CompanionGroups)
        {
            var owner = _groups.FirstOrDefault(group => group.Id.Equals(ownerId, StringComparison.OrdinalIgnoreCase));
            var companion = _groups.FirstOrDefault(group => group.Id.Equals(companionId, StringComparison.OrdinalIgnoreCase));
            if (owner == null || companion == null) continue;
            foreach (var option in owner.Options.Where(option => option.IsComposition))
                ResolveCompanionSelection(owner, option.Id, companion);
        }

        SortGroupsAndOptions();
    }

    public bool TryCreateSessionCharacterComposition(
        string groupId,
        IReadOnlyList<string> sourceOptionIds,
        out string optionId)
    {
        optionId = BaseOptionId;
        var group = _groups.FirstOrDefault(candidate => candidate.Id.Equals(
            groupId,
            StringComparison.OrdinalIgnoreCase));
        if (group == null || !IsCharacterAppearanceGroup(group.Id))
        {
            return false;
        }

        var available = CharacterSkinCompositionPolicy.ResolveAvailableSourceIds(
            sourceOptionIds,
            GetRawCharacterOptions(groupId).Select(option => option.Id));
        if (available.Count == 0)
        {
            return false;
        }
        if (available.Count == 1)
        {
            optionId = available[0];
            return true;
        }

        var sessionOptionId = CharacterSkinCompositionPolicy.CreateSessionId(
            groupId,
            available);
        optionId = sessionOptionId;
        if (group.Options.Any(option => option.Id.Equals(
                sessionOptionId,
                StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var composition = new CharacterSkinComposition
        {
            Id = optionId,
            GroupId = group.Id,
            Name = "Session composition",
            SourceOptionIds = available.ToList()
        };
        if (!TryBuildCompositionOption(group, composition, session: true, out var sessionOption))
        {
            optionId = BaseOptionId;
            return false;
        }

        group.Options.Add(sessionOption);
        return true;
    }

    public IReadOnlyList<string> ClearSessionCharacterCompositions()
    {
        var affectedGroups = _groups
            .Where(group => group.Options.RemoveAll(option =>
                option.IsSessionComposition) > 0)
            .Select(group => group.Id)
            .ToArray();
        if (affectedGroups.Length > 0)
        {
            SortGroupsAndOptions();
        }

        return affectedGroups;
    }

    public IReadOnlySet<string> CardProviderRoots => _cardGroups
        .SelectMany(group => group.Options)
        .Select(option => option.ProviderRootPath)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Cast<string>()
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool TryGetVisualProviderId(string groupId, string optionId, out string providerId)
    {
        var option = _groups.FirstOrDefault(group => group.Id.Equals(
                groupId,
                StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(candidate => candidate.Id.Equals(
                optionId,
                StringComparison.OrdinalIgnoreCase));
        providerId = option?.EffectiveProviderId ?? string.Empty;
        return option != null && !optionId.Equals(BaseOptionId, StringComparison.OrdinalIgnoreCase);
    }

    public bool TryGetVisualProviderSource(
        string groupId,
        string optionId,
        out string providerId,
        out string pckPath,
        out IReadOnlyList<string> safeResourceRoots,
        out IReadOnlyDictionary<string, VisualResourceBinding> resourceBindings)
    {
        providerId = string.Empty;
        pckPath = string.Empty;
        safeResourceRoots = [];
        resourceBindings = new Dictionary<string, VisualResourceBinding>(
            StringComparer.OrdinalIgnoreCase);
        var option = _groups.FirstOrDefault(group => group.Id.Equals(
                groupId,
                StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(candidate => candidate.Id.Equals(
                optionId,
                StringComparison.OrdinalIgnoreCase));
        if (option == null || optionId.Equals(BaseOptionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Runtime registrations can expose an option ID chosen by the skin DLL instead of the
        // manifest ID. Resolve the PCK from the actual archive that owns the option's files
        // rather than guessing a loaded Mod by name.
        var indexes = _cosmeticIndexes.Concat(_baselineIndexes.Skip(1)).ToArray();
        var index = indexes.FirstOrDefault(candidate => candidate.Mod.Id.Equals(
                        option.EffectiveProviderId,
                        StringComparison.OrdinalIgnoreCase)) ??
                    indexes.FirstOrDefault(candidate => option.Assets.Values
                        .SelectMany(asset => asset.Files)
                        .Any(file => ReferenceEquals(file.Archive, candidate.Archive)));
        if (index?.Mod.PckPath == null || !File.Exists(index.Mod.PckPath))
        {
            return false;
        }

        providerId = index.Mod.Id;
        pckPath = index.Mod.PckPath;
        resourceBindings = option.Assets
            .Select(pair => new
            {
                TargetPath = pair.Key,
                SourcePath = pair.Value.SourcePath,
                Paths = (IReadOnlyList<string>)pair.Value.Files
                    .Where(file => ReferenceEquals(file.Archive, index.Archive))
                    .Select(file => file.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .Where(binding => binding.Paths.Count > 0)
            .ToDictionary(
                binding => binding.TargetPath,
                binding => new VisualResourceBinding(binding.SourcePath, binding.Paths),
                StringComparer.OrdinalIgnoreCase);
        safeResourceRoots = resourceBindings.Values
            .SelectMany(binding => binding.Files)
            .Concat(option.ManagedMonsterScene?.Files
                .Where(file => ReferenceEquals(file.Archive, index.Archive))
                .Select(file => file.Path) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return true;
    }

    internal static bool IsSafeOnlineResourceRootForGroup(string resourcePath, string groupId)
    {
        var sourcePath = resourcePath;
        if (sourcePath.EndsWith(".remap", StringComparison.OrdinalIgnoreCase))
        {
            sourcePath = sourcePath[..^6];
        }
        else if (sourcePath.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
        {
            sourcePath = sourcePath[..^7];
        }

        var normalizedPath = NormalizeTakeoverPath(sourcePath);
        var identity = TryGetPrimaryGroup(normalizedPath);
        return identity != null && identity.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsCharacterImageResourceForProvider(string providerId, string resourcePath) =>
        TryGetPrimaryGroup(NormalizeTakeoverPath(resourcePath)) != null ||
        TryGetRuntimeProviderAsset(providerId, NormalizeTakeoverPath(resourcePath)) != null;

    public bool IsBaseGameResource(string resourcePath) => _gameArchive.Contains(resourcePath);

    public bool TryReadBaseGameResource(string resourcePath, out byte[] bytes)
    {
        if (!_gameArchive.Contains(resourcePath))
        {
            bytes = [];
            return false;
        }

        bytes = _gameArchive.ReadFile(resourcePath);
        return true;
    }

    public bool TryAddSessionVisualProvider(
        string optionId,
        string optionName,
        string pckPath,
        string expectedGroupId,
        IReadOnlyDictionary<string, VisualResourceBinding> resourceBindings,
        out string error)
    {
        error = string.Empty;
        if (_groups.SelectMany(group => group.Options).Any(option =>
                option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase)))
        {
            error = "外观选项已存在。";
            return false;
        }

        PckArchive? archive = null;
        PckResourceIndex? index = null;
        try
        {
            archive = PckArchive.Open(pckPath);
            index = PckResourceIndex.Build(
                new SkinModDescriptor(optionId, optionName, pckPath, false),
                archive,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                remapFilter: null);
            archive = null;
            SkinGroup discoveredGroup;
            if (resourceBindings.Count > 0)
            {
                var boundAssets = new Dictionary<string, ResourceAsset>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var binding in resourceBindings)
                {
                    if (!IsSafeOnlineResourceRootForGroup(binding.Key, expectedGroupId))
                    {
                        error = $"联机资源映射不属于角色 {expectedGroupId}：{binding.Key}。";
                        return false;
                    }

                    var asset = new ResourceAsset(binding.Value.SourcePath);
                    foreach (var resourcePath in binding.Value.Files)
                    {
                        if (!index.Archive.Contains(resourcePath))
                        {
                            error = $"安全资源包缺少映射文件：{resourcePath}。";
                            return false;
                        }
                        asset.AddFile(index.Archive, resourcePath);
                    }
                    if (asset.Files.Count > 0)
                    {
                        boundAssets[binding.Key] = asset;
                    }
                }

                discoveredGroup = new SkinGroup(
                    expectedGroupId,
                    TryGetPrimaryGroup(resourceBindings.Keys.FirstOrDefault() ?? string.Empty)
                        ?.DisplayName ?? expectedGroupId);
                discoveredGroup.Options.Add(new SkinOption(
                    optionId,
                    optionName,
                    boundAssets,
                    ProviderId: optionId));
            }
            else
            {
                var discovered = BuildGroups([index], _baselineIndexes);
                var matchingGroups = discovered
                    .Where(group => group.Id.Equals(expectedGroupId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matchingGroups.Length != 1 || matchingGroups[0].Options.Count != 1)
                {
                    error = "安全资源包未形成匹配的角色外观。";
                    return false;
                }
                discoveredGroup = matchingGroups[0];
            }

            var target = _groups.FirstOrDefault(group => group.Id.Equals(
                expectedGroupId,
                StringComparison.OrdinalIgnoreCase));
            if (!IsCharacterAppearanceOption(discoveredGroup.Options[0]))
            {
                error = "找不到对应的角色外观分组。";
                return false;
            }

            // A player may not have any local skin for this character. In that case the normal
            // catalog has no group yet, but an online-only provider must still be attachable.
            if (target == null)
            {
                target = new SkinGroup(discoveredGroup.Id, discoveredGroup.DisplayName);
                _groups.Add(target);
            }

            target.Options.Add(discoveredGroup.Options[0]);
            target.Options.Sort((left, right) => string.Compare(
                left.Name,
                right.Name,
                StringComparison.CurrentCultureIgnoreCase));
            _cosmeticIndexes.Add(index);
            index = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
        finally
        {
            index?.Dispose();
            archive?.Dispose();
        }
    }

    public IReadOnlyList<string> RemoveSessionVisualProvider(string optionId)
    {
        var affectedGroups = _groups
            .Where(group => group.Options.RemoveAll(option => option.Id.Equals(
                optionId,
                StringComparison.OrdinalIgnoreCase)) > 0)
            .Select(group => group.Id)
            .ToArray();
        foreach (var index in _cosmeticIndexes.Where(index => index.Mod.Id.Equals(
                     optionId,
                     StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _cosmeticIndexes.Remove(index);
            index.Dispose();
        }
        _groups.RemoveAll(group => group.Options.Count == 0);

        return affectedGroups;
    }

    public IReadOnlySet<string> GetSelectedLocalizationProviderIds(
        IReadOnlyDictionary<string, string> selections) =>
        GetSelectedLocalizationProviderPriority(selections)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetSelectedLocalizationProviderPriority(
        IReadOnlyDictionary<string, string> selections)
    {
        var providers = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups)
        {
            selections.TryGetValue(group.Id, out var selectedId);
            var selected = group.Options.FirstOrDefault(option => option.Id.Equals(
                selectedId,
                StringComparison.OrdinalIgnoreCase));
            if (selected == null)
            {
                continue;
            }

            var sources = selected.IsComposition
                ? selected.CompositionSourceOptionIds
                    .Select(sourceId => group.Options.FirstOrDefault(option =>
                        !option.IsComposition &&
                        option.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase)))
                    .Where(option => option != null)
                    .Cast<SkinOption>()
                : [selected];
            foreach (var source in sources)
            {
                if (_cosmeticLocalizationProviders.Contains(source.EffectiveProviderId) &&
                    seen.Add(source.EffectiveProviderId))
                {
                    providers.Add(source.EffectiveProviderId);
                }
            }
        }

        return providers;
    }

    public IReadOnlyList<string> FilterModdedLocalizationTables(
        IEnumerable<string> localizationPaths,
        IReadOnlyDictionary<string, string> selections)
    {
        var paths = localizationPaths.ToArray();
        if (_cosmeticLocalizationProviders.Count == 0)
        {
            return paths;
        }

        var selectedProviderPriority = GetSelectedLocalizationProviderPriority(selections);
        var selectedProviders = selectedProviderPriority.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var passthrough = paths.Where(path =>
            !_cosmeticLocalizationPaths.Contains(path) ||
            !TryGetLocalizationProviderId(path, out _));
        var selectedCosmetic = selectedProviderPriority
            .Reverse()
            .SelectMany(providerId => paths.Where(path =>
                _cosmeticLocalizationPaths.Contains(path) &&
                TryGetLocalizationProviderId(path, out var pathProviderId) &&
                pathProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)));
        return passthrough.Concat(selectedCosmetic)
            .Where(path =>
                !_cosmeticLocalizationPaths.Contains(path) ||
                !TryGetLocalizationProviderId(path, out var providerId) ||
                selectedProviders.Contains(providerId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool IsManagedCosmeticLocalizationPath(string path) =>
        _cosmeticLocalizationPaths.Contains(path);

    private static bool TryGetLocalizationProviderId(string path, out string providerId)
    {
        providerId = string.Empty;
        if (!path.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = path[6..];
        var separator = relative.IndexOf('/');
        if (separator <= 0 ||
            !relative[(separator + 1)..].StartsWith("localization/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        providerId = relative[..separator];
        return true;
    }

    public static SkinCatalog Build(string gamePckPath, IEnumerable<SkinModDescriptor> mods)
    {
        var modList = AssignProviderInstanceIdentities(mods);
        var gameArchive = PckArchive.Open(gamePckPath);
        var baselineIndexes = new List<PckResourceIndex>();
        var cosmeticIndexes = new List<PckResourceIndex>();
        try
        {
            // importedToSource 有意跨索引共享：皮肤 Mod 的 PCK 常常只携带
            // .godot/imported/ 载荷而不带 .import/.remap，需要借助游戏 PCK
            // 先登记的 remap 映射把载荷归到正确源路径。相同哈希的导入文件内容
            // 必然一致，后注册覆盖先注册在视觉上没有差别。
            var importedToSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            baselineIndexes.Add(PckResourceIndex.Build(
                new SkinModDescriptor("game", "游戏原版", gamePckPath, true),
                gameArchive,
                importedToSource,
                IsAnimationRemap));

            foreach (var mod in modList)
            {
                if (mod.PckPath == null || !File.Exists(mod.PckPath))
                {
                    continue;
                }

                var archive = PckArchive.Open(mod.PckPath);
                try
                {
                    var index = PckResourceIndex.Build(
                        mod,
                        archive,
                        importedToSource,
                        remapFilter: null);
                    if (mod.AffectsGameplay)
                    {
                        baselineIndexes.Add(index);
                    }
                    else
                    {
                        cosmeticIndexes.Add(index);
                    }
                }
                catch
                {
                    // 索引构建失败时释放未登记到任何列表的档案句柄。
                    archive.Dispose();
                    throw;
                }
            }

            var groups = BuildGroups(cosmeticIndexes, baselineIndexes);
            var cardGroups = BuildCardGroups(cosmeticIndexes);
            var pckCardOptions = BuildPckCardOptions(cosmeticIndexes, baselineIndexes);
            var catalog = new SkinCatalog(
                gameArchive,
                baselineIndexes,
                cosmeticIndexes,
                groups,
                cardGroups,
                pckCardOptions,
                modList);
            catalog.SortGroupsAndOptions();
            return catalog;
        }
        catch
        {
            foreach (var index in baselineIndexes.Skip(1).Concat(cosmeticIndexes))
            {
                index.Dispose();
            }

            gameArchive.Dispose();
            throw;
        }
    }

    public static IReadOnlyList<SkinProviderProbe> ProbeSkinProviders(
        IEnumerable<SkinModDescriptor> mods,
        string? gamePckPath = null)
    {
        var modList = AssignProviderInstanceIdentities(mods);
        var providers = new List<SkinProviderProbe>();
        var importedToSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var baselineIndexes = new List<PckResourceIndex>();
        try
        {
            if (gamePckPath != null && File.Exists(gamePckPath))
            {
                TryAddProbeBaselineIndex(
                    new SkinModDescriptor("game", "游戏原版", gamePckPath, true),
                    importedToSource,
                    baselineIndexes,
                    IsAnimationRemap);
            }

            foreach (var baselineMod in modList.Where(mod =>
                         mod.AffectsGameplay &&
                         mod.PckPath != null &&
                         File.Exists(mod.PckPath) &&
                         !string.Equals(mod.PckPath, gamePckPath, StringComparison.OrdinalIgnoreCase)))
            {
                TryAddProbeBaselineIndex(
                    baselineMod,
                    importedToSource,
                    baselineIndexes,
                    remapFilter: null);
            }

            foreach (var mod in modList.Where(mod => !mod.AffectsGameplay))
            {
                var visualGroups = 0;
                var cardAssets = 0;
                var cardPresentations = 0;
                var managedScriptCount = 0;
                var hasInteractiveScenes = false;
                if (mod.PckPath != null && File.Exists(mod.PckPath))
                {
                    PckArchive? archive = null;
                    PckResourceIndex? index = null;
                    try
                    {
                        archive = PckArchive.Open(mod.PckPath);
                        index = PckResourceIndex.Build(
                            mod,
                            archive,
                            importedToSource,
                            remapFilter: null);
                        managedScriptCount = mod.HasDll
                            ? CountManagedGodotScripts(archive)
                            : 0;
                        hasInteractiveScenes = managedScriptCount > 0 && ContainsInteractiveScene(archive);
                        visualGroups = CountProbeVisualGroups(index, baselineIndexes);
                        var frameworkContracts = FrameworkSkinContractScanner.Scan(
                                mod.RootPath,
                                mod.ResourceNamespaceId)
                            .Where(contract => FrameworkContractResourceClosureComplete(
                                index,
                                baselineIndexes,
                                contract))
                            .ToArray();
                        visualGroups = Math.Max(
                            visualGroups,
                            frameworkContracts
                                .Select(contract => contract.TargetGroupId)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Count());
                        var configuredCardGroups = BuildCardGroups([index]);
                        cardAssets = configuredCardGroups
                            .Sum(group => group.Options.Sum(option =>
                                option.NormalPortraits.Count + option.AncientPortraits.Count));
                        cardPresentations = configuredCardGroups
                            .Sum(group => group.Options.Sum(option => option.CardPresentations.Count));
                        var pckCardOptions = BuildPckCardOptions([index]);
                        cardAssets += pckCardOptions.Sum(option =>
                            option.Assets.Count +
                            option.NormalPortraits.Count +
                            option.AncientPortraits.Count);
                        cardPresentations += pckCardOptions.Sum(option => option.CardPresentations.Count);
                    }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"无法探测皮肤提供者 {mod.Id}: {exception.Message}");
                    }
                    finally
                    {
                        if (index != null)
                        {
                            index.Dispose();
                        }
                        else
                        {
                            archive?.Dispose();
                        }
                    }
                }

                var runtimeImages = 0;
                var hasResourceBackedCosmetics = visualGroups > 0 || cardAssets > 0 || cardPresentations > 0;
                if (mod.RootPath != null)
                {
                    runtimeImages = DiscoverRuntimeAncientImages(mod).Count;
                    hasResourceBackedCosmetics |= runtimeImages > 0;

                    if (mod.HasDll && visualGroups == 0 && cardAssets == 0 && cardPresentations == 0 &&
                        LooksLikeDllSkinProvider(mod))
                    {
                        // 只读取 PE 字符串，不把程序集载入运行时。纯 DLL 皮肤即使没有
                        // 可识别 PCK，只要明显补丁了视觉入口，也会被加载器隔离。
                        visualGroups = 1;
                    }
                }

                if (visualGroups > 0 || cardAssets > 0 || cardPresentations > 0 || runtimeImages > 0)
                {
                    providers.Add(new SkinProviderProbe(
                        mod.Id,
                        mod.RootPath,
                        visualGroups,
                        cardAssets,
                        cardPresentations,
                        runtimeImages,
                        managedScriptCount,
                        hasInteractiveScenes,
                        mod.ResourceNamespaceId,
                        HasResourceBackedCosmetics: hasResourceBackedCosmetics));
                }
            }
        }
        finally
        {
            foreach (var baselineIndex in baselineIndexes)
            {
                baselineIndex.Dispose();
            }
        }

        return providers;
    }

    private static SkinModDescriptor[] AssignProviderInstanceIdentities(
        IEnumerable<SkinModDescriptor> mods)
    {
        var modList = mods.ToArray();
        var identities = ProviderInstanceIdentityPolicy.Resolve(modList
            .Select(mod => new ProviderInstanceCandidate(
                mod.ResourceNamespaceId,
                mod.Name,
                mod.RootPath))
            .ToArray());
        return modList
            .Select((mod, index) => mod with
            {
                Id = identities[index].InstanceId,
                Name = identities[index].DisplayName,
                ManifestId = identities[index].ManifestId
            })
            .ToArray();
    }

    private static void TryAddProbeBaselineIndex(
        SkinModDescriptor mod,
        Dictionary<string, string> importedToSource,
        ICollection<PckResourceIndex> baselineIndexes,
        Func<string, bool>? remapFilter)
    {
        if (mod.PckPath == null)
        {
            return;
        }

        PckArchive? archive = null;
        try
        {
            archive = PckArchive.Open(mod.PckPath);
            var index = PckResourceIndex.Build(mod, archive, importedToSource, remapFilter);
            baselineIndexes.Add(index);
            archive = null;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"无法建立皮肤探测基线 {mod.Id}: {exception.Message}");
        }
        finally
        {
            archive?.Dispose();
        }
    }

    private static int CountProbeVisualGroups(
        PckResourceIndex index,
        IReadOnlyCollection<PckResourceIndex> baselineIndexes)
    {
        if (baselineIndexes.Count > 0)
        {
            return BuildGroups([index], baselineIndexes)
                .Count(group => group.Options.Count > 0);
        }

        var groupIds = index.Assets.Keys
            .Select(TryGetPrimaryGroup)
            .Where(group => group != null)
            .Cast<GroupIdentity>()
            .Select(group => group.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in BuildGroups([index]).Where(group => group.Options.Count > 0))
        {
            groupIds.Add(group.Id);
        }

        return groupIds.Count;
    }

    private static int CountManagedGodotScripts(PckArchive archive)
    {
        var info = new FileInfo(archive.Path);
        lock (ManagedScriptCountCacheSync)
        {
            if (ManagedScriptCountCache.TryGetValue(archive.Path, out var cached) &&
                cached.Length == info.Length &&
                cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                return cached.Count;
            }
        }

        var scriptPaths = archive.Paths
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var resourcePath in archive.Paths.Where(path =>
                     path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase)))
        {
            var text = Encoding.UTF8.GetString(archive.ReadFile(resourcePath));
            foreach (Match match in EmbeddedResourcePathRegex().Matches(text))
            {
                if (match.Value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    scriptPaths.Add(match.Value);
                }
            }
        }

        var count = scriptPaths.Count;
        lock (ManagedScriptCountCacheSync)
        {
            ManagedScriptCountCache[archive.Path] = new ManagedScriptCountCacheEntry(
                info.Length,
                info.LastWriteTimeUtc,
                count);
        }

        return count;
    }

    private static bool ContainsInteractiveScene(PckArchive archive)
    {
        foreach (var path in archive.Paths.Where(path =>
                     path.EndsWith(".scn", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase)))
        {
            string text;
            try
            {
                text = Encoding.UTF8.GetString(archive.ReadFile(path));
            }
            catch
            {
                continue;
            }

            // Do not match a mod name. These are Godot scene/input markers and work for any
            // provider that expresses click/drag behaviour through exported scene metadata.
            var hasHitRegion = text.Contains("Touch_Box_", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("InputEventMouse", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("GuiInput", StringComparison.OrdinalIgnoreCase);
            var hasInteractionData = text.Contains("ClickAnim", StringComparison.OrdinalIgnoreCase) ||
                                     text.Contains("DragSpeed", StringComparison.OrdinalIgnoreCase) ||
                                     text.Contains("MaxDragRadius", StringComparison.OrdinalIgnoreCase) ||
                                     text.Contains("IsAbsoluteDrag", StringComparison.OrdinalIgnoreCase);
            if (hasHitRegion && hasInteractionData)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeDllSkinProvider(SkinModDescriptor mod)
    {
        if (mod.RootPath == null)
        {
            return false;
        }

        var assemblyPath = System.IO.Path.Combine(
            mod.RootPath,
            mod.ResourceNamespaceId + ".dll");
        try
        {
            var info = new FileInfo(assemblyPath);
            if (!info.Exists || info.Length <= 0 || info.Length > 32 * 1024 * 1024)
            {
                return false;
            }

            var bytes = File.ReadAllBytes(assemblyPath);
            var metadata = Encoding.Latin1.GetString(bytes);
            var unicodeMetadata = Encoding.Unicode.GetString(bytes);
            var usesPatchMechanism = ContainsMetadata("HarmonyLib") ||
                                     ContainsMetadata("HarmonyPatch") ||
                                     ContainsMetadata("PatchAll");
            if (!usesPatchMechanism)
            {
                return false;
            }

            var hasSkinResourcePath = new[]
            {
                "scenes/creature_visuals",
                "spineskins/",
                "screens/char_select",
                "packed/character_select",
                "events/background_scenes",
                "card_portraits",
                "card_atlas.sprites",
                "map_marker_",
                "ui/run_history"
            }.Any(path => ContainsMetadata(path, StringComparison.OrdinalIgnoreCase));
            var hasDirectCharacterPresentationPatch = HasDirectVisualHarmonyPatch(assemblyPath);
            if (!hasSkinResourcePath && !hasDirectCharacterPresentationPatch)
            {
                return false;
            }

            // References alone are not enough for the broad CardModel/AssetCache APIs: card UI
            // libraries naturally mention those names without replacing a skin. Prefer actual
            // HarmonyPatch attribute targets, which can be inspected without loading the DLL.
            // Keep a narrow string fallback for dynamically resolved creature visual patches.
            return hasDirectCharacterPresentationPatch ||
                   HasTarget("CharacterModel", "CreateVisuals") ||
                   HasTarget("MonsterModel", "CreateVisuals") ||
                   HasTarget("EventModel", "CreateBackgroundScene");

            bool HasTarget(string typeName, params string[] methodNames) =>
                ContainsMetadata(typeName) &&
                methodNames.Any(methodName => ContainsMetadata(methodName));

            bool ContainsMetadata(
                string value,
                StringComparison comparison = StringComparison.Ordinal) =>
                metadata.Contains(value, comparison) ||
                unicodeMetadata.Contains(value, comparison);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasDirectVisualHarmonyPatch(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!peReader.HasMetadata)
        {
            return false;
        }

        var reader = peReader.GetMetadataReader();
        var hasCreatureLifecyclePatch = false;
        var hasCharacterSelectPatch = false;
        var hasRestSiteCharacterPatch = false;
        var hasMerchantRoomPatch = false;
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var patchMetadata = new StringBuilder();
            AppendHarmonyPatchMetadata(reader, type.GetCustomAttributes(), patchMetadata);
            foreach (var methodHandle in type.GetMethods())
            {
                AppendHarmonyPatchMetadata(
                    reader,
                    reader.GetMethodDefinition(methodHandle).GetCustomAttributes(),
                    patchMetadata);
            }

            if (patchMetadata.Length == 0)
            {
                continue;
            }

            var value = patchMetadata.ToString();
            if (HasDirectVisualPatchTargetMetadata(value))
            {
                return true;
            }

            // A creature lifecycle patch by itself is not skin evidence: additive combat VFX
            // mods also attach effect nodes from NCreature._Ready and may load canonical creature
            // scenes to place those effects. Private full-character packs pair that hook with a
            // character-select presentation patch. Requiring an actual second patch target keeps
            // additive visual mods in the game's normal loading flow without a provider allow-list.
            hasCreatureLifecyclePatch |=
                HasPatchTarget(value, "NCreature", "_Ready", "SetAnimationTrigger");
            hasCharacterSelectPatch |=
                HasPatchTarget(value, "NCharacterSelectScreen", "SelectCharacter") ||
                HasPatchTarget(value, "NCharacterSelectButton", "Init");
            hasRestSiteCharacterPatch |=
                HasPatchTarget(value, "NRestSiteCharacter", "_Ready");
            hasMerchantRoomPatch |=
                HasPatchTarget(value, "NMerchantRoom", "AfterRoomIsLoaded");
        }

        if (hasCreatureLifecyclePatch &&
            (hasCharacterSelectPatch || hasRestSiteCharacterPatch || hasMerchantRoomPatch))
        {
            return true;
        }

        // A few framework-independent Spine packs emit the Harmony patches through a
        // reflection helper, so the target arguments are not present as HarmonyPatch metadata.
        // Keep the fallback generic: require the actual Spine skin resource namespace plus at
        // least two concrete lifecycle entry points. This does not match ordinary VFX patches
        // that only hook NCreature._Ready, and it does not depend on a provider name or ID.
        return HasRuntimeSpineSkinPatchMetadata(assemblyPath);

        static bool HasPatchTarget(string metadata, string typeName, params string[] members) =>
            metadata.Contains(typeName, StringComparison.Ordinal) &&
            members.Any(member => metadata.Contains(member, StringComparison.Ordinal));
    }

    private static bool HasRuntimeSpineSkinPatchMetadata(string assemblyPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(assemblyPath);
            if (!ContainsBinaryString(bytes, "Harmony", StringComparison.OrdinalIgnoreCase) ||
                !ContainsBinaryString(bytes, "spineskins/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var lifecycleHooks = 0;
            if (ContainsBinaryString(bytes, "NCreature", StringComparison.Ordinal) &&
                ContainsBinaryString(bytes, "_Ready", StringComparison.Ordinal))
            {
                lifecycleHooks++;
            }

            if (ContainsBinaryString(bytes, "NRestSiteCharacter", StringComparison.Ordinal) &&
                ContainsBinaryString(bytes, "_Ready", StringComparison.Ordinal))
            {
                lifecycleHooks++;
            }

            if (ContainsBinaryString(bytes, "NMerchantRoom", StringComparison.Ordinal) &&
                ContainsBinaryString(bytes, "AfterRoomIsLoaded", StringComparison.Ordinal))
            {
                lifecycleHooks++;
            }

            return lifecycleHooks >= 2;
        }
        catch
        {
            return false;
        }

        static bool ContainsBinaryString(
            byte[] bytes,
            string value,
            StringComparison comparison)
        {
            var ascii = Encoding.Latin1.GetString(bytes);
            if (ascii.Contains(value, comparison))
            {
                return true;
            }

            // User strings in some third-party assemblies are UTF-16 blobs at an odd byte
            // offset, so decoding the entire file as UTF-16 can miss them. Search the encoded
            // byte sequence directly as well.
            var utf16 = Encoding.Unicode.GetBytes(value);
            return bytes.AsSpan().IndexOf(utf16) >= 0;
        }
    }

    internal static bool HasDirectVisualPatchTargetMetadata(string value)
    {
        return HasPatchTarget(value, "CharacterModel", "CreateVisuals", "CharacterSelectIcon", "IconTexture") ||
               HasPatchTarget(value, "MonsterModel", "CreateVisuals") ||
               HasPatchTarget(value, "EventModel", "CreateBackgroundScene", "MapIcon", "RunHistoryIcon") ||
               HasPatchTarget(value, "NMerchantButton", "_Ready", "MerchantVisual", "SetSkin") ||
               HasPatchTarget(value, "NMerchantHand", "_Ready", "skeleton") ||
               HasPatchTarget(value, "CardModel", "Portrait", "PortraitPath") ||
               HasPatchTarget(value, "AssetCache", "GetScene", "GetTexture2D", "GetAsset") ||
               HasPatchTarget(value, "AtlasManager", "GetSprite", "LoadAtlas");

        static bool HasPatchTarget(string metadata, string typeName, params string[] members) =>
            metadata.Contains(typeName, StringComparison.Ordinal) &&
            members.Any(member => metadata.Contains(member, StringComparison.Ordinal));
    }

    private static void AppendHarmonyPatchMetadata(
        MetadataReader reader,
        CustomAttributeHandleCollection attributes,
        StringBuilder destination)
    {
        foreach (var attributeHandle in attributes)
        {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            if (!GetAttributeTypeName(reader, attribute.Constructor)
                    .Equals("HarmonyPatch", StringComparison.Ordinal))
            {
                continue;
            }

            var bytes = reader.GetBlobBytes(attribute.Value);
            destination.Append(Encoding.UTF8.GetString(bytes));
            destination.Append('\n');
        }
    }

    private static string GetAttributeTypeName(MetadataReader reader, EntityHandle constructor)
    {
        EntityHandle typeHandle;
        switch (constructor.Kind)
        {
            case HandleKind.MemberReference:
                typeHandle = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                typeHandle = reader.GetMethodDefinition((MethodDefinitionHandle)constructor)
                    .GetDeclaringType();
                break;
            default:
                return string.Empty;
        }

        return typeHandle.Kind switch
        {
            HandleKind.TypeDefinition => reader.GetString(
                reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle).Name),
            HandleKind.TypeReference => reader.GetString(
                reader.GetTypeReference((TypeReferenceHandle)typeHandle).Name),
            _ => string.Empty
        };
    }

    public bool IsRuntimeProviderOption(string groupId, string optionId)
    {
        return Groups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(option => option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))?
            .IsRuntimeProvider == true;
    }

    public bool TryGetSelectedFrameworkContract(
        string groupId,
        string? optionId,
        out FrameworkCharacterSkinContract contract)
    {
        contract = Groups.FirstOrDefault(group => group.Id.Equals(
                groupId,
                StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(option => option.Id.Equals(
                optionId,
                StringComparison.OrdinalIgnoreCase))?
            .FrameworkContract!;
        return contract != null;
    }

    public bool ProviderUsesManagedCharacterScene(string groupId, string optionId)
    {
        var providerId = ResolveVisualProviderId(optionId);
        if (!_managedGodotScriptProviders.Contains(providerId))
        {
            return false;
        }

        var option = Groups.FirstOrDefault(group =>
                group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(candidate =>
                candidate.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase));
        return option?.Assets.Keys.Any(path =>
        {
            var scene = CreatureVisualSceneRegex().Match(path);
            return scene.Success &&
                   scene.Groups[1].Value.Equals(groupId, StringComparison.OrdinalIgnoreCase);
        }) == true;
    }

    public bool ProviderUsesManagedGodotScripts(string optionId) =>
        _managedGodotScriptProviders.Contains(ResolveVisualProviderId(optionId));

    /// <summary>
    /// A provider can contain selectable cards and still have an independently useful scene
    /// behaviour layer (for example a Spine scene with click/drag hit boxes).  This is separate
    /// from the full-runtime classification: card resources remain owned by Skin Changer, while
    /// the provider's input/animation scripts are enabled only while one of its visual groups is
    /// selected.
    /// </summary>
    public bool ProviderUsesInteractiveRuntime(string optionId) =>
        _interactiveRuntimeProviders.Contains(ResolveVisualProviderId(optionId));

    public bool ProviderUsesDirectCharacterRuntime(string optionId) =>
        _directCharacterRuntimeProviders.Contains(ResolveVisualProviderId(optionId));

    public IReadOnlySet<string> GetSelectedDirectCharacterRuntimeProviders(
        IReadOnlyDictionary<string, string> selections) =>
        _directCharacterRuntimeProviders
            .Where(providerId => _groups.Any(group =>
                selections.TryGetValue(group.Id, out var selectedId) &&
                SelectionUsesProvider(group.Id, selectedId, providerId)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Providers with scene behaviour or managed Godot scripts must keep their exported package
    /// intact. Splitting only the selected scene into an alias overlay can strand public atlas,
    /// skeleton and script references that are resolved dynamically at runtime.
    /// </summary>
    public bool ProviderRequiresCoherentRuntimePackage(string providerId) =>
        ProviderUsesFullRuntime(providerId) ||
        ProviderUsesInteractiveRuntime(providerId) ||
        ProviderUsesManagedGodotScripts(providerId) ||
        ProviderUsesDirectCharacterRuntime(providerId);

    public IReadOnlySet<string> GetSelectedInteractiveRuntimeProviders(
        IReadOnlyDictionary<string, string> selections) =>
        _groups
            .Where(group => selections.TryGetValue(group.Id, out var selectedId))
            .Select(group => ResolveVisualProviderId(selections[group.Id]))
            .Where(ProviderUsesInteractiveRuntime)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Some DLL providers expose one statically discoverable replacement profile per monster.
    /// Skin Changer can keep their shared behaviour layer active while routing the provider's own
    /// IsEnabled(profile) decision back to the independently selected monster group.
    /// </summary>
    public bool ProviderUsesScopedMonsterRuntime(string optionId) =>
        _scopedMonsterRuntimeProviders.Contains(ResolveVisualProviderId(optionId));

    public IReadOnlyList<string> GetProviderResourcePackPaths(string optionId) =>
        _cosmeticIndexes
            .Where(index => index.Mod.Id.Equals(
                ResolveVisualProviderId(optionId),
                StringComparison.OrdinalIgnoreCase))
            .Select(index => index.Archive.Path)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<string> GetRuntimeProviderGroups(string optionId)
    {
        var providerId = ResolveVisualProviderId(optionId);
        return _groups
            .Where(group => group.Options.Any(option =>
                option.EffectiveProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)))
            .Select(group => group.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetScopedMonsterRuntimeProviderGroups(string optionId) =>
        _scopedMonsterRuntimeProviderGroups.GetValueOrDefault(optionId) ?? [];

    public IReadOnlySet<string> GetSelectedScopedMonsterRuntimeProviders(
        IReadOnlyDictionary<string, string> selections) =>
        _scopedMonsterRuntimeProviders
            .Where(providerId => _scopedMonsterRuntimeProviderGroups[providerId].Any(groupId =>
                selections.TryGetValue(groupId, out var selectedId) &&
                SelectionUsesProvider(groupId, selectedId, providerId)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string? ResolveManagedMonsterGroupId(string monsterId)
    {
        var monsterToken = NormalizeResourceToken(monsterId);
        return _groups.FirstOrDefault(group =>
            NormalizeResourceToken(group.Id).Equals(monsterToken, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>
    /// A DLL-backed provider that owns visual groups is an inseparable cosmetic runtime bundle.
    /// Its selectable card resources remain independently owned by Skin Changer; only the
    /// non-resource behaviour layer is enabled. A provider that spans several visual groups is
    /// activated only when all of those groups select it, so its original callbacks cannot force a
    /// partially selected character, companion or monster skin.
    /// </summary>
    public bool ProviderUsesFullRuntime(string optionId) =>
        _fullRuntimeProviders.Contains(ResolveVisualProviderId(optionId));

    public IReadOnlyList<string> GetFullRuntimeProviderGroups(string optionId) =>
        _fullRuntimeProviderGroups.GetValueOrDefault(ResolveVisualProviderId(optionId)) ?? [];

    public bool IsCharacterAppearanceGroup(string groupId)
    {
        var group = Groups.FirstOrDefault(candidate =>
            candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        return group != null && CharacterSkinCompositionPolicy.CanComposeCharacterGroup(
            _characterAppearanceGroupIds.Contains(group.Id),
            group.Options.Any(IsCharacterAppearanceOption),
            group.Options.Any(option => option.IsRuntimeProvider));
    }

    public bool IsFullRuntimeProviderFullySelected(
        string optionId,
        IReadOnlyDictionary<string, string> selections)
    {
        var providerId = ResolveVisualProviderId(optionId);
        if (!_fullRuntimeProviderGroups.TryGetValue(providerId, out var groupIds) || groupIds.Count == 0)
        {
            return false;
        }

        return groupIds.All(groupId =>
            selections.TryGetValue(groupId, out var selectedId) &&
            SelectionUsesProvider(groupId, selectedId, providerId));
    }

    public IReadOnlySet<string> GetFullySelectedFullRuntimeProviders(
        IReadOnlyDictionary<string, string> selections) =>
        _fullRuntimeProviders
            .Where(providerId => IsFullRuntimeProviderFullySelected(providerId, selections))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> BuildVisualSelectionTransaction(
        string groupId,
        string optionId,
        IReadOnlyDictionary<string, string> selections)
    {
        var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var requestedProviderId = ResolveVisualProviderId(optionId);
        var targetGroupIds = RuntimeProviderScopePolicy.ResolveCharacterSelectionTargets(
            ProviderUsesFullRuntime(requestedProviderId),
            GetFullRuntimeProviderGroups(requestedProviderId),
            groupId);
        var displacedProviders = targetGroupIds
            .Select(targetGroupId => selections.TryGetValue(targetGroupId, out var selectedId)
                ? ResolveVisualProviderId(selectedId)
                : null)
            .Where(providerId =>
                providerId != null &&
                ProviderUsesFullRuntime(providerId) &&
                !providerId.Equals(requestedProviderId, StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var displacedProviderId in displacedProviders)
        {
            foreach (var ownedGroupId in GetFullRuntimeProviderGroups(displacedProviderId))
            {
                if (selections.TryGetValue(ownedGroupId, out var selectedId) &&
                    SelectionUsesProvider(ownedGroupId, selectedId, displacedProviderId))
                {
                    updates[ownedGroupId] = BaseOptionId;
                }
            }
        }

        updates[groupId] = optionId;
        if (ProviderUsesFullRuntime(requestedProviderId))
        {
            foreach (var ownedGroupId in GetFullRuntimeProviderGroups(requestedProviderId))
            {
                if (ownedGroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ownedOption = _groups.First(group => group.Id.Equals(
                        ownedGroupId,
                        StringComparison.OrdinalIgnoreCase))
                    .Options.FirstOrDefault(candidate =>
                        !candidate.IsComposition &&
                        candidate.EffectiveProviderId.Equals(
                            requestedProviderId,
                            StringComparison.OrdinalIgnoreCase));
                if (ownedOption != null)
                {
                    updates[ownedGroupId] = ownedOption.Id;
                }
            }
        }

        if (!CompanionGroups.Any(pair => updates.ContainsKey(pair.Owner) || updates.ContainsKey(pair.Companion)))
            return updates;

        var workingSelections = new Dictionary<string, string>(selections, StringComparer.OrdinalIgnoreCase);
        foreach (var update in updates) workingSelections[update.Key] = update.Value;
        foreach (var update in BuildCompanionSelectionUpdates(workingSelections, updates.Keys))
            updates[update.Key] = update.Value;

        return updates;
    }

    public IReadOnlyDictionary<string, string> BuildCompanionSelectionUpdates(
        IReadOnlyDictionary<string, string> selections,
        IEnumerable<string>? affectedGroups = null)
    {
        var affected = affectedGroups?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ownerId, companionId) in CompanionGroups)
        {
            if (affected != null && !affected.Contains(ownerId) && !affected.Contains(companionId)) continue;
            var owner = _groups.FirstOrDefault(group => group.Id.Equals(ownerId, StringComparison.OrdinalIgnoreCase));
            var companion = _groups.FirstOrDefault(group => group.Id.Equals(companionId, StringComparison.OrdinalIgnoreCase));
            if (owner == null || companion == null) continue;
            updates[companion.Id] = ResolveCompanionSelection(
                owner, selections.GetValueOrDefault(owner.Id) ?? BaseOptionId, companion);
        }
        return updates;
    }

    private string ResolveCompanionSelection(SkinGroup owner, string ownerOptionId, SkinGroup companion)
    {
        var ownerOption = owner.Options.FirstOrDefault(option => option.Id.Equals(
            ownerOptionId, StringComparison.OrdinalIgnoreCase));
        if (ownerOption == null || ownerOptionId.Equals(BaseOptionId, StringComparison.OrdinalIgnoreCase))
            return BaseOptionId;

        var sourceIds = GetCompositionSourceOptionIds(owner.Id, ownerOptionId)
            .Select(sourceId => owner.Options.FirstOrDefault(option => !option.IsComposition &&
                option.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase)))
            .Where(option => option != null)
            .Select(source => companion.Options.FirstOrDefault(option => !option.IsComposition &&
                                  option.Id.Equals(source!.Id, StringComparison.OrdinalIgnoreCase)) ??
                              companion.Options.FirstOrDefault(option => !option.IsComposition &&
                                  option.EffectiveProviderId.Equals(source!.EffectiveProviderId, StringComparison.OrdinalIgnoreCase)))
            .Where(option => option != null)
            .Select(option => option!.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceIds.Count == 0) return BaseOptionId;
        if (sourceIds.Count == 1) return sourceIds[0];

        // Use the same priority/asset merge as the owner, without persisting a second editable
        // composition. Its lifetime follows the owner (saved versus multiplayer session).
        var id = (ownerOption.IsSessionComposition ? "companion:session:" : "companion:saved:") +
                 CharacterSkinCompositionPolicy.CreateSessionId(companion.Id, sourceIds);
        if (companion.Options.Any(option => option.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return id;
        var composition = new CharacterSkinComposition
        {
            Id = id, GroupId = companion.Id, Name = ownerOption.Name, SourceOptionIds = sourceIds
        };
        if (!TryBuildCompositionOption(companion, composition, ownerOption.IsSessionComposition, out var merged))
            return BaseOptionId;
        companion.Options.Add(merged);
        return id;
    }

    public string ResolveVisualProviderId(string optionOrProviderId)
    {
        if (string.IsNullOrWhiteSpace(optionOrProviderId))
        {
            return optionOrProviderId;
        }

        return _groups.SelectMany(group => group.Options)
                   .FirstOrDefault(option => option.Id.Equals(
                       optionOrProviderId,
                       StringComparison.OrdinalIgnoreCase))?
                   .EffectiveProviderId ?? optionOrProviderId;
    }

    public string ResolveStoredVisualSelectionId(string groupId, string selectionId)
    {
        var group = _groups.FirstOrDefault(candidate => candidate.Id.Equals(
            groupId,
            StringComparison.OrdinalIgnoreCase));
        if (group == null || group.Options.Any(option => option.Id.Equals(
                selectionId,
                StringComparison.OrdinalIgnoreCase)))
        {
            return selectionId;
        }

        foreach (var option in group.Options)
        {
            var identity = FindProviderIdentity(option.EffectiveProviderId);
            if (identity != null && ProviderInstanceIdentityPolicy.IsOptionSelectionAlias(
                    identity.ManifestId,
                    identity.InstanceId,
                    option.Id,
                    selectionId))
            {
                return option.Id;
            }
        }

        return selectionId;
    }

    public string ResolveStoredCardSelectionId(string groupId, string selectionId)
    {
        var group = _cardGroups.FirstOrDefault(candidate => candidate.Id.Equals(
            groupId,
            StringComparison.OrdinalIgnoreCase));
        if (group == null || group.Options.Any(option => option.Id.Equals(
                selectionId,
                StringComparison.OrdinalIgnoreCase)))
        {
            return selectionId;
        }

        foreach (var option in group.Options)
        {
            var identity = FindProviderIdentity(option.ProviderId ?? option.Id);
            if (identity != null && ProviderInstanceIdentityPolicy.IsOptionSelectionAlias(
                    identity.ManifestId,
                    identity.InstanceId,
                    option.Id,
                    selectionId))
            {
                return option.Id;
            }
        }

        return selectionId;
    }

    public string ResolveStoredProviderId(
        string providerId,
        IReadOnlySet<string>? allowedProviderIds = null)
    {
        if (_providerInstanceIdentities.Any(identity =>
                identity.InstanceId.Equals(providerId, StringComparison.OrdinalIgnoreCase) &&
                (allowedProviderIds == null || allowedProviderIds.Contains(identity.InstanceId))))
        {
            return providerId;
        }

        foreach (var identity in _providerInstanceIdentities)
        {
            if (allowedProviderIds != null && !allowedProviderIds.Contains(identity.InstanceId))
            {
                continue;
            }

            if (ProviderInstanceIdentityPolicy.IsOptionSelectionAlias(
                    identity.ManifestId,
                    identity.InstanceId,
                    identity.InstanceId,
                    providerId))
            {
                return identity.InstanceId;
            }
        }

        return providerId;
    }

    private ProviderInstanceIdentity? FindProviderIdentity(string providerId) =>
        _providerInstanceIdentities.FirstOrDefault(identity => identity.InstanceId.Equals(
            providerId,
            StringComparison.OrdinalIgnoreCase));

    private bool SelectionUsesProvider(
        string groupId,
        string selectionId,
        string providerId) =>
        _groups.FirstOrDefault(group => group.Id.Equals(
                groupId,
                StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(option => option.Id.Equals(
                selectionId,
                StringComparison.OrdinalIgnoreCase))?
            .EffectiveProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase) == true;

    public bool SelectionUsesVisualProvider(
        string groupId,
        string selectionId,
        string providerId) =>
        SelectionUsesProvider(
            groupId,
            selectionId,
            ResolveVisualProviderId(providerId));

    public bool IsResourceBackedOption(string groupId, string optionId)
    {
        var option = Groups.FirstOrDefault(group =>
                group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(option =>
                option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase));
        return option is { Assets.Count: > 0 } || option?.ManagedMonsterScene != null;
    }

    public string? GetRuntimeImagePath(string groupId, string optionId)
    {
        return Groups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(option => option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))?
            .RuntimeImagePath;
    }

    public RuntimeMonsterVisualMode? GetRuntimeMonsterVisualMode(string groupId, string optionId)
    {
        return Groups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(option => option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))?
            .RuntimeMonsterVisualMode;
    }

    public AncientLayeredImagePaths? GetAncientLayeredImagePaths(string groupId, string optionId)
    {
        var option = Groups.FirstOrDefault(group =>
                group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
            .Options.FirstOrDefault(candidate =>
                candidate.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase));
        if (option == null)
        {
            return null;
        }

        string? character = null;
        string? backgroundCover = null;
        string? mask = null;
        string? sleepingCharacter = null;
        foreach (var sourcePath in option.Assets.Keys)
        {
            var match = AncientLayerImageRegex().Match(NormalizeTakeoverPath(sourcePath));
            if (!match.Success ||
                !match.Groups["id"].Value.Equals(groupId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (match.Groups["kind"].Value.ToLowerInvariant())
            {
                case "character":
                    character = sourcePath;
                    break;
                case "background_cover":
                    backgroundCover = sourcePath;
                    break;
                case "character_mask":
                    mask = sourcePath;
                    break;
                case "character_sleeping":
                    sleepingCharacter = sourcePath;
                    break;
            }
        }

        return character == null
            ? null
            : new AncientLayeredImagePaths(character, backgroundCover, mask, sleepingCharacter);
    }

    public string? FindGroupIdForResourcePath(string resourcePath)
    {
        if (_resourceGroupIds.TryGetValue(NormalizeTakeoverPath(resourcePath), out var assignedGroupId))
        {
            return assignedGroupId;
        }

        var identity = TryGetPrimaryGroup(resourcePath) ??
                       TryGetCharacterSelectIconGroup(resourcePath) ??
                       TryGetCharacterUiTextureGroup(resourcePath) ??
                       TryGetCharacterMapMarkerGroup(resourcePath) ??
                       TryGetCharacterIconSceneGroup(resourcePath) ??
                       TryGetAncientIconGroup(resourcePath);
        return identity != null && Groups.Any(group =>
            group.Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase))
            ? identity.Id
            : null;
    }

    public ResourceAsset? ResolveBaseline(string sourcePath)
    {
        for (var i = _baselineIndexes.Count - 1; i >= 0; i--)
        {
            var index = _baselineIndexes[i];
            if (index.Assets.TryGetValue(sourcePath, out var known))
            {
                return known;
            }

            var lazy = index.TryBuildAsset(sourcePath);
            if (lazy != null)
            {
                return lazy;
            }
        }

        return null;
    }

    public Dictionary<string, ResourceFile> BuildOverlay(
        IReadOnlyDictionary<string, string> selections,
        IReadOnlySet<string>? onlyGroups = null)
    {
        var files = new Dictionary<string, ResourceFile>(StringComparer.OrdinalIgnoreCase);
        // Skin providers are mounted through generated overlays instead of their original PCKs.
        // Keep every non-character localization table visible at all times so event/card/gameplay
        // text cannot disappear merely because the same provider also contains cosmetic assets.
        foreach (var file in _passthroughLocalizationFiles)
        {
            files[file.Key] = file.Value;
        }

        var includedGroups = Groups
            .Where(group => onlyGroups == null || onlyGroups.Contains(group.Id))
            .ToArray();
        var selectedProviders = includedGroups
            .Select(group =>
            {
                selections.TryGetValue(group.Id, out var selectedId);
                return group.Options.FirstOrDefault(option =>
                    option.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
            })
            .Where(option => option?.IsRuntimeProvider == true)
            .Cast<SkinOption>()
            .GroupBy(option => option.Id, StringComparer.OrdinalIgnoreCase)
            .Select(options =>
            {
                var first = options.First();
                var assets = options
                    .SelectMany(option => option.Assets)
                    .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        pairs => pairs.Key,
                        pairs => pairs.Last().Value,
                        StringComparer.OrdinalIgnoreCase);
                return first with { Assets = assets };
            })
            .ToArray();
        var selectableProviderFiles = Groups
            .SelectMany(group => group.Options)
            .Where(option => option.IsRuntimeProvider)
            .SelectMany(option => option.Assets.Values)
            .SelectMany(asset => asset.Files)
            .Concat(_pckCardOptions
                .SelectMany(option => option.Assets.Values)
                .SelectMany(asset => asset.Files))
            .Concat(_configuredCardGroups
                .SelectMany(group => group.Options)
                .SelectMany(option => option.Assets.Values)
                .SelectMany(asset => asset.Files))
            .Select(file => NormalizeTakeoverPath(file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isolatedRelicProviderPaths = selectedProviders.ToDictionary(
            option => option.Id,
            GetIsolatedRelicProviderPaths,
            StringComparer.OrdinalIgnoreCase);

        // Resource packs cannot be unloaded. Before an affected runtime bundle is selected or
        // deselected, restore every canonical game/mod resource that any of its full packages can
        // shadow. The selected package and explicit group mappings below then win in that order.
        // Private provider namespaces have no baseline and are harmless after the callbacks stop.
        var relevantFullRuntimeProviders = includedGroups
            .SelectMany(group => group.Options)
            .Select(option => option.EffectiveProviderId)
            .Where(ProviderUsesFullRuntime)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var providerId in relevantFullRuntimeProviders)
        {
            foreach (var file in CollectFullRuntimeProviderBaselineOverlay(providerId))
            {
                files[file.Key] = file.Value;
            }
        }

        // Full runtime providers can legitimately contain canonical support paths in addition to
        // private files. Mount them only for a coherent all-groups selection; explicit group
        // mapping below remains the final authority for every catalog-owned resource.
        foreach (var selected in selectedProviders.Where(option =>
                     ProviderUsesFullRuntime(option.Id) &&
                     IsFullRuntimeProviderFullySelected(option.Id, selections)))
        {
            foreach (var file in CollectSelectedProviderOverlayDependencies(selected))
            {
                if (isolatedRelicProviderPaths[selected.Id].Contains(
                        NormalizeTakeoverPath(file.Key)))
                {
                    continue;
                }

                if (!ShouldMountProviderDependency(selected, file.Key, selectableProviderFiles))
                {
                    continue;
                }

                files[file.Key] = file.Value;
            }
        }

        foreach (var group in includedGroups)
        {
            selections.TryGetValue(group.Id, out var selectedId);
            var selected = FrameworkRegistryCooperation.FilterAssets(group.Options.FirstOrDefault(option =>
                option.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)));
            var sourcePaths = group.Options
                .SelectMany(option => option.Assets.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var sourcePath in sourcePaths)
            {
                // 先古事件会在线程预加载阶段先验证原背景场景。复杂皮肤场景若直接
                // 覆盖这个路径，任何脚本或 Spine 依赖加载失败都会中断整个事件
                // 布局，连玩法选项也无法创建。先古场景已有独立运行时加载与最终
                // 结果接管，因此这里始终保留游戏原场景供预加载使用。
                var takeoverSourcePath = NormalizeTakeoverPath(sourcePath);
                if (AncientBackgroundSceneRegex().IsMatch(takeoverSourcePath))
                {
                    continue;
                }

                // A character skin may replace one character-specific relic slice while bundling
                // the entire (often older) shared relic atlas. Keep every public atlas resource
                // on the game baseline; the selected slice is loaded through an isolated alias by
                // the RelicModel getter patch instead.
                var asset = !IsRelicAtlasSpritePath(takeoverSourcePath) &&
                            selected != null &&
                            selected.Assets.TryGetValue(sourcePath, out var selectedAsset)
                    ? selectedAsset
                    : ResolveBaseline(sourcePath);
                if (asset == null)
                {
                    continue;
                }

                foreach (var file in asset.Files)
                {
                    var targetPath = MapAssetFilePath(sourcePath, asset.SourcePath, file.Path);
                    files[targetPath] = file;
                    var takeoverPath = NormalizeTakeoverPath(targetPath);
                    if (!takeoverPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        files[takeoverPath] = file;
                    }
                }
            }

        }

        // Older builds could already have mounted a provider's shared relic atlas during this
        // session. Reassert the current game's atlas whenever an affected character group is
        // rebuilt, including when the skin is deselected.
        var relicAtlasPaths = includedGroups
            .SelectMany(group => group.Options)
            .SelectMany(option => option.Assets
                .Where(pair => IsRelicAtlasSpritePath(pair.Key))
                .SelectMany(pair => EnumerateDependencyPaths(pair.Value)))
            .Where(IsRelicAtlasTexturePath)
            .Concat(includedGroups
                .SelectMany(group => group.Options)
                .SelectMany(GetIsolatedRelicProviderPaths)
                .Where(IsRelicAtlasTexturePath))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var relicAtlasPath in relicAtlasPaths)
        {
            var baseline = ResolveBaseline(relicAtlasPath);
            if (baseline == null)
            {
                continue;
            }

            foreach (var file in baseline.Files)
            {
                files[file.Path] = file;
            }
        }

        // 代码型外观 Mod 常把场景、骨骼和贴图放在自己的 res://<ModId>/
        // 命名空间，再由 DLL 把游戏资源入口路由过去。接管 DLL 路由以后仍需把
        // 当前所选提供者的私有依赖一起挂载，否则主场景能替换但内部引用会丢失。
        foreach (var selected in selectedProviders.Where(option => !ProviderUsesFullRuntime(option.Id)))
        {
            foreach (var file in CollectSelectedProviderOverlayDependencies(selected))
            {
                if (isolatedRelicProviderPaths[selected.Id].Contains(
                        NormalizeTakeoverPath(file.Key)))
                {
                    continue;
                }

                if (!ShouldMountProviderDependency(selected, file.Key, selectableProviderFiles))
                {
                    continue;
                }

                files[file.Key] = file.Value;
            }
        }

        return files;
    }

    internal IReadOnlySet<string> GetIsolatedRelicProviderPaths(SkinOption selected)
    {
        if (!selected.IsComposition &&
            _isolatedRelicProviderPaths.TryGetValue(
                selected.EffectiveProviderId,
                out var cached))
        {
            return cached;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in GetSelectionProviderIndexes(selected))
        {
            foreach (var asset in index.Assets.Where(pair =>
                         IsRelicAtlasSpritePath(pair.Key) ||
                         IsRelicAtlasTexturePath(pair.Key)))
            {
                paths.Add(NormalizeTakeoverPath(asset.Key));
                foreach (var file in asset.Value.Files)
                {
                    paths.Add(NormalizeTakeoverPath(file.Path));
                }
            }
        }

        if (!selected.IsComposition)
        {
            _isolatedRelicProviderPaths[selected.EffectiveProviderId] = paths;
        }

        return paths;
    }

    internal IReadOnlyList<string> GetProviderRelicSpritePaths(SkinOption selected) =>
        selected.Assets.Keys
            .Where(IsRelicAtlasSpritePath)
            .Concat(GetOptionProviderIds(selected)
                .SelectMany(providerId => GetProviderRelicAssets(providerId).Keys))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public string? FindSelectedRelicIconGroup(
        string resourcePath,
        IReadOnlyDictionary<string, string> selections,
        IReadOnlyList<string> providerPriority)
    {
        var normalizedPath = NormalizeTakeoverPath(resourcePath);
        if (!IsRelicAtlasSpritePath(normalizedPath))
        {
            return null;
        }

        var candidates = new List<(SkinGroup Group, SkinOption Option, string ProviderId)>();
        foreach (var group in Groups)
        {
            if (!selections.TryGetValue(group.Id, out var selectedId))
            {
                continue;
            }

            var selected = group.Options.FirstOrDefault(option =>
                option.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
            if (selected != null &&
                IsCharacterAppearanceOption(selected) &&
                TryResolveProviderAsset(selected, normalizedPath, out var asset))
            {
                var assetProviderId = asset.Files
                    .Select(file => _cosmeticIndexes.FirstOrDefault(index =>
                        ReferenceEquals(index.Archive, file.Archive))?.Mod.Id)
                    .FirstOrDefault(providerId => providerId != null) ??
                    selected.EffectiveProviderId;
                candidates.Add((group, selected, assetProviderId));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        for (var i = providerPriority.Count - 1; i >= 0; i--)
        {
            var providerId = providerPriority[i];
            var prioritized = candidates.FirstOrDefault(candidate =>
                candidate.ProviderId.Equals(
                    providerId,
                    StringComparison.OrdinalIgnoreCase));
            if (prioritized.Group != null)
            {
                return prioritized.Group.Id;
            }
        }

        return candidates[^1].Group.Id;
    }

    public string? FindRelicIconOwnerGroup(string resourcePath)
    {
        var normalizedPath = NormalizeTakeoverPath(resourcePath);
        if (!IsRelicAtlasSpritePath(normalizedPath))
        {
            return null;
        }

        if (_relicOwnerGroups.TryGetValue(normalizedPath, out var cached))
        {
            return cached;
        }

        var owner = Groups.FirstOrDefault(group => group.Options.Any(option =>
            IsCharacterAppearanceOption(option) &&
            TryResolveProviderAsset(option, normalizedPath, out _)))?.Id;
        _relicOwnerGroups[normalizedPath] = owner;
        return owner;
    }

    public bool TryGetBaselineRelicTextureDefinition(
        string resourcePath,
        out BaselineRelicTextureDefinition definition)
    {
        var normalizedPath = NormalizeTakeoverPath(resourcePath);
        if (_baselineRelicTextureDefinitions.TryGetValue(normalizedPath, out var cached))
        {
            definition = cached!;
            return cached != null;
        }

        BaselineRelicTextureDefinition? parsed = null;
        var baseline = ResolveBaseline(normalizedPath);
        var directFile = baseline == null ? null : FindDirectFile(baseline, normalizedPath);
        if (directFile != null)
        {
            var text = Encoding.UTF8.GetString(directFile.Archive.ReadFile(directFile.Path));
            var atlasPath = EmbeddedResourcePathRegex()
                .Matches(text)
                .Select(match => match.Value)
                .FirstOrDefault(IsRelicAtlasTexturePath);
            var region = ParseAtlasTextureRect(text, "region");
            if (atlasPath != null && region != null)
            {
                parsed = new BaselineRelicTextureDefinition(
                    atlasPath,
                    region.Value,
                    ParseAtlasTextureRect(text, "margin") ?? default,
                    AtlasTextureFilterClipRegex().IsMatch(text));
            }
        }

        _baselineRelicTextureDefinitions[normalizedPath] = parsed;
        definition = parsed!;
        return parsed != null;
    }

    private static RelicTextureRect? ParseAtlasTextureRect(string text, string property)
    {
        var match = AtlasTextureRectRegex().Match(text);
        while (match.Success)
        {
            if (match.Groups["property"].Value.Equals(property, StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(match.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                float.TryParse(match.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                float.TryParse(match.Groups["width"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var width) &&
                float.TryParse(match.Groups["height"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
            {
                return new RelicTextureRect(x, y, width, height);
            }

            match = match.NextMatch();
        }

        return null;
    }

    internal bool TryResolveProviderAsset(
        SkinOption selected,
        string sourcePath,
        out ResourceAsset asset)
    {
        var normalizedPath = NormalizeTakeoverPath(sourcePath);
        if (selected.Assets.TryGetValue(normalizedPath, out asset!) ||
            selected.Assets.TryGetValue(sourcePath, out asset!))
        {
            return true;
        }

        foreach (var providerId in GetOptionProviderIds(selected))
        {
            if (GetProviderRelicAssets(providerId).TryGetValue(normalizedPath, out asset!))
            {
                return true;
            }
        }

        asset = null!;
        return false;
    }

    private static IReadOnlyList<string> GetOptionProviderIds(SkinOption selected) =>
        selected.IsComposition
            ? selected.CompositionSourceProviderIds
            : [selected.EffectiveProviderId];

    private IReadOnlyList<PckResourceIndex> GetSelectionProviderIndexes(SkinOption selected)
    {
        return GetOptionProviderIds(selected)
            .SelectMany(providerId => _cosmeticIndexes.Where(index => index.Mod.Id.Equals(
                providerId,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private IReadOnlyDictionary<string, ResourceAsset> GetProviderRelicAssets(string providerId)
    {
        if (_providerRelicAssets.TryGetValue(providerId, out var cached))
        {
            return cached;
        }

        var assets = new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in _cosmeticIndexes.Where(index => index.Mod.Id.Equals(
                     providerId,
                     StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var pair in index.Assets.Where(pair => IsRelicAtlasSpritePath(pair.Key)))
            {
                assets[NormalizeTakeoverPath(pair.Key)] = pair.Value;
            }
        }

        _providerRelicAssets[providerId] = assets;
        return assets;
    }

    private static bool IsCharacterAppearanceOption(SkinOption option) =>
        option.Assets.Keys.Any(path =>
            CharacterPathRegex().IsMatch(path) ||
            CharacterSelectSceneRegex().IsMatch(path) ||
            AnyCharacterSelectSceneRegex().IsMatch(path) ||
            MerchantCharacterSceneRegex().IsMatch(path) ||
            RestSiteCharacterSceneRegex().IsMatch(path) ||
            CharacterSelectIconRegex().IsMatch(path) ||
            CharacterUiTextureRegex().IsMatch(path) ||
            CharacterIconSceneRegex().IsMatch(path) ||
            CharacterMapMarkerRegex().IsMatch(path));

    private static bool IsCharacterIconSourcePath(string sourcePath) =>
        TryGetCharacterIconGroup(NormalizeTakeoverPath(sourcePath)) != null;

    private static GroupIdentity? TryGetCharacterIconGroup(string sourcePath) =>
        TryGetCharacterSelectIconGroup(sourcePath) ??
        TryGetCharacterUiTextureGroup(sourcePath) ??
        TryGetCharacterIconSceneGroup(sourcePath) ??
        TryGetCharacterMapMarkerGroup(sourcePath);

    internal static bool IsRelicAtlasSpritePath(string path) =>
        RelicAtlasSpriteRegex().IsMatch(NormalizeTakeoverPath(path));

    internal static bool IsRelicAtlasTexturePath(string path) =>
        RelicAtlasTextureRegex().IsMatch(NormalizeTakeoverPath(path));

    private IReadOnlyDictionary<string, ResourceFile> CollectFullRuntimeProviderBaselineOverlay(
        string providerId)
    {
        providerId = ResolveVisualProviderId(providerId);
        if (_fullRuntimeProviderBaselineOverlays.TryGetValue(providerId, out var cached))
        {
            return cached;
        }

        var files = new Dictionary<string, ResourceFile>(StringComparer.OrdinalIgnoreCase);
        var sourcePaths = _cosmeticIndexes
            .Where(index => index.Mod.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(index =>
            {
                var namespaceToken = NormalizeResourceToken(index.Mod.ResourceNamespaceId);
                return index.Archive.Paths
                    .Where(path => !IsProviderProjectControlFile(path))
                    .Select(NormalizeTakeoverPath)
                    .Concat(index.Assets.Keys)
                    .Where(path => !IsProviderNamespacePath(path, namespaceToken));
            })
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in sourcePaths)
        {
            var baseline = ResolveBaseline(sourcePath);
            if (baseline == null)
            {
                continue;
            }

            foreach (var file in baseline.Files)
            {
                var targetPath = MapAssetFilePath(sourcePath, baseline.SourcePath, file.Path);
                files[targetPath] = file;
                var takeoverPath = NormalizeTakeoverPath(targetPath);
                if (!takeoverPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    files[takeoverPath] = file;
                }
            }
        }

        _fullRuntimeProviderBaselineOverlays[providerId] = files;
        return files;
    }

    private static bool ShouldMountProviderDependency(
        SkinOption selected,
        string dependencyPath,
        IReadOnlySet<string> selectableProviderFiles)
    {
        var takeoverPath = NormalizeTakeoverPath(dependencyPath);
        if (AncientBackgroundSceneRegex().IsMatch(StripResourceRedirectSuffix(takeoverPath)))
        {
            return false;
        }

        if (!selectableProviderFiles.Contains(takeoverPath))
        {
            return true;
        }

        // A provider may contain many independently selectable creatures. Its scene dependency
        // graph can also contain stale or prefix-compressed references to another creature. Never
        // let selecting one group globally mount files owned by a different group; merged options
        // above still allow every group from the same provider that is actually selected together.
        return selected.Assets.Values
            .SelectMany(asset => asset.Files)
            .Select(file => NormalizeTakeoverPath(file.Path))
            .Contains(takeoverPath, StringComparer.OrdinalIgnoreCase);
    }

    public Dictionary<string, ResourceFile> BuildCardOverlay(
        IReadOnlyDictionary<string, string> selections,
        IReadOnlyDictionary<string, IReadOnlyList<string>> priorityStacks,
        IReadOnlySet<string>? onlyGroups = null)
    {
        var files = new Dictionary<string, ResourceFile>(StringComparer.OrdinalIgnoreCase);
        var selectedProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in CardGroups)
        {
            if (onlyGroups != null && !onlyGroups.Contains(group.Id))
            {
                continue;
            }

            var selectedOptions = priorityStacks.TryGetValue(group.Id, out var priorityIds)
                ? priorityIds
                    .Select(id => group.Options.FirstOrDefault(option =>
                        option.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    .Where(option => option != null)
                    .Cast<CardSkinOption>()
                    .ToArray()
                : [];
            if (selectedOptions.Length == 0 &&
                selections.TryGetValue("cards:" + group.Id, out var selectedId))
            {
                var legacySelected = group.Options.FirstOrDefault(option =>
                    option.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
                if (legacySelected != null)
                {
                    selectedOptions = [legacySelected];
                }
            }

            foreach (var selected in selectedOptions)
            {
                selectedProviderIds.Add(selected.ProviderId ?? selected.Id);
            }

            var sourcePaths = group.Options
                .SelectMany(option => option.Assets.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var sourcePath in sourcePaths)
            {
                // Keep canonical game paths neutral. The effective provider is resolved once per
                // card at runtime, then its portrait and presentation resources are loaded through
                // an isolated alias. Mounting the first provider that happens to own each path here
                // would independently compose a portrait from one mod, a frame from another, and
                // an Ancient layout from a third before the card node is even refreshed.
                var asset = ResolveBaseline(sourcePath);
                if (asset == null)
                {
                    continue;
                }

                foreach (var file in asset.Files)
                {
                    var targetPath = MapAssetFilePath(sourcePath, asset.SourcePath, file.Path);
                    files[targetPath] = file;
                    var takeoverPath = NormalizeTakeoverPath(targetPath);
                    if (!takeoverPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        files[takeoverPath] = file;
                    }
                }
            }
        }

        foreach (var selection in selections
                     .Where(pair => pair.Key.StartsWith("cards:item:", StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Value)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var option = CardGroups.SelectMany(group => group.Options)
                .FirstOrDefault(candidate => candidate.Id.Equals(selection, StringComparison.OrdinalIgnoreCase));
            if (option != null)
            {
                selectedProviderIds.Add(option.ProviderId ?? option.Id);
            }
        }

        foreach (var file in BuildCardProviderNamespaceOverlay(selectedProviderIds))
        {
            files[file.Key] = file.Value;
        }

        return files;
    }

    public Dictionary<string, ResourceFile> BuildCardProviderNamespaceOverlay(
        IEnumerable<string> providerIds) =>
        BuildProviderNamespaceOverlay(providerIds);

    private Dictionary<string, ResourceFile> BuildProviderNamespaceOverlay(
        IEnumerable<string> providerIds)
    {
        var files = new Dictionary<string, ResourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var providerId in providerIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var index in _cosmeticIndexes.Where(index =>
                         index.Mod.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var file in CollectProviderNamespaceFiles(
                             index,
                             index.Mod.ResourceNamespaceId))
                {
                    files[file.Path] = file;
                }
            }
        }

        return files;
    }

    private static IReadOnlyCollection<ResourceFile> CollectProviderNamespaceFiles(
        PckResourceIndex index,
        string providerId)
    {
        var idToken = NormalizeResourceToken(providerId);
        var paths = index.Archive.Paths
            .Where(path => IsProviderNamespacePath(path, idToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(paths);
        while (queue.TryDequeue(out var path))
        {
            if (!MayContainResourceReferences(path))
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(index.Archive.ReadFile(path));
            foreach (Match reference in ResourcePathRegex().Matches(text))
            {
                var referencedPath = reference.Groups[1].Value;
                if (index.Archive.Contains(referencedPath) && paths.Add(referencedPath))
                {
                    queue.Enqueue(referencedPath);
                }
            }
        }

        return paths.Select(path => new ResourceFile(index.Archive, path)).ToArray();
    }

    private IReadOnlyDictionary<string, ResourceFile> CollectSelectedProviderOverlayDependencies(
        SkinOption selected)
    {
        var runtimeIndexes = _cosmeticIndexes
            .Where(index => index.Mod.Id.Equals(
                selected.EffectiveProviderId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var assetIndexes = selected.Assets.Values
            .SelectMany(asset => asset.Files)
            .Select(file => _cosmeticIndexes.FirstOrDefault(index =>
                ReferenceEquals(index.Archive, file.Archive)))
            .Where(index => index != null)
            .Cast<PckResourceIndex>();
        var indexes = runtimeIndexes
            .Concat(assetIndexes)
            .Distinct()
            .ToArray();
        var files = new Dictionary<string, ResourceFile>(StringComparer.OrdinalIgnoreCase);
        if (ProviderUsesFullRuntime(selected.Id))
        {
            // A full DLL skin is one inseparable visual bundle. Its binary scenes can store
            // resource paths in prefix-compressed form (for example "res://img/attack/" followed by
            // hundreds of frame names), so text-reference walking can never reconstruct the whole
            // dependency graph. Mount the provider package at its original paths while selected,
            // excluding only project/editor metadata that must never replace the running game.
            foreach (var file in runtimeIndexes
                         .SelectMany(index => index.Archive.Paths
                    .Where(path => !IsProviderProjectControlFile(path))
                    .Select(path => new ResourceFile(index.Archive, path)))
                         .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.Last()))
            {
                files[file.Path] = file;
            }
        }

        var queue = new Queue<(PckResourceIndex Index, ResourceFile File)>();
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Character localization tables are discovered by LocManager rather than referenced from
        // the character scenes, so dependency walking can never reach them. Other localization
        // tables are mounted permanently by BuildOverlay and must never follow a skin selection.
        foreach (var index in indexes)
        {
            foreach (var path in index.Archive.Paths.Where(path =>
                         _cosmeticLocalizationPaths.Contains(path)))
            {
                files[path] = new ResourceFile(index.Archive, path);
            }
        }

        foreach (var assetFile in selected.Assets.Values.SelectMany(asset => asset.Files))
        {
            var index = indexes.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Archive, assetFile.Archive));
            if (index != null)
            {
                // 当前选项自身的文件也保留一份原始路径。私有目录不一定以 Mod ID
                // 命名，也常见 res://custom、res://assets 或非英文顶层目录。
                Enqueue(index, assetFile, includeInOverlay: true);
            }
        }

        while (queue.TryDequeue(out var pending))
        {
            if (!MayContainResourceReferences(pending.File.Path))
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(pending.File.Archive.ReadFile(pending.File.Path));
            foreach (Match match in EmbeddedResourcePathRegex().Matches(text))
            {
                var sourcePath = match.Value;
                // A merged skin may contain winning assets from several providers. Resolve each
                // scene's private dependencies only inside the provider that supplied that scene;
                // falling through to another source can mix skeletons, atlases or scripts.
                var candidates = new[] { pending.Index }
                    .Concat(indexes.Where(index =>
                        !ReferenceEquals(index, pending.Index) &&
                        index.Mod.Id.Equals(
                            pending.Index.Mod.Id,
                            StringComparison.OrdinalIgnoreCase)));
                ResourceAsset? dependency = null;
                PckResourceIndex? dependencyIndex = null;
                foreach (var candidate in candidates)
                {
                    dependency = candidate.Assets.GetValueOrDefault(sourcePath) ??
                                 candidate.TryBuildAsset(sourcePath);
                    if (dependency != null)
                    {
                        dependencyIndex = candidate;
                        break;
                    }
                }

                if (dependency == null || dependencyIndex == null)
                {
                    continue;
                }

                foreach (var dependencyFile in dependency.Files)
                {
                    Enqueue(dependencyIndex, dependencyFile, includeInOverlay: true);
                }

                // Spine atlas 内的页名通常只是相对文件名，不会以 res:// 形式
                // 出现在场景或资源里，因此上面的引用扫描看不到它们。按当前已
                // 引用 atlas 的所在目录补齐贴图资产，不依赖目录命名方式。
                foreach (var textureAsset in GetSiblingAtlasTextureAssets(
                             dependencyIndex,
                             sourcePath))
                {
                    foreach (var textureFile in textureAsset.Files)
                    {
                        Enqueue(dependencyIndex, textureFile, includeInOverlay: true);
                    }
                }
            }
        }

        return files;

        void Enqueue(PckResourceIndex index, ResourceFile file, bool includeInOverlay)
        {
            if (includeInOverlay)
            {
                files[file.Path] = file;
            }

            var key = index.Mod.Id + "\n" + file.Path;
            if (queued.Add(key))
            {
                queue.Enqueue((index, file));
            }
        }
    }

    private static bool IsProviderLocalizationFile(string path, string providerId)
    {
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
               TryGetLocalizationProviderId(path, out var ownerId) &&
               ownerId.Equals(providerId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCharacterLocalizationFile(string path) =>
        path.EndsWith("/characters.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsProviderProjectControlFile(string path)
    {
        if (path.Equals("res://project.binary", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("res://project.godot", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("res://export_presets.cfg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gdextension", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.StartsWith("res://.godot/", StringComparison.OrdinalIgnoreCase) &&
               !path.StartsWith("res://.godot/imported/", StringComparison.OrdinalIgnoreCase) &&
               !path.StartsWith("res://.godot/exported/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<ResourceAsset> GetSiblingAtlasTextureAssets(
        PckResourceIndex index,
        string atlasSourcePath)
    {
        if (!atlasSourcePath.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase) &&
            !atlasSourcePath.EndsWith(".spatlas", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var separator = atlasSourcePath.LastIndexOf('/');
        if (separator < 0)
        {
            yield break;
        }

        var directory = atlasSourcePath[..(separator + 1)];
        var sourcePaths = index.Assets.Keys
            .Concat(index.Archive.Paths.Select(NormalizeAtlasTextureCandidatePath))
            .Where(path => path != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in sourcePaths)
        {
            if (!IsAtlasTextureSourcePath(sourcePath) ||
                !sourcePath.StartsWith(directory, StringComparison.OrdinalIgnoreCase) ||
                sourcePath[directory.Length..].Contains('/'))
            {
                continue;
            }

            var asset = index.Assets.GetValueOrDefault(sourcePath) ??
                        index.TryBuildAsset(sourcePath);
            if (asset != null)
            {
                yield return asset;
            }
        }
    }

    private static string? NormalizeAtlasTextureCandidatePath(string path)
    {
        if (path.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
        {
            return path[..^7];
        }

        if (path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase))
        {
            return path[..^6];
        }

        return IsAtlasTextureSourcePath(path) ? path : null;
    }

    private static bool IsAtlasTextureSourcePath(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    private static bool IsProviderNamespacePath(string path, string idToken)
    {
        if (idToken.Length == 0 || !path.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = path[6..];
        var separator = relative.IndexOf('/');
        var topLevel = separator < 0 ? relative : relative[..separator];
        var topLevelToken = NormalizeResourceToken(topLevel);
        return topLevelToken.Equals(idToken, StringComparison.OrdinalIgnoreCase) ||
               topLevelToken.StartsWith(idToken, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeResourceToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public void FinalizeCardGroups(IEnumerable<CardCatalogEntry> cards)
    {
        var cardEntries = cards
            .Where(card => !string.IsNullOrWhiteSpace(card.PortraitPath) &&
                           !string.IsNullOrWhiteSpace(card.CatalogGroupId) &&
                           !string.IsNullOrWhiteSpace(card.FilterGroupId))
            .ToArray();
        var cardsByType = cardEntries
            .GroupBy(card => card.TypeName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                entries => entries.Key,
                entries => entries.First(),
                StringComparer.OrdinalIgnoreCase);
        var knownCardGroups = cardEntries
            .SelectMany(card => new[]
            {
                card.CatalogGroupId,
                card.FilterGroupId,
                card.PoolGroupId,
                TryGetCardPortraitGroup(card.PortraitPath)
            })
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uniqueCardTypesByStem = BuildUniqueCardTypesByStem(cardEntries, knownCardGroups);
        var groups = new Dictionary<string, CardSkinGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var configuredGroup in _configuredCardGroups)
        {
            foreach (var option in configuredGroup.Options)
            {
                AddCardOption(groups, configuredGroup.Id, option);

                var specialGroupIds = option.NormalPortraits.Keys
                    .Concat(option.AncientPortraits.Keys)
                    .Select(cardType => cardsByType.GetValueOrDefault(cardType))
                    .Where(card => card != null &&
                                   !card.FilterGroupId.Equals(
                                       configuredGroup.Id,
                                       StringComparison.OrdinalIgnoreCase))
                    .Select(card => card!.FilterGroupId)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var specialGroupId in specialGroupIds)
                {
                    var normal = option.NormalPortraits
                        .Where(pair => cardsByType.TryGetValue(pair.Key, out var card) &&
                                       card.FilterGroupId.Equals(
                                           specialGroupId,
                                           StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    var ancient = option.AncientPortraits
                        .Where(pair => cardsByType.TryGetValue(pair.Key, out var card) &&
                                       card.FilterGroupId.Equals(
                                           specialGroupId,
                                           StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    var presentations = option.CardPresentations
                        .Where(pair => cardsByType.TryGetValue(pair.Key, out var card) &&
                                       card.FilterGroupId.Equals(
                                           specialGroupId,
                                           StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    AddCardOption(groups, specialGroupId, option with
                    {
                        NormalPortraits = normal,
                        AncientPortraits = ancient,
                        Assets = new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase),
                        CardPresentations = presentations
                    });
                }
            }
        }

        foreach (var option in _pckCardOptions)
        {
            var assetsByGroup = new Dictionary<string, Dictionary<string, ResourceAsset>>(
                StringComparer.OrdinalIgnoreCase);
            var normalByGroup = new Dictionary<string, Dictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);
            var ancientByGroup = new Dictionary<string, Dictionary<string, AncientCardPortrait>>(
                StringComparer.OrdinalIgnoreCase);
            var presentationsByGroup =
                new Dictionary<string, Dictionary<string, CardPresentationDefinition>>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (var card in cardEntries)
            {
                var assets = option.Assets
                    .Where(pair => CardArtMatches(pair.Key, card, knownCardGroups))
                    .ToArray();
                var hasPresentation = option.CardPresentations.TryGetValue(
                    card.TypeName,
                    out var presentation);
                var hasNormalPortrait = option.NormalPortraits.TryGetValue(
                    card.TypeName,
                    out var normalPortrait);
                var hasAncientPortrait = option.AncientPortraits.TryGetValue(
                    card.TypeName,
                    out var ancientPortrait);
                if (assets.Length == 0 &&
                    !hasNormalPortrait &&
                    !hasAncientPortrait &&
                    TryGetUniqueSharedPoolPortrait(
                        option.Assets.Keys,
                        card,
                        knownCardGroups,
                        uniqueCardTypesByStem,
                        out var sharedPoolPortrait))
                {
                    // Some character-skin authors store generated/token cards under their
                    // character directory (for example silent/shiv) even though the game owns
                    // that card through a shared pool (token/shiv). Route an exact, globally
                    // unambiguous stem as an explicit per-card portrait. Keeping it out of the
                    // group's broad asset matcher prevents the cross-character/card-type leaks
                    // that a blanket category bypass would reintroduce.
                    hasNormalPortrait = true;
                    normalPortrait = sharedPoolPortrait;
                }
                if (assets.Length == 0 &&
                    !hasNormalPortrait &&
                    !hasAncientPortrait &&
                    !hasPresentation)
                {
                    continue;
                }

                var groupId = card.FilterGroupId.Equals(
                    card.CatalogGroupId,
                    StringComparison.OrdinalIgnoreCase)
                    ? card.CatalogGroupId
                    : card.FilterGroupId;
                if (!assetsByGroup.TryGetValue(groupId, out var groupAssets))
                {
                    groupAssets = new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase);
                    assetsByGroup.Add(groupId, groupAssets);
                }

                foreach (var asset in assets)
                {
                    groupAssets[asset.Key] = asset.Value;
                }
                if (hasNormalPortrait && normalPortrait != null)
                {
                    if (!normalByGroup.TryGetValue(groupId, out var groupPortraits))
                    {
                        groupPortraits = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase);
                        normalByGroup.Add(groupId, groupPortraits);
                    }
                    groupPortraits[card.TypeName] = normalPortrait;
                }
                if (hasAncientPortrait && ancientPortrait != null)
                {
                    if (!ancientByGroup.TryGetValue(groupId, out var groupPortraits))
                    {
                        groupPortraits = new Dictionary<string, AncientCardPortrait>(
                            StringComparer.OrdinalIgnoreCase);
                        ancientByGroup.Add(groupId, groupPortraits);
                    }
                    groupPortraits[card.TypeName] = ancientPortrait;
                }
                if (hasPresentation && presentation != null)
                {
                    if (!presentationsByGroup.TryGetValue(groupId, out var groupPresentations))
                    {
                        groupPresentations = new Dictionary<string, CardPresentationDefinition>(
                            StringComparer.OrdinalIgnoreCase);
                        presentationsByGroup.Add(groupId, groupPresentations);
                    }
                    groupPresentations[card.TypeName] = presentation;
                }
            }

            foreach (var groupId in assetsByGroup.Keys
                         .Union(normalByGroup.Keys, StringComparer.OrdinalIgnoreCase)
                         .Union(ancientByGroup.Keys, StringComparer.OrdinalIgnoreCase)
                         .Union(presentationsByGroup.Keys, StringComparer.OrdinalIgnoreCase))
            {
                AddCardOption(groups, groupId, option with
                {
                    NormalPortraits = normalByGroup.GetValueOrDefault(groupId) ??
                                      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    AncientPortraits = ancientByGroup.GetValueOrDefault(groupId) ??
                                       new Dictionary<string, AncientCardPortrait>(
                                           StringComparer.OrdinalIgnoreCase),
                    Assets = assetsByGroup.GetValueOrDefault(groupId) ??
                             new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase),
                    CardPresentations = presentationsByGroup.GetValueOrDefault(groupId) ??
                                        new Dictionary<string, CardPresentationDefinition>(
                                            StringComparer.OrdinalIgnoreCase)
                });
            }
        }

        _cardGroups.Clear();
        _cardGroups.AddRange(groups.Values
            .Where(group => group.Options.Count > 0)
            .OrderBy(group => GroupSortOrder(group.Id))
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase));
        foreach (var group in _cardGroups)
        {
            group.Options.Sort((left, right) =>
                string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
        }
    }

    private static IReadOnlyDictionary<string, string> BuildUniqueCardTypesByStem(
        IReadOnlyList<CardCatalogEntry> cards,
        IReadOnlySet<string> knownCardGroups)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards)
        {
            var stems = new[]
                {
                    NormalizeCardToken(card.TypeName),
                    TryGetCardArtIdentity(card.PortraitPath, knownCardGroups)?.Stem
                }
                .Where(stem => !string.IsNullOrWhiteSpace(stem))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var stem in stems)
            {
                if (ambiguous.Contains(stem))
                {
                    continue;
                }

                if (owners.TryGetValue(stem, out var owner) &&
                    !owner.Equals(card.TypeName, StringComparison.OrdinalIgnoreCase))
                {
                    owners.Remove(stem);
                    ambiguous.Add(stem);
                    continue;
                }

                owners[stem] = card.TypeName;
            }
        }

        return owners;
    }

    private static bool TryGetUniqueSharedPoolPortrait(
        IEnumerable<string> assetPaths,
        CardCatalogEntry card,
        IReadOnlySet<string> knownCardGroups,
        IReadOnlyDictionary<string, string> uniqueCardTypesByStem,
        out string portraitPath)
    {
        portraitPath = string.Empty;
        if (card.PoolGroupId.Equals(card.FilterGroupId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var portrait = TryGetCardArtIdentity(card.PortraitPath, knownCardGroups);
        var typeStem = NormalizeCardToken(card.TypeName);
        if (portrait == null)
        {
            return false;
        }

        var candidates = assetPaths
            .Select(path => (Path: path, Identity: TryGetCardArtIdentity(path, knownCardGroups)))
            .Where(candidate => candidate.Identity != null &&
                                !string.IsNullOrWhiteSpace(candidate.Identity.Category) &&
                                !candidate.Identity.Category.Equals(
                                    card.PoolGroupId,
                                    StringComparison.OrdinalIgnoreCase) &&
                                !candidate.Identity.Category.Equals(
                                    portrait.Category,
                                    StringComparison.OrdinalIgnoreCase) &&
                                (candidate.Identity.Stem.Equals(
                                     typeStem,
                                     StringComparison.OrdinalIgnoreCase) ||
                                 candidate.Identity.Stem.Equals(
                                     portrait.Stem,
                                     StringComparison.OrdinalIgnoreCase)) &&
                                uniqueCardTypesByStem.TryGetValue(
                                    candidate.Identity.Stem,
                                    out var owner) &&
                                owner.Equals(card.TypeName, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => SharedPoolPortraitScore(path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }

        portraitPath = candidates[0];
        return true;
    }

    private static int SharedPoolPortraitScore(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".tres", StringComparison.OrdinalIgnoreCase) ? 0 :
            extension.Equals(".res", StringComparison.OrdinalIgnoreCase) ? 1 :
            extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }

    private static void AddCardOption(
        IDictionary<string, CardSkinGroup> groups,
        string groupId,
        CardSkinOption option)
    {
        if (!groups.TryGetValue(groupId, out var group))
        {
            group = new CardSkinGroup(groupId, DisplayName(groupId));
            groups.Add(groupId, group);
        }

        var existingIndex = group.Options.FindIndex(existing =>
            existing.Id.Equals(option.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            group.Options[existingIndex] = group.Options[existingIndex].Merge(option);
        }
        else
        {
            group.Options.Add(option);
        }
    }

    public IReadOnlySet<string> GetAffectedSourcePaths(string groupId)
    {
        var group = Groups.First(group =>
            group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        var affected = group.Options
            .SelectMany(option => option.Assets.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var index in _baselineIndexes)
        {
            foreach (var sourcePath in index.Assets.Keys)
            {
                var identity = TryGetPrimaryGroup(sourcePath);
                if (identity?.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase) == true &&
                    sourcePath.EndsWith(".tres", StringComparison.OrdinalIgnoreCase))
                {
                    affected.Add(sourcePath);
                }
            }
        }

        return affected;
    }

    public IReadOnlySet<string> GetRuntimeDependencyRestoreGroups(
        string loadedGroupId,
        IEnumerable<string> dependencyPaths)
    {
        var mountedPaths = dependencyPaths
            .Select(NormalizeTakeoverPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mountedPaths.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var affectedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups.Where(group =>
                     !group.Id.Equals(loadedGroupId, StringComparison.OrdinalIgnoreCase)))
        {
            var overlaps = group.Options
                .SelectMany(option => option.Assets)
                .Any(pair =>
                    mountedPaths.Contains(NormalizeTakeoverPath(pair.Key)) ||
                    pair.Value.Files.Any(file =>
                        mountedPaths.Contains(NormalizeTakeoverPath(file.Path))));
            if (overlaps)
            {
                affectedGroups.Add(group.Id);
            }
        }

        return affectedGroups;
    }

    public Dictionary<string, ResourceFile> BuildBaselineDependencyOverlay(
        IEnumerable<string> dependencyPaths)
    {
        var files = new Dictionary<string, ResourceFile>(StringComparer.OrdinalIgnoreCase);
        var sourcePaths = dependencyPaths
            .Select(NormalizeTakeoverPath)
            .Select(StripResourceRedirectSuffix)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in sourcePaths)
        {
            var baseline = ResolveBaseline(sourcePath);
            if (baseline == null)
            {
                continue;
            }

            foreach (var file in baseline.Files)
            {
                var targetPath = MapAssetFilePath(sourcePath, baseline.SourcePath, file.Path);
                files[targetPath] = file;
                var takeoverPath = NormalizeTakeoverPath(targetPath);
                if (!takeoverPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    files[takeoverPath] = file;
                }
            }
        }

        return files;
    }

    public RuntimeResourceOverlay BuildRuntimeResourceOverlay(
        string groupId,
        string selectionId,
        IReadOnlyCollection<string> resourcePaths,
        string aliasToken,
        bool includeProviderDependencies = false,
        bool reuseMountedPrivateDependencies = false)
    {
        var group = Groups.First(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        var selected = FrameworkRegistryCooperation.FilterAssets(group.Options.FirstOrDefault(option =>
            option.Id.Equals(selectionId, StringComparison.OrdinalIgnoreCase)));
        // Runtime callers ask for the exact resources needed by the current screen/context.
        // Pulling every asset in the group made a character-select icon request also copy the
        // combat, merchant and rest-site scenes into a new PCK. Dependencies of the requested
        // roots are collected below, so unrelated top-level assets must stay lazy.
        var sourcePaths = resourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IncludeAtlasTexturePages(selected, sourcePaths);

        var resources = new List<RuntimeResource>();
        foreach (var sourcePath in sourcePaths)
        {
            var baseline = ResolveBaseline(sourcePath);
            var providerRelic = selected != null && IsRelicAtlasSpritePath(sourcePath) &&
                                TryResolveProviderAsset(selected, sourcePath, out var providerRelicAsset)
                ? providerRelicAsset
                : null;
            var primary = providerRelic ??
                          (selected != null && selected.Assets.TryGetValue(sourcePath, out var selectedAsset)
                ? selectedAsset
                : selected?.ManagedMonsterScene != null &&
                  sourcePath.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
                    ? selected.ManagedMonsterScene
                    : baseline);
            if (primary == null)
            {
                continue;
            }

            resources.Add(CreateRuntimeResource(sourcePath, primary, baseline));
        }

        IncludeAliasedDependencyChain(
            selected,
            resources,
            reuseMountedPrivateDependencies);

        var overlay = BuildAliasedResourceOverlay(
            resources,
            resourcePaths,
            aliasToken,
            redirectDirectScenesAtCanonicalPath: true);
        if (selected == null ||
            !includeProviderDependencies ||
            reuseMountedPrivateDependencies ||
            ProviderUsesFullRuntime(selected.Id))
        {
            // A coherent full-runtime selection is mounted once by BuildOverlay before any
            // preview/combat scene is rebuilt. Copying the same complete provider package into
            // every per-monster alias PCK is redundant and, for large animated packs, can create
            // hundreds of multi-hundred-megabyte cache files while browsing the Bestiary.
            return overlay;
        }

        var dependencyFiles = CollectSelectedProviderDependencies(
            selected,
            resources,
            overlay.SourceAliases,
            overlay.PayloadAliases);
        if (dependencyFiles.Count == 0)
        {
            return overlay;
        }

        var files = overlay.Files.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencyFiles)
        {
            // Canonical redirect files emitted by the alias overlay deliberately point binary
            // scene dependencies at their fresh payload aliases. Do not replace those bridges
            // with the provider's original .remap/.import file.
            files.TryAdd(dependency.Key, dependency.Value);
        }

        var canonicalDependencyPaths = overlay.CanonicalDependencyPaths
            .Concat(dependencyFiles.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new RuntimeResourceOverlay(
            overlay.ResourcePaths,
            files,
            overlay.SourceAliases,
            overlay.PayloadAliases,
            canonicalDependencyPaths);
    }

    internal RuntimeResourceOverlay BuildIsolatedRelicResourceOverlay(
        string groupId,
        string selectionId,
        IReadOnlyCollection<string> resourcePaths,
        string aliasToken)
    {
        if (resourcePaths.Any(path =>
                !IsRelicAtlasSpritePath(path) &&
                !IsRelicAtlasTexturePath(path)))
        {
            throw new ArgumentException(
                "遗物私有资源包只能包含遗物图集或其切片。",
                nameof(resourcePaths));
        }

        var overlay = BuildRuntimeResourceOverlay(
            groupId,
            selectionId,
            resourcePaths,
            aliasToken);

        // Binary AtlasTexture payloads keep the atlas' canonical res:// path internally. The
        // ordinary runtime overlay therefore emits temporary canonical .remap/.import bridges.
        // A provider-wide relic bundle must never emit those bridges: mounting its atlas at the
        // game's public path poisons Godot's shared atlas cache, so icons loaded after switching
        // away can retain the previous provider's texture and use incompatible regions. The
        // private slice initially resolves against the game's public atlas and is immediately
        // rebound to the provider's private atlas by SkinService.
        var files = overlay.Files
            .Where(pair => !overlay.CanonicalDependencyPaths.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return new RuntimeResourceOverlay(
            overlay.ResourcePaths,
            files,
            overlay.SourceAliases,
            overlay.PayloadAliases,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private RuntimeResource CreateRuntimeResource(
        string sourcePath,
        ResourceAsset primary,
        ResourceAsset? baseline = null)
    {
        baseline ??= ResolveBaseline(sourcePath);
        var directFile = FindDirectFile(primary, sourcePath);
        var remapFile = FindRemapFile(primary, sourcePath);
        var payloadFiles = GetImportedPayloadFiles(primary, sourcePath);
        if (directFile == null && remapFile == null && baseline != null && !ReferenceEquals(primary, baseline))
        {
            directFile = FindDirectFile(baseline, sourcePath);
            remapFile = FindRemapFile(baseline, sourcePath);
            if (payloadFiles.Length == 0)
            {
                payloadFiles = GetImportedPayloadFiles(baseline, sourcePath);
            }
        }

        return new RuntimeResource(sourcePath, directFile, remapFile, payloadFiles);
    }

    private void IncludeAliasedDependencyChain(
        SkinOption? selected,
        List<RuntimeResource> resources,
        bool reuseMountedPrivateDependencies = false,
        IReadOnlySet<string>? availableSourcePaths = null)
    {
        IReadOnlyList<PckResourceIndex> selectedIndexes = selected == null
            ? []
            : GetSelectionProviderIndexes(selected);
        var selectedProviderIds = selected == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : GetOptionProviderIds(selected).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var discoveredResourcePaths = resources
            .Select(resource => resource.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (availableSourcePaths != null)
        {
            discoveredResourcePaths.UnionWith(availableSourcePaths);
        }
        var queue = new Queue<RuntimeResource>(resources);
        var selectableProviderFiles = reuseMountedPrivateDependencies
            ? Groups
                .SelectMany(group => group.Options)
                .Where(option => option.IsRuntimeProvider)
                .SelectMany(option => option.Assets.Values)
                .SelectMany(asset => asset.Files)
                .Concat(_pckCardOptions
                    .SelectMany(option => option.Assets.Values)
                    .SelectMany(asset => asset.Files))
                .Concat(_configuredCardGroups
                    .SelectMany(group => group.Options)
                    .SelectMany(option => option.Assets.Values)
                    .SelectMany(asset => asset.Files))
                .Select(file => NormalizeTakeoverPath(file.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        IReadOnlySet<string> isolatedRelicProviderPaths = selected == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : GetIsolatedRelicProviderPaths(selected);

        while (queue.TryDequeue(out var resource))
        {
            // Exported Godot scenes/resources are commonly binary .scn/.res payloads behind a
            // text .remap file. Their external resource paths remain readable in the binary
            // string table, so scan both the source and every payload instead of stopping at the
            // outer .tscn/.tres entry. Otherwise only the scene name is isolated while its Spine
            // skeleton/atlas silently binds to the skin that occupied the canonical cache first.
            var dependencyFiles = new[] { resource.DirectFile, resource.RemapFile }
                .Where(file => file != null)
                .Cast<ResourceFile>()
                .Concat(resource.PayloadFiles);
            foreach (var dependencyFile in dependencyFiles)
            {
                // Text resources can safely be rewritten to private copies of their imported
                // Spine/texture payloads. Binary .scn/.res files cannot: their embedded strings
                // must keep resolving through the temporary canonical bridge instead.
                var allowImportedPayloadAlias = IsRewritableTextResource(dependencyFile.Path);
                foreach (var sourcePath in EnumerateDependencyPaths(dependencyFile))
                {
                    if (discoveredResourcePaths.Contains(sourcePath) ||
                        !CanAliasDependency(sourcePath, allowImportedPayloadAlias))
                    {
                        continue;
                    }

                    var dependencyOwner = selectedIndexes.FirstOrDefault(candidate =>
                        ReferenceEquals(candidate.Archive, dependencyFile.Archive));
                    if (TryResolveSelected(
                            sourcePath,
                            dependencyOwner,
                            out var selectedAsset,
                            out var selectedIndex))
                    {
                        IncludeResource(sourcePath, selectedAsset, selectedIndex);
                        continue;
                    }

                    var baseline = ResolveBaseline(sourcePath);
                    if (baseline != null)
                    {
                        IncludeResource(sourcePath, baseline, FindOwningIndex(baseline));
                    }
                }
            }
        }

        return;

        void IncludeResource(
            string sourcePath,
            ResourceAsset asset,
            PckResourceIndex? index,
            bool requiresAliasedLocation = false)
        {
            if (discoveredResourcePaths.Contains(sourcePath))
            {
                return;
            }

            var runtimeResource = CreateRuntimeResource(sourcePath, asset);
            if (runtimeResource.DirectFile == null && runtimeResource.RemapFile == null)
            {
                return;
            }

            discoveredResourcePaths.Add(sourcePath);
            queue.Enqueue(runtimeResource);

            // The globally mounted visual overlay already exposes the selected provider's
            // dependency graph. Keep provider-exclusive paths there to avoid copying a large
            // private animation package into every temporary PCK. Public game paths and paths
            // shared by multiple providers must still be copied below: native Godot/Spine caches
            // can retain the first provider's object even when the mounted bytes have changed.
            if (reuseMountedPrivateDependencies &&
                CanReuseMountedPrivateDependency(
                    sourcePath,
                    asset,
                    index,
                    requiresAliasedLocation))
            {
                return;
            }

            resources.Add(runtimeResource);

            if (index == null)
            {
                return;
            }

            foreach (var textureAsset in GetSiblingAtlasTextureAssets(index, sourcePath))
            {
                // Spine resolves every page name relative to the atlas resource. Once the atlas
                // lives in this fresh alias namespace, all of its sibling pages must live there
                // too; a canonical page from the mounted provider cannot satisfy that path.
                IncludeResource(
                    textureAsset.SourcePath,
                    textureAsset,
                    index,
                    requiresAliasedLocation: true);
            }
        }

        PckResourceIndex? FindOwningIndex(ResourceAsset asset)
        {
            return _cosmeticIndexes
                       .Concat(_baselineIndexes)
                       .FirstOrDefault(candidate => asset.Files.Any(file =>
                           ReferenceEquals(candidate.Archive, file.Archive)));
        }

        bool TryResolveSelected(
            string sourcePath,
            PckResourceIndex? preferredIndex,
            out ResourceAsset asset,
            out PckResourceIndex? index)
        {
            if (selected != null && selected.Assets.TryGetValue(sourcePath, out var configured))
            {
                asset = configured;
                index = selectedIndexes.FirstOrDefault(candidate => configured.Files.Any(file =>
                    ReferenceEquals(candidate.Archive, file.Archive)));
                return true;
            }

            var candidates = preferredIndex == null
                ? selectedIndexes
                : selectedIndexes.Where(candidate => candidate.Mod.Id.Equals(
                    preferredIndex.Mod.Id,
                    StringComparison.OrdinalIgnoreCase));
            foreach (var candidate in candidates)
            {
                var dependency = candidate.Assets.GetValueOrDefault(sourcePath) ??
                                 candidate.TryBuildAsset(sourcePath);
                if (dependency == null)
                {
                    continue;
                }

                asset = dependency;
                index = candidate;
                return true;
            }

            asset = null!;
            index = null;
            return false;
        }

        bool CanReuseMountedPrivateDependency(
            string sourcePath,
            ResourceAsset asset,
            PckResourceIndex? index,
            bool requiresAliasedLocation)
        {
            var belongsToSelectedProvider =
                selected != null &&
                index != null &&
                selectedProviderIds.Contains(index.Mod.Id) &&
                asset.Files.Any(file => ReferenceEquals(file.Archive, index.Archive));

            var providerFiles = asset.Files
                .Where(file => index != null && ReferenceEquals(file.Archive, index.Archive))
                .ToArray();
            var isMountedBySelectedOverlay = selected != null &&
                providerFiles.Length > 0 &&
                providerFiles.All(file =>
                    !isolatedRelicProviderPaths.Contains(NormalizeTakeoverPath(file.Path)) &&
                    ShouldMountProviderDependency(selected!, file.Path, selectableProviderFiles));

            // A logical game path, or a provider-only path supplied by more than one skin Mod,
            // can already be owned by a previously mounted pack and by Godot/Spine's native
            // resource cache. Such paths must be copied into this selection's fresh alias PCK.
            // Only paths unique to the selected provider are safe to reuse from its complete pack;
            // this preserves the large-provider optimization without letting the first skin win.
            var normalizedSourcePath = NormalizeTakeoverPath(sourcePath);
            var isProviderExclusivePath =
                ResolveBaseline(sourcePath) == null &&
                !_cosmeticIndexes.Any(candidate =>
                    (index == null || !candidate.Mod.Id.Equals(
                        index.Mod.Id,
                        StringComparison.OrdinalIgnoreCase)) &&
                    (candidate.Assets.ContainsKey(sourcePath) ||
                     candidate.Assets.ContainsKey(normalizedSourcePath) ||
                     candidate.Archive.Contains(sourcePath) ||
                     candidate.Archive.Contains(normalizedSourcePath)));

            // Mirror BuildOverlay's filtering exactly. Provider resources excluded there (for
            // example another independently selectable creature or an isolated relic atlas)
            // cannot be reused canonically and therefore stay in this private alias package.
            return RuntimeDependencyIsolationPolicy.CanReuseMountedProviderDependency(
                belongsToSelectedProvider,
                isProviderExclusivePath,
                isMountedBySelectedOverlay,
                requiresAliasedLocation);
        }
    }

    private static IEnumerable<string> EnumerateDependencyPaths(ResourceAsset asset) =>
        asset.Files
            .Where(file => IsDependencyGraphTextResource(file.Path))
            .SelectMany(EnumerateDependencyPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateDependencyPaths(ResourceFile? file)
    {
        if (file == null || !IsDependencyGraphTextResource(file.Path))
        {
            return [];
        }

        var text = Encoding.UTF8.GetString(file.Archive.ReadFile(file.Path));
        return EmbeddedResourcePathRegex()
            .Matches(text)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsDependencyGraphTextResource(string path) =>
        path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".scn", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".res", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gdshader", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".spatlas", StringComparison.OrdinalIgnoreCase);

    private static bool CanAliasDependency(string path, bool allowImportedPayloadAlias) =>
        (allowImportedPayloadAlias ||
         !path.StartsWith("res://.godot/imported/", StringComparison.OrdinalIgnoreCase)) &&
        !path.StartsWith("res://.godot/exported/", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".gdc", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, byte[]> CollectSelectedProviderDependencies(
        SkinOption selected,
        IReadOnlyCollection<RuntimeResource> resources,
        IReadOnlyDictionary<string, string> sourceAliases,
        IReadOnlyDictionary<string, string> payloadAliases)
    {
        var indexes = GetSelectionProviderIndexes(selected);
        if (indexes.Count == 0)
        {
            return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(PckResourceIndex? Index, ResourceFile File)>();
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in resources
                     .SelectMany(resource => new[] { resource.DirectFile, resource.RemapFile }
                         .Where(file => file != null)
                         .Cast<ResourceFile>()
                         .Concat(resource.PayloadFiles)))
        {
            var index = indexes.FirstOrDefault(candidate => ReferenceEquals(candidate.Archive, file.Archive));
            // 基线场景本身也必须参与扫描：它可能引用提供者中没有角色 ID 的
            // 辅助贴图或脚本，而这些资源无法靠文件名归入皮肤分组。
            Enqueue(index, file);
        }

        while (queue.TryDequeue(out var pending))
        {
            if (!MayContainResourceReferences(pending.File.Path))
            {
                continue;
            }

            // 扫描始终用原始文本（提供者索引按原始路径登记），写入时才做别名重写。
            var bytes = pending.File.Archive.ReadFile(pending.File.Path);
            var text = Encoding.UTF8.GetString(bytes);
            foreach (Match match in EmbeddedResourcePathRegex().Matches(text))
            {
                var sourcePath = match.Value;
                if (sourceAliases.ContainsKey(sourcePath) || payloadAliases.ContainsKey(sourcePath))
                {
                    continue;
                }

                PckResourceIndex? dependencyIndex = null;
                ResourceAsset? dependency = null;
                var candidates = pending.Index == null
                    ? indexes
                    : indexes.Where(index => index.Mod.Id.Equals(
                        pending.Index.Mod.Id,
                        StringComparison.OrdinalIgnoreCase));
                foreach (var candidate in candidates)
                {
                    dependency = candidate.Assets.GetValueOrDefault(sourcePath) ??
                                 candidate.TryBuildAsset(sourcePath);
                    if (dependency != null)
                    {
                        dependencyIndex = candidate;
                        break;
                    }
                }

                if (dependency == null || dependencyIndex == null)
                {
                    continue;
                }

                IncludeAsset(dependencyIndex, dependency);
                foreach (var textureAsset in GetSiblingAtlasTextureAssets(dependencyIndex, sourcePath))
                {
                    IncludeAsset(dependencyIndex, textureAsset);
                }
            }
        }

        return result;

        void IncludeAsset(PckResourceIndex index, ResourceAsset asset)
        {
            foreach (var file in asset.Files)
            {
                // 挂在原始路径上的依赖副本同样重写文本引用：二进制资源
                // (.scn/.res) 内部无法重写，其回退引用会落到这些原始路径副本，
                // 重写后整条链都指向别名空间的新鲜副本，避免命中游戏缓存的
                // 原版贴图导致预览图混用资源。
                var dependencyBytes = file.Archive.ReadFile(file.Path);
                if (IsRewritableTextResource(file.Path))
                {
                    dependencyBytes = RewriteTextResource(
                        dependencyBytes,
                        sourceAliases,
                        payloadAliases);
                }

                result[file.Path] = dependencyBytes;
                var takeoverPath = NormalizeTakeoverPath(file.Path);
                if (!takeoverPath.Equals(file.Path, StringComparison.OrdinalIgnoreCase))
                {
                    result[takeoverPath] = dependencyBytes;
                }

                Enqueue(index, file);
            }
        }

        void Enqueue(PckResourceIndex? index, ResourceFile file)
        {
            var key = (index?.Mod.Id ?? file.Archive.Path) + "\n" + file.Path;
            if (queued.Add(key))
            {
                queue.Enqueue((index, file));
            }
        }
    }

    private static bool IsRewritableTextResource(string path) =>
        path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".spatlas", StringComparison.OrdinalIgnoreCase);

    private static bool MayContainResourceReferences(string path) =>
        path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".scn", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".res", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gdc", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".spatlas", StringComparison.OrdinalIgnoreCase);

    public RuntimeResourceOverlay BuildIsolatedCardResource(
        string groupId,
        string selectionId,
        string resourcePath,
        bool useSelectedProvider,
        string aliasToken)
    {
        var overlay = BuildIsolatedCardResources(
            groupId,
            selectionId,
            [resourcePath],
            useSelectedProvider,
            aliasToken);
        if (!overlay.ResourcePaths.ContainsKey(resourcePath))
        {
            throw new InvalidOperationException($"找不到独立卡牌资源：{resourcePath}");
        }

        return overlay;
    }

    /// <summary>
    /// Reads a provider's original raster card image without going through Godot's imported
    /// resource loader.  Exported card projects are allowed to ship a plain PNG/JPEG/WebP with
    /// no .import/.ctex pair; those files are valid card sources but cannot be loaded from a
    /// generated PCK via ResourceLoader.Load&lt;Texture2D&gt;.
    /// </summary>
    public bool TryReadCardImageBytes(
        string groupId,
        string selectionId,
        string resourcePath,
        bool useSelectedProvider,
        out byte[] bytes)
    {
        bytes = [];
        ResourceAsset? asset;
        if (useSelectedProvider)
        {
            var option = CardGroups
                .FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
                .Options.FirstOrDefault(candidate =>
                    candidate.Id.Equals(selectionId, StringComparison.OrdinalIgnoreCase));
            option ??= _pckCardOptions.FirstOrDefault(candidate =>
                candidate.Id.Equals(selectionId, StringComparison.OrdinalIgnoreCase));
            asset = option == null ? null : ResolveCardProviderAsset(option, resourcePath);
        }
        else
        {
            asset = ResolveBaseline(resourcePath);
        }

        var directFile = asset == null ? null : FindDirectFile(asset, resourcePath);
        if (directFile == null || !IsRasterImagePath(directFile.Path))
        {
            return false;
        }

        bytes = directFile.Archive.ReadFile(directFile.Path);
        return bytes.Length > 0;
    }

    private static bool IsRasterImagePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    public RuntimeResourceOverlay BuildIsolatedCardResources(
        string groupId,
        string selectionId,
        IEnumerable<string> resourcePaths,
        bool useSelectedProvider,
        string aliasToken,
        IReadOnlyDictionary<string, string>? existingResourcePaths = null)
    {
        CardSkinOption? option = null;
        if (useSelectedProvider)
        {
            option = CardGroups
                .FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))?
                .Options.FirstOrDefault(candidate =>
                    candidate.Id.Equals(selectionId, StringComparison.OrdinalIgnoreCase));
            option ??= _pckCardOptions.FirstOrDefault(candidate =>
                candidate.Id.Equals(selectionId, StringComparison.OrdinalIgnoreCase));
        }

        var resources = new List<RuntimeResource>();
        foreach (var resourcePath in resourcePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var asset = useSelectedProvider
                ? option == null ? null : ResolveCardProviderAsset(option, resourcePath)
                : ResolveBaseline(resourcePath);
            if (asset == null)
            {
                continue;
            }

            resources.Add(new RuntimeResource(
                resourcePath,
                FindDirectFile(asset, resourcePath),
                FindRemapFile(asset, resourcePath),
                GetImportedPayloadFiles(asset, resourcePath)));
        }

        if (resources.Count == 0)
        {
            throw new InvalidOperationException("找不到任何可隔离的卡牌资源。");
        }

        // Include the complete dependency chain for both selected and baseline portraits. The
        // baseline AtlasTexture files share a large game atlas; without aliasing that atlas first,
        // IgnoreDeep decodes and uploads a separate copy for every visible card.
        IncludeAliasedDependencyChain(
            useSelectedProvider && option != null
                ? new SkinOption(
                    option.Id,
                    option.Name,
                    option.Assets,
                    ProviderId: option.ProviderId)
                : null,
            resources,
            availableSourcePaths: existingResourcePaths?.Keys.ToHashSet(
                StringComparer.OrdinalIgnoreCase));

        return BuildAliasedResourceOverlay(
            resources,
            resources.Select(resource => resource.SourcePath).ToArray(),
            aliasToken,
            existingSourceAliases: existingResourcePaths);
    }

    private ResourceAsset? ResolveCardProviderAsset(CardSkinOption option, string resourcePath)
    {
        if (option.Assets.TryGetValue(resourcePath, out var configured))
        {
            return configured;
        }

        foreach (var index in _cosmeticIndexes.Where(index =>
                     string.Equals(
                         index.Mod.RootPath,
                         option.ProviderRootPath,
                         StringComparison.OrdinalIgnoreCase)))
        {
            if (index.Assets.TryGetValue(resourcePath, out var known))
            {
                return known;
            }

            var lazy = index.TryBuildAsset(resourcePath);
            if (lazy != null)
            {
                return lazy;
            }
        }

        return null;
    }

    private static RuntimeResourceOverlay BuildAliasedResourceOverlay(
        IReadOnlyCollection<RuntimeResource> resources,
        IReadOnlyCollection<string> resourcePaths,
        string aliasToken,
        bool redirectDirectScenesAtCanonicalPath = false,
        IReadOnlyDictionary<string, string>? existingSourceAliases = null)
    {
        var sourceAliases = existingSourceAliases == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(
                existingSourceAliases,
                StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resources)
        {
            sourceAliases[resource.SourcePath] = BuildRuntimeSourceAlias(resource, aliasToken);
        }
        var payloadAliases = resources
            .SelectMany(resource => resource.PayloadFiles)
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                file => file.Path,
                file => $"res://sts2_skin_runtime/{aliasToken}/_payload/{file.Path[6..]}",
                StringComparer.OrdinalIgnoreCase);
        var canReuseExternalDependencies =
            AliasedDependencyCachePolicy.CanReuseExternalDependencies(
                resources
                    .SelectMany(resource => new[] { resource.DirectFile, resource.RemapFile }
                        .Where(file => file != null)
                        .Cast<ResourceFile>()
                        .Concat(resource.PayloadFiles))
                    .DistinctBy(
                        file => file.Archive.Path + "\n" + file.Path,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(file => new AliasedDependencyReference(
                        IsRewritableTextResource(file.Path),
                        EnumerateDependencyPaths(file).ToArray())),
                sourceAliases.Keys.Concat(payloadAliases.Keys));

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var canonicalRedirectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resources)
        {
            if (resource.DirectFile != null)
            {
                var bytes = resource.DirectFile.Archive.ReadFile(resource.DirectFile.Path);
                files[sourceAliases[resource.SourcePath]] = RewriteAliasedResourceBytes(
                    resource.DirectFile.Path,
                    bytes,
                    sourceAliases,
                    payloadAliases);

                // A raw provider scene can coexist with a later-loaded Mod that supplies a
                // .tscn.remap for the same canonical path. Godot prefers the lingering remap,
                // so provider callbacks that load the canonical path after our initial rebuild
                // can silently receive another Mod's scene. Point the canonical scene at this
                // selection's isolated direct-file alias for the lifetime of the overlay too.
                if (redirectDirectScenesAtCanonicalPath &&
                    resource.SourcePath.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase))
                {
                    var directCanonicalRemapPath = resource.SourcePath + ".remap";
                    files[directCanonicalRemapPath] = Encoding.UTF8.GetBytes(
                        $"[remap]\n\npath=\"{sourceAliases[resource.SourcePath]}\"\n");
                    canonicalRedirectPaths.Add(directCanonicalRemapPath);
                }
            }

            foreach (var payloadFile in resource.PayloadFiles)
            {
                var bytes = payloadFile.Archive.ReadFile(payloadFile.Path);
                files[payloadAliases[payloadFile.Path]] = RewriteAliasedResourceBytes(
                    payloadFile.Path,
                    bytes,
                    sourceAliases,
                    payloadAliases,
                    rewriteOnlySpineAtlas: true,
                    stripUids: false);
            }

            if (resource.RemapFile == null)
            {
                continue;
            }

            var remapText = Encoding.UTF8.GetString(resource.RemapFile.Archive.ReadFile(resource.RemapFile.Path));
            var replacements = new Dictionary<string, string>(sourceAliases, StringComparer.OrdinalIgnoreCase);
            foreach (Match match in ResourcePathRegex().Matches(remapText))
            {
                var originalPath = match.Groups[1].Value;
                if (replacements.ContainsKey(originalPath))
                {
                    continue;
                }

                var payloadFile = MatchImportedFile(originalPath, resource.PayloadFiles);
                if (payloadFile != null)
                {
                    replacements[originalPath] = payloadAliases[payloadFile.Path];
                }
            }

            var remapSuffix = resource.RemapFile.Path.EndsWith(".import", StringComparison.OrdinalIgnoreCase)
                ? ".import"
                : ".remap";
            var rewrittenRemap =
                RewriteTextResource(Encoding.UTF8.GetBytes(remapText), replacements, null);
            files[sourceAliases[resource.SourcePath] + remapSuffix] = rewrittenRemap;

            // Binary .scn/.res payloads keep canonical external-resource strings internally and
            // cannot be safely rewritten byte-for-byte. While this runtime overlay is active,
            // make those canonical source paths resolve to the aliased payloads as well. This is
            // what makes the dependency isolation real when a different skin populated Godot's
            // cache before the hot switch.
            var canonicalRemapPath = resource.SourcePath + remapSuffix;
            files[canonicalRemapPath] = rewrittenRemap;
            canonicalRedirectPaths.Add(canonicalRemapPath);
        }

        // Return every discovered logical dependency, not just the requested roots. Runtime
        // callers can then explicitly rebind native resources (notably SpineSkeletonDataResource)
        // before a hot-swapped node enters the tree. Binary PackedScenes cannot have those paths
        // rewritten safely in-place.
        var aliasedResourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourcePath in resources
                     .Select(resource => resource.SourcePath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!sourceAliases.TryGetValue(resourcePath, out var aliasedResourcePath) ||
                (!files.ContainsKey(aliasedResourcePath) &&
                 !files.ContainsKey(aliasedResourcePath + ".import") &&
                 !files.ContainsKey(aliasedResourcePath + ".remap")))
            {
                throw new InvalidOperationException($"无法为 {resourcePath} 创建独立皮肤资源。");
            }

            aliasedResourcePaths[resourcePath] = aliasedResourcePath;
        }

        return new RuntimeResourceOverlay(
            aliasedResourcePaths,
            files,
            sourceAliases,
            payloadAliases,
            canonicalRedirectPaths,
            canReuseExternalDependencies);
    }

    internal static byte[] RewriteAliasedResourceBytes(
        string path,
        byte[] bytes,
        IReadOnlyDictionary<string, string> sourceAliases,
        IReadOnlyDictionary<string, string> payloadAliases,
        bool rewriteOnlySpineAtlas = false,
        bool stripUids = true)
    {
        var shouldRewrite = rewriteOnlySpineAtlas
            ? path.EndsWith(".spatlas", StringComparison.OrdinalIgnoreCase)
            : IsRewritableTextResource(path);
        return shouldRewrite
            ? RewriteTextResource(bytes, sourceAliases, payloadAliases, stripUids)
            : bytes;
    }

    private static string BuildRuntimeSourceAlias(RuntimeResource resource, string aliasToken)
    {
        // Imported resources can contain both a raw source file and a .import redirect whose
        // casing differs (Defect.png.import beside defect.png). Godot/Spine resolves the logical
        // imported path case-sensitively, so the redirect path is authoritative whenever present.
        // Plain direct resources still retain their concrete exported casing.
        var concretePath = resource.RemapFile != null
            ? StripResourceRedirectSuffix(NormalizeTakeoverPath(resource.RemapFile.Path))
            : resource.DirectFile != null
                ? NormalizeTakeoverPath(resource.DirectFile.Path)
                : resource.SourcePath;
        if (!concretePath.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            concretePath = resource.SourcePath;
        }

        return $"res://sts2_skin_runtime/{aliasToken}/{concretePath[6..]}";
    }

    private static string StripResourceRedirectSuffix(string path)
    {
        if (path.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
        {
            return path[..^7];
        }

        return path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase)
            ? path[..^6]
            : path;
    }

    private void IncludeAtlasTexturePages(SkinOption? selected, HashSet<string> sourcePaths)
    {
        var atlasDirectories = sourcePaths
            .Where(path => path.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase))
            .Select(path => path[..(path.LastIndexOf('/') + 1)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (atlasDirectories.Length == 0)
        {
            return;
        }

        var candidates = _baselineIndexes
            .SelectMany(index => index.Assets.Keys)
            .Concat(selected?.Assets.Keys ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!candidate.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                !candidate.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var directory in atlasDirectories)
            {
                if (!candidate.StartsWith(directory, StringComparison.OrdinalIgnoreCase) ||
                    candidate[directory.Length..].Contains('/'))
                {
                    continue;
                }

                sourcePaths.Add(candidate);
                break;
            }
        }
    }

    public void Dispose()
    {
        foreach (var index in _baselineIndexes.Skip(1).Concat(_cosmeticIndexes))
        {
            index.Dispose();
        }

        _gameArchive.Dispose();
    }

    private static IReadOnlyList<SkinGroup> BuildGroups(
        IEnumerable<PckResourceIndex> cosmeticIndexes,
        IEnumerable<PckResourceIndex>? baselineIndexes = null)
    {
        var indexes = cosmeticIndexes.ToArray();
        var baselines = (baselineIndexes ?? []).ToArray();
        var managedMonsterAssetGroups = BuildManagedMonsterAssetGroups(baselines);
        var knownGroupIds = baselines
            .SelectMany(index => index.Assets.Keys)
            .Select(TryGetDefinedBaselineGroup)
            .Where(group => group != null)
            .Cast<GroupIdentity>()
            .Select(group => group.Id)
            .Concat(managedMonsterAssetGroups.Values.Select(group => group.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownCharacterGroupIds = baselines
            .SelectMany(index => index.Assets.Keys)
            .Select(TryGetUnambiguousCharacterGroup)
            .Where(group => group != null)
            .Cast<GroupIdentity>()
            .Select(group => group.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, SkinGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in indexes)
        {
            var detectedPrimaryGroups = index.Assets.Keys
                .Select(path => TryGetPrimaryGroup(path) ??
                                managedMonsterAssetGroups.GetValueOrDefault(
                                    NormalizeTakeoverPath(path)))
                // A cosmetic provider may remap its private animation folders into ordinary
                // looking character/monster paths. Those folders are dependencies of a real
                // skin, not new game entities. Only IDs present in the game or a loaded gameplay
                // Mod baseline may create selectable groups; otherwise one complex provider can
                // leave phantom selections behind and keep its entire PCK active after switching
                // away, contaminating unrelated character and monster skins.
                .Where(group => group != null && knownGroupIds.Contains(group.Id))
                .Cast<GroupIdentity>()
                .DistinctBy(group => group.Id)
                .ToArray();
            var anchoredCharacterGroupIds = index.Assets.Keys
                .Select(path => TryGetCharacterVisualAnchorGroup(path, knownCharacterGroupIds))
                .Where(group => group != null)
                .Cast<GroupIdentity>()
                .Select(group => group.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var eligibleCharacterGroupIds = CharacterGroupEvidencePolicy.ResolveEligibleGroups(
                detectedPrimaryGroups
                    .Where(group => knownCharacterGroupIds.Contains(group.Id))
                    .Select(group => group.Id),
                anchoredCharacterGroupIds);
            var primaryGroups = detectedPrimaryGroups
                .Where(group =>
                    !knownCharacterGroupIds.Contains(group.Id) ||
                    eligibleCharacterGroupIds.Contains(group.Id))
                .ToArray();

            if (primaryGroups.Length == 0)
            {
                continue;
            }

            var assigned = new Dictionary<string, Dictionary<string, ResourceAsset>>(StringComparer.OrdinalIgnoreCase);
            foreach (var identity in primaryGroups)
            {
                assigned[identity.Id] = new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var asset in index.Assets.Values)
            {
                if (IsCardArtSourcePath(asset.SourcePath))
                {
                    continue;
                }

                var identity = TryGetPrimaryGroup(asset.SourcePath) ??
                               managedMonsterAssetGroups.GetValueOrDefault(
                                   NormalizeTakeoverPath(asset.SourcePath));
                if (identity != null &&
                    knownGroupIds.Contains(identity.Id) &&
                    assigned.TryGetValue(identity.Id, out var primaryAssets))
                {
                    primaryAssets[asset.SourcePath] = asset;
                    continue;
                }

                var tokenMatches = primaryGroups
                    .Where(group => ContainsGroupToken(asset.SourcePath, group.Id))
                    .ToArray();
                if (tokenMatches.Length == 1)
                {
                    assigned[tokenMatches[0].Id][asset.SourcePath] = asset;
                }
            }

            foreach (var identity in primaryGroups)
            {
                var assets = assigned[identity.Id];
                if (assets.Count == 0)
                {
                    continue;
                }

                if (!groups.TryGetValue(identity.Id, out var group))
                {
                    group = new SkinGroup(identity.Id, identity.DisplayName);
                    groups.Add(identity.Id, group);
                }

                group.Options.Add(new SkinOption(
                    index.Mod.Id,
                    index.Mod.Name,
                    assets,
                    IsCharacterIconOnly:
                        knownCharacterGroupIds.Contains(identity.Id) &&
                        !anchoredCharacterGroupIds.Contains(identity.Id) &&
                        assets.Keys.Any(IsCharacterIconSourcePath)));
            }
        }

        AddPckRuntimeProviderOptions(indexes, baselines, groups, knownGroupIds);
        AddDirectCharacterRuntimeProviderOptions(
            indexes,
            groups,
            knownCharacterGroupIds);
        AddManagedMonsterSceneOptions(indexes, groups, knownGroupIds);
        AddRuntimeMonsterVisualModeOptions(indexes, groups);

        foreach (var group in groups.Values)
        {
            group.Options.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
        }

        return groups.Values
            .OrderBy(group => GroupSortOrder(group.Id))
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static void AddDirectCharacterRuntimeProviderOptions(
        IReadOnlyCollection<PckResourceIndex> indexes,
        IDictionary<string, SkinGroup> groups,
        IReadOnlySet<string> knownCharacterGroupIds)
    {
        foreach (var index in indexes.Where(index => index.Mod.HasDll && index.Mod.RootPath != null))
        {
            var primaryAssembly = System.IO.Path.Combine(
                index.Mod.RootPath!,
                index.Mod.ResourceNamespaceId + ".dll");
            var assemblyPaths = File.Exists(primaryAssembly)
                ? [primaryAssembly]
                : Directory.EnumerateFiles(
                        index.Mod.RootPath!,
                        "*.dll",
                        SearchOption.TopDirectoryOnly)
                    .ToArray();
            if (!assemblyPaths.Any(HasDirectVisualHarmonyPatch))
            {
                continue;
            }

            var targetGroupIds = DirectCharacterRuntimeTargetScanner.Scan(
                index.Mod.RootPath,
                index.Mod.ResourceNamespaceId,
                knownCharacterGroupIds);
            foreach (var targetGroupId in targetGroupIds)
            {
                if (!groups.TryGetValue(targetGroupId, out var group))
                {
                    group = new SkinGroup(targetGroupId, DisplayName(targetGroupId));
                    groups.Add(targetGroupId, group);
                }

                var existingIndex = group.Options.FindIndex(option =>
                    option.Id.Equals(index.Mod.Id, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    group.Options[existingIndex] = group.Options[existingIndex] with
                    {
                        IsRuntimeProvider = true,
                        IsDirectCharacterRuntimeProvider = true,
                        IsCharacterIconOnly = false
                    };
                    continue;
                }

                // Some full character skins never replace a canonical game resource. Their DLL
                // hides the stock model and instantiates a private scene in combat, character
                // select, shops and rest sites. Keep an explicit zero-asset option so selection
                // still mounts the complete provider PCK and activates its original presentation
                // callbacks only for the declared target character.
                group.Options.Add(new SkinOption(
                    index.Mod.Id,
                    index.Mod.Name,
                    new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase),
                    IsRuntimeProvider: true,
                    IsDirectCharacterRuntimeProvider: true));
            }
        }
    }

    private static IReadOnlyDictionary<string, GroupIdentity> BuildManagedMonsterAssetGroups(
        IEnumerable<PckResourceIndex> baselineIndexes)
    {
        var candidates = new Dictionary<string, List<GroupIdentity>>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in baselineIndexes.Where(index =>
                     index.Mod.AffectsGameplay && index.Mod.HasDll))
        {
            foreach (var declaration in ManagedMonsterSceneScanner.ScanDeclaredAssets(
                         index.Mod.RootPath,
                         index.Mod.ResourceNamespaceId))
            {
                var groupId = declaration.ModelTypeName.ToLowerInvariant();
                var identity = new GroupIdentity(groupId, DisplayName(groupId));
                foreach (var resourcePath in declaration.ResourcePaths)
                {
                    var path = NormalizeTakeoverPath(resourcePath);
                    if (!candidates.TryGetValue(path, out var owners))
                    {
                        owners = [];
                        candidates.Add(path, owners);
                    }

                    if (owners.All(owner => !owner.Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        owners.Add(identity);
                    }
                }
            }
        }

        // If two gameplay monsters intentionally share the same canonical image, ownership is
        // ambiguous and the resource must not be assigned to either selectable skin group.
        return candidates
            .Where(pair => pair.Value.Count == 1)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value[0],
                StringComparer.OrdinalIgnoreCase);
    }

    private static void AddManagedMonsterSceneOptions(
        IReadOnlyCollection<PckResourceIndex> indexes,
        IDictionary<string, SkinGroup> groups,
        IReadOnlySet<string> knownGroupIds)
    {
        foreach (var index in indexes.Where(index => index.Mod.HasDll))
        {
            var replacements = ManagedMonsterSceneScanner.Scan(
                index.Mod.RootPath,
                index.Mod.ResourceNamespaceId);
            foreach (var replacement in replacements)
            {
                var sceneAsset = index.Assets.GetValueOrDefault(replacement.ScenePath) ??
                                 index.TryBuildAsset(replacement.ScenePath);
                if (sceneAsset == null)
                {
                    continue;
                }

                var modelToken = NormalizeResourceToken(replacement.ModelTypeName);
                var group = groups.Values.FirstOrDefault(candidate =>
                    NormalizeResourceToken(candidate.Id).Equals(
                        modelToken,
                        StringComparison.OrdinalIgnoreCase));
                if (group == null)
                {
                    var groupId = replacement.ModelTypeName.ToLowerInvariant();
                    if (!knownGroupIds.Contains(groupId))
                    {
                        continue;
                    }

                    group = new SkinGroup(groupId, DisplayName(groupId));
                    groups.Add(groupId, group);
                }

                var existingIndex = group.Options.FindIndex(option =>
                    option.Id.Equals(index.Mod.Id, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    group.Options[existingIndex] = group.Options[existingIndex] with
                    {
                        IsRuntimeProvider = true,
                        ManagedMonsterScene = sceneAsset
                    };
                }
                else
                {
                    group.Options.Add(new SkinOption(
                        index.Mod.Id,
                        index.Mod.Name,
                        new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase),
                        IsRuntimeProvider: true,
                        ManagedMonsterScene: sceneAsset));
                }
            }

            AddManagedMonsterRuntimeProfileOptions(index, groups, knownGroupIds);
        }
    }

    private static void AddManagedMonsterRuntimeProfileOptions(
        PckResourceIndex index,
        IDictionary<string, SkinGroup> groups,
        IReadOnlySet<string> knownGroupIds)
    {
        var profiles = ManagedMonsterSceneScanner.ScanRuntimeProfiles(
            index.Mod.RootPath,
            index.Mod.ResourceNamespaceId);
        if (profiles.Count == 0)
        {
            return;
        }

        var routedProfiles = profiles
            .Select(profile =>
            {
                var identity = TryGetPrimaryGroup(profile.TargetScenePath);
                var assets = profile.ProviderResourcePaths
                    .Select(index.TryBuildAsset)
                    .Where(asset => asset != null)
                    .Cast<ResourceAsset>()
                    .DistinctBy(asset => asset.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return (Profile: profile, Identity: identity, Assets: assets);
            })
            // The private visual resource is the second independent signal that this is a real
            // data-driven skin profile rather than an unrelated reference to a game scene.
            .Where(entry => entry.Identity != null &&
                            knownGroupIds.Contains(entry.Identity.Id) &&
                            entry.Assets.Length > 0)
            .ToArray();
        if (routedProfiles.Length == 0)
        {
            return;
        }

        var targetGroupIds = routedProfiles
            .Select(entry => entry.Identity!.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var providerResourcePrefixes = routedProfiles
            .SelectMany(entry => entry.Profile.ProviderResourcePaths)
            .Select(GetProviderResourcePrefix)
            .Where(prefix => prefix != null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The ordinary path scanner sees folders such as animations/monsters/<foreign model id>
        // and would otherwise expose those source-model ids as phantom STS2 monsters. Once an
        // explicit runtime profile has mapped them to canonical creature scenes, remove only the
        // provider-private phantom options and leave canonical/data-only monster packs untouched.
        foreach (var group in groups.Values.Where(group => !targetGroupIds.Contains(group.Id)).ToArray())
        {
            group.Options.RemoveAll(option =>
                option.Id.Equals(index.Mod.Id, StringComparison.OrdinalIgnoreCase) &&
                option.Assets.Count > 0 &&
                option.Assets.Values.All(asset => providerResourcePrefixes.Any(prefix =>
                    asset.SourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))));
            if (group.Options.Count == 0)
            {
                groups.Remove(group.Id);
            }
        }

        foreach (var entry in routedProfiles)
        {
            var identity = entry.Identity!;
            if (!groups.TryGetValue(identity.Id, out var group))
            {
                group = new SkinGroup(identity.Id, identity.DisplayName);
                groups.Add(identity.Id, group);
            }

            var profileAssets = entry.Assets.ToDictionary(
                asset => asset.SourcePath,
                StringComparer.OrdinalIgnoreCase);
            var existingIndex = group.Options.FindIndex(option =>
                option.Id.Equals(index.Mod.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                var existing = group.Options[existingIndex];
                var mergedAssets = new Dictionary<string, ResourceAsset>(
                    existing.Assets,
                    StringComparer.OrdinalIgnoreCase);
                foreach (var asset in profileAssets)
                {
                    mergedAssets[asset.Key] = asset.Value;
                }

                group.Options[existingIndex] = existing with
                {
                    Assets = mergedAssets,
                    IsRuntimeProvider = true,
                    IsManagedMonsterRuntimeProfile = true
                };
            }
            else
            {
                group.Options.Add(new SkinOption(
                    index.Mod.Id,
                    index.Mod.Name,
                    profileAssets,
                    IsRuntimeProvider: true,
                    IsManagedMonsterRuntimeProfile: true));
            }
        }

        static string? GetProviderResourcePrefix(string resourcePath)
        {
            const string prefix = "res://";
            if (!resourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var separator = resourcePath.IndexOf('/', prefix.Length);
            return separator < 0 ? null : resourcePath[..(separator + 1)];
        }
    }

    private static void AddRuntimeMonsterVisualModeOptions(
        IReadOnlyCollection<PckResourceIndex> indexes,
        IDictionary<string, SkinGroup> groups)
    {
        var indexesByProvider = indexes.ToDictionary(
            index => index.Mod.Id,
            StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups.Values)
        {
            for (var optionIndex = group.Options.Count - 1; optionIndex >= 0; optionIndex--)
            {
                var option = group.Options[optionIndex];
                if (!indexesByProvider.TryGetValue(option.Id, out var index))
                {
                    continue;
                }

                var modes = RuntimeMonsterVisualModeScanner.Scan(
                    index,
                    index.Mod.Id,
                    option);
                if (modes.Count < 2)
                {
                    continue;
                }

                var defaultMode = modes.FirstOrDefault(mode =>
                                      mode.ModeName.Contains("Default", StringComparison.OrdinalIgnoreCase) ||
                                      mode.ModeName.Contains("Performance", StringComparison.OrdinalIgnoreCase)) ??
                                  modes[0];
                group.Options.RemoveAt(optionIndex);
                for (var modeIndex = modes.Count - 1; modeIndex >= 0; modeIndex--)
                {
                    var mode = modes[modeIndex];
                    var modeAssets = new Dictionary<string, ResourceAsset>(
                        option.Assets,
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var resourcePath in mode.ResourcePaths)
                    {
                        var asset = index.TryBuildAsset(resourcePath);
                        if (asset != null)
                        {
                            modeAssets[resourcePath] = asset;
                        }
                    }

                    // Keep the original provider id for the provider's default/performance mode so existing
                    // selections remain valid. Other modes are independent persistent choices.
                    var id = mode == defaultMode
                        ? option.Id
                        : $"{option.Id}::visual-mode:{mode.ModeName.ToLowerInvariant()}";
                    group.Options.Insert(optionIndex, option with
                    {
                        Id = id,
                        Name = option.Name + " · " + mode.DisplayName,
                        Assets = modeAssets,
                        RuntimeMonsterVisualMode = mode,
                        ProviderId = mode.ProviderId
                    });
                }
            }
        }
    }

    private static IReadOnlyList<CardSkinGroup> BuildCardGroups(IEnumerable<PckResourceIndex> cosmeticIndexes)
    {
        var groups = new Dictionary<string, CardSkinGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in cosmeticIndexes)
        {
            foreach (var configPath in index.Archive.Paths.Where(path =>
                         path.EndsWith("/card_replacements.json", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var config = DeserializeCardJson<CardReplacementConfig>(
                        index.Archive.ReadFile(configPath));
                    if (config == null)
                    {
                        continue;
                    }

                    // CardPortraitsCore treats differential and Ancient entries as optional,
                    // per-card modes. The PCK option builder below exposes those modes as
                    // independent SkinChanger sources. Letting this legacy path also fold the
                    // Ancient entry into the base provider would force its portrait/layout back
                    // onto the normal source when FinalizeCardGroups merges matching IDs.
                    if (config.AncientReplacements.Count > 0 ||
                        config.NormalReplacements.Any(entry =>
                            !string.IsNullOrWhiteSpace(entry.DifferentialPortrait)))
                    {
                        continue;
                    }

                    var groupIds = config.NormalReplacements
                        .Select(entry => TryGetCardPortraitGroup(entry.PortraitPath))
                        .Concat(config.AncientReplacements.SelectMany(entry => new[]
                        {
                            TryGetCardPortraitGroup(entry.NormalPortrait),
                            TryGetCardPortraitGroup(entry.AncientPortrait)
                        }))
                        .Where(id => id != null)
                        .Cast<string>()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    foreach (var groupId in groupIds)
                    {
                        var normal = config.NormalReplacements
                            .Where(entry => TryGetCardPortraitGroup(entry.PortraitPath)?.Equals(
                                groupId, StringComparison.OrdinalIgnoreCase) == true)
                            .Where(entry => !string.IsNullOrWhiteSpace(entry.CardType) &&
                                            !string.IsNullOrWhiteSpace(entry.PortraitPath))
                            .GroupBy(entry => entry.CardType, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                entries => entries.Key,
                                entries => entries.Last().PortraitPath,
                                StringComparer.OrdinalIgnoreCase);
                        var ancient = config.AncientReplacements
                            .Where(entry => TryGetCardPortraitGroup(entry.PathForGrouping)?.Equals(
                                groupId, StringComparison.OrdinalIgnoreCase) == true)
                            .Where(entry => !string.IsNullOrWhiteSpace(entry.CardType))
                            .GroupBy(entry => entry.CardType, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                entries => entries.Key,
                                entries => new AncientCardPortrait(
                                    entries.Last().NormalPortrait,
                                    entries.Last().AncientPortrait),
                                StringComparer.OrdinalIgnoreCase);
                        var presentations = config.AncientReplacements
                            .Where(entry => TryGetCardPortraitGroup(entry.PathForGrouping)?.Equals(
                                groupId, StringComparison.OrdinalIgnoreCase) == true)
                            .Where(entry => !string.IsNullOrWhiteSpace(entry.CardType))
                            .GroupBy(
                                entry => NormalizeCardPresentationType(entry.CardType),
                                StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                entries => entries.Key,
                                _ => new CardPresentationDefinition(UseAncientLayout: true),
                                StringComparer.OrdinalIgnoreCase);
                        if (normal.Count == 0 && ancient.Count == 0)
                        {
                            continue;
                        }

                        if (!groups.TryGetValue(groupId, out var group))
                        {
                            group = new CardSkinGroup(groupId, DisplayName(groupId));
                            groups.Add(groupId, group);
                        }

                        var existingIndex = group.Options.FindIndex(option =>
                            option.Id.Equals(index.Mod.Id, StringComparison.OrdinalIgnoreCase));
                        var option = new CardSkinOption(
                            index.Mod.Id,
                            index.Mod.Name,
                            normal,
                            ancient,
                            ProviderRootPath: index.Mod.RootPath,
                            ProviderId: index.Mod.Id,
                            Presentations: presentations);
                        if (existingIndex >= 0)
                        {
                            group.Options[existingIndex] = group.Options[existingIndex].Merge(option);
                        }
                        else
                        {
                            group.Options.Add(option);
                        }
                    }
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine($"无法读取卡牌皮肤配置 {configPath}: {exception.Message}");
                }
            }
        }

        foreach (var group in groups.Values)
        {
            group.Options.Sort((left, right) =>
                string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
        }

        return groups.Values
            .OrderBy(group => GroupSortOrder(group.Id))
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<CardSkinOption> BuildPckCardOptions(
        IEnumerable<PckResourceIndex> cosmeticIndexes,
        IReadOnlyList<PckResourceIndex>? baselineIndexes = null)
    {
        var options = new List<CardSkinOption>();
        var knownCardGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 基线按“游戏原版 -> 玩法 Mod”的顺序建立。逐个扩充已知颜色，既能让
        // 后续玩法 Mod 添加新角色颜色，也不会把其前置画风目录误登记成颜色。
        foreach (var baselineIndex in baselineIndexes ?? [])
        {
            foreach (var path in baselineIndex.Archive.Paths
                         .Select(NormalizeIndexedSourcePath)
                         .Where(IsCardArtSourcePath))
            {
                var category = TryGetCardArtPathLayout(path, knownCardGroups)?.Category;
                if (!string.IsNullOrWhiteSpace(category))
                {
                    knownCardGroups.Add(category);
                }
            }
        }
        foreach (var index in cosmeticIndexes)
        {
            var providerBehavior = ProviderCardBehaviorScanner.Scan(
                index.Mod.RootPath,
                index.Assets.Values);
            var exportedPortraits = LoadExportedCardPortraits(index);
            var normalPortraits = new Dictionary<string, string>(
                exportedPortraits.Normal,
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in ManagedCardPortraitReplacementScanner.Scan(
                         index.Mod.RootPath,
                         index.Mod.ResourceNamespaceId))
            {
                // Only accept DLL declarations backed by this provider's own PCK. Besides
                // preventing stale paths from becoming blank card art, TryBuildAsset registers
                // private non-standard folders so isolated loading can resolve them later.
                if (index.TryBuildAsset(pair.Value) != null)
                {
                    normalPortraits[pair.Key] = pair.Value;
                }
            }
            var detectedPresentations = LoadCardPresentations(
                index,
                providerBehavior.Presentations,
                normalPortraits.Keys
                    .Concat(exportedPortraits.Ancient.Keys)
                    .Concat(exportedPortraits.Modes.SelectMany(mode => mode.Portraits.Keys))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            var optionalAncientCards = exportedPortraits.Modes
                .Where(mode => mode.UseAncientLayout)
                .SelectMany(mode => mode.Portraits.Keys)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var presentations = detectedPresentations.ToDictionary(
                pair => pair.Key,
                pair => optionalAncientCards.Contains(pair.Key)
                    ? pair.Value with { UseAncientLayout = false }
                    : pair.Value,
                StringComparer.OrdinalIgnoreCase);
            var standardAssets = index.Assets.Values
                .Where(asset => IsCardArtSourcePath(asset.SourcePath))
                .ToArray();
            var looseCandidates = index.Assets.Values
                .Where(asset => IsLooseProviderCardArtPath(
                    index.Mod.ResourceNamespaceId,
                    asset.SourcePath))
                .ToArray();
            var looseAssets = IsBulkLooseCardPack(index.Mod.ResourceNamespaceId, looseCandidates)
                ? looseCandidates
                : [];
            var allAssets = standardAssets
                .Concat(looseAssets)
                .DistinctBy(asset => asset.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var changedAssets = baselineIndexes == null
                ? allAssets
                : allAssets.Where(asset => AssetDiffersFromBaseline(asset, baselineIndexes)).ToArray();
            if (changedAssets.Length == 0 &&
                presentations.Count == 0 &&
                normalPortraits.Count == 0 &&
                exportedPortraits.Ancient.Count == 0 &&
                exportedPortraits.Modes.Count == 0)
            {
                continue;
            }

            var originalVariantKeys = allAssets
                .Select(asset => GetCardVariantKey(asset.SourcePath, knownCardGroups))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var variants = changedAssets
                .GroupBy(
                    asset => GetCardVariantKey(asset.SourcePath, knownCardGroups),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => new CardArtVariant(
                    group.Key,
                    group.ToArray(),
                    group.Select(asset => TryGetCardArtIdentity(
                            asset.SourcePath,
                            knownCardGroups)?.Stem)
                        .Where(stem => !string.IsNullOrEmpty(stem))
                        .Cast<string>()
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)))
                .OrderBy(group => group.Key.Length == 0 ? 0 : 1)
                .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            // An exported project is already one intentional package. Its manifest is
            // authoritative, so filename-derived variant splitting must not fragment it.
            var splitVariants = normalPortraits.Count == 0 &&
                                exportedPortraits.Ancient.Count == 0 &&
                                variants.Length > 1 &&
                                VariantsOverlap(variants);
            var optionVariants = splitVariants
                ? variants
                :
                [
                    new CardArtVariant(
                        variants.Length == 1 ? variants[0].Key : string.Empty,
                        changedAssets,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                ];
            var exposeVariants = splitVariants ||
                                 (optionVariants[0].Key.Length > 0 && originalVariantKeys.Length > 1);
            var optionCount = optionVariants.Length + exportedPortraits.Modes.Count;
            var unnamedOptionOrdinal = 0;
            foreach (var variant in optionVariants)
            {
                var variantId = optionVariants.Length == 1 || variant.Key.Length == 0
                    ? index.Mod.Id
                    : index.Mod.Id + "::variant:" + variant.Key.ToLowerInvariant();
                var namedVariant = exposeVariants && variant.Key.Length > 0
                    ? DisplayCardVariant(variant.Key)
                    : null;
                var variantName = CardSkinOptionNamingPolicy.Build(
                    index.Mod.Name,
                    namedVariant,
                    namedVariant == null ? ++unnamedOptionOrdinal : 0,
                    optionCount);
                var option = new CardSkinOption(
                    variantId,
                    variantName,
                    new Dictionary<string, string>(
                        normalPortraits,
                        StringComparer.OrdinalIgnoreCase),
                    MergeAncientPortraits(
                        providerBehavior.AncientPortraits,
                        exportedPortraits.Ancient)
                        .Where(pair => !splitVariants || variant.Stems.Contains(
                            NormalizeCardPresentationType(pair.Key)))
                        .ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.OrdinalIgnoreCase),
                    variant.Assets.ToDictionary(
                        asset => asset.SourcePath,
                        asset => asset,
                        StringComparer.OrdinalIgnoreCase),
                    index.Mod.RootPath,
                    index.Mod.Id,
                    Presentations: presentations
                        .Where(pair => !splitVariants || variant.Stems.Contains(
                            NormalizeCardPresentationType(pair.Key)))
                        .ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.OrdinalIgnoreCase));
                options.AddRange(CardLayoutVariantPolicy.Expand(option, index.Mod.ResourceNamespaceId));
            }

            foreach (var mode in exportedPortraits.Modes)
            {
                var modePresentations = new Dictionary<string, CardPresentationDefinition>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var cardType in mode.Portraits.Keys)
                {
                    if (detectedPresentations.TryGetValue(cardType, out var detected))
                    {
                        modePresentations[cardType] = detected with
                        {
                            UseAncientLayout = mode.UseAncientLayout
                        };
                    }
                    else if (mode.UseAncientLayout)
                    {
                        modePresentations[cardType] = new CardPresentationDefinition(
                            UseAncientLayout: true);
                    }
                }

                var modeAssets = new Dictionary<string, ResourceAsset>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var portraitPath in mode.Portraits.Values)
                {
                    if (index.TryBuildAsset(portraitPath) is { } asset)
                    {
                        modeAssets[asset.SourcePath] = asset;
                    }
                }

                options.Add(new CardSkinOption(
                    index.Mod.Id + "::portrait-mode:" + mode.IdSuffix,
                    CardSkinOptionNamingPolicy.Build(
                        index.Mod.Name,
                        namedVariant: null,
                        ++unnamedOptionOrdinal,
                        optionCount),
                    new Dictionary<string, string>(
                        mode.Portraits,
                        StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, AncientCardPortrait>(
                        StringComparer.OrdinalIgnoreCase),
                    modeAssets,
                    index.Mod.RootPath,
                    index.Mod.Id,
                    Presentations: modePresentations));
            }
        }

        return options;
    }

    private static IReadOnlyDictionary<string, AncientCardPortrait> MergeAncientPortraits(
        IReadOnlyDictionary<string, AncientCardPortrait> inferred,
        IReadOnlyDictionary<string, AncientCardPortrait> exported)
    {
        if (inferred.Count == 0)
        {
            return exported;
        }

        if (exported.Count == 0)
        {
            return inferred;
        }

        var merged = new Dictionary<string, AncientCardPortrait>(
            inferred,
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in exported)
        {
            if (merged.TryGetValue(pair.Key, out var existing))
            {
                merged[pair.Key] = new AncientCardPortrait(
                    pair.Value.NormalPortrait ?? existing.NormalPortrait,
                    pair.Value.AncientPortrait ?? existing.AncientPortrait);
            }
            else
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return merged;
    }

    private static string NormalizeIndexedSourcePath(string path)
    {
        if (path.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
        {
            return path[..^7];
        }

        return path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase)
            ? path[..^6]
            : path;
    }

    private static IReadOnlyDictionary<string, CardPresentationDefinition> LoadCardPresentations(
        PckResourceIndex index,
        IReadOnlyDictionary<string, CardPresentationDefinition>? providerBehavior = null,
        IEnumerable<string>? declaredCardTypes = null)
    {
        var presentations = new Dictionary<string, CardPresentationDefinition>(
            StringComparer.OrdinalIgnoreCase);
        var explicitUiModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configPath in index.Archive.Paths.Where(path =>
                     path.EndsWith("/frame_replacements.json", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith("/framed_card_project.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var document = DeserializeCardJson<CardFrameReplacementDocument>(
                    index.Archive.ReadFile(configPath));
                if (document == null)
                {
                    continue;
                }

                foreach (var entry in document.Entries.Where(entry =>
                             !string.IsNullOrWhiteSpace(entry.CardId)))
                {
                    var cardType = NormalizeCardPresentationType(entry.CardId);
                    presentations[cardType] =
                        new CardPresentationDefinition(
                            entry.UiMode.Equals("Ancient", StringComparison.OrdinalIgnoreCase),
                            EmptyToNull(entry.Frame),
                            EmptyToNull(entry.FrameMaterial),
                            EmptyToNull(entry.BannerTexture),
                            EmptyToNull(entry.BannerMaterial),
                            EmptyToNull(entry.PortraitBorder),
                            EmptyToNull(entry.PortraitBorderMaterial),
                            EmptyToNull(entry.AncientTextBg),
                            EmptyToNull(entry.TextBackgroundMaterial),
                            EmptyToNull(entry.EnergyIcon),
                            EmptyToNull(entry.Highlight),
                            EmptyToNull(entry.HighlightMaterial),
                            entry.FrameVisible,
                            entry.BannerVisible,
                            entry.TextBackgroundVisible,
                            entry.PortraitBorderVisible,
                            entry.EnergyIconVisible,
                            entry.HighlightVisible,
                            entry.TypePlaqueVisible,
                            entry.TypeLabelVisible,
                            entry.DescriptionVisible,
                            entry.InfectionOverlayVisible);
                    if (!string.IsNullOrWhiteSpace(entry.UiMode))
                    {
                        explicitUiModes.Add(cardType);
                    }
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"无法读取卡牌呈现配置 {configPath}: {exception.Message}");
            }
        }

        var knownCardStems = index.Assets.Keys
            .Select(path => TryGetCardArtIdentity(path))
            .Where(identity => identity != null)
            .Select(identity => identity!.Stem)
            .Concat(declaredCardTypes ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var inferred in ManagedCardPresentationScanner.Scan(
                     index.Mod.RootPath,
                     knownCardStems,
                     declaredCardTypes?.ToArray()))
        {
            if (presentations.TryGetValue(inferred.Key, out var configured))
            {
                // An omitted uiMode is not an explicit request for the normal layout. Exported
                // card managers commonly keep frame visibility in JSON while routing the same
                // declared portraits to AncientPortrait from their disabled DLL patch. Preserve
                // the inferred Ancient-vs-expanded intent; a non-empty uiMode remains
                // authoritative because it is the provider's explicit layout declaration.
                if (!explicitUiModes.Contains(inferred.Key) &&
                    (inferred.Value.UseAncientLayout ||
                     inferred.Value.UseExpandedPortraitLayout))
                {
                    presentations[inferred.Key] = configured with
                    {
                        UseAncientLayout = inferred.Value.UseAncientLayout,
                        UseExpandedPortraitLayout = inferred.Value.UseExpandedPortraitLayout
                    };
                }
            }
            else
            {
                presentations.Add(inferred.Key, inferred.Value);
            }
        }

        if (providerBehavior != null)
        {
            foreach (var inferred in providerBehavior)
            {
                // Exported declarative manifests remain authoritative. Sidecar behavior only
                // restores provider intent that would disappear with its patches disabled.
                presentations.TryAdd(inferred.Key, inferred.Value);
            }
        }

        return presentations;
    }

    private static T? DeserializeCardJson<T>(byte[] bytes)
    {
        return JsonSerializer.Deserialize<T>(
            StripUtf8Bom(bytes).Span,
            CardReplacementJsonOptions);
    }

    private static ReadOnlyMemory<byte> StripUtf8Bom(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            ? bytes.AsMemory(Encoding.UTF8.Preamble.Length)
            : bytes;

    private static ExportedCardPortraits LoadExportedCardPortraits(
        PckResourceIndex index)
    {
        var normalPortraits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ancientPortraits = new Dictionary<string, AncientCardPortrait>(
            StringComparer.OrdinalIgnoreCase);
        var differentialPortraits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ancientStylePortraits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ancientDifferentialPortraits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var configPath in index.Archive.Paths.Where(path =>
                     path.EndsWith("/card_replacements.json", StringComparison.OrdinalIgnoreCase)))
        {
            ReadExportedPortraitEntries(
                index,
                configPath,
                normalPortraits,
                ancientPortraits,
                ["image", "portrait"],
                ["ancientImage", "ancientPortrait"],
                requireStaticKind: true,
                overwrite: true);
            ReadCardPortraitsCoreEntries(
                index,
                configPath,
                normalPortraits,
                differentialPortraits,
                ancientStylePortraits,
                ancientDifferentialPortraits);
        }

        foreach (var configPath in index.Archive.Paths.Where(path =>
                     path.EndsWith("/framed_card_project.json", StringComparison.OrdinalIgnoreCase)))
        {
            ReadExportedPortraitEntries(
                index,
                configPath,
                normalPortraits,
                ancientPortraits,
                ["portrait", "image"],
                [],
                requireStaticKind: false,
                overwrite: true);
        }

        foreach (var configPath in index.Archive.Paths.Where(path =>
                     path.EndsWith("/animations/card_animations.json", StringComparison.OrdinalIgnoreCase)))
        {
            // SkinChanger does not play an isolated export's timeline yet, but its
            // declared fallback still gives the player a correct static portrait.
            ReadExportedPortraitEntries(
                index,
                configPath,
                normalPortraits,
                ancientPortraits,
                ["fallbackImage", "image", "portrait"],
                [],
                requireStaticKind: false,
                overwrite: false);
        }

        var modes = new List<ExportedCardPortraitMode>();
        if (differentialPortraits.Count > 0)
        {
            modes.Add(new ExportedCardPortraitMode(
                "differential",
                "{skin-changer-differential}",
                differentialPortraits,
                UseAncientLayout: false));
        }
        if (ancientStylePortraits.Count > 0)
        {
            modes.Add(new ExportedCardPortraitMode(
                "ancient",
                "{skin-changer-ancient-style}",
                ancientStylePortraits,
                UseAncientLayout: true));
        }
        if (ancientDifferentialPortraits.Count > 0)
        {
            modes.Add(new ExportedCardPortraitMode(
                "ancient-differential",
                "{skin-changer-ancient-differential}",
                ancientDifferentialPortraits,
                UseAncientLayout: true));
        }

        return new ExportedCardPortraits(normalPortraits, ancientPortraits, modes);
    }

    private static void ReadCardPortraitsCoreEntries(
        PckResourceIndex index,
        string configPath,
        IDictionary<string, string> normalPortraits,
        IDictionary<string, string> differentialPortraits,
        IDictionary<string, string> ancientStylePortraits,
        IDictionary<string, string> ancientDifferentialPortraits)
    {
        try
        {
            using var document = JsonDocument.Parse(StripUtf8Bom(
                index.Archive.ReadFile(configPath)));
            if (TryGetJsonProperty(document.RootElement, "normalReplacements", out var normalEntries) &&
                normalEntries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in normalEntries.EnumerateArray())
                {
                    var cardId = TryGetJsonString(entry, "cardType");
                    if (string.IsNullOrWhiteSpace(cardId))
                    {
                        continue;
                    }

                    var cardType = NormalizeCardPresentationType(cardId);
                    AddOwnedPortrait(index, entry, "portraitPath", cardType, normalPortraits);
                    AddOwnedPortrait(index, entry, "differentialPortrait", cardType, differentialPortraits);
                }
            }

            if (!TryGetJsonProperty(document.RootElement, "ancientReplacements", out var ancientEntries) ||
                ancientEntries.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in ancientEntries.EnumerateArray())
            {
                var cardId = TryGetJsonString(entry, "cardType");
                if (string.IsNullOrWhiteSpace(cardId))
                {
                    continue;
                }

                var cardType = NormalizeCardPresentationType(cardId);
                AddOwnedPortrait(index, entry, "normalPortrait", cardType, normalPortraits);
                var hasAncientPortrait = AddOwnedPortrait(
                    index,
                    entry,
                    "ancientPortrait",
                    cardType,
                    ancientStylePortraits);
                if (hasAncientPortrait)
                {
                    AddOwnedPortrait(
                        index,
                        entry,
                        "differentialPortrait",
                        cardType,
                        ancientDifferentialPortraits);
                }
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"无法读取 CardPortraitsCore 配置 {configPath}: {exception.Message}");
        }

        static bool AddOwnedPortrait(
            PckResourceIndex index,
            JsonElement entry,
            string property,
            string cardType,
            IDictionary<string, string> portraits)
        {
            var path = TryGetJsonString(entry, property);
            if (string.IsNullOrWhiteSpace(path) ||
                !path.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
                index.TryBuildAsset(path) == null)
            {
                return false;
            }

            portraits[cardType] = path;
            return true;
        }
    }

    private static void ReadExportedPortraitEntries(
        PckResourceIndex index,
        string configPath,
        IDictionary<string, string> normalPortraits,
        IDictionary<string, AncientCardPortrait> ancientPortraits,
        IReadOnlyList<string> portraitProperties,
        IReadOnlyList<string> ancientPortraitProperties,
        bool requireStaticKind,
        bool overwrite)
    {
        try
        {
            using var document = JsonDocument.Parse(StripUtf8Bom(
                index.Archive.ReadFile(configPath)));
            if (!TryGetJsonProperty(document.RootElement, "entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                var cardId = TryGetJsonString(entry, "cardId");
                if (string.IsNullOrWhiteSpace(cardId))
                {
                    continue;
                }

                if (requireStaticKind)
                {
                    var kind = TryGetJsonString(entry, "kind");
                    if (!string.IsNullOrWhiteSpace(kind) &&
                        !kind.Equals("image", StringComparison.OrdinalIgnoreCase) &&
                        !kind.Equals("static", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                var portrait = portraitProperties
                    .Select(property => TryGetJsonString(entry, property))
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
                var ancientPortrait = ancientPortraitProperties
                    .Select(property => TryGetJsonString(entry, property))
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
                if (string.IsNullOrWhiteSpace(portrait) && string.IsNullOrWhiteSpace(ancientPortrait))
                {
                    continue;
                }

                if (!IsResourceReference(portrait) || !IsResourceReference(ancientPortrait))
                {
                    continue;
                }

                var cardType = NormalizeCardPresentationType(cardId);
                if (!string.IsNullOrWhiteSpace(portrait))
                {
                    if (overwrite)
                    {
                        normalPortraits[cardType] = portrait;
                    }
                    else
                    {
                        normalPortraits.TryAdd(cardType, portrait);
                    }
                }

                if (!string.IsNullOrWhiteSpace(ancientPortrait))
                {
                    if (overwrite || !ancientPortraits.ContainsKey(cardType))
                    {
                        ancientPortraits[cardType] = new AncientCardPortrait(
                            portrait,
                            ancientPortrait);
                    }
                    else
                    {
                        var existing = ancientPortraits[cardType];
                        ancientPortraits[cardType] = new AncientCardPortrait(
                            existing.NormalPortrait ?? portrait,
                            existing.AncientPortrait ?? ancientPortrait);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"无法读取卡牌管理器导出配置 {configPath}: {exception.Message}");
        }

        static bool IsResourceReference(string? path) =>
            string.IsNullOrWhiteSpace(path) ||
            path.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("uid://", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetJsonString(JsonElement element, string propertyName) =>
        TryGetJsonProperty(element, propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetJsonProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeCardPresentationType(string cardId)
    {
        var separator = cardId.LastIndexOf('.');
        return separator >= 0 ? cardId[(separator + 1)..] : cardId;
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsBulkLooseCardPack(
        string providerId,
        IReadOnlyCollection<ResourceAsset> candidates)
    {
        if (candidates.Count < 20)
        {
            return false;
        }

        var directChildren = candidates.Count(asset =>
            IsDirectProviderChild(providerId, asset.SourcePath));
        return directChildren >= 20 && directChildren * 100 >= candidates.Count * 80;
    }

    private static bool IsDirectProviderChild(string providerId, string path)
    {
        if (!IsProviderNamespacePath(path, NormalizeResourceToken(providerId)))
        {
            return false;
        }

        var relative = path[6..];
        var firstSeparator = relative.IndexOf('/');
        return firstSeparator >= 0 && !relative[(firstSeparator + 1)..].Contains('/');
    }

    private static bool VariantsOverlap(IReadOnlyList<CardArtVariant> variants)
    {
        for (var left = 0; left < variants.Count; left++)
        {
            for (var right = left + 1; right < variants.Count; right++)
            {
                if (variants[left].Stems.Overlaps(variants[right].Stems))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool AssetDiffersFromBaseline(
        ResourceAsset asset,
        IReadOnlyList<PckResourceIndex> baselineIndexes)
    {
        ResourceAsset? baseline = null;
        for (var index = baselineIndexes.Count - 1; index >= 0 && baseline == null; index--)
        {
            baseline = baselineIndexes[index].Assets.GetValueOrDefault(asset.SourcePath) ??
                       baselineIndexes[index].TryBuildAsset(asset.SourcePath);
        }

        if (baseline == null)
        {
            return true;
        }

        foreach (var file in asset.Files)
        {
            var path = NormalizeTakeoverPath(file.Path);
            var baselineFile = baseline.Files.FirstOrDefault(candidate =>
                NormalizeTakeoverPath(candidate.Path).Equals(path, StringComparison.OrdinalIgnoreCase));
            if (baselineFile == null ||
                file.Archive.GetFileSize(file.Path) != baselineFile.Archive.GetFileSize(baselineFile.Path) ||
                !file.Archive.GetFileMd5(file.Path)
                    .SequenceEqual(baselineFile.Archive.GetFileMd5(baselineFile.Path)))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetCardVariantKey(
        string path,
        IReadOnlySet<string>? knownCardGroups = null)
    {
        var variant = TryGetCardArtPathLayout(path, knownCardGroups)?.Variant ?? string.Empty;
        return variant.Equals("beta", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : variant;
    }

    private static string DisplayCardVariant(string variant)
    {
        if (variant.Length == 0)
        {
            return "{skin-changer-default}";
        }

        return variant
            .Replace('/', ' ')
            .Replace('_', ' ')
            .Replace('-', ' ')
            .CapitalizeWords();
    }

    private static string? TryGetCardPortraitGroup(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var match = CardPortraitGroupRegex().Match(path);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static bool IsCardArtSourcePath(string path)
    {
        if (!CardArtPathRegex().IsMatch(path) || !IsCardArtResourceExtension(path))
        {
            return false;
        }

        // A card-art root must be followed by at least one directory (normally the
        // card color/pool).  UI overhauls commonly put their generic card widgets in
        // a flat `images/cards/` folder; those files are not card portraits and must
        // not make the whole UI mod look like a skin provider.
        return TryGetCardArtPathLayout(path, knownCardGroups: null) != null;
    }

    private static bool IsCardArtResourceExtension(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tres", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".res", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLooseProviderCardArtPath(string providerId, string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".tres", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsProviderNamespacePath(path, NormalizeResourceToken(providerId));
    }

    private static bool CardArtMatches(
        string assetPath,
        CardCatalogEntry card,
        IReadOnlySet<string> knownCardGroups)
    {
        var asset = TryGetCardArtIdentity(assetPath, knownCardGroups);
        var portrait = TryGetCardArtIdentity(card.PortraitPath, knownCardGroups);
        if (asset == null || portrait == null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(asset.Category) &&
            !asset.Category.Equals(card.PoolGroupId, StringComparison.OrdinalIgnoreCase) &&
            !asset.Category.Equals(portrait.Category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var typeStem = NormalizeCardToken(card.TypeName);
        return CardStemsMatch(asset.Stem, portrait.Stem) ||
               CardStemsMatch(asset.Stem, typeStem);
    }

    private static CardArtIdentity? TryGetCardArtIdentity(
        string? path,
        IReadOnlySet<string>? knownCardGroups = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var category = TryGetCardArtPathLayout(path, knownCardGroups)?.Category ?? string.Empty;
        var fileName = path[(path.LastIndexOf('/') + 1)..];
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
        return new CardArtIdentity(category, stem);
    }

    private static CardArtPathLayout? TryGetCardArtPathLayout(
        string path,
        IReadOnlySet<string>? knownCardGroups)
    {
        var root = CardArtPathRegex().Match(path);
        var fileSeparator = path.LastIndexOf('/');
        if (!root.Success || fileSeparator < root.Index + root.Length)
        {
            return null;
        }

        var directories = path[(root.Index + root.Length)..fileSeparator]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (directories.Length == 0)
        {
            return null;
        }

        var groupIndex = knownCardGroups == null || knownCardGroups.Count == 0
            ? 0
            : Array.FindIndex(directories, knownCardGroups.Contains);
        if (groupIndex < 0)
        {
            groupIndex = 0;
        }

        var category = directories[groupIndex].ToLowerInvariant();
        var variant = string.Join('/', directories.Where((_, index) => index != groupIndex));
        return new CardArtPathLayout(category, variant);
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

    private static bool IsDirectCharacterImagePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddPckRuntimeProviderOptions(
        IReadOnlyCollection<PckResourceIndex> indexes,
        IReadOnlyCollection<PckResourceIndex> baselineIndexes,
        IDictionary<string, SkinGroup> groups,
        IReadOnlySet<string> knownGroupIds)
    {
        foreach (var index in indexes)
        {
            var enabledGroupIds = ReadEnabledRuntimeGroupIds(index.Mod);
            var frameworkContracts = FrameworkSkinContractScanner.Scan(
                    index.Mod.RootPath,
                    index.Mod.ResourceNamespaceId)
                .Where(contract => FrameworkContractResourceClosureComplete(
                    index,
                    baselineIndexes,
                    contract))
                .ToArray();
            var frameworkTargetIds = frameworkContracts
                .Select(contract => contract.TargetGroupId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var contract in frameworkContracts)
            {
                if (!knownGroupIds.Contains(contract.TargetGroupId))
                {
                    continue;
                }

                if (!groups.TryGetValue(contract.TargetGroupId, out var frameworkGroup))
                {
                    frameworkGroup = new SkinGroup(
                        contract.TargetGroupId,
                        DisplayName(contract.TargetGroupId));
                    groups.Add(contract.TargetGroupId, frameworkGroup);
                }

                var mappedAssets = new Dictionary<string, ResourceAsset>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var resource in contract.CharacterResources)
                {
                    var canonicalPath = GetFrameworkCharacterCanonicalPath(
                        resource.Key,
                        contract.TargetGroupId);
                    var asset = index.Assets.GetValueOrDefault(resource.Value) ??
                                index.TryBuildAsset(resource.Value);
                    if (canonicalPath != null && asset != null)
                    {
                        mappedAssets[canonicalPath] = asset;
                    }
                }

                // Remove a coarse Mod-level option for this same character. Framework contracts
                // are deliberately split by SkinData registration, so retaining the aggregate
                // option would mix the resources of two skins from one DLL.
                frameworkGroup.Options.RemoveAll(option =>
                    option.Id.Equals(index.Mod.Id, StringComparison.OrdinalIgnoreCase));
                var frameworkOptionId = ProviderInstanceIdentityPolicy.ScopeOptionId(
                    index.Mod.ResourceNamespaceId,
                    index.Mod.Id,
                    contract.OptionId);
                var existingFrameworkIndex = frameworkGroup.Options.FindIndex(option =>
                    option.Id.Equals(frameworkOptionId, StringComparison.OrdinalIgnoreCase));
                var frameworkOption = new SkinOption(
                    frameworkOptionId,
                    DistinguishDuplicateProviderOptionName(
                        index.Mod,
                        contract.DisplayName),
                    mappedAssets,
                    IsRuntimeProvider: true,
                    ProviderId: index.Mod.Id,
                    FrameworkContract: contract);
                if (existingFrameworkIndex >= 0)
                {
                    frameworkGroup.Options[existingFrameworkIndex] = frameworkOption;
                }
                else
                {
                    frameworkGroup.Options.Add(frameworkOption);
                }
            }

            var managedCharacterReplacements = ManagedCharacterAssetReplacementScanner.Scan(
                index.Mod.RootPath,
                index.Mod.ResourceNamespaceId);
            var managedRuntimeMappings = managedCharacterReplacements
                .SelectMany(replacement => replacement.CanonicalPathsByProviderPath.Select(pair =>
                    new KeyValuePair<string, RuntimeProviderAsset>(
                        pair.Key,
                        new RuntimeProviderAsset(
                            new GroupIdentity(
                                replacement.TargetGroupId,
                                DisplayName(replacement.TargetGroupId)),
                            pair.Value))))
                .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    pairs => pairs.Key,
                    pairs => pairs.Last().Value,
                    StringComparer.OrdinalIgnoreCase);
            var managedDependencyPaths = CollectManagedCharacterDependencyPaths(
                index,
                managedRuntimeMappings.Keys);
            var frameworkDependencyPaths = CollectManagedCharacterDependencyPaths(
                index,
                frameworkContracts.SelectMany(contract => contract.ResourcePaths));
            var declarativeDependencyPaths = managedDependencyPaths
                .Concat(frameworkDependencyPaths)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var indexedAssets = index.Assets.Values.ToArray();
            // The lightweight PCK index eagerly registers canonical game animation/scene paths,
            // but a DLL provider may keep its routed scene below any private top-level folder.
            // Discover direct resources by their game-facing structure before building options;
            // this keeps arbitrary provider namespaces working without indexing every project file.
            var privateRuntimeAssets = index.Archive.Paths
                // Exported Godot projects commonly ship only `scene.tscn.remap` plus the
                // `.godot/exported/*.scn` payload. Normalize the remap source so private scenes
                // remain discoverable even when the source `.tscn` itself is absent from the PCK.
                // The same pass includes raw images mentioned by the managed replacement
                // scanner.  These are often private avatar/icon paths without a .import file;
                // they cannot be recognized from their filename alone, but the scanner has
                // already proved that the provider routes them to a character asset.
                .Where(path => path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".tscn.remap", StringComparison.OrdinalIgnoreCase) ||
                               IsDirectCharacterImagePath(path))
                .Select(path => path.EndsWith(".tscn.remap", StringComparison.OrdinalIgnoreCase)
                    ? path[..^6]
                    : path)
                .Where(path =>
                    managedRuntimeMappings.ContainsKey(NormalizeTakeoverPath(path)) ||
                    TryGetRuntimeProviderAsset(index.Mod.ResourceNamespaceId, path) != null)
                .Select(index.TryBuildAsset)
                .Where(asset => asset != null)
                .Cast<ResourceAsset>()
                .ToArray();
            var runtimeAssets = indexedAssets
                .Concat(privateRuntimeAssets)
                .DistinctBy(asset => asset.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(asset => (
                    Asset: asset,
                    Mapping: managedRuntimeMappings.TryGetValue(asset.SourcePath, out var managedMapping)
                        ? managedMapping
                        : declarativeDependencyPaths.Contains(asset.SourcePath)
                            ? null
                            : TryGetRuntimeProviderAsset(
                                index.Mod.ResourceNamespaceId,
                                asset.SourcePath)))
                .Where(pair => pair.Mapping != null)
                .Select(pair => (pair.Asset, Mapping: pair.Mapping!))
                .ToArray();

            // Framework registrations often name every private resource after the skin instead
            // of the replaced character. Remove the resulting phantom character option after the
            // explicit registration has routed those resources to the real character group.
            var managedTargetIds = managedCharacterReplacements
                .Select(replacement => replacement.TargetGroupId)
                .Concat(frameworkTargetIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var sourceGroup in groups.Values
                         .Where(group => !managedTargetIds.Contains(group.Id))
                         .ToArray())
            {
                sourceGroup.Options.RemoveAll(option =>
                    option.Id.Equals(index.Mod.Id, StringComparison.OrdinalIgnoreCase) &&
                    option.Assets.Count > 0 &&
                    option.Assets.Values.Any(asset =>
                        declarativeDependencyPaths.Contains(asset.SourcePath)));
                if (sourceGroup.Options.Count == 0)
                {
                    groups.Remove(sourceGroup.Id);
                }
            }

            var identities = runtimeAssets
                .Select(pair => pair.Mapping.Identity)
                .Where(identity =>
                    knownGroupIds.Contains(identity.Id) &&
                    !frameworkTargetIds.Contains(identity.Id) &&
                    (enabledGroupIds == null ||
                     enabledGroupIds.Contains(identity.Id) ||
                     managedTargetIds.Contains(identity.Id)))
                .DistinctBy(identity => identity.Id)
                .ToArray();
            foreach (var identity in identities)
            {
                if (!groups.TryGetValue(identity.Id, out var group))
                {
                    group = new SkinGroup(identity.Id, identity.DisplayName);
                    groups.Add(identity.Id, group);
                }

                var mappedAssets = runtimeAssets
                    .Where(pair => pair.Mapping.Identity.Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(pair => pair.Mapping.CanonicalPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        pairs => pairs.Key,
                        pairs => pairs.Last().Asset,
                        StringComparer.OrdinalIgnoreCase);
                var existingIndex = group.Options.FindIndex(option =>
                    option.Id.Equals(index.Mod.Id, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    var existing = group.Options[existingIndex];
                    var mergedAssets = new Dictionary<string, ResourceAsset>(
                        existing.Assets, StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in mappedAssets)
                    {
                        mergedAssets[pair.Key] = pair.Value;
                    }

                    group.Options[existingIndex] = existing with
                    {
                        Assets = mergedAssets,
                        IsRuntimeProvider = true,
                        IsCharacterIconOnly = existing.IsCharacterIconOnly &&
                                              mappedAssets.Keys.All(IsCharacterIconSourcePath)
                    };
                    continue;
                }

                group.Options.Add(new SkinOption(
                    index.Mod.Id,
                    index.Mod.Name,
                    mappedAssets,
                    IsRuntimeProvider: true));
            }
        }
    }

    private static bool FrameworkContractResourceClosureComplete(
        PckResourceIndex index,
        IReadOnlyCollection<PckResourceIndex> baselineIndexes,
        FrameworkCharacterSkinContract contract) =>
        contract.ResourcePaths.Count > 0 &&
        contract.ResourcePaths.All(path =>
            ResourceExists(index, path) ||
            baselineIndexes.Any(baseline => ResourceExists(baseline, path)));

    private static bool ResourceExists(PckResourceIndex index, string path) =>
        index.Archive.Paths.Contains(path, StringComparer.OrdinalIgnoreCase) ||
        index.Archive.Paths.Contains(path + ".remap", StringComparer.OrdinalIgnoreCase) ||
        index.Archive.Paths.Contains(path + ".import", StringComparer.OrdinalIgnoreCase);

    internal static string? GetFrameworkCharacterCanonicalPath(
        string propertyName,
        string targetGroupId) => propertyName switch
        {
            "CombatVisual" => $"res://scenes/creature_visuals/{targetGroupId}.tscn",
            "MerchantVisual" => $"res://scenes/merchant/characters/{targetGroupId}_merchant.tscn",
            "RestVisual" => $"res://scenes/rest_site/characters/{targetGroupId}_rest_site.tscn",
            "CharacterSelectBg" => $"res://scenes/screens/char_select/char_select_bg_{targetGroupId}.tscn",
            "CharacterSelectPortrait" => $"res://images/packed/character_select/char_select_{targetGroupId}.png",
            "CharacterSelectTransition" => $"res://materials/transitions/{targetGroupId}_transition_mat.tres",
            "CharacterIcon" => $"res://images/ui/top_panel/character_icon_{targetGroupId}.png",
            "CharacterIconOutline" => $"res://images/ui/top_panel/character_icon_{targetGroupId}_outline.png",
            "CharacterIconScene" => $"res://scenes/ui/character_icons/{targetGroupId}_icon.tscn",
            "CharacterMapMarker" => $"res://images/packed/map/icons/map_marker_{targetGroupId}.png",
            "CardTrail" => $"res://scenes/vfx/card_trail_{targetGroupId}.tscn",
            "HandPoint" => $"res://images/ui/hands/multiplayer_hand_{targetGroupId}_point.png",
            "HandRock" => $"res://images/ui/hands/multiplayer_hand_{targetGroupId}_rock.png",
            "HandPaper" => $"res://images/ui/hands/multiplayer_hand_{targetGroupId}_paper.png",
            "HandScissors" => $"res://images/ui/hands/multiplayer_hand_{targetGroupId}_scissors.png",
            _ => null
        };

    private static IReadOnlySet<string> CollectManagedCharacterDependencyPaths(
        PckResourceIndex index,
        IEnumerable<string> roots)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ResourceAsset>();
        foreach (var root in roots)
        {
            var asset = index.Assets.GetValueOrDefault(root) ?? index.TryBuildAsset(root);
            if (asset != null && result.Add(asset.SourcePath))
            {
                queue.Enqueue(asset);
            }
        }

        while (queue.TryDequeue(out var asset))
        {
            foreach (var file in asset.Files.Where(file => MayContainResourceReferences(file.Path)))
            {
                var text = Encoding.UTF8.GetString(file.Archive.ReadFile(file.Path));
                foreach (Match match in EmbeddedResourcePathRegex().Matches(text))
                {
                    Include(index.Assets.GetValueOrDefault(match.Value) ??
                            index.TryBuildAsset(match.Value));
                    foreach (var sibling in GetSiblingAtlasTextureAssets(index, match.Value))
                    {
                        Include(sibling);
                    }
                }
            }
        }

        return result;

        void Include(ResourceAsset? dependency)
        {
            if (dependency != null && result.Add(dependency.SourcePath))
            {
                queue.Enqueue(dependency);
            }
        }
    }

    private static HashSet<string>? ReadEnabledRuntimeGroupIds(SkinModDescriptor mod)
    {
        if (mod.RootPath == null || !Directory.Exists(mod.RootPath))
        {
            return null;
        }

        foreach (var configPath in Directory.EnumerateFiles(mod.RootPath, "*_config.cfg", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                var replacements = document.RootElement.EnumerateObject()
                    .FirstOrDefault(property =>
                        property.Name.Equals("template_replacements", StringComparison.OrdinalIgnoreCase));
                if (replacements.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                return replacements.Value.EnumerateObject()
                    .Where(property => property.Value.ValueKind == JsonValueKind.True)
                    .Select(property => property.Name.ToLowerInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"无法读取运行时皮肤配置 {configPath}: {exception.Message}");
            }
        }

        return null;
    }

    private void AddImageRuntimeProviderOptions(IEnumerable<SkinModDescriptor> mods)
    {
        foreach (var mod in mods.Where(mod => !mod.AffectsGameplay && mod.RootPath != null))
        {
            var imagesByGroup = DiscoverRuntimeAncientImages(mod)
                .GroupBy(image => image.GroupId, StringComparer.OrdinalIgnoreCase);
            foreach (var groupImages in imagesByGroup)
            {
                var images = groupImages
                    .OrderByDescending(image =>
                        image.Name.Equals("default", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(image => image.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(image => image.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                for (var index = 0; index < images.Length; index++)
                {
                    var image = images[index];
                    // Keep the original provider ID for the preferred/only image so selections
                    // written by older SkinChanger builds remain valid. Additional images receive
                    // path-qualified IDs, allowing any number of variants in the same provider.
                    var optionId = index == 0
                        ? mod.Id
                        : $"{mod.Id}:image:{image.RelativePath.ToLowerInvariant()}";
                    var optionName = images.Length == 1
                        ? mod.Name
                        : $"{mod.Name} · {image.Name.Replace('_', ' ')}";
                    AddRuntimeProviderOption(
                        image.GroupId,
                        optionId,
                        optionName,
                        image.Path,
                        mod.Id);
                }
            }
        }
    }

    private static IReadOnlyList<RuntimeAncientImage> DiscoverRuntimeAncientImages(SkinModDescriptor mod)
    {
        if (mod.RootPath == null || !Directory.Exists(mod.RootPath))
        {
            return [];
        }

        try
        {
            var rootPath = System.IO.Path.GetFullPath(mod.RootPath);
            var rootWriteTimeUtc = Directory.GetLastWriteTimeUtc(rootPath);
            lock (RuntimeAncientImageCacheSync)
            {
                if (RuntimeAncientImageCache.TryGetValue(rootPath, out var cached) &&
                    cached.RootWriteTimeUtc == rootWriteTimeUtc)
                {
                    return cached.Images;
                }
            }

            var images = new List<RuntimeAncientImage>();
            foreach (var path in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                var extension = System.IO.Path.GetExtension(path);
                if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsSpineAtlasTexture(path))
                {
                    continue;
                }

                var relativePath = System.IO.Path.GetRelativePath(rootPath, path)
                    .Replace('\\', '/');
                var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                var groupId = segments
                    .Reverse()
                    .Skip(1)
                    .FirstOrDefault(segment => KnownAncientIds.Contains(segment));
                if (groupId == null && KnownAncientIds.Contains(name))
                {
                    groupId = name;
                }

                if (groupId == null || name.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                images.Add(new RuntimeAncientImage(
                    groupId.ToLowerInvariant(),
                    path,
                    relativePath,
                    name));
            }

            var discovered = images
                .DistinctBy(image => image.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            lock (RuntimeAncientImageCacheSync)
            {
                RuntimeAncientImageCache[rootPath] = new RuntimeAncientImageCacheEntry(
                    rootWriteTimeUtc,
                    discovered);
            }

            return discovered;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"无法扫描外置先古图库 {mod.Id}: {exception.Message}");
            return [];
        }
    }

    private static bool IsSpineAtlasTexture(string imagePath)
    {
        var stemPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(imagePath) ?? string.Empty,
            System.IO.Path.GetFileNameWithoutExtension(imagePath));
        if (!File.Exists(stemPath + ".atlas"))
        {
            return false;
        }

        return new[] { ".spjson", ".skel", ".json" }
            .Any(extension => File.Exists(stemPath + extension));
    }

    private static string DistinguishDuplicateProviderOptionName(
        SkinModDescriptor mod,
        string optionName)
    {
        if (mod.Id.Equals(mod.ResourceNamespaceId, StringComparison.OrdinalIgnoreCase))
        {
            return optionName;
        }

        var rankMarker = mod.Name.LastIndexOf(" · ", StringComparison.Ordinal);
        var suffix = rankMarker >= 0
            ? mod.Name[rankMarker..]
            : " · " + mod.Name;
        return optionName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? optionName
            : optionName + suffix;
    }

    private void AddRuntimeProviderOption(
        string groupId,
        string optionId,
        string optionName,
        string runtimeImagePath,
        string providerId)
    {
        var group = _groups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            group = new SkinGroup(groupId, DisplayName(groupId));
            _groups.Add(group);
        }

        var existingIndex = group.Options.FindIndex(option =>
            option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            var existing = group.Options[existingIndex];
            group.Options[existingIndex] = existing with
            {
                IsRuntimeProvider = true,
                RuntimeImagePath = runtimeImagePath,
                ProviderId = providerId
            };
            return;
        }

        group.Options.Add(new SkinOption(
            optionId,
            optionName,
            new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase),
            IsRuntimeProvider: true,
            RuntimeImagePath: runtimeImagePath,
            ProviderId: providerId));
    }

    private static RuntimeProviderAsset? TryGetRuntimeProviderAsset(
        string providerId,
        string sourcePath)
    {
        var canonicalPath = CanonicalizeRuntimeProviderPath(providerId, sourcePath);
        var identity = TryGetRuntimeProviderGroup(sourcePath) ??
                       TryGetPrimaryGroup(canonicalPath) ??
                       TryGetCharacterSelectIconGroup(canonicalPath) ??
                       TryGetCharacterUiTextureGroup(canonicalPath) ??
                       TryGetCharacterMapMarkerGroup(canonicalPath) ??
                       TryGetCharacterIconSceneGroup(canonicalPath) ??
                       TryGetCharacterSupplementGroup(canonicalPath);
        return identity == null ? null : new RuntimeProviderAsset(identity, canonicalPath);
    }

    private static string CanonicalizeRuntimeProviderPath(string providerId, string sourcePath)
    {
        if (sourcePath.StartsWith("res://custom/", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeRuntimeProviderCanonicalPath("res://" + sourcePath[13..]);
        }

        // 通用支持 res://<ModId>/<游戏原资源相对路径>。不匹配提供者自己的
        // 顶层目录时保持原路径，避免把普通资源目录误当成游戏入口。
        if (!IsProviderNamespacePath(sourcePath, NormalizeResourceToken(providerId)))
        {
            return NormalizeRuntimeProviderCanonicalPath(sourcePath);
        }

        var relative = sourcePath[6..];
        var separator = relative.IndexOf('/');
        return separator < 0
            ? sourcePath
            : NormalizeRuntimeProviderCanonicalPath("res://" + relative[(separator + 1)..]);
    }

    private static string NormalizeRuntimeProviderCanonicalPath(string path)
    {
        // Some providers keep a complete character-select scene in an arbitrary private
        // directory (for example res://<ModId>/animations/char_select/) and route to it from
        // a Harmony property patch. The file name is still the stable game-facing contract, so
        // recognize it independently of the provider's folder layout.
        var characterSelectMatch = AnyCharacterSelectSceneRegex().Match(path);
        if (characterSelectMatch.Success)
        {
            return "res://scenes/screens/char_select/char_select_bg_" +
                   characterSelectMatch.Groups[1].Value + ".tscn";
        }

        const string creatureTemplatePrefix = "res://scenes/creature_visuals/templates/";
        const string creatureTemplateSuffix = "_template.tscn";
        if (TryMapCharacterTemplatePath(
                path,
                creatureTemplatePrefix,
                creatureTemplateSuffix,
                "res://scenes/creature_visuals/",
                ".tscn",
                out var creaturePath))
        {
            return creaturePath;
        }

        const string merchantTemplatePrefix = "res://scenes/merchant/characters/templates/";
        const string merchantTemplateSuffix = "_merchant_template.tscn";
        if (TryMapCharacterTemplatePath(
                path,
                merchantTemplatePrefix,
                merchantTemplateSuffix,
                "res://scenes/merchant/characters/",
                "_merchant.tscn",
                out var merchantPath))
        {
            return merchantPath;
        }

        const string restTemplatePrefix = "res://scenes/rest_site/characters/templates/";
        const string restTemplateSuffix = "_rest_site_template.tscn";
        if (TryMapCharacterTemplatePath(
                path,
                restTemplatePrefix,
                restTemplateSuffix,
                "res://scenes/rest_site/characters/",
                "_rest_site.tscn",
                out var restPath))
        {
            return restPath;
        }

        const string privateCharacterSelectPrefix = "res://scenes/character_select/";
        const string shortCharacterSelectPrefix = "res://scenes/char_select/";
        const string gameCharacterSelectPrefix = "res://scenes/screens/char_select/";
        if (path.StartsWith(privateCharacterSelectPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return gameCharacterSelectPrefix + path[privateCharacterSelectPrefix.Length..];
        }

        return path.StartsWith(shortCharacterSelectPrefix, StringComparison.OrdinalIgnoreCase)
            ? gameCharacterSelectPrefix + path[shortCharacterSelectPrefix.Length..]
            : path;
    }

    private static bool TryMapCharacterTemplatePath(
        string path,
        string templatePrefix,
        string templateSuffix,
        string canonicalPrefix,
        string canonicalSuffix,
        out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (!path.StartsWith(templatePrefix, StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(templateSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var characterId = path[templatePrefix.Length..^templateSuffix.Length];
        if (string.IsNullOrWhiteSpace(characterId) || characterId.Contains('/'))
        {
            return false;
        }

        canonicalPath = canonicalPrefix + characterId + canonicalSuffix;
        return true;
    }

    private static GroupIdentity? TryGetCharacterSupplementGroup(string sourcePath)
    {
        foreach (var regex in new[] { CharacterIconTemplateRegex(), MultiplayerHandRegex() })
        {
            var match = regex.Match(sourcePath);
            if (match.Success)
            {
                var id = match.Groups[1].Value.ToLowerInvariant();
                return new GroupIdentity(id, DisplayName(id));
            }
        }

        return null;
    }

    private static string MapAssetFilePath(string targetSourcePath, string assetSourcePath, string filePath)
    {
        if (targetSourcePath.Equals(assetSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return filePath;
        }

        if (filePath.Equals(assetSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return targetSourcePath;
        }

        foreach (var suffix in new[] { ".import", ".remap" })
        {
            if (filePath.Equals(assetSourcePath + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return targetSourcePath + suffix;
            }
        }

        return filePath;
    }

    private void SortGroupsAndOptions()
    {
        foreach (var group in _groups)
        {
            group.Options.Sort((left, right) =>
                string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
        }

        _groups.Sort((left, right) =>
        {
            var order = GroupSortOrder(left.Id).CompareTo(GroupSortOrder(right.Id));
            return order != 0
                ? order
                : string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        });
    }

    private static void MergeCharacterSelectIconPacks(
        IReadOnlyCollection<PckResourceIndex> indexes,
        IReadOnlyDictionary<string, SkinGroup> groups)
    {
        foreach (var index in indexes)
        {
            var hasPrimaryAppearance = index.Assets.Keys.Any(path => TryGetPrimaryGroup(path) != null);
            if (hasPrimaryAppearance)
            {
                continue;
            }

            var iconGroups = index.Assets.Values
                .Select(asset => (Asset: asset, Group: TryGetCharacterSelectIconGroup(asset.SourcePath)))
                .Where(pair => pair.Group != null)
                .GroupBy(pair => pair.Group!.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var iconGroup in iconGroups)
            {
                if (!groups.TryGetValue(iconGroup.Key, out var group))
                {
                    continue;
                }

                var iconAssets = iconGroup.ToDictionary(
                    pair => pair.Asset.SourcePath,
                    pair => pair.Asset,
                    StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < group.Options.Count; i++)
                {
                    var option = group.Options[i];
                    var mergedAssets = new Dictionary<string, ResourceAsset>(option.Assets, StringComparer.OrdinalIgnoreCase);
                    foreach (var iconAsset in iconAssets)
                    {
                        mergedAssets.TryAdd(iconAsset.Key, iconAsset.Value);
                    }

                    group.Options[i] = option with { Assets = mergedAssets };
                }
            }
        }
    }

    private static bool TryBuildCompositionOption(
        SkinGroup group,
        CharacterSkinComposition composition,
        bool session,
        out SkinOption option)
    {
        option = null!;
        var rawOptions = group.Options
            .Where(candidate => !candidate.IsComposition)
            .ToDictionary(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase);
        var sources = rawOptions.ToDictionary(
            pair => pair.Key,
            pair => new CharacterSkinCompositionSource<ResourceAsset>(
                pair.Key,
                pair.Value.IsRuntimeProvider,
                pair.Value.Assets),
            StringComparer.OrdinalIgnoreCase);
        var resolved = CharacterSkinCompositionPolicy.ResolveAssets(
            composition.SourceOptionIds,
            sources,
            NormalizeTakeoverPath);
        if (resolved.SourceOptionIds.Count == 0)
        {
            return false;
        }

        var dynamicSource = resolved.DynamicSourceId == null
            ? null
            : rawOptions[resolved.DynamicSourceId];
        option = new SkinOption(
            composition.Id,
            composition.Name,
            resolved.Assets,
            IsRuntimeProvider: dynamicSource?.IsRuntimeProvider == true,
            IsDirectCharacterRuntimeProvider: dynamicSource?.IsDirectCharacterRuntimeProvider == true,
            RuntimeImagePath: dynamicSource?.RuntimeImagePath,
            ManagedMonsterScene: dynamicSource?.ManagedMonsterScene,
            RuntimeMonsterVisualMode: dynamicSource?.RuntimeMonsterVisualMode,
            ProviderId: dynamicSource?.EffectiveProviderId,
            IsManagedMonsterRuntimeProfile: dynamicSource?.IsManagedMonsterRuntimeProfile == true,
            FrameworkContract: dynamicSource?.FrameworkContract,
            IsCharacterIconOnly: false)
        {
            CompositionSourceOptionIds = resolved.SourceOptionIds,
            CompositionSourceProviderIds = resolved.SourceOptionIds
                .Select(sourceId => rawOptions[sourceId].EffectiveProviderId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            IsSessionComposition = session
        };
        return true;
    }

    private static GroupIdentity? TryGetDefinedBaselineGroup(string sourcePath)
    {
        var characterDependency = CharacterPathRegex().Match(sourcePath);
        if (characterDependency.Success &&
            !sourcePath.StartsWith(
                "res://animations/characters/",
                StringComparison.OrdinalIgnoreCase))
        {
            // character_select, merchant and rest_site contain many helper animation folders
            // (for example liveevent or a provider's numeric Spine name). They may be assigned to
            // a character already proved elsewhere, but cannot define a character by themselves.
            return null;
        }

        return TryGetPrimaryGroup(sourcePath);
    }

    private static GroupIdentity? TryGetPrimaryGroup(string sourcePath)
    {
        // Character portraits and selection/map icons live under images/ rather than the
        // animation/scene directories. They still belong to the same character group and must
        // be accepted by the online-safe resource filter; otherwise remote players fall back to
        // the base avatar even though the provider supplied a valid canonical icon binding.
        foreach (var characterAssetRegex in new[]
                 {
                     CharacterSelectIconRegex(),
                     CharacterUiTextureRegex(),
                     CharacterIconSceneRegex(),
                     CharacterMapMarkerRegex()
                 })
        {
            var asset = characterAssetRegex.Match(sourcePath);
            if (asset.Success)
            {
                return new GroupIdentity(
                    asset.Groups[1].Value.ToLowerInvariant(),
                    DisplayName(asset.Groups[1].Value));
            }
        }

        var character = CharacterPathRegex().Match(sourcePath);
        if (character.Success)
        {
            var id = character.Groups[1].Value.ToLowerInvariant();
            foreach (var suffix in new[] { "_rest_site", "_merchant", "_character_select" })
            {
                if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    id = id[..^suffix.Length];
                    break;
                }
            }

            return new GroupIdentity(id, DisplayName(id));
        }

        var monster = MonsterPathRegex().Match(sourcePath);
        if (monster.Success)
        {
            var id = monster.Groups[1].Value.ToLowerInvariant();
            return new GroupIdentity(id, DisplayName(id));
        }

        // The merchant is a room presentation rather than a CreatureModel.  Cosmetic packs
        // therefore replace its room/background skeletons and shop scenes instead of a creature
        // scene. Keep those resources in one selectable runtime bundle; playable-character
        // merchant scenes are matched separately below and remain owned by their character.
        if (FakeMerchantAppearancePathRegex().IsMatch(NormalizeTakeoverPath(sourcePath)))
        {
            // The reverse merchant is both an event NPC and a no-HP creature. Keep all of its
            // presentation resources in the same group so a skin can be selected from Other
            // Compendium and from the event creature without splitting the provider's bundle.
            return new GroupIdentity("fake_merchant_monster", DisplayName("fake_merchant_monster"));
        }

        if (MerchantAppearancePathRegex().IsMatch(NormalizeTakeoverPath(sourcePath)))
        {
            return new GroupIdentity("merchant", DisplayName("merchant"));
        }

        foreach (var sceneRegex in new[]
                 {
                     CreatureVisualSceneRegex(),
                     CharacterSelectSceneRegex(),
                     MerchantCharacterSceneRegex(),
                     RestSiteCharacterSceneRegex()
                 })
        {
            var scene = sceneRegex.Match(sourcePath);
            if (!scene.Success)
            {
                continue;
            }

            var id = scene.Groups[1].Value.ToLowerInvariant();
            return new GroupIdentity(id, DisplayName(id));
        }

        var ancientScene = AncientBackgroundSceneRegex().Match(sourcePath);
        if (ancientScene.Success)
        {
            var id = ancientScene.Groups[1].Value.ToLowerInvariant();
            return new GroupIdentity(id, DisplayName(id));
        }

        var ancientAnimation = AncientBackgroundAnimationRegex().Match(sourcePath);
        if (ancientAnimation.Success)
        {
            var id = ancientAnimation.Groups[1].Value.ToLowerInvariant();
            if (id.EndsWith("_room", StringComparison.OrdinalIgnoreCase))
            {
                id = id[..^5];
            }

            if (KnownAncientIds.Contains(id))
            {
                return new GroupIdentity(id, DisplayName(id));
            }
        }

        // Some data-only Ancient skins replace the game's static placeholder directly instead
        // of supplying a scene or animation directory. These are ordinary canonical resources
        // and should be isolated/selected like every other PCK-backed skin.
        var ancientStaticImage = AncientStaticImageRegex().Match(NormalizeTakeoverPath(sourcePath));
        if (ancientStaticImage.Success)
        {
            var id = ancientStaticImage.Groups["id"].Value.ToLowerInvariant();
            if (id.EndsWith("_placeholder", StringComparison.OrdinalIgnoreCase))
            {
                id = id[..^12];
            }

            if (KnownAncientIds.Contains(id))
            {
                return new GroupIdentity(id, DisplayName(id));
            }
        }

        // 有些代码型先古皮肤不提供替换场景，而是在运行时把完整画布图层
        // 叠到原场景的占位图上。按通用的 <先古 ID>_character 等资源约定
        // 归组，随后由本 Mod 自己完成图层合成，无需执行提供者 DLL。
        var ancientLayer = AncientLayerImageRegex().Match(NormalizeTakeoverPath(sourcePath));
        if (ancientLayer.Success)
        {
            var id = ancientLayer.Groups["id"].Value.ToLowerInvariant();
            if (KnownAncientIds.Contains(id))
            {
                return new GroupIdentity(id, DisplayName(id));
            }
        }

        // 商人 NPC 不纳入本 Mod 的管理范围（无切换界面，其呈现依赖提供者
        // 自身的代码补丁）。不识别 merchant 分组后，纯商人 Mod 不会被当作
        // 皮肤提供者，走游戏原加载器，表现与未安装本 Mod 时一致。
        return null;
    }

    private static GroupIdentity? TryGetUnambiguousCharacterGroup(string sourcePath)
    {
        var character = CharacterPathRegex().Match(sourcePath);
        if (character.Success)
        {
            var id = character.Groups[1].Value.ToLowerInvariant();
            foreach (var suffix in new[] { "_rest_site", "_merchant", "_character_select" })
            {
                if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    id = id[..^suffix.Length];
                    break;
                }
            }

            return new GroupIdentity(id, DisplayName(id));
        }

        foreach (var sceneRegex in new[]
                 {
                     CharacterSelectSceneRegex(),
                     MerchantCharacterSceneRegex(),
                     RestSiteCharacterSceneRegex()
                 })
        {
            var scene = sceneRegex.Match(sourcePath);
            if (scene.Success)
            {
                var id = scene.Groups[1].Value.ToLowerInvariant();
                return new GroupIdentity(id, DisplayName(id));
            }
        }

        return null;
    }

    private static GroupIdentity? TryGetCharacterVisualAnchorGroup(
        string sourcePath,
        IReadOnlySet<string> knownCharacterGroupIds)
    {
        var direct = TryGetUnambiguousCharacterGroup(sourcePath);
        if (direct != null && knownCharacterGroupIds.Contains(direct.Id))
        {
            return direct;
        }

        var creatureScene = CreatureVisualSceneRegex().Match(sourcePath);
        if (!creatureScene.Success)
        {
            return null;
        }

        var id = creatureScene.Groups[1].Value.ToLowerInvariant();
        return knownCharacterGroupIds.Contains(id)
            ? new GroupIdentity(id, DisplayName(id))
            : null;
    }

    private static GroupIdentity? TryGetCharacterSelectIconGroup(string sourcePath)
    {
        var icon = CharacterSelectIconRegex().Match(sourcePath);
        if (!icon.Success)
        {
            return null;
        }

        var id = icon.Groups[1].Value.ToLowerInvariant();
        return new GroupIdentity(id, DisplayName(id));
    }

    private static GroupIdentity? TryGetCharacterUiTextureGroup(string sourcePath)
    {
        var match = CharacterUiTextureRegex().Match(sourcePath);
        if (!match.Success)
        {
            return null;
        }

        var id = match.Groups[1].Value.ToLowerInvariant();
        return new GroupIdentity(id, DisplayName(id));
    }

    private static GroupIdentity? TryGetCharacterIconSceneGroup(string sourcePath)
    {
        var match = CharacterIconSceneRegex().Match(sourcePath);
        if (!match.Success)
        {
            return null;
        }

        var id = match.Groups[1].Value.ToLowerInvariant();
        return new GroupIdentity(id, DisplayName(id));
    }

    private static GroupIdentity? TryGetCharacterMapMarkerGroup(string sourcePath)
    {
        var match = CharacterMapMarkerRegex().Match(sourcePath);
        if (!match.Success)
        {
            return null;
        }

        var id = match.Groups[1].Value.ToLowerInvariant();
        return new GroupIdentity(id, DisplayName(id));
    }

    private static GroupIdentity? TryGetAncientIconGroup(string sourcePath)
    {
        foreach (var regex in new[] { AncientMapIconRegex(), AncientRunHistoryIconRegex() })
        {
            var match = regex.Match(sourcePath);
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups[1].Value.ToLowerInvariant();
            if (KnownAncientIds.Contains(id))
            {
                return new GroupIdentity(id, DisplayName(id));
            }
        }

        return null;
    }

    private static GroupIdentity? TryGetRuntimeProviderGroup(string sourcePath)
    {
        foreach (var regex in new[]
                 {
                     AnyCharacterSelectSceneRegex(),
                     RuntimeCharacterSelectSceneRegex(),
                     RuntimeCharacterSelectIconRegex(),
                     RuntimeCreatureTemplateRegex(),
                     RuntimeMerchantTemplateRegex(),
                     RuntimeRestSiteTemplateRegex(),
                     RuntimeCharacterIconTemplateRegex()
                 })
        {
            var match = regex.Match(sourcePath);
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups[1].Value.ToLowerInvariant();
            return new GroupIdentity(id, DisplayName(id));
        }

        return null;
    }

    private static bool ContainsGroupToken(string path, string groupId)
    {
        return Regex.IsMatch(path, $"(?:^|[/_.-]){Regex.Escape(groupId)}(?:[/_.-]|$)", RegexOptions.IgnoreCase);
    }

    private static bool IsAnimationRemap(string path)
    {
        return path.StartsWith("res://animations/", StringComparison.OrdinalIgnoreCase) &&
               (path.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase));
    }

    private static ResourceFile? FindDirectFile(ResourceAsset? asset, string sourcePath) =>
        asset?.Files.FirstOrDefault(file =>
            NormalizeTakeoverPath(file.Path).Equals(sourcePath, StringComparison.OrdinalIgnoreCase) ||
            NormalizeTakeoverPath(file.Path).Equals(asset.SourcePath, StringComparison.OrdinalIgnoreCase));

    private static ResourceFile? FindRemapFile(ResourceAsset? asset, string sourcePath) =>
        asset?.Files.FirstOrDefault(file =>
            NormalizeTakeoverPath(file.Path).Equals(sourcePath + ".import", StringComparison.OrdinalIgnoreCase) ||
            NormalizeTakeoverPath(file.Path).Equals(sourcePath + ".remap", StringComparison.OrdinalIgnoreCase) ||
            NormalizeTakeoverPath(file.Path).Equals(asset.SourcePath + ".import", StringComparison.OrdinalIgnoreCase) ||
            NormalizeTakeoverPath(file.Path).Equals(asset.SourcePath + ".remap", StringComparison.OrdinalIgnoreCase));

    private static ResourceFile[] GetImportedPayloadFiles(ResourceAsset asset, string sourcePath) =>
        asset.Files.Where(file =>
                !file.Path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase) &&
                !file.Path.Equals(sourcePath + ".import", StringComparison.OrdinalIgnoreCase) &&
                !file.Path.Equals(sourcePath + ".remap", StringComparison.OrdinalIgnoreCase) &&
                IsImportedPayloadPath(file.Path))
            .ToArray();

    private static bool IsImportedPayloadPath(string path) =>
        path.EndsWith(".spatlas", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".spskel", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ctex", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("res://.godot/exported/", StringComparison.OrdinalIgnoreCase) &&
        (path.EndsWith(".scn", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".res", StringComparison.OrdinalIgnoreCase));

    internal static string NormalizeTakeoverPath(string path)
    {
        var match = WaifuAssetsPathRegex().Match(path);
        return match.Success ? "res://" + match.Groups[1].Value : path;
    }

    private static ResourceFile? MatchImportedFile(string targetPath, IReadOnlyList<ResourceFile> files)
    {
        var exact = files.FirstOrDefault(file => file.Path.Equals(targetPath, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        var suffix = targetPath.EndsWith(".spatlas", StringComparison.OrdinalIgnoreCase) ? ".spatlas"
            : targetPath.EndsWith(".spskel", StringComparison.OrdinalIgnoreCase) ? ".spskel"
            : targetPath.EndsWith(".ctex", StringComparison.OrdinalIgnoreCase) ? ".ctex"
            : System.IO.Path.GetExtension(targetPath);
        var matches = files.Where(file => file.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static byte[] RewriteTextResource(
        byte[] bytes,
        IReadOnlyDictionary<string, string> replacements,
        IReadOnlyDictionary<string, string>? extraReplacements,
        bool stripUids = true)
    {
        var text = Encoding.UTF8.GetString(bytes);
        // 按 key 长度降序替换，避免短 key 是长 key 前缀时破坏后者；
        // 同时要求 key 后紧跟路径终止符，防止 ".gd" 误伤 ".gdc" 这类前缀引用。
        foreach (var replacement in replacements
                     .Concat(extraReplacements ?? new Dictionary<string, string>())
                     .OrderByDescending(pair => pair.Key.Length))
        {
            text = Regex.Replace(
                text,
                Regex.Escape(replacement.Key) + "(?=[\\x00\\\"'\\s,\\]\\[]|$)",
                replacement.Value.Replace("$", "$$"),
                RegexOptions.IgnoreCase);
        }

        if (stripUids)
        {
            text = UidLineRegex().Replace(text, string.Empty);
            text = UidAttributeRegex().Replace(text, string.Empty);
        }

        return Encoding.UTF8.GetBytes(text);
    }

    private static int GroupSortOrder(string id) => id switch
    {
        "ironclad" => 0,
        "silent" => 1,
        "regent" => 2,
        "necrobinder" => 3,
        "defect" => 4,
        "watcher" => 5,
        "colorless" => 6,
        "ancients" => 7,
        "misc" => 8,
        "neow" => 9,
        "merchant" => 10,
        _ => 100
    };

    private static string DisplayName(string id) => id switch
    {
        "ironclad" => "铁甲战士",
        "silent" => "静默猎手",
        "regent" => "储君",
        "necrobinder" => "亡灵契约师",
        "defect" => "故障机器人",
        "watcher" => "观者",
        "colorless" => "无色",
        "ancients" => "先古",
        "misc" => "其他",
        "neow" => "涅奥",
        "merchant" => "商人",
        "fake_merchant_monster" => "商人？？？",
        "byrdpip" => "异鸟宝宝",
        _ => id.Replace('_', ' ').Trim().CapitalizeWords()
    };

    internal static readonly HashSet<string> KnownAncientIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "darv",
        "neow",
        "nonupeipe",
        "orobas",
        "pael",
        "tanx",
        "tezcatara",
        "vakuu"
    };

    [GeneratedRegex("^res://animations/(?:characters|character_select|merchant|rest_site)/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterPathRegex();

    [GeneratedRegex("(?:^|/)card_portraits/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex CardPortraitGroupRegex();

    [GeneratedRegex("/(?:card_portraits|[^/]*card[^/]*\\.sprites|cards?|card_art|cardart)/", RegexOptions.IgnoreCase)]
    private static partial Regex CardArtPathRegex();

    [GeneratedRegex("/(?:card_portraits|[^/]*cards?\\.sprites|cards?|card_art|cardart)/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex CardArtIdentityRegex();

    [GeneratedRegex("^res://animations/monsters/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex MonsterPathRegex();

    [GeneratedRegex(
        "^(?:res://animations/backgrounds/merchant_room/|" +
        "res://animations/customs/merchant/|" +
        "res://scenes/rooms/merchant_button\\.tscn$|" +
        "res://scenes/merchant/(?!characters/)[^/]+\\.tscn$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex MerchantAppearancePathRegex();

    [GeneratedRegex(
        "^(?:res://animations/backgrounds/fake_merchant_room/(?:top|hand)/.*|" +
        "res://scenes/backgrounds/fake_merchant_event_encounter/.*|" +
        "res://scenes/events/custom/fake_merchant(?:_button|_inventory)?\\.tscn)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex FakeMerchantAppearancePathRegex();

    [GeneratedRegex("^res://scenes/creature_visuals/([^/.]+)\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex CreatureVisualSceneRegex();

    [GeneratedRegex("^res://scenes/screens/char_select/char_select_bg_([^/.]+)\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterSelectSceneRegex();

    [GeneratedRegex("(?:^|/)char_select_bg_([^/.]+)\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex AnyCharacterSelectSceneRegex();

    [GeneratedRegex("^res://scenes/merchant/characters/([^/.]+)_merchant\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex MerchantCharacterSceneRegex();

    [GeneratedRegex("^res://scenes/rest_site/characters/([^/.]+)_rest_site\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex RestSiteCharacterSceneRegex();

    [GeneratedRegex("^res://scenes/events/background_scenes/([^/.]+)\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex AncientBackgroundSceneRegex();

    [GeneratedRegex("^res://animations/backgrounds/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex AncientBackgroundAnimationRegex();

    [GeneratedRegex(
        "^res://images/ancients/(?<id>.+?)_(?<kind>character|character_sleeping|character_mask|background_cover)\\.(?:png|webp|jpe?g|svg)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AncientLayerImageRegex();

    [GeneratedRegex(
        "^res://images/(?:ancients/)?(?<id>[^/.]+?)(?:_placeholder)?\\.(?:png|webp|jpe?g|svg)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AncientStaticImageRegex();

    [GeneratedRegex("^res://images/packed/character_select/char_select_([^/.]+?)(?:_locked)?\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterSelectIconRegex();

    [GeneratedRegex("^res://images/ui/top_panel/character_icon_([^/.]+?)(?:_outline)?\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterUiTextureRegex();

    [GeneratedRegex(
        "^res://images/atlases/relic(?:_outline)?_atlas\\.sprites/[^/]+\\.tres$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RelicAtlasSpriteRegex();

    [GeneratedRegex(
        "^res://images/atlases/relic(?:_outline)?_atlas\\.png$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RelicAtlasTextureRegex();

    [GeneratedRegex(
        "(?m)^\\s*(?<property>region|margin)\\s*=\\s*Rect2\\(\\s*(?<x>-?[0-9.]+)\\s*,\\s*(?<y>-?[0-9.]+)\\s*,\\s*(?<width>-?[0-9.]+)\\s*,\\s*(?<height>-?[0-9.]+)\\s*\\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex AtlasTextureRectRegex();

    [GeneratedRegex("(?m)^\\s*filter_clip\\s*=\\s*true\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AtlasTextureFilterClipRegex();

    [GeneratedRegex("^res://scenes/ui/character_icons/([^/.]+?)_icon\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterIconSceneRegex();

    [GeneratedRegex("^res://images/packed/map/icons/map_marker_([^/.]+)\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterMapMarkerRegex();

    [GeneratedRegex("^res://images/packed/map/ancients/ancient_node_([^/.]+?)(?:_outline)?\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex AncientMapIconRegex();

    [GeneratedRegex("^res://images/ui/run_history/([^/.]+?)(?:_outline)?\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex AncientRunHistoryIconRegex();

    [GeneratedRegex("^res://custom/scenes/screens/char_select/char_select_bg_([^/.]+)\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeCharacterSelectSceneRegex();

    [GeneratedRegex("^res://custom/images/packed/character_select/char_select_([^/.]+?)(?:_locked)?\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeCharacterSelectIconRegex();

    [GeneratedRegex("^res://scenes/creature_visuals/templates/([^/.]+?)_template\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeCreatureTemplateRegex();

    [GeneratedRegex("^res://scenes/merchant/characters/templates/([^/.]+?)_merchant_template\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeMerchantTemplateRegex();

    [GeneratedRegex("^res://scenes/rest_site/characters/templates/([^/.]+?)_rest_site_template\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeRestSiteTemplateRegex();

    [GeneratedRegex("^res://custom/scenes/ui/character_icons/([^/.]+?)_icon\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeCharacterIconTemplateRegex();

    [GeneratedRegex("^res://scenes/ui/character_icons/templates/([^/.]+?)_icon\\.tscn$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterIconTemplateRegex();

    [GeneratedRegex("^res://images/ui/hands/multiplayer_hand_([^/.]+?)_(?:paper|point|rock|scissors)\\.(?:png|tres)$", RegexOptions.IgnoreCase)]
    private static partial Regex MultiplayerHandRegex();

    [GeneratedRegex("^res://waifu_assets/[^/]+/(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WaifuAssetsPathRegex();

    [GeneratedRegex("\"(res://[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ResourcePathRegex();

    [GeneratedRegex(
        "res://[^\\x00\\\"'\\r\\n\\t \\]\\[(){}<>]+?\\.(?:spatlas|spskel|ctex|tscn|tres|cs|gdc|gd|gdshader|scn|res|png|webp|jpe?g|svg|skel|atlas|json|ogg|wav|mp3)(?=[\\x00\\\"'\\r\\n\\t \\]\\[(){}<>]|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedResourcePathRegex();

    [GeneratedRegex("^uid=\"uid://[^\"]+\"\\r?\\n", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex UidLineRegex();

    [GeneratedRegex("\\s+uid=\"uid://[^\"]+\"", RegexOptions.IgnoreCase)]
    private static partial Regex UidAttributeRegex();

    private sealed record GroupIdentity(string Id, string DisplayName);
    private sealed record RuntimeProviderAsset(GroupIdentity Identity, string CanonicalPath);
    private sealed record CardArtPathLayout(string Category, string Variant);
    private sealed record CardArtIdentity(string Category, string Stem);
    private sealed record CardArtVariant(string Key, ResourceAsset[] Assets, HashSet<string> Stems);
    private sealed record RuntimeResource(
        string SourcePath,
        ResourceFile? DirectFile,
        ResourceFile? RemapFile,
        IReadOnlyList<ResourceFile> PayloadFiles);
}

internal sealed record SkinModDescriptor(
    string Id,
    string Name,
    string? PckPath,
    bool AffectsGameplay,
    string? RootPath = null,
    bool HasDll = false,
    string? ManifestId = null)
{
    public string ResourceNamespaceId => ManifestId ?? Id;
}

internal sealed record SkinProviderProbe(
    string Id,
    string? RootPath,
    int VisualGroupCount,
    int CardAssetCount,
    int CardPresentationCount,
    int RuntimeImageCount,
    int ManagedScriptCount,
    bool HasInteractiveScenes,
    string? ManifestId = null,
    bool HasResourceBackedCosmetics = false)
{
    public string ResourceNamespaceId => ManifestId ?? Id;
}

internal sealed record AncientLayeredImagePaths(
    string Character,
    string? BackgroundCover,
    string? Mask,
    string? SleepingCharacter);

internal sealed record RuntimeAncientImage(
    string GroupId,
    string Path,
    string RelativePath,
    string Name);

internal sealed record RuntimeAncientImageCacheEntry(
    DateTime RootWriteTimeUtc,
    IReadOnlyList<RuntimeAncientImage> Images);

internal sealed record ManagedScriptCountCacheEntry(
    long Length,
    DateTime LastWriteTimeUtc,
    int Count);

internal sealed class SkinGroup(string id, string displayName)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public List<SkinOption> Options { get; } = [];
}

internal sealed record SkinOption(
    string Id,
    string Name,
    IReadOnlyDictionary<string, ResourceAsset> Assets,
    bool IsRuntimeProvider = false,
    bool IsDirectCharacterRuntimeProvider = false,
    string? RuntimeImagePath = null,
    ResourceAsset? ManagedMonsterScene = null,
    RuntimeMonsterVisualMode? RuntimeMonsterVisualMode = null,
    string? ProviderId = null,
    bool IsManagedMonsterRuntimeProfile = false,
    FrameworkCharacterSkinContract? FrameworkContract = null,
    bool IsCharacterIconOnly = false)
{
    public string EffectiveProviderId =>
        ProviderId ?? RuntimeMonsterVisualMode?.ProviderId ?? Id;

    public IReadOnlyList<string> CompositionSourceOptionIds { get; init; } = [];

    public IReadOnlyList<string> CompositionSourceProviderIds { get; init; } = [];

    public bool IsSessionComposition { get; init; }

    public bool IsComposition => CompositionSourceOptionIds.Count > 0;
}

internal sealed record RuntimeMonsterVisualMode(
    string ProviderId,
    string AssemblyPath,
    string ServiceTypeName,
    string EnumTypeName,
    string SetterName,
    string ModeName,
    string DisplayName)
{
    public IReadOnlyList<string> ResourcePaths { get; init; } = [];
}

internal sealed class CardSkinGroup(string id, string displayName)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public List<CardSkinOption> Options { get; } = [];
}

internal sealed record CardSkinOption(
    string Id,
    string Name,
    IReadOnlyDictionary<string, string> NormalPortraits,
    IReadOnlyDictionary<string, AncientCardPortrait> AncientPortraits,
    IReadOnlyDictionary<string, ResourceAsset>? PckAssets = null,
    string? ProviderRootPath = null,
    string? ProviderId = null,
    IReadOnlyDictionary<string, CardPresentationDefinition>? Presentations = null)
{
    public IReadOnlyDictionary<string, ResourceAsset> Assets { get; init; } =
        PckAssets ?? new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, CardPresentationDefinition> CardPresentations { get; init; } =
        Presentations ?? new Dictionary<string, CardPresentationDefinition>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> CardNames { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string GetNameForCard(string cardType) => CardNames.GetValueOrDefault(cardType) ?? Name;

    public CardSkinOption Merge(CardSkinOption other)
    {
        var normal = new Dictionary<string, string>(NormalPortraits, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in other.NormalPortraits)
        {
            normal[pair.Key] = pair.Value;
        }

        var ancient = new Dictionary<string, AncientCardPortrait>(AncientPortraits, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in other.AncientPortraits)
        {
            ancient[pair.Key] = pair.Value;
        }

        var assets = new Dictionary<string, ResourceAsset>(Assets, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in other.Assets)
        {
            assets[pair.Key] = pair.Value;
        }
        var presentations = new Dictionary<string, CardPresentationDefinition>(
            CardPresentations,
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in other.CardPresentations)
        {
            presentations[pair.Key] = pair.Value;
        }
        var names = new Dictionary<string, string>(CardNames, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in other.CardNames)
        {
            names[pair.Key] = pair.Value;
        }

        return this with
        {
            NormalPortraits = normal,
            AncientPortraits = ancient,
            Assets = assets,
            CardPresentations = presentations,
            CardNames = names,
            ProviderRootPath = ProviderRootPath ?? other.ProviderRootPath,
            ProviderId = ProviderId ?? other.ProviderId
        };
    }

    public string? GetPortraitPath(string cardType, bool useAncientStyle)
    {
        if (AncientPortraits.TryGetValue(cardType, out var ancient))
        {
            var path = useAncientStyle ? ancient.AncientPortrait : ancient.NormalPortrait;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return NormalPortraits.GetValueOrDefault(cardType);
    }
}

internal sealed record CardCatalogEntry(
    string TypeName,
    string PortraitPath,
    string PoolGroupId,
    string CatalogGroupId,
    string FilterGroupId);

internal sealed record AncientCardPortrait(string? NormalPortrait, string? AncientPortrait);

internal sealed record ExportedCardPortraits(
    IReadOnlyDictionary<string, string> Normal,
    IReadOnlyDictionary<string, AncientCardPortrait> Ancient,
    IReadOnlyList<ExportedCardPortraitMode> Modes);

internal sealed record ExportedCardPortraitMode(
    string IdSuffix,
    string NameMarker,
    IReadOnlyDictionary<string, string> Portraits,
    bool UseAncientLayout);

internal sealed record CardPresentationDefinition(
    bool UseAncientLayout = false,
    string? Frame = null,
    string? FrameMaterial = null,
    string? BannerTexture = null,
    string? BannerMaterial = null,
    string? PortraitBorder = null,
    string? PortraitBorderMaterial = null,
    string? AncientTextBackground = null,
    string? TextBackgroundMaterial = null,
    string? EnergyIcon = null,
    string? Highlight = null,
    string? HighlightMaterial = null,
    bool? FrameVisible = null,
    bool? BannerVisible = null,
    bool? TextBackgroundVisible = null,
    bool? PortraitBorderVisible = null,
    bool? EnergyIconVisible = null,
    bool? HighlightVisible = null,
    bool? TypePlaqueVisible = null,
    bool? TypeLabelVisible = null,
    bool? DescriptionVisible = null,
    bool? InfectionOverlayVisible = null,
    bool UseFullFrameArt = false,
    bool UseExpandedPortraitLayout = false,
    string? FrameOverlay = null,
    bool? PortraitVisible = null,
    float? FrameOverlayOffsetTop = null,
    float? FrameOverlayOffsetBottom = null,
    float? FrameOverlayOffsetLeft = null,
    float? FrameOverlayOffsetRight = null,
    float? FrameOverlayScaleX = null,
    float? FrameOverlayScaleY = null)
{
    public IEnumerable<string> ResourcePaths => new[]
        {
            Frame,
            FrameMaterial,
            BannerTexture,
            BannerMaterial,
            PortraitBorder,
            PortraitBorderMaterial,
            AncientTextBackground,
            TextBackgroundMaterial,
            EnergyIcon,
            Highlight,
            HighlightMaterial,
            FrameOverlay
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase);
}

internal sealed class CardFrameReplacementDocument
{
    public List<CardFrameReplacementEntry> Entries { get; set; } = [];
}

internal sealed class CardFrameReplacementEntry
{
    public string CardId { get; set; } = string.Empty;
    public string UiMode { get; set; } = string.Empty;
    public string? Frame { get; set; }
    public string? FrameMaterial { get; set; }
    public string? BannerTexture { get; set; }
    public string? BannerMaterial { get; set; }
    public string? PortraitBorder { get; set; }
    public string? PortraitBorderMaterial { get; set; }
    public string? AncientTextBg { get; set; }
    public string? TextBackgroundMaterial { get; set; }
    public string? EnergyIcon { get; set; }
    public string? Highlight { get; set; }
    public string? HighlightMaterial { get; set; }
    public bool? FrameVisible { get; set; }
    public bool? BannerVisible { get; set; }
    public bool? TextBackgroundVisible { get; set; }
    public bool? PortraitBorderVisible { get; set; }
    public bool? EnergyIconVisible { get; set; }
    public bool? HighlightVisible { get; set; }
    public bool? TypePlaqueVisible { get; set; }
    public bool? TypeLabelVisible { get; set; }
    public bool? DescriptionVisible { get; set; }
    public bool? InfectionOverlayVisible { get; set; }
}

internal sealed class CardReplacementConfig
{
    public List<NormalCardReplacement> NormalReplacements { get; set; } = [];
    public List<AncientCardReplacement> AncientReplacements { get; set; } = [];
}

internal sealed class NormalCardReplacement
{
    public string CardType { get; set; } = string.Empty;
    public string PortraitPath { get; set; } = string.Empty;
    public string? DifferentialPortrait { get; set; }
}

internal sealed class AncientCardReplacement
{
    public string CardType { get; set; } = string.Empty;
    public string? NormalPortrait { get; set; }
    public string? AncientPortrait { get; set; }
    public string? DifferentialPortrait { get; set; }
    public string? ConfigKey { get; set; }
    public string? PathForGrouping =>
        !string.IsNullOrWhiteSpace(AncientPortrait) ? AncientPortrait : NormalPortrait;
}

internal sealed record RuntimeResourceOverlay(
    IReadOnlyDictionary<string, string> ResourcePaths,
    IReadOnlyDictionary<string, byte[]> Files,
    IReadOnlyDictionary<string, string> SourceAliases,
    IReadOnlyDictionary<string, string> PayloadAliases,
    IReadOnlySet<string> CanonicalDependencyPaths,
    bool CanReuseExternalDependencies = false);

internal readonly record struct RelicTextureRect(float X, float Y, float Width, float Height);

internal sealed record BaselineRelicTextureDefinition(
    string AtlasPath,
    RelicTextureRect Region,
    RelicTextureRect Margin,
    bool FilterClip);

internal sealed record VisualResourceBinding(
    string SourcePath,
    IReadOnlyList<string> Files);

internal sealed class ResourceAsset(string sourcePath)
{
    public string SourcePath { get; } = sourcePath;
    public List<ResourceFile> Files { get; } = [];

    public void AddFile(PckArchive archive, string path)
    {
        if (Files.All(file => !file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            Files.Add(new ResourceFile(archive, path));
        }
    }
}

internal sealed record ResourceFile(PckArchive Archive, string Path);

internal sealed partial class PckResourceIndex : IDisposable
{
    private readonly Dictionary<string, string> _importedToSource;

    private PckResourceIndex(SkinModDescriptor mod, PckArchive archive, Dictionary<string, string> importedToSource)
    {
        Mod = mod;
        Archive = archive;
        _importedToSource = importedToSource;
    }

    public SkinModDescriptor Mod { get; }
    public PckArchive Archive { get; }
    public Dictionary<string, ResourceAsset> Assets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static PckResourceIndex Build(
        SkinModDescriptor mod,
        PckArchive archive,
        Dictionary<string, string> importedToSource,
        Func<string, bool>? remapFilter)
    {
        var index = new PckResourceIndex(mod, archive, importedToSource);
        foreach (var remapPath in archive.Paths.Where(IsRemapPath))
        {
            if (remapFilter != null && !remapFilter(remapPath))
            {
                continue;
            }

            index.AddRemap(remapPath);
        }

        foreach (var path in archive.Paths.Where(IsDirectAnimationResource))
        {
            var sourcePath = SkinCatalog.NormalizeTakeoverPath(path);
            index.GetAsset(sourcePath).AddFile(archive, path);
        }

        foreach (var importedPath in archive.Paths.Where(IsImportedPath))
        {
            if (importedToSource.TryGetValue(importedPath, out var sourcePath))
            {
                index.GetAsset(sourcePath).AddFile(archive, importedPath);
            }
        }

        // A few character providers ship their portraits/icons as raw image files without a
        // matching .import entry.  The old index only registered imported payloads, so those
        // files never reached the character option and could not be included in a multiplayer
        // safe package.  Keep this lazy and generic: only direct images that can be mapped to a
        // known character/runtime-provider group are indexed; card art and unrelated images stay
        // out of the catalog.
        foreach (var path in archive.Paths.Where(IsDirectCharacterImageResource))
        {
            var sourcePath = SkinCatalog.NormalizeTakeoverPath(path);
            if (!SkinCatalog.IsCharacterImageResourceForProvider(
                    mod.ResourceNamespaceId,
                    sourcePath))
            {
                continue;
            }

            index.GetAsset(sourcePath).AddFile(archive, path);
        }

        return index;
    }

    public ResourceAsset? TryBuildAsset(string sourcePath)
    {
        if (Assets.TryGetValue(sourcePath, out var existing))
        {
            return existing;
        }

        var normalizedSourcePath = SkinCatalog.NormalizeTakeoverPath(sourcePath);
        if (!normalizedSourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase) &&
            Assets.TryGetValue(normalizedSourcePath, out existing))
        {
            return existing;
        }

        foreach (var suffix in new[] { ".import", ".remap" })
        {
            var remapPath = sourcePath + suffix;
            if (Archive.Contains(remapPath))
            {
                AddRemap(remapPath);
                return Assets.GetValueOrDefault(normalizedSourcePath);
            }
        }

        if (Archive.Contains(sourcePath))
        {
            var asset = GetAsset(normalizedSourcePath);
            asset.AddFile(Archive, sourcePath);
            return asset;
        }

        return null;
    }

    public void Dispose() => Archive.Dispose();

    private void AddRemap(string remapPath)
    {
        var sourcePath = remapPath.EndsWith(".import", StringComparison.OrdinalIgnoreCase)
            ? remapPath[..^7]
            : remapPath[..^6];
        sourcePath = SkinCatalog.NormalizeTakeoverPath(sourcePath);
        var asset = GetAsset(sourcePath);
        asset.AddFile(Archive, remapPath);

        var text = Encoding.UTF8.GetString(Archive.ReadFile(remapPath));
        foreach (Match match in RemapTargetRegex().Matches(text))
        {
            var targetPath = match.Groups[1].Value;
            _importedToSource[targetPath] = sourcePath;
            if (Archive.Contains(targetPath))
            {
                asset.AddFile(Archive, targetPath);
            }
        }

        // Godot 4 .import 的 [deps] dest_files 数组可能包含多个导入产物（音频/字体/3D 等），
        // 逐一登记，避免这些 payload 在覆盖包生成时丢失。
        var destFiles = DestFilesArrayRegex().Match(text);
        if (destFiles.Success)
        {
            foreach (Match pathMatch in QuotedPathRegex().Matches(destFiles.Value))
            {
                var targetPath = pathMatch.Groups[1].Value;
                _importedToSource[targetPath] = sourcePath;
                if (Archive.Contains(targetPath))
                {
                    asset.AddFile(Archive, targetPath);
                }
            }
        }
    }

    private ResourceAsset GetAsset(string sourcePath)
    {
        if (!Assets.TryGetValue(sourcePath, out var asset))
        {
            asset = new ResourceAsset(sourcePath);
            Assets.Add(sourcePath, asset);
        }

        return asset;
    }

    private static bool IsRemapPath(string path) =>
        path.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase);

    private static bool IsImportedPath(string path) =>
        path.StartsWith("res://.godot/imported/", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectAnimationResource(string path) =>
        (SkinCatalog.NormalizeTakeoverPath(path).StartsWith("res://animations/", StringComparison.OrdinalIgnoreCase) ||
         SkinCatalog.NormalizeTakeoverPath(path).StartsWith("res://backgrounds/", StringComparison.OrdinalIgnoreCase) ||
         SkinCatalog.NormalizeTakeoverPath(path).StartsWith("res://scenes/", StringComparison.OrdinalIgnoreCase)) &&
        (path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase));

    private static bool IsDirectCharacterImageResource(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("path(?:\\.[a-z0-9_]+)?\\s*=\\s*\"(res://[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex RemapTargetRegex();

    [GeneratedRegex("dest_files\\s*=\\s*\\[[^\\]]*\\]", RegexOptions.IgnoreCase)]
    private static partial Regex DestFilesArrayRegex();

    [GeneratedRegex("\"(res://[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedPathRegex();
}

internal static class DisplayTextExtensions
{
    public static string CapitalizeWords(this string value)
    {
        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
