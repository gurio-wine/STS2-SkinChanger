using System.Text;
using System.Text.RegularExpressions;

namespace STS2SkinChanger.Catalog;

/// <summary>
/// One installed pure asset card-art pack.
///
/// A pack is a Card Art Editor bundle: no code, no PCK and no manifest, just a JSON object
/// whose <c>overrides</c> array carries a framed PNG per card as base64. The editor already
/// cropped each image into the card rectangle's aspect ratio, so displaying it needs no
/// editor behaviour to be replayed.
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
    bool UsesRenderedImage)
{
    /// <summary>The pack this entry came from, filled in by the scanner.</summary>
    public string PackId { get; init; } = string.Empty;

    public bool IsFullArt => DisplayMode.Equals("full_art", StringComparison.OrdinalIgnoreCase);
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

    // The rendered override is the framed card image; the edit source is the uncropped master
    // kept for further editing. Both are matched as a key plus an opening quote, so the shorter
    // key cannot match inside the longer one.
    [GeneratedRegex("\"png_base64\"\\s*:\\s*\"(?<v>[A-Za-z0-9+/=]*)\"")]
    public static partial Regex RenderedRegex();

    [GeneratedRegex("\"edit_source_png_base64\"\\s*:\\s*\"(?<v>[A-Za-z0-9+/=]*)\"")]
    private static partial Regex EditSourceRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SafeIdRegex();

    public static bool IsBundleHeader(ReadOnlySpan<char> head) =>
        head.Contains("\"" + BundleFormat + "\"", StringComparison.Ordinal);

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
    /// The text may be a whole bundle, a single slice, or a slice plus the next entry's preamble;
    /// only entries that carry a source path and a decodable PNG survive, so no separate framing
    /// check is needed. Callers that must reject a non-bundle file check the header first.
    /// </summary>
    public static IReadOnlyList<CardArtPackCard> ParseCards(string bundleText)
    {
        var best = new Dictionary<string, CardArtPackCard>(StringComparer.OrdinalIgnoreCase);

        // A `source_path` always starts an entry, so everything between two of them is exactly
        // one override object.
        var matches = SourcePathRegex().Matches(bundleText);
        for (var index = 0; index < matches.Count; index++)
        {
            var start = matches[index].Index;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : bundleText.Length;
            Add(best, bundleText[start..end]);
        }

        return best.Values
            .OrderBy(card => card.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void Add(IDictionary<string, CardArtPackCard> target, string entry)
    {
        // The slice runs from this entry's source_path to the start of the next one, so it may
        // still carry the closing braces of the bundle. Those follow the last key and are never
        // matched, while the base64 payloads can themselves end in '=', so nothing is trimmed.
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

        // An empty value is not a payload: a bundle may declare png_base64 while shipping only
        // the edit source, and the uncropped master is still better than showing no art at all.
        var payload = NonEmpty(RenderedRegex(), entry) ?? NonEmpty(EditSourceRegex(), entry);
        if (payload == null)
        {
            return;
        }

        var bytes = Decode(payload);
        if (bytes == null || !bytes.AsSpan().StartsWith(PngSignature))
        {
            return;
        }

        // A bundle may repeat a card; keep the largest rendition available.
        if (target.TryGetValue(sourcePath, out var existing) && existing.Bytes.Length >= bytes.Length)
        {
            return;
        }

        target[sourcePath] = new CardArtPackCard(
            sourcePath,
            cardId,
            bytes,
            Value(DisplayModeRegex(), entry) ?? "original",
            Value(TypeRegex(), entry) ?? "static",
            UsesRenderedImage: NonEmpty(RenderedRegex(), entry) != null);
    }

    private static string? NonEmpty(Regex regex, string entry) =>
        regex.Match(entry) is { Success: true } match && match.Groups["v"].Value.Length > 0
            ? match.Groups["v"].Value
            : null;

    /// <summary>
    /// Extracts every complete override object from a window that may begin or end mid-entry.
    /// A trailing fragment is returned separately so the caller can prepend it to the next read.
    /// </summary>
    public static (IReadOnlyList<string> Entries, string Remainder) SplitEntries(string window)
    {
        var entries = new List<string>();
        var matches = SourcePathRegex().Matches(window);
        if (matches.Count == 0)
        {
            return (entries, window);
        }

        for (var index = 0; index + 1 < matches.Count; index++)
        {
            var start = matches[index].Index;
            entries.Add(window[start..matches[index + 1].Index]);
        }

        // The last entry is only complete once the chunk that follows it has been read.
        return (entries, window[matches[^1].Index..]);
    }

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
