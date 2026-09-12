using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace STS2SkinChanger.Catalog;

/// <summary>
/// One installed pure asset card-art pack.
///
/// A pack is a Card Art Editor bundle: no code, no PCK and no manifest, just a JSON object
/// whose <c>overrides</c> array carries the card art per card as base64, plus the editor's own
/// crop and adjustment metadata.
/// </summary>
internal sealed record CardArtPack(
    string Id,
    string Name,
    string Path,
    string Root,
    IReadOnlyList<CardArtPackCard> Cards);

/// <summary>Result of scanning the installed card-art packs.</summary>
/// <param name="Packs">Packs that yielded at least one usable card image.</param>
/// <param name="Warnings">Problems worth reporting; scanning never throws for a bad pack.</param>
internal sealed record CardArtPackScanResult(
    IReadOnlyList<CardArtPack> Packs,
    IReadOnlyList<string> Warnings);

internal sealed record CardArtPackCard(
    string SourcePath,
    string CardId,
    byte[] Bytes,
    string DisplayMode,
    string ArtType,
    bool UsesEditSource)
{
    /// <summary>The pack this entry came from, filled in by the scanner.</summary>
    public string PackId { get; init; } = string.Empty;

    public bool IsFullArt => DisplayMode.Equals("full_art", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the artwork was composed for the whole card rather than for the framed art window.
    ///
    /// The bundle states a display mode, but the editor composes the image for that mode: full art
    /// becomes a card-shaped canvas, everything else stays at the art window's wide aspect. The
    /// image's own shape is therefore what decides where it can be shown without being cropped,
    /// which keeps a bundle that only ships one rendition honest about the layout it gets.
    /// </summary>
    public bool IsCardShaped =>
        CardArtPackScanPolicy.TryReadPngSize(Bytes, out var width, out var height) && height > width;
}

/// <summary>
/// Parses the documented <c>card_art_bundle</c> format. Kept free of engine and logging
/// dependencies so the format rules stay unit testable offline.
/// </summary>
internal static partial class CardArtPackScanPolicy
{
    public const string BundleFormat = "card_art_bundle";
    public const string PackSuffix = ".cardartpack.json";

    /// <summary>Largest single override object accepted, guarding against a malformed bundle.</summary>
    public const int MaxEntryChars = 64 << 20;

    public const int MaxMetadataBytes = 64 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    [GeneratedRegex("\"source_path\"\\s*:\\s*\"(?<v>[^\"]+)\"")]
    public static partial Regex SourcePathRegex();

    [GeneratedRegex("\"display_mode\"\\s*:\\s*\"(?<v>[^\"]*)\"")]
    private static partial Regex DisplayModeRegex();

    [GeneratedRegex("\"type\"\\s*:\\s*\"(?<v>[^\"]*)\"")]
    private static partial Regex TypeRegex();

    // The pre-rendered card image and the uncropped master the editor keeps for further editing.
    // Both are matched as a key plus an opening quote, so the shorter key cannot match inside the
    // longer one.
    [GeneratedRegex("\"png_base64\"\\s*:\\s*\"(?<v>[A-Za-z0-9+/=]*)\"")]
    public static partial Regex RenderedRegex();

    [GeneratedRegex("\"edit_source_png_base64\"\\s*:\\s*\"(?<v>[A-Za-z0-9+/=]*)\"")]
    private static partial Regex EditSourceRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SafeIdRegex();

    public static bool IsBundleHeader(ReadOnlySpan<char> head) =>
        head.Contains("\"" + BundleFormat + "\"", StringComparison.Ordinal);

    /// <summary>Reads the pixel size a PNG declares in its IHDR chunk.</summary>
    public static bool TryReadPngSize(byte[] png, out int width, out int height)
    {
        width = 0;
        height = 0;
        // 8 signature bytes, then the IHDR chunk length and type, then width and height.
        if (png.Length < 24 || !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            return false;
        }

        width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        return width > 0 && height > 0;
    }

    /// <summary>
    /// The presentation a bundle entry asks for. Card Art Editor's full-art mode composes the
    /// artwork onto the whole card instead of into the framed art window, so a card-shaped image
    /// renders through the game's own tall portrait canvas.
    /// </summary>
    /// <returns><c>null</c> when the image keeps the game's framed layout.</returns>
    public static CardPresentationDefinition? BuildCardPresentation(CardArtPackCard card) =>
        card.IsCardShaped
            ? new CardPresentationDefinition(
                UseExpandedPortraitLayout: true,
                PortraitVisible: true,
                PortraitBorderVisible: false,
                BannerVisible: true,
                TextBackgroundVisible: true)
            : null;

    /// <summary>
    /// Normalises a pack identifier into something usable as a persisted selection key.
    /// </summary>
    public static string NormalizePackId(string? declaredId, string fileName)
    {
        var stem = fileName.EndsWith(PackSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^PackSuffix.Length]
            : System.IO.Path.GetFileNameWithoutExtension(fileName);
        var candidate = string.IsNullOrWhiteSpace(declaredId) ? stem : declaredId;
        return SafeIdRegex().IsMatch(candidate) ? candidate : "card_art_pack";
    }

    /// <summary>
    /// Reads every override object out of one bundle's raw text, keeping the largest rendition
    /// when a card is repeated.
    ///
    /// The text may be a whole bundle or any slice of one; only entries that carry a source path
    /// and a decodable PNG survive, so no separate framing check is needed. Callers that must
    /// reject a non-bundle file check the header first.
    /// </summary>
    public static IReadOnlyList<CardArtPackCard> ParseCards(string bundleText)
    {
        var best = new Dictionary<string, CardArtPackCard>(StringComparer.OrdinalIgnoreCase);
        var (entries, _) = SplitEntries(bundleText);
        foreach (var entry in entries)
        {
            Add(best, entry);
        }

        return best.Values
            .OrderBy(card => card.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void Add(IDictionary<string, CardArtPackCard> target, string entry)
    {
        if (SourcePathRegex().Match(entry) is not { Success: true } source)
        {
            return;
        }

        var sourcePath = source.Groups["v"].Value;
        var cardId = StemOf(sourcePath);
        if (string.IsNullOrWhiteSpace(sourcePath) || cardId.Length == 0)
        {
            return;
        }

        // A static override is displayed from the uncropped master: that is the rendition Card
        // Art Editor itself imports, and the pre-rendered `png_base64` is only the art window's
        // crop of it, which cannot fill a whole card. An entry that ships no master still gets
        // the crop, which the image's own shape then routes into the framed art window.
        var editSource = NonEmpty(EditSourceRegex(), entry);
        var rendered = NonEmpty(RenderedRegex(), entry);
        var payload = editSource ?? rendered;
        if (payload == null)
        {
            return;
        }

        var bytes = Decode(payload);
        if (bytes == null || !bytes.AsSpan().StartsWith(PngSignature))
        {
            return;
        }

        // A bundle may repeat a card across renditions, and the better one has to win.
        var candidate = new CardArtPackCard(
            sourcePath,
            cardId,
            bytes,
            Value(DisplayModeRegex(), entry) ?? "original",
            Value(TypeRegex(), entry) ?? "static",
            UsesEditSource: editSource != null);
        if (target.TryGetValue(sourcePath, out var existing) &&
            !ReplacesExisting(existing, candidate))
        {
            return;
        }

        target[sourcePath] = candidate;
    }

    /// <summary>
    /// Whether a repeated card entry replaces the one already read. The uncropped master beats a
    /// pre-rendered art window crop whatever their sizes are, and between two renditions of the
    /// same kind the larger image wins, so a low-resolution sibling cannot downgrade a card.
    /// </summary>
    public static bool ReplacesExisting(CardArtPackCard existing, CardArtPackCard candidate) =>
        candidate.UsesEditSource != existing.UsesEditSource
            ? candidate.UsesEditSource
            : candidate.Bytes.Length > existing.Bytes.Length;

    private static string? NonEmpty(Regex regex, string entry) =>
        regex.Match(entry) is { Success: true } match && match.Groups["v"].Value.Length > 0
            ? match.Groups["v"].Value
            : null;

    /// <summary>
    /// Extracts every complete override object from a window that may begin or end mid-entry, and
    /// returns the trailing text that is not closed yet so the caller can prepend it to the next
    /// read.
    ///
    /// An entry is delimited by its own braces and never by one of its fields. The editor writes
    /// the payloads *before* the path inside each object, so a slice taken between two
    /// <c>source_path</c> values would hand every card the artwork of the entry that follows it —
    /// which is exactly the order the bundle stores. Only object boundaries are positional here.
    /// </summary>
    public static (IReadOnlyList<string> Entries, string Remainder) SplitEntries(string window)
    {
        var entries = new List<string>();
        var openBraces = new List<int>(4);
        var inString = false;

        // Everything before this offset is complete and has been read; what follows may still be
        // an unfinished object and has to be scanned again with the next chunk.
        var boundary = 0;
        for (var index = 0; index < window.Length; index++)
        {
            var character = window[index];
            if (inString)
            {
                // Paths and base64 never carry escapes, but honouring them keeps the scan exact.
                if (character == '\\')
                {
                    index++;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    openBraces.Add(index);
                    break;
                case '}':
                    if (openBraces.Count == 0)
                    {
                        break;
                    }

                    var start = openBraces[^1];
                    openBraces.RemoveAt(openBraces.Count - 1);
                    boundary = index + 1;
                    var candidate = window[start..boundary];
                    if (IsEntryObject(candidate))
                    {
                        entries.Add(candidate);
                    }

                    break;
            }
        }

        return (entries, window[boundary..]);
    }

    /// <summary>
    /// Whether one object is a card override rather than the bundle that carries them. Both hold a
    /// <c>source_path</c> once the array is complete, so the framing key is what separates them.
    /// </summary>
    private static bool IsEntryObject(ReadOnlySpan<char> candidate) =>
        candidate.Contains("\"source_path\"", StringComparison.Ordinal) &&
        !candidate.Contains("\"overrides\"", StringComparison.Ordinal);

    private static string? Value(Regex regex, string entry) =>
        regex.Match(entry) is { Success: true } match ? match.Groups["v"].Value : null;

    private static byte[]? Decode(string base64)
    {
        try
        {
            return base64.Length == 0 ? null : Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string StemOf(string sourcePath)
    {
        var slash = sourcePath.LastIndexOf('/');
        var fileName = slash >= 0 ? sourcePath[(slash + 1)..] : sourcePath;
        var extension = fileName.LastIndexOf('.');
        return extension > 0 ? fileName[..extension] : fileName;
    }
}
