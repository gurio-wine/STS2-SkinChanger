using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.HoverTips;
using STS2SkinChanger.Pck;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

// One winning provider owns the whole card. Only its pure state-art resolver is called;
// never initialize its Harmony patches, model getters, preload-all job or gameplay replacement.
internal static class StatefulCardArtRuntime
{
    private static readonly Dictionary<string, Runtime?> Runtimes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> RetryAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Warnings = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<CardModel, WeakReference<NCard>> CardNodes = new();
    internal static void Bind(NCard card)
    {
        if (card.Model == null) return;
        CardNodes.Remove(card.Model);
        CardNodes.Add(card.Model, new(card));
    }

    internal static bool NodeForCard(CardModel __0, ref NCard? __result)
    {
        __result = CardNodes.TryGetValue(__0, out var reference) && reference.TryGetTarget(out var node) &&
            GodotObject.IsInstanceValid(node) && ReferenceEquals(node.Model, __0) ? node : null;
        return false;
    }

    internal static Runtime? For(CardSkinOption? option)
    {
        if (option?.StatefulArt == null || option.ProviderId == null) return null;
        if (Runtimes.TryGetValue(option.ProviderId, out var runtime) && runtime != null) return runtime;
        if (RetryAt.GetValueOrDefault(option.ProviderId) > System.Environment.TickCount64) return null;
        try
        {
            var assembly = ManagedSkinModLoader.GetStatefulCardArtAssembly(option.ProviderId);
            if (assembly == null) return null; // Dependency may not yet have loaded; allow a later attempt.
            if (Runtimes.TryGetValue(option.ProviderId, out runtime) && runtime != null) return runtime;
            if (RetryAt.GetValueOrDefault(option.ProviderId) > System.Environment.TickCount64) return null;
            runtime = new Runtime(option.StatefulArt, assembly);
            Runtimes[option.ProviderId] = runtime;
            ModLog.Info($"Stateful card art ready: {option.ProviderId}; per-card ownership, no original PatchAll or preload.");
            return runtime;
        }
        catch (Exception exception)
        {
            Warn(option.ProviderId, exception);
            Runtimes[option.ProviderId] = null;
            // A selection can arrive before BaseLib finishes loading. Do not permanently turn
            // that transient ordering failure into image-only mode, or retry once per card/frame.
            RetryAt[option.ProviderId] = System.Environment.TickCount64 + 2000;
            return null;
        }
    }

    internal static Runtime? For(CardModel? card) => card == null ? null : For(SkinService.GetSelectedCardOption(card));
    internal static void Warn(string operation, Exception exception)
    {
        var error = exception.GetBaseException();
        if (Warnings.Add(operation + error.Message)) ModLog.Warn($"Stateful card art {operation}: {error.Message}; retaining safe card rendering.");
    }

    internal static string? ValidatePortrait(StatefulCardArtContract contract, string? path, bool upgraded)
    {
        if (path == null || !path.StartsWith(contract.AtlasRoot, StringComparison.Ordinal) ||
            !path.EndsWith(".tres", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal) ||
            !contract.AvailablePortraits.Contains(path)) return null;
        if (upgraded && !path.EndsWith("_plus.tres", StringComparison.Ordinal))
        {
            var plus = path[..^5] + "_plus.tres";
            if (contract.AvailablePortraits.Contains(plus)) return plus;
        }
        return path;
    }

    internal sealed class Runtime
    {
        internal StatefulCardArtContract Contract { get; }
        internal Assembly Assembly { get; }
        private readonly Type _settings;
        private readonly Type _art;
        private readonly Dictionary<Type, Type> _handlers = new();
        private readonly Dictionary<Type, object?> _instances = new();
        private readonly Dictionary<string, PropertyInfo?> _settingsProperties = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _localizations = new(StringComparer.Ordinal);

