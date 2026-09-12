using System.Text;
using System.Text.Json;

namespace STS2SkinChanger.Catalog;

/// <summary>
/// Discovers and reads pure asset card-art packs (<c>*.cardartpack.json</c>) that Card Art
/// Editor exports and consumes.
///
/// The bundle format itself lives in <see cref="CardArtPackScanPolicy"/>; this type owns file
/// discovery and bounded reading, and reports problems as data so the caller decides how to
/// surface them. Nothing from a provider is loaded or executed, and entries are decoded one at
/// a time so peak cost stays at a single card image.
/// </summary>
internal static class CardArtPackScanner
{
    public static CardArtPackScanResult Scan(IEnumerable<string> scanRoots)
    {
        var packs = new List<CardArtPack>();
        var warnings = new List<string>();
        foreach (var root in NormalizeRoots(scanRoots))
        {
            foreach (var packPath in EnumeratePackFiles(root))
            {
                try
                {
                    var pack = ReadPack(packPath, warnings);
                    if (pack != null)
                    {
                        packs.Add(pack);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException or
                    InvalidDataException or FormatException or ArgumentException)
                {
                    warnings.Add($"无法读取卡图包 {packPath}：{exception.GetBaseException().Message}");
                }
            }
        }

        return new CardArtPackScanResult(PreferHighResolution(packs), warnings);
    }

    /// <summary>
    /// Collapses renditions of the same pack into one source. A pack is usually published as a
    /// full-size bundle plus a low-resolution sibling, and both cover the very same cards, so
    /// offering them separately would show one artwork twice under the same name.
    /// </summary>
    private static IReadOnlyList<CardArtPack> PreferHighResolution(IReadOnlyList<CardArtPack> packs)
    {
        var best = new Dictionary<string, CardArtPack>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var pack in packs)
        {
            var key = pack.Id;
            if (!best.TryGetValue(key, out var existing))
            {
                best.Add(key, pack);
                order.Add(key);
                continue;
            }

            if (TotalBytes(pack) > TotalBytes(existing))
            {
                best[key] = pack;
            }
        }

        return order.Select(key => best[key]).ToArray();

        static long TotalBytes(CardArtPack pack)
        {
            var total = 0L;
            foreach (var card in pack.Cards)
            {
                total += card.Bytes.Length;
            }

            return total;
        }
    }

