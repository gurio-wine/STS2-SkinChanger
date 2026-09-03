using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;
using System.Text;

internal static class CardLayoutVariantTests
{
    public static void Run(string gamePckPath)
    {
        const string provider = "SilentCardAnimerRework";
        const string portrait =
            "res://generated/assets/card_art/MegaCrit.Sts2.Core.Models.Cards.EscapePlan_card_art.png";
        var root = Path.Combine(Path.GetTempPath(), "skin-changer-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pck = Path.Combine(root, "cards.pck");
            PckArchive.Write(pck, new Dictionary<string, byte[]>
            {
                ["res://generated/card_replacements.json"] = Encoding.UTF8.GetBytes(
                    $$"""
                    {"entries":[
                      {"cardId":"MegaCrit.Sts2.Core.Models.Cards.EscapePlan","kind":"image","image":"{{portrait}}"},
                      {"cardId":"MegaCrit.Sts2.Core.Models.Cards.Acrobatics","kind":"image","image":"res://generated/other.png"}
                    ]}
                    """),
                ["res://generated/frame_replacements.json"] = Encoding.UTF8.GetBytes("{\"entries\":[]}"),
                [portrait] = [1, 2, 3],
                ["res://generated/other.png"] = [4, 5, 6]
            });
            using var catalog = SkinCatalog.Build(gamePckPath,
                [new SkinModDescriptor(provider, "Test card art", pck, false, root, false)]);
            Check(catalog.PckCardOptions.Count == 2, "requested card did not get two layout variants");
            var plain = catalog.PckCardOptions.Single(option => option.Id == provider);
            var effects = catalog.PckCardOptions.Single(option => option.Id != provider);
            Check(plain.CardPresentations["EscapePlan"] is
                { UseExpandedPortraitLayout: true, UseAncientLayout: false }, "no-effects layout is wrong");
            Check(effects.CardPresentations["EscapePlan"] is
                { UseExpandedPortraitLayout: false, UseAncientLayout: true }, "Ancient-effects layout is wrong");
            Check(plain.GetPortraitPath("EscapePlan", false) == portrait &&
                  effects.GetPortraitPath("EscapePlan", false) == portrait &&
                  effects.GetPortraitPath("EscapePlan", true) == portrait,
                "layout variants must use the same provider image in either art preference");
            Check(plain.NormalPortraits.Count == 2 && plain.CardPresentations.Count == 1 &&
                  effects.NormalPortraits.Count == 1 && effects.CardPresentations.Count == 1 &&
                  effects.AncientPortraits.Count == 0 && effects.Assets.Keys.All(path => path == portrait),
                "single-card variant leaked other cards or assets");
            Check(plain.GetNameForCard("EscapePlan") == "Test card art · 1" &&
                  plain.GetNameForCard("Acrobatics") == plain.Name &&
                  effects.GetNameForCard("EscapePlan") == "Test card art · 2",
                "variant names must use only the provider name and ordinal, without descriptive suffixes");

            var cards = new[] { "EscapePlan", "Acrobatics" }.Select(type => new CardCatalogEntry(
                type, $"res://images/card_portraits/silent/{type.ToLowerInvariant()}.png",
                "silent", "silent", "silent")).ToArray();
            catalog.FinalizeCardGroups(cards);
            var group = catalog.CardGroups.Single(group => group.Id == "silent");
            Check(group.Options.Count == 2 &&
                  group.Options.Single(option => option.Id == provider).GetNameForCard("EscapePlan") ==
                  plain.GetNameForCard("EscapePlan"), "category routing lost the variants or per-card names");
            Check(catalog.ResolveStoredCardSelectionId("silent", provider) == provider &&
                  catalog.ResolveStoredCardSelectionId("silent", effects.Id) == effects.Id,
                "old and new selection IDs must remain stable");
            foreach (var option in group.Options)
            {
                var overlay = catalog.BuildIsolatedCardResource("silent", option.Id, portrait,
                    useSelectedProvider: true, "self-test/layout/" + option.Id);
                Check(overlay.ResourcePaths.ContainsKey(portrait) && overlay.Files.Count > 0,
                    "layout variant failed to isolate the selected provider's image");
            }

            var original = plain with
            {
                CardPresentations = new Dictionary<string, CardPresentationDefinition>(),
                CardNames = new Dictionary<string, string>()
            };
            Check(CardLayoutVariantPolicy.Expand(original, "OtherProvider").Single() == original,
                "matching exported paths from another provider must not trigger a layout override");
            Check(CardLayoutVariantPolicy.Expand(original with
            {
                NormalPortraits = new Dictionary<string, string> { ["EscapePlan"] = "res://other.png" }
            }, provider).Count == 1, "a changed source image must not inherit a stale layout rule");
            Check(CardLayoutVariantPolicy.Expand(plain, provider).Count == 1,
                "already declared presentation must not be replaced or duplicated");
            var scoped = original with { Id = provider + "::source:test", ProviderId = provider + "::source:test" };
            Check(CardLayoutVariantPolicy.Expand(scoped, provider).All(option =>
                    option.Id.StartsWith(scoped.Id, StringComparison.Ordinal) && option.ProviderId == scoped.ProviderId),
                "duplicate provider instances lost their resource ownership");
            Console.WriteLine("Card layout variant isolation, naming and saved-selection tests passed.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