        internal Runtime(StatefulCardArtContract contract, Assembly assembly)
        {
            Contract = contract;
            Assembly = assembly;
            _settings = assembly.GetType(contract.SettingsType, true)!;
            _art = assembly.GetType(contract.ArtType, true)!;
            if (_art.TypeInitializer?.GetMethodBody()?.GetILAsByteArray() is not [0x2a])
                throw new InvalidOperationException("Provider was already initialized outside the isolated loader; restart with corrected load order.");
            using (var pack = PckArchive.Open(Path.ChangeExtension(contract.AssemblyPath, ".pck")))
            {
                if (Contract.AvailablePortraits.Count == 0)
                    Contract = contract with { AvailablePortraits = pack.Paths.Where(path => path.StartsWith(contract.AtlasRoot, StringComparison.Ordinal) &&
                        path.EndsWith(".tres", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal) };
                foreach (var path in pack.Paths.Where(path => path.StartsWith(contract.ResourceRoot + "/localization/", StringComparison.Ordinal) &&
                             new[] { "artists.json", "usernames.json", "event_chatter.json" }.Contains(Path.GetFileName(path))))
                    _localizations[path] = pack.ReadFile(path);
            }
            InstallLocalization();
            // Config registration is intentionally separate from the provider initializer.
            AccessTools.Method(_settings, "Init").Invoke(null, null);
            RegisterHandlers(assembly);
            // This optional supplement contains card-state resolvers for beta-only card types.
            // Never load it on stable merely because the file is present.
            if (typeof(CardModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.Cards.Dowsing") != null)
            {
                var supplement = Path.ChangeExtension(contract.AssemblyPath, ".Beta.betapack");
                if (File.Exists(supplement))
                    RegisterHandlers(AssemblyLoadContext.GetLoadContext(assembly)!.LoadFromAssemblyPath(supplement));
            }
            var nodeHelper = assembly.GetTypes().FirstOrDefault(type => type.Name == "NCardAwareCardsPatch");
            if (nodeHelper != null && AccessTools.Method(nodeHelper, "get_Node", [typeof(CardModel)]) is {} method)
                new Harmony("Gurio.SkinChanger.stateful-card-node").Patch(method,
                    prefix: new HarmonyMethod(typeof(StatefulCardArtRuntime), nameof(NodeForCard)));
        }

        private void RegisterHandlers(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes().Where(type => !type.IsAbstract && type.BaseType is { IsGenericType: true }))
            {
                if (type.BaseType!.GetGenericTypeDefinition().FullName != Contract.ArtType + "`1") continue;
                _handlers[type.BaseType.GetGenericArguments()[0]] = type;
            }
        }

        internal bool Setting(string name, bool fallback = false)
        {
            try { return Property(name)?.GetValue(null) is bool value ? value : fallback; }
            catch (Exception exception) { Warn("setting " + name, exception); return fallback; }
        }
        private PropertyInfo? Property(string name)
        {
            if (!_settingsProperties.TryGetValue(name, out var property))
                _settingsProperties[name] = property = AccessTools.Property(_settings, name);
            return property;
        }
        internal bool Enabled => Setting("UseCustomArt", true);
        internal bool Simple => Setting("UseSimpleMode");

        internal void InstallLocalization()
        {
            if (LocManager.Instance == null || AccessTools.Field(typeof(LocManager), "_tables").GetValue(LocManager.Instance)
                is not Dictionary<string, LocTable> tables) return;
            foreach (var name in new[] { "artists", "usernames", "event_chatter" })
            {
                var data = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var language in new[] { "eng", LocManager.Instance.Language }.Distinct())
                {
                    if (!_localizations.TryGetValue(Contract.ResourceRoot + "/localization/" + language + "/" + name + ".json", out var bytes)) continue;
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(bytes);
                    if (parsed != null) foreach (var pair in parsed) data[pair.Key] = pair.Value;
                }
                // Dedicated table names are rewritten in the loaded provider copy. Never replace
                // another Mod's generic "artists"/"event_chatter" tables or game localization.
                var table = Contract.LocalizationPrefix + name;
                tables[table] = new LocTable(table, data);
            }
        }

        internal IEnumerable<IHoverTip> Credits(CardModel card)
        {
            var type = Assembly.GetType(Contract.SettingsType[..Contract.SettingsType.LastIndexOf('.')] + ".Credits");
            return AccessTools.Method(type, "Tooltips")?.Invoke(null, [card]) is IEnumerable<IHoverTip> tips ? tips.ToArray() : [];
        }

        internal string? Portrait(CardModel card, string? baseline)
        {
            string? path = null;
            try
            {
                var cardType = card.GetType();
                if ((!Simple || cardType.Name == "MadScience") && _handlers.TryGetValue(cardType, out var handlerType))
                {
                    if (!_instances.TryGetValue(handlerType, out var handler))
                        _instances[handlerType] = handler = Activator.CreateInstance(handlerType, nonPublic: true);
                    if (handler != null)
                    {
                        var img = _art.GetMethod("Get", [typeof(CardModel)])!.Invoke(handler, [card]);
                        path = img?.GetType().GetProperty("PortraitPath")?.GetValue(img) as string;
                    }
                }
            }
            catch (Exception exception) { Warn(card.GetType().Name, exception); }
            return ValidatePortrait(Contract, path, card.IsUpgraded) ?? ValidatePortrait(Contract, baseline, card.IsUpgraded);
        }

        internal CardPresentationDefinition? Presentation(CardModel card)
        {
            if (!Enabled) return null;
            var ancient = Setting("MakeEverythingAncient") || card.Rarity.ToString() == "Ancient";
            return new(UseAncientLayout: ancient, UseExpandedPortraitLayout: !ancient,
                BannerVisible: !Setting("HideTitleBanner", true),
                TextBackgroundVisible: !Setting("HideDescription", true),
                DescriptionVisible: !Setting("HideDescription", true),
                EnergyIconVisible: !Setting("HideEnergy", true) ||
                    (card.GetType().Name == "FranticEscape" && Setting("ExcludeFranticEscape")),
                TypeLabelVisible: !Setting("HideType", true), TypePlaqueVisible: !Setting("HideType", true));
        }
    }

    internal static void RefreshLocalization()
    {
        foreach (var runtime in Runtimes.Values.OfType<Runtime>())
            try { runtime.InstallLocalization(); } catch (Exception exception) { Warn("localization", exception); }
    }

    internal static bool Owns(Assembly assembly) => Runtimes.Values.Any(runtime => ReferenceEquals(runtime?.Assembly, assembly));

    internal static Runtime? Ensure(string providerId, StatefulCardArtContract contract) =>
        For(new CardSkinOption(providerId, providerId, new Dictionary<string, string>(),
            new Dictionary<string, AncientCardPortrait>(), ProviderId: providerId) { StatefulArt = contract });
}

[HarmonyPatch(typeof(LocManager), nameof(LocManager.SetLanguage))]
internal static class StatefulCardLocalizationPatch
{
    private static void Postfix() => StatefulCardArtRuntime.RefreshLocalization();
}
