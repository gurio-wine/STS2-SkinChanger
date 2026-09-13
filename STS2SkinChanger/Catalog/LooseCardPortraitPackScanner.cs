namespace STS2SkinChanger.Catalog;

/// <summary>
/// A provider folder that may keep its card art as plain image files. The scanner never loads or
/// executes the provider; it only reads the pictures the author already installed.
/// </summary>
internal sealed record LooseCardPortraitProvider(string Id, string Name, string RootPath);

/// <summary>
/// Recognises a provider that ships its card art as ordinary image files instead of a Card Art
/// Editor bundle: a folder of pictures whose file names are the cards they replace.
///
/// Such a provider replaces card art from its own code, which Skin Changer holds back while it is
/// not selected, so its artwork would otherwise never appear. Reading the folder directly makes
/// the same pictures selectable like any other card skin, and the bytes travel inline exactly like
/// a bundle's, so preview, mounting and switching take the existing path.
///
/// Only names decide which card a picture covers, and a folder is a pack only when it covers cards
/// in bulk: a provider's incidental artwork (icons, backgrounds, one or two portraits) is not a
/// card pack, and an ambiguous name resolves to nothing rather than to an arbitrary card.
/// </summary>
internal static class LooseCardPortraitPackScanner
{
    /// <summary>Largest single picture accepted, guarding against a malformed or truncated file.</summary>
    public const int MaxImageBytes = 32 << 20;

    private const int MaxScannedFiles = 20000;
    private const int MaxCardEntries = 4096;

    public static IReadOnlyList<CardArtPack> Scan(
        IEnumerable<LooseCardPortraitProvider> providers,
        IReadOnlyList<CardCatalogEntry> cards)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(cards);
        var packs = new List<CardArtPack>();
        if (cards.Count == 0)
        {
            return packs;
        }

        foreach (var provider in providers)
        {
            var pack = ScanProvider(provider, cards);
            if (pack != null)
            {
                packs.Add(pack);
            }
        }

        return packs;
    }

    /// <summary>
    /// Whether a folder's images are a card pack rather than a provider's incidental pictures.
    /// </summary>
    public static bool IsPack(int imageCount, int matchedCards) =>
        LooseCardPortraitPackPolicy.IsPack(imageCount, matchedCards);

    private static CardArtPack? ScanProvider(
        LooseCardPortraitProvider provider,
        IReadOnlyList<CardCatalogEntry> cards)
    {
        if (string.IsNullOrWhiteSpace(provider.Id) ||
            string.IsNullOrWhiteSpace(provider.RootPath) ||
            !Directory.Exists(provider.RootPath))
        {
            return null;
        }

        string root;
        try
        {
            root = Path.GetFullPath(provider.RootPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // A folder that mounts a package already goes through the PCK route, and a bundle is
        // already read by its own scanner: reading the same art twice would offer it twice.
        if (HasFileWithExtension(root, ".pck", SearchOption.TopDirectoryOnly) ||
            HasFileWithExtension(root, CardArtPackScanPolicy.PackSuffix, SearchOption.AllDirectories))
        {
            return null;
        }

        // The Mod ID stays the pack ID so the provider's own row reads as taken over, while the
        // resource path only needs a token that is safe inside a res:// path.
        var pathToken = LooseCardPortraitPackPolicy.SanitizePathToken(provider.Id);
        string? bestFolder = null;
        var bestCards = new List<CardArtPackCard>();
        foreach (var (folder, images) in EnumerateImageFolders(root))
        {
            if (images.Count < LooseCardPortraitPackPolicy.MinimumCards)
            {
                continue;
            }

            var entries = new List<CardArtPackCard>();
            foreach (var image in images)
            {
                if (SkinCatalog.ResolveCardForPortraitStem(
                        Path.GetFileNameWithoutExtension(image),
                        cards) == null)
                {
                    continue;
                }

                if (TryReadCard(image, pathToken, root, out var entry))
                {
                    entries.Add(entry);
                }
            }

            if (!IsPack(images.Count, entries.Count) || entries.Count <= bestCards.Count)
            {
                continue;
            }

            bestFolder = folder;
            bestCards = entries;
        }

        if (bestFolder == null || bestCards.Count == 0)
        {
            return null;
        }

        return new CardArtPack(
            provider.Id,
            string.IsNullOrWhiteSpace(provider.Name) ? provider.Id : provider.Name,
            bestFolder,
            root,
            bestCards
                .OrderBy(card => card.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Take(MaxCardEntries)
                .ToArray());
    }

    private static bool TryReadCard(
        string imagePath,
        string pathToken,
        string root,
        out CardArtPackCard entry)
    {
        entry = null!;
        try
        {
            var info = new FileInfo(imagePath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxImageBytes)
            {
                return false;
            }

            var bytes = File.ReadAllBytes(imagePath);
            var relative = Path.GetRelativePath(root, imagePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            // Card Art Editor's full-art mode composes artwork onto the whole card instead of into
            // the framed art window, so the picture's own shape selects the canvas, exactly as it
            // does for a bundle entry.
            var displayMode =
                CardArtPackScanPolicy.TryReadPngSize(bytes, out var width, out var height) &&
                height > width
                    ? "full_art"
                    : "art_window";
            entry = new CardArtPackCard(
                $"res://{pathToken}/{relative}",
                Path.GetFileNameWithoutExtension(imagePath),
                bytes,
                displayMode,
                "static",
                UsesEditSource: false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
            PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The image files of every folder below the provider root, keyed by folder. Walking is
    /// explicit so one unreadable folder cannot abort the scan of the rest.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> EnumerateImageFolders(
        string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var visited = 0;
        while (pending.Count > 0 && visited < MaxScannedFiles)
        {
            var directory = pending.Pop();
            string[] files;
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (!Path.GetFileName(child).StartsWith('.'))
                    {
                        pending.Push(child);
                    }
                }

                files = Directory.GetFiles(directory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            visited += files.Length;
            var images = files
                .Where(IsCardImageFile)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (images.Length > 0)
            {
                yield return new KeyValuePair<string, IReadOnlyList<string>>(directory, images);
            }
        }
    }

    private static bool IsCardImageFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') ||
            name.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".remap", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFileWithExtension(string root, string extension, SearchOption searchOption)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*" + extension, searchOption)
                .Any(file => !Path.GetFileName(file).StartsWith('.'));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
