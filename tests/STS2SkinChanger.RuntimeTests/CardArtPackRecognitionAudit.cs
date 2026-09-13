using System.Reflection;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;
using STS2SkinChanger.Pck;

/// <summary>
/// Read-only check of where an installed pure asset card-art pack lands: the Mod list marking,
/// the Workshop catalog export that drives active items and submission candidates, and the
/// submission scan that encodes the item. Mirrors SkinService startup without the engine.
/// </summary>
internal static class CardArtPackRecognitionAudit
{
    internal static void Run(string gamePck, params string[] modRoots)
    {
        var scan = CardArtPackScanner.Scan(modRoots);
        foreach (var warning in scan.Warnings)
        {
            Console.WriteLine("warning: " + warning);
        }

        var packs = scan.Packs;
        Console.WriteLine($"packs={packs.Count}");
        foreach (var pack in packs)
        {
            Console.WriteLine(
                $"  pack {pack.Id} ({pack.Name}) cards={pack.Cards.Count} " +
                $"workshop={SkinCatalog.WorkshopSourceId(pack.Root)} root={pack.Root}");
        }

        if (packs.Count == 0)
        {
            return;
        }

        var cards = CardEntries(gamePck);
        Console.WriteLine($"cards={cards.Length}");

        var descriptors = packs
            .Select(pack => new SkinModDescriptor(pack.Id, pack.Name, null, false, pack.Root, false))
            .ToArray();
        using var catalog = SkinCatalog.Build(gamePck, descriptors);
        var (covered, usedPacks) = catalog.AttachCardArtPacks(packs, cards);
        catalog.FinalizeCardGroups(cards);
        Console.WriteLine(
            $"covered={covered.Count} packs={string.Join('、', usedPacks.Select(pack => pack.Name))}");
        foreach (var item in catalog.ExportWorkshopCatalog())
        {
            Console.WriteLine(
                $"  catalog {item.Id}: " +
                string.Join("|", item.Targets.Select(target => target.Kind + ":" + target.Target)));
        }

        var loader = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ManagedSkinModLoader", true)!;
        loader.GetMethod("RememberArtPackProviders")!.Invoke(null, [usedPacks.Select(pack => pack.Id).ToArray()]);
        var display = loader.GetMethod("IsManagedProviderForDisplay", BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic)!;
        foreach (var pack in usedPacks)
        {
            var marked = (bool)display.Invoke(null, [new Mod
            {
                path = pack.Root,
                manifest = new ModManifest { id = pack.Id, name = pack.Name }
            }])!;
            Console.WriteLine($"  mod-list marking {pack.Id}: {marked}");
        }

        foreach (var root in modRoots.Where(Directory.Exists))
        {
            var names = WorkshopSubmissionScanner.SubmittableNames(root);
            Console.WriteLine($"  submittable names {root}: {string.Join(" / ", names)}");
            var id = SkinCatalog.WorkshopSourceId(root);
            if (id == 0)
            {
                continue;
            }

            var result = WorkshopSubmissionScanner.Scan(
                new(id, Path.GetFileName(root), root),
                gamePck,
                cards,
                "0.111.0",
                new Dictionary<string, string>());
            Console.WriteLine(result.Item == null
                ? $"  submission scan {id}: rejected ({result.Error})"
                : $"  submission scan {id}: targets=" +
                  string.Join("|", result.Item.Targets.Select(target => target.Kind + ":" + target.Target)) +
                  $" restart={result.Item.RestartRequired}");
        }
    }

    /// <summary>
    /// Read-only check of a provider that keeps its card art as ordinary image files: which cards
    /// its folder covers, and whether the resulting option reaches the same card rows and Workshop
    /// export as a bundle. Only file names and image bytes are read; the provider's own DLL is
    /// neither loaded nor executed, and no game process is started. This cannot replace an in-game
    /// check of what the player sees.
    /// </summary>
    internal static void RunLoose(string gamePck, string providerRoot, string providerId)
    {
        var cards = CardEntries(gamePck);
        Console.WriteLine($"cards={cards.Length}");
        var packs = LooseCardPortraitPackScanner.Scan(
            [new LooseCardPortraitProvider(providerId, providerId, providerRoot)],
            cards);
        Console.WriteLine($"packs={packs.Count}");
        foreach (var pack in packs)
        {
            Console.WriteLine(
                $"  pack {pack.Id} ({pack.Name}) cards={pack.Cards.Count} " +
                $"folder={pack.Path} root={pack.Root}");
        }

        if (packs.Count == 0)
        {
            return;
        }

        using var catalog = SkinCatalog.Build(gamePck, []);
        var (covered, usedPacks) = catalog.AttachCardArtPacks(packs, cards);
        catalog.FinalizeCardGroups(cards);
        Console.WriteLine(
            $"covered={covered.Count} packs={string.Join('、', usedPacks.Select(pack => pack.Name))}");
        foreach (var group in catalog.CardGroups)
        {
            foreach (var option in group.Options.Where(option =>
                         usedPacks.Any(pack => pack.Id.Equals(option.Id, StringComparison.OrdinalIgnoreCase))))
            {
                Console.WriteLine(
                    $"  option {group.Id}/{option.Id}: {option.NormalPortraits.Count} portraits, " +
                    $"{option.CardPresentations.Count} presentations, " +
                    $"{option.Assets.Count} assets");
            }
        }
    }

    /// <summary>
    /// Sweeps every provider folder below a Workshop content root and lists the ones whose images
    /// read as a card pack. This is how the recognition rule is checked against everything that is
    /// actually installed: a provider that only holds icons, scenes or its own project files must
    /// not become a card pack. Read-only, no DLL is loaded and no game process is started.
    /// </summary>
    internal static void RunLooseSweep(string gamePck, string workshopRoot)
    {
        var cards = CardEntries(gamePck);
        Console.WriteLine($"cards={cards.Length}");
        var providers = Directory.EnumerateDirectories(workshopRoot)
            .Where(folder => !Path.GetFileName(folder).StartsWith('.'))
            .Select(folder => new LooseCardPortraitProvider(
                Path.GetFileName(folder),
                Path.GetFileName(folder),
                folder))
            .ToArray();
        Console.WriteLine($"providers={providers.Length}");
        var packs = LooseCardPortraitPackScanner.Scan(providers, cards);
        Console.WriteLine($"packs={packs.Count}");
        foreach (var pack in packs)
        {
            Console.WriteLine(
                $"  pack {pack.Id} cards={pack.Cards.Count} folder={pack.Path}");
        }
    }

    internal static CardCatalogEntry[] CardEntries(string gamePck)    {
        var cards = new List<(CardCatalogEntry Entry, bool IsPortrait)>();
        using var archive = PckArchive.Open(gamePck);
        foreach (var path in archive.Paths)
        {
            var source = path.EndsWith(".import") ? path[..^7] : path.EndsWith(".remap") ? path[..^6] : path;
            var marker = new[] { "/card_portraits/", "/card_atlas.sprites/" }
                .FirstOrDefault(source.Contains);
            if (marker == null)
            {
                continue;
            }

            var tail = source[(source.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
            if (!tail.Contains('/'))
            {
                continue;
            }

            var group = tail.Split('/')[0];
            cards.Add((new(Path.GetFileNameWithoutExtension(source), source, group, group, group),
                marker == "/card_portraits/"));
        }

        // The live catalog builds one entry per card model. A PCK holds both the portrait and the
        // atlas sprite of the same card, so keep the portrait and one entry per card name.
        return cards
            .GroupBy(card => card.Entry.TypeName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(card => card.IsPortrait).First().Entry)
            .ToArray();
    }
}
