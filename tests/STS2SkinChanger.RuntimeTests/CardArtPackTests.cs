using System.Buffers.Binary;
using System.Text;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;
using STS2SkinChanger.Pck;

/// <summary>
/// Verifies that a pure asset card-art pack is read as data, including against a real installed
/// bundle when one is present. A pack carries no code, so the whole risk is in the format rules.
/// </summary>
internal static class CardArtPackTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public static void Run()
    {
        CheckFormatRules();
        CheckWorkshopRecognition();
        CheckRealPacks();
        Console.WriteLine(
            "Card art pack contracts passed: bundle parsing, master-over-crop preference, " +
            "malformed-entry rejection, canvas routing, Workshop recognition and real pack dimensions.");
    }

    /// <summary>
    /// A pure asset pack ships no cosmetic manifest, so it is recognised through its bundle alone.
    /// The Workshop item it was installed from must still be offered for submission, exported as
    /// a taken-over card provider, and marked in the Mod list like any other provider.
    /// </summary>
    private static void CheckWorkshopRecognition()
    {
        var root = Directory.CreateTempSubdirectory("sts2-card-art-recognition-");
        try
        {
            var item = Path.Combine(root.FullName, "2868840", "424242");
            Directory.CreateDirectory(item);
            File.WriteAllText(
                Path.Combine(item, "Sample_Pack.cardartpack.json"),
                "{\"format\":\"card_art_bundle\",\"version\":1,\"count\":1,\"overrides\":[" +
                Entry("res://images/packed/card_portraits/silent/abrasive.png", "full_art",
                    MakePng(1000, 760, withAlpha: true), MakePng(1212, 1704, withAlpha: false)) + "]}",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(item, "Sample_Pack.json.json"),
                "{\"id\":\"Sample_Pack\",\"name\":\"Sample Pack\",\"version\":\"1.0.0\"}",
                new UTF8Encoding(false));

            var game = Path.Combine(root.FullName, "game.pck");
            PckArchive.Write(game, new Dictionary<string, byte[]>());
            var cards = new[]
            {
                new CardCatalogEntry("Abrasive", "res://images/packed/card_portraits/silent/abrasive.png",
                    "silent", "silent", "silent")
            };

            // The companion descriptor carries none of the cosmetic manifest fields, so the
            // folder must not be judged by that file alone.
            Require(WorkshopSubmissionScanner.ReadDescriptors(item).Length == 0,
                "a card-art pack folder must not be mistaken for a cosmetic manifest.");
            Require(WorkshopSubmissionScanner.SubmittableNames(item).SequenceEqual(new[] { "Sample Pack" }),
                "a pack-only folder must still be nameable for submission.");
            var described = CardArtPackScanner.Describe(item).Single();
            Require(described is { Id: "Sample_Pack", Name: "Sample Pack" },
                $"the pack descriptor must come from the bundle's companion file, got {described}.");

            // The export must not depend on the loader having registered a descriptor for the
            // pack: the folder the bundle sits in is what ties it to its Workshop item.
            using (var catalog = SkinCatalog.Build(game, []))
            {
                var (covered, used) = catalog.AttachCardArtPacks(
                    CardArtPackScanner.Scan([item]).Packs, cards);
                catalog.FinalizeCardGroups(cards);
                Require(covered.Count == 1 && used.Count == 1 && used[0].Id == "Sample_Pack",
                    "the pack must register the cards it covers.");
                var exported = catalog.ExportWorkshopCatalog().SingleOrDefault(entry => entry.Id == 424242);
                Require(exported != null && exported.Targets.Any(target => target.Kind == "cards"),
                    "an attached pack must export its Workshop item as a taken-over card provider.");
            }

            var scan = WorkshopSubmissionScanner.Scan(
                new(424242, "Sample Pack", item), game, cards, "0.111.0", new Dictionary<string, string>());
            Require(scan.Item is { RestartRequired: true } &&
                    scan.Item.Targets.Any(target => target.Kind == "cards"),
                $"a downloaded pack must be submittable and must not promise hot loading: {scan.Error}");
            Require(!string.Join("", WorkshopSubmissionCodec.Encode(scan.Item!, "1.0.7.1", "0.111.0"))
                    .Contains(":\\", StringComparison.Ordinal),
                "a submission must not encode the author's local paths.");

            ManagedSkinModLoader.RememberArtPackProviders(["Sample_Pack"]);
            Require(ManagedSkinModLoader.IsManagedProviderForDisplay(new Mod
            {
                path = item,
                manifest = new ModManifest { id = "Sample_Pack", name = "Sample Pack" }
            }), "a taken-over pack's Mod must be marked like any other provider.");
            Require(!ManagedSkinModLoader.IsManagedProviderForDisplay(new Mod
            {
                path = item,
                manifest = new ModManifest { id = "Unrelated_Pack", name = "Unrelated" }
            }), "an unrelated Mod must not inherit the pack's marking.");
        }
        finally
        {
            try { Directory.Delete(root.FullName, recursive: true); }
            catch (IOException) { }
        }
    }

    private static void CheckFormatRules()
    {
        var root = Path.Combine(Path.GetTempPath(), "sts2-card-art-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var rendered = MakePng(1000, 760, withAlpha: true);
            var master = MakePng(1212, 1704, withAlpha: false);
            var tiny = MakePng(600, 456, withAlpha: true);

            // Two override objects for the same card: the second must replace the first, and the
            // uncropped master must win over the pre-rendered art window crop, which cannot fill
            // a whole card.
            var first = Entry(
                "res://images/packed/card_portraits/silent/abrasive.png", "full_art", tiny, null);
            var second = Entry(
                "res://images/packed/card_portraits/silent/abrasive.png", "full_art", rendered, master);

            var bundle = string.Concat(
                "{\"format\":\"card_art_bundle\",\"version\":1,\"count\":2,\"overrides\":[",
                first,
                ",",
                second,
                "]}");
            Require(CardArtPackScanPolicy.SourcePathRegex().Matches(bundle).Count == 2,
                "the sample fixture must contain exactly two entries.");
            Require(bundle.Contains(Convert.ToBase64String(rendered), StringComparison.Ordinal) &&
                    bundle.Contains(Convert.ToBase64String(tiny), StringComparison.Ordinal),
                "the sample fixture must carry both entry payloads.");

            File.WriteAllText(
                Path.Combine(root, "Sample_Pack.cardartpack.json"),
                bundle,
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(root, "Sample_Pack.json.json"),
                "{\"id\":\"Sample_Pack\",\"name\":\"Sample Pack\",\"version\":\"1.0.0\"}",
                new UTF8Encoding(false));

            var packs = CardArtPackScanner.Scan([root]).Packs;
            Require(packs.Count == 1, $"one source expected for the sample pack, got {packs.Count}.");
            var pack = packs[0];
            Require(pack.Id == "Sample_Pack", $"declared id must be used, got {pack.Id}.");
            Require(pack.Name == "Sample Pack", $"declared name must be used, got {pack.Name}.");
            Require(pack.Cards.Count == 1,
                $"repeated entries for one card must collapse, got {pack.Cards.Count}: " +
                string.Join(", ", pack.Cards.Select(card => $"{card.CardId}/{card.Bytes.Length}")));

            var abrasive = pack.Cards.Single(card => card.CardId == "abrasive");
            Require(abrasive.IsFullArt, "display_mode=full_art must be recognised as full art.");
            Require(abrasive.SourcePath.StartsWith("res://", StringComparison.Ordinal) &&
                    abrasive.Bytes.Length > 0 &&
                    abrasive.Bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature),
                "the card must keep its resource path and a decoded PNG payload.");
            Require(abrasive.UsesEditSource && ReadPngSize(abrasive.Bytes) == (1212, 1704),
                $"the uncropped master must be displayed, got {ReadPngSize(abrasive.Bytes)}.");

            // A card-shaped image covers the whole card through the tall portrait canvas.
            Require(CardArtPackScanPolicy.BuildCardPresentation(abrasive) is
                    { UseExpandedPortraitLayout: true, PortraitVisible: true, UseFullFrameArt: false },
                "a card-shaped image must render through the tall portrait canvas.");

            // The art window's wide crop must never be stretched over a whole card: that is what
            // magnified the artwork and cropped its sides.
            var windowCrop = abrasive with
            {
                Bytes = rendered,
                UsesEditSource = false,
                DisplayMode = "full_art"
            };
            Require(!windowCrop.IsCardShaped,
                "a 1000x760 art window crop must not count as card shaped.");
            Require(CardArtPackScanPolicy.BuildCardPresentation(windowCrop) == null,
                "an art window crop must stay in the game's framed art window.");
            Require(ReadPngSize(windowCrop.Bytes) == (1000, 760),
                "the framed layout fixture must stay at the art window's own size.");

            // Entries without usable art must be dropped instead of registering a broken card.
            File.WriteAllText(
                Path.Combine(root, "Broken_Pack.cardartpack.json"),
                "{\"format\":\"card_art_bundle\",\"version\":1,\"overrides\":[" +
                Entry("res://images/packed/card_portraits/silent/accelerant.png", "full_art", rendered, null)
                    .Replace(Convert.ToBase64String(rendered), "not-valid-base64!!") + "," +
                "{\"display_mode\":\"full_art\",\"png_base64\":\"\"}]}",
                new UTF8Encoding(false));
            // A bundle whose entries all fail validation must be skipped entirely rather than
            // registering a card with no usable artwork.
            var broken = CardArtPackScanner.Scan([root]).Packs
                .SingleOrDefault(candidate => candidate.Path.EndsWith(
                    "Broken_Pack.cardartpack.json",
                    StringComparison.OrdinalIgnoreCase));
            Require(broken == null,
                $"a bundle without usable art must be skipped, kept {broken?.Cards.Count} cards.");

            // A file that is not a bundle must not be treated as one, so only the sample pack
            // remains a source at this point.
            File.WriteAllText(
                Path.Combine(root, "unrelated.json"),
                "{\"packs\":{}}",
                new UTF8Encoding(false));
            Require(CardArtPackScanner.Scan([root]).Packs.Count == 1,
                "a JSON file that is not a card_art_bundle must not be read as a pack.");

            // A second bundle beside the first stays its own source, and nested scan roots must
            // not read the same pack twice.
            File.WriteAllText(
                Path.Combine(root, "Sample_Pack_Lowres.cardartpack.json"),
                "{\"format\":\"card_art_bundle\",\"version\":1,\"overrides\":[" +
                Entry("res://images/packed/card_portraits/silent/abrasive.png", "full_art", tiny, null) +
                "]}",
                new UTF8Encoding(false));
            // Renditions of the same pack collapse into one source, and the fuller one wins.
            var widened = CardArtPackScanner.Scan([root, Directory.GetParent(root)!.FullName]).Packs;
            Require(widened.Count == 1,
                $"renditions of one pack must collapse into a single source, got {widened.Count}.");
            Require(widened[0].Path.EndsWith(
                    "Sample_Pack.cardartpack.json",
                    StringComparison.OrdinalIgnoreCase),
                $"the fuller rendition must win, kept {Path.GetFileName(widened[0].Path)}.");

            // Regression: each override stores its payloads *before* its own source_path, so
            // entries must be delimited by their braces. Reading between two paths instead would
            // give every card the artwork of the entry after it and drop the bundle's last card.
            var orderRoot = Path.Combine(root, "order");
            Directory.CreateDirectory(orderRoot);
            File.WriteAllText(
                Path.Combine(orderRoot, "Order_Pack.cardartpack.json"),
                "{\"format\":\"card_art_bundle\",\"version\":1,\"count\":2,\"overrides\":[" +
                Entry("res://images/packed/card_portraits/silent/abrasive.png", "full_art",
                    MakePng(1000, 760, withAlpha: true), MakePng(1212, 1704, withAlpha: false)) + "," +
                Entry("res://images/packed/card_portraits/silent/accelerant.png", "full_art",
                    MakePng(500, 380, withAlpha: true), MakePng(600, 844, withAlpha: false)) + "]}",
                new UTF8Encoding(false));

            var ordered = CardArtPackScanner.Scan([orderRoot]).Packs.Single().Cards;
            Require(ordered.Count == 2,
                $"both entries must be read, got {ordered.Count}: " +
                string.Join(", ", ordered.Select(card => card.CardId)));
            Require(ReadPngSize(ordered.Single(card => card.CardId == "abrasive").Bytes) == (1212, 1704),
                "a card must display the artwork stored in its own entry, not the next entry's.");
            Require(ReadPngSize(ordered.Single(card => card.CardId == "accelerant").Bytes) == (600, 844),
                "the last entry of a bundle must keep its own artwork.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static void CheckRealPacks()
    {
        const string workshop = @"D:\Programs\Steam\steamapps\workshop\content\2868840";
        if (!Directory.Exists(workshop))
        {
            Console.WriteLine("Skipping real pack check: no Workshop content folder on this machine.");
            return;
        }

        var packs = CardArtPackScanner.Scan([workshop]).Packs
            .Where(pack => pack.Cards.Count > 0)
            .ToArray();
        if (packs.Length == 0)
        {
            Console.WriteLine("Skipping real pack check: no installed card art pack found.");
            return;
        }

        var duplicateIds = packs
            .GroupBy(pack => pack.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Require(duplicateIds.Length == 0,
            "a pack id must resolve to exactly one source: " + string.Join(", ", duplicateIds));

        foreach (var pack in packs)
        {
            Console.WriteLine(
                $"  installed pack {pack.Id} ({pack.Name}): {pack.Cards.Count} cards, " +
                $"full art={pack.Cards.Count(card => card.IsFullArt)}, " +
                $"card shaped={pack.Cards.Count(card => card.IsCardShaped)}, " +
                $"from master={pack.Cards.Count(card => card.UsesEditSource)}");
        }

        var silent = packs.FirstOrDefault(pack => pack.Cards.Any(card => card.CardId == "abrasive"));
        if (silent == null)
        {
            return;
        }

        // The whole-card canvas and the artwork must agree on the card's own aspect, otherwise
        // covering one with the other has to crop and magnify the picture.
        const double cardAspect = 300.0 / 422.0;
        var sample = silent.Cards.Single(card => card.CardId == "abrasive");
        Require(sample.UsesEditSource,
            "an installed pack must display the uncropped master instead of the art window crop.");
        Require(sample.IsCardShaped,
            "the displayed card art must be shaped like a card, not like the art window.");
        var (width, height) = ReadPngSize(sample.Bytes);
        var aspect = (double)width / height;
        Require(Math.Abs(aspect - cardAspect) < 0.02,
            $"the displayed card art must match the card's {cardAspect:F3} aspect, " +
            $"got {width}x{height} ({aspect:F3}).");
        Require(sample.Bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature),
            "the card image must be a PNG.");

        var duplicated = silent.Cards
            .GroupBy(card => card.CardId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Require(duplicated.Length == 0,
            "a pack must not address the same card twice: " + string.Join(", ", duplicated));

        // One card per override: a bundle whose artwork shifted by one entry, or whose final entry
        // was dropped, cannot match the number of entries the bundle actually declares.
        var declared = CountDeclaredEntries(silent.Path);
        Require(silent.Cards.Count == declared,
            $"an installed pack must yield one card per entry: declared {declared}, " +
            $"read {silent.Cards.Count}.");
    }

    /// <summary>Counts a bundle's override entries without using the reader under test.</summary>
    private static int CountDeclaredEntries(string packPath)
    {
        const string marker = "\"source_path\"";
        var count = 0;
        using var reader = new StreamReader(packPath);
        var buffer = new char[1 << 20];
        var overlap = string.Empty;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var window = overlap + new string(buffer, 0, read);
            for (var index = window.IndexOf(marker, StringComparison.Ordinal);
                 index >= 0;
                 index = window.IndexOf(marker, index + marker.Length, StringComparison.Ordinal))
            {
                count++;
            }

            // A marker straddling two reads must still be counted exactly once.
            overlap = window[^Math.Min(marker.Length - 1, window.Length)..];
        }

        return count;
    }

    private static (int Width, int Height) ReadPngSize(byte[] bytes) =>
        (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)),
         BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));

    private static string Entry(string sourcePath, string displayMode, byte[]? payload, byte[]? editSource)
    {
        var builder = new StringBuilder();
        builder.Append("{\"adjust_offset_x\":0.0,\"adjust_offset_y\":0.0,\"adjust_zoom\":1.0,");
        builder.Append("\"display_mode\":\"").Append(displayMode).Append("\",");
        if (editSource != null)
        {
            builder.Append("\"edit_source_png_base64\":\"")
                .Append(Convert.ToBase64String(editSource))
                .Append("\",");
        }

        builder.Append("\"height\":760,");
        builder.Append("\"png_base64\":\"")
            .Append(payload == null ? string.Empty : Convert.ToBase64String(payload))
            .Append("\",");
        builder.Append("\"source_path\":\"").Append(sourcePath).Append("\",");
        builder.Append("\"type\":\"static\",\"updated_at\":\"2026-01-01T00:00:00\",\"width\":1000}");
        return builder.ToString();
    }

    /// <summary>Builds a real PNG so the reader exercises signature validation and IHDR parsing.</summary>
    private static byte[] MakePng(int width, int height, bool withAlpha)
    {
        using var stream = new MemoryStream();
        stream.Write(PngSignature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = withAlpha ? (byte)6 : (byte)2;
        WriteChunk(stream, "IHDR", header);

        // The reader only validates the signature and reads IHDR, so a stored deflate block keeps
        // this fixture small while still being a structurally valid PNG.
        var raw = new byte[(withAlpha ? 4 : 3) * width + 1];
        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(
                   compressed,
                   System.IO.Compression.CompressionLevel.NoCompression,
                   leaveOpen: true))
        {
            deflate.Write(raw);
        }

        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(Encoding.ASCII.GetBytes(type));
        stream.Write(data);
        // CRCs are never verified by the reader, so a placeholder keeps the fixture simple.
        stream.Write([0, 0, 0, 0]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
