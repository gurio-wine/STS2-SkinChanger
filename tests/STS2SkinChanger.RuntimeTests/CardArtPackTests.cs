using System.Buffers.Binary;
using System.Text;
using STS2SkinChanger.Catalog;

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
        CheckRealPacks();
        Console.WriteLine(
            "Card art pack contracts passed: bundle parsing, rendered-override preference, " +
            "malformed-entry rejection and real pack dimensions.");
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
            // cropped png_base64 must win over the uncropped edit source.
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

            // The bundle's display mode decides the card layout: full art paints the artwork
            // across the whole card, so it must not render inside the framed art window.
            var fullArtPresentation = CardArtPackScanPolicy.BuildCardPresentation(abrasive);
            Require(fullArtPresentation is
                    { UseExpandedPortraitLayout: true, PortraitVisible: true, UseFullFrameArt: false },
                "a full-art entry must render through the tall portrait canvas.");
            Require(CardArtPackScanPolicy.BuildCardPresentation(abrasive with { DisplayMode = "default" }) == null,
                "an entry that keeps the game's display mode must leave the framed layout alone.");

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
                $"cropped={pack.Cards.Count(card => card.UsesRenderedImage)}");
        }

        var silent = packs.FirstOrDefault(pack => pack.Cards.Any(card => card.CardId == "abrasive"));
        if (silent == null)
        {
            return;
        }

        var sample = silent.Cards.Single(card => card.CardId == "abrasive");
        Require(sample.UsesRenderedImage,
            "an installed pack must expose the cropped png_base64 rendition.");
        var (width, height) = ReadPngSize(sample.Bytes);
        Require(width == 1000 && height == 760,
            $"the cropped card image must be 1000x760 like the game's own card art, got {width}x{height}.");
        Require(sample.Bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature),
            "the card image must be a PNG.");

        var duplicated = silent.Cards
            .GroupBy(card => card.CardId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Require(duplicated.Length == 0,
            "a pack must not address the same card twice: " + string.Join(", ", duplicated));
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