    private static IReadOnlyList<string> NormalizeRoots(IEnumerable<string> scanRoots)
    {
        var result = new List<string>();
        foreach (var root in scanRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(root).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            // A caller may hand us both a provider root and its parent Workshop folder.
            // Keeping only the shallowest roots avoids reading the same pack twice.
            if (result.Any(existing => IsUnder(full, existing)))
            {
                continue;
            }

            result.RemoveAll(existing => IsUnder(existing, full));
            result.Add(full);
        }

        return result;

        static bool IsUnder(string candidate, string parent) =>
            candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumeratePackFiles(string root)
    {
        // Walk explicitly rather than with SearchOption.AllDirectories: file enumeration is lazy,
        // so one unreadable folder (a protected directory, a denied junction) would otherwise
        // abort the whole scan. Keeping our own stack lets a bad folder be skipped on its own.
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var child in TakeAll(children))
            {
                pending.Push(child);
            }

            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateFiles(directory, "*" + CardArtPackScanPolicy.PackSuffix);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var match in TakeAll(matches))
            {
                yield return match;
            }
        }
    }

    /// <summary>
    /// Materialises one directory listing, stopping at the first unreadable child so the rest of
    /// the scan can continue.
    /// </summary>
    private static IReadOnlyList<string> TakeAll(IEnumerable<string> sequence)
    {
        var items = new List<string>();
        try
        {
            using var enumerator = sequence.GetEnumerator();
            while (enumerator.MoveNext())
            {
                items.Add(enumerator.Current!);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Keep whatever was reachable before the failure.
        }

        return items;
    }

    private static CardArtPack? ReadPack(string packPath, ICollection<string> warnings)
    {
        if (!HasBundleHeader(packPath))
        {
            warnings.Add($"忽略卡图包 {packPath}：缺少 {CardArtPackScanPolicy.BundleFormat} 标识。");
            return null;
        }

        var root = Path.GetDirectoryName(packPath)!;
        var (declaredId, declaredName) = ReadMetadata(root);
        var fileName = Path.GetFileName(packPath);
        var packId = CardArtPackScanPolicy.NormalizePackId(declaredId, fileName);

        var cards = ReadCards(packPath, packId, warnings);
        if (cards.Count == 0)
        {
            warnings.Add($"卡图包 {packPath} 不含可用的卡面覆盖，已跳过。");
            return null;
        }

        // A full-art entry whose only image is the art window's crop cannot cover a whole card.
        // Reporting it keeps a pack that disagrees with itself visible instead of silently
        // rendering the artwork on the smaller canvas.
        var windowCrops = cards.Count(card => card.IsFullArt && !card.IsCardShaped);
        if (windowCrops > 0)
        {
            warnings.Add(
                $"卡图包 {packPath} 有 {windowCrops} 张声明全画幅的卡面实际是卡框裁切图，" +
                "已按卡框尺寸呈现。");
        }

        return new CardArtPack(packId, declaredName ?? packId, packPath, root, cards);
    }

    private static bool HasBundleHeader(string packPath)
    {
        using var stream = OpenRead(packPath);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var head = new char[4096];
        var read = reader.Read(head, 0, head.Length);
        return read > 0 && CardArtPackScanPolicy.IsBundleHeader(new ReadOnlySpan<char>(head, 0, read));
    }

    private static IReadOnlyList<CardArtPackCard> ReadCards(
        string packPath,
        string packId,
        ICollection<string> warnings)
    {
        var best = new Dictionary<string, CardArtPackCard>(StringComparer.OrdinalIgnoreCase);
        using var stream = OpenRead(packPath);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // The bundle is one line of JSON, so it only ever arrives in pieces. Every `source_path`
        // starts an entry, letting each override be decoded and released independently instead
        // of holding the whole bundle, or even the whole chunk, in memory.
        const int chunkChars = 512 * 1024;
        var buffer = new char[chunkChars];
        var carry = string.Empty;
        var warningsIssued = false;
        int read;

        while ((read = reader.Read(buffer, 0, chunkChars)) > 0)
        {
            var window = carry + new string(buffer, 0, read);
            if (window.Length > CardArtPackScanPolicy.MaxEntryChars)
            {
                if (!warningsIssued)
                {
                    warningsIssued = true;
                    warnings.Add(
                        $"卡图包 {packPath} 的单条覆盖超出 " +
                        $"{CardArtPackScanPolicy.MaxEntryChars / (1 << 20)}MB 解析上限，已停止读取。");
                }

                break;
            }

            // Everything before the final entry boundary is complete and can be decoded now.
            var (entries, remainder) = CardArtPackScanPolicy.SplitEntries(window);
            foreach (var entry in entries)
            {
                Merge(best, CardArtPackScanPolicy.ParseCards(entry), packId);
            }

            carry = remainder;
        }

        // The last entry is closed by the bundle's own trailing brackets.
        if (carry.Length > 0)
        {
            Merge(best, CardArtPackScanPolicy.ParseCards(carry), packId);
        }

        return best.Values
            .OrderBy(card => card.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        static void Merge(
            IDictionary<string, CardArtPackCard> target,
            IReadOnlyList<CardArtPackCard> parsed,
            string owner)
        {
            foreach (var card in parsed)
            {
                if (!target.TryGetValue(card.SourcePath, out var existing) ||
                    CardArtPackScanPolicy.ReplacesExisting(existing, card))
                {
                    target[card.SourcePath] = card with { PackId = owner };
                }
            }
        }
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);

    private static (string? Id, string? Name) ReadMetadata(string root)
    {
        // The companion descriptor is named "<id>.json.json"; accept any single small JSON
        // beside the pack so a renamed export still resolves its declared name.
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(path).EndsWith(
                    CardArtPackScanPolicy.PackSuffix,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (new FileInfo(path).Length is 0 or > CardArtPackScanPolicy.MaxMetadataBytes)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path).TrimStart('\uFEFF'));
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var declared = document.RootElement.TryGetProperty("name", out var name) &&
                               name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null;
                return (id.GetString(), declared);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A malformed companion file must not stop the pack from loading.
            }
        }

        return default;
    }
}
