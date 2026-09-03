namespace STS2SkinChanger.Catalog;

/// <summary>
/// Explicit, user-requested presentation variants. These are not skin detection rules:
/// a tall image or a reference to AncientPortrait alone does not imply Ancient effects.
/// </summary>
internal static class CardLayoutVariantPolicy
{
    public const string WithoutEffectsMarker = "{skin-changer-expanded-without-effects}";
    public const string WithEffectsMarker = "{skin-changer-expanded-with-effects}";

    private sealed record Request(string ProviderNamespace, string CardType, string PortraitPath);

    private static readonly Request[] Requests =
    [
        new("SilentCardAnimerRework", "EscapePlan",
            "res://generated/assets/card_art/MegaCrit.Sts2.Core.Models.Cards.EscapePlan_card_art.png")
    ];

    public static IReadOnlyList<CardSkinOption> Expand(CardSkinOption source, string providerNamespace)
    {
        var primary = source;
        var variants = new List<CardSkinOption>();
        foreach (var request in Requests)
        {
            // Scope to the audited image and respect any future author-declared layout.
            // Instance IDs may differ when multiple snapshots of a provider are installed.
            if (!request.ProviderNamespace.Equals(providerNamespace, StringComparison.OrdinalIgnoreCase) ||
                source.CardPresentations.ContainsKey(request.CardType) ||
                source.AncientPortraits.ContainsKey(request.CardType) ||
                !source.NormalPortraits.TryGetValue(request.CardType, out var portrait) ||
                !request.PortraitPath.Equals(portrait, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var presentations = new Dictionary<string, CardPresentationDefinition>(
                primary.CardPresentations, StringComparer.OrdinalIgnoreCase)
            {
                [request.CardType] = new(UseExpandedPortraitLayout: true)
            };
            var names = new Dictionary<string, string>(primary.CardNames, StringComparer.OrdinalIgnoreCase)
            {
                [request.CardType] = source.Name + " · " + WithoutEffectsMarker
            };
            // Keep the original ID and the rest of the pack unchanged, including existing
            // per-card selections, category priority, presets and normal card labels.
            primary = primary with { CardPresentations = presentations, CardNames = names };
            variants.Add(new CardSkinOption(
                source.Id + "::card-layout:" + request.CardType.ToLowerInvariant() + ":ancient",
                source.Name + " · " + WithEffectsMarker,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [request.CardType] = portrait },
                new Dictionary<string, AncientCardPortrait>(StringComparer.OrdinalIgnoreCase),
                source.Assets.Where(pair => pair.Key.Equals(portrait, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                source.ProviderRootPath,
                source.ProviderId,
                new Dictionary<string, CardPresentationDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    [request.CardType] = new(UseAncientLayout: true)
                }));
        }

        variants.Insert(0, primary);
        return variants;
    }
}
