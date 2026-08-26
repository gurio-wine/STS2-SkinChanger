using System.Text.Json;

namespace STS2SkinChanger.Catalog;

/// <summary>
/// Reads provider-owned sidecar settings without loading or executing provider code.
/// Some visual providers use a card image as the whole frame, or explicitly route an
/// image to the Ancient portrait node. Those intentions are not visible in the PCK path
/// alone, so preserve them when the provider's patches are disabled by takeover.
/// </summary>
internal static class ProviderCardBehaviorScanner
{
    public static ProviderCardBehaviorHints Scan(
        string? providerRoot,
        IReadOnlyCollection<ResourceAsset> assets)
    {
        var ancientPortraits = new Dictionary<string, AncientCardPortrait>(
            StringComparer.OrdinalIgnoreCase);
        var presentations = new Dictionary<string, CardPresentationDefinition>(
            StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(providerRoot) ||
            !Directory.Exists(providerRoot) ||
            assets.Count == 0)
        {
            return new ProviderCardBehaviorHints(ancientPortraits, presentations);
        }

        var cardAssets = assets
            .Where(asset => IsImage(asset.SourcePath) &&
                            HasDirectoryToken(asset.SourcePath, "cards"))
            .GroupBy(asset => NormalizeToken(FileStem(asset.SourcePath)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(asset => asset.SourcePath.Length).First(),
                StringComparer.OrdinalIgnoreCase);
        if (cardAssets.Count == 0)
        {
            return new ProviderCardBehaviorHints(ancientPortraits, presentations);
        }

        var borderAssets = assets
            .Where(asset => IsImage(asset.SourcePath) &&
                            HasDirectoryToken(asset.SourcePath, "border"))
            .ToArray();
        foreach (var jsonPath in Directory.EnumerateFiles(
                     providerRoot,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(jsonPath).TrimStart('\uFEFF'));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var geometry = ReadFrameOverlayGeometry(document.RootElement);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var behavior = Classify(property.Name);
                    if (behavior == ProviderCardBehavior.None)
                    {
                        continue;
                    }

                    foreach (var cardEntry in property.Value.EnumerateObject())
                    {
                        if (!IsEnabled(cardEntry.Value) ||
                            !cardAssets.TryGetValue(NormalizeToken(cardEntry.Name), out var cardAsset))
                        {
                            continue;
                        }

                        var cardType = cardEntry.Name;
                        switch (behavior)
                        {
                            case ProviderCardBehavior.AncientPortrait:
                                ancientPortraits[cardType] = new AncientCardPortrait(
                                    NormalPortrait: null,
                                    AncientPortrait: cardAsset.SourcePath);
                                break;
                            case ProviderCardBehavior.FullFrameArt:
                                var category = GetCategory(cardAsset.SourcePath, "cards");
                                var border = borderAssets.FirstOrDefault(asset =>
                                    GetCategory(asset.SourcePath, "border").Equals(
                                        category,
                                        StringComparison.OrdinalIgnoreCase));
                                presentations[cardType] = new CardPresentationDefinition(
                                    Frame: cardAsset.SourcePath,
                                    FrameOverlay: border?.SourcePath,
                                    UseFullFrameArt: true,
                                    FrameVisible: true,
                                    TextBackgroundVisible: true,
                                    PortraitVisible: false,
                                    PortraitBorderVisible: false,
                                    FrameOverlayOffsetTop: geometry.OffsetTop,
                                    FrameOverlayOffsetBottom: geometry.OffsetBottom,
                                    FrameOverlayOffsetLeft: geometry.OffsetLeft,
                                    FrameOverlayOffsetRight: geometry.OffsetRight,
                                    FrameOverlayScaleX: geometry.ScaleX,
                                    FrameOverlayScaleY: geometry.ScaleY);
                                break;
                        }
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"无法读取卡牌提供者设置 {jsonPath}: {exception.Message}");
            }
        }

        return new ProviderCardBehaviorHints(ancientPortraits, presentations);
    }

    private static ProviderCardBehavior Classify(string name)
    {
        var token = NormalizeToken(name);
        if (!token.Contains("card", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderCardBehavior.None;
        }

        if (token.Contains("ancient", StringComparison.OrdinalIgnoreCase) &&
            (token.Contains("portrait", StringComparison.OrdinalIgnoreCase) ||
             token.Contains("image", StringComparison.OrdinalIgnoreCase)))
        {
            return ProviderCardBehavior.AncientPortrait;
        }

        return token.Contains("border", StringComparison.OrdinalIgnoreCase) &&
               (token.Contains("enabled", StringComparison.OrdinalIgnoreCase) ||
                token.Contains("replace", StringComparison.OrdinalIgnoreCase) ||
                token.Contains("custom", StringComparison.OrdinalIgnoreCase))
            ? ProviderCardBehavior.FullFrameArt
            : ProviderCardBehavior.None;
    }

    private static bool IsEnabled(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => bool.TryParse(value.GetString(), out var enabled) && enabled,
        _ => false
    };

    private static FrameOverlayGeometry ReadFrameOverlayGeometry(JsonElement root) => new(
        ReadNumber(root, "customBorderOffsetTop"),
        ReadNumber(root, "customBorderOffsetBottom"),
        ReadNumber(root, "customBorderOffsetLeft"),
        ReadNumber(root, "customBorderOffsetRight"),
        ReadNumber(root, "customBorderScaleX"),
        ReadNumber(root, "customBorderScaleY"));

    private static float? ReadNumber(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetSingle(out var number))
            {
                return number;
            }

            if (property.Value.ValueKind == JsonValueKind.String &&
                float.TryParse(
                    property.Value.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out number))
            {
                return number;
            }
        }

        return null;
    }

    private static string GetCategory(string path, string marker)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 1 < parts.Length; index++)
        {
            if (parts[index].Equals(marker, StringComparison.OrdinalIgnoreCase))
            {
                return parts[index + 1];
            }
        }

        return string.Empty;
    }

    private static bool HasDirectoryToken(string path, string token) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(token, StringComparison.OrdinalIgnoreCase));

    private static string FileStem(string path)
    {
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        var extension = fileName.LastIndexOf('.');
        return extension < 0 ? fileName : fileName[..extension];
    }

    private static bool IsImage(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tres", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".res", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private enum ProviderCardBehavior
    {
        None,
        AncientPortrait,
        FullFrameArt
    }

    private sealed record FrameOverlayGeometry(
        float? OffsetTop,
        float? OffsetBottom,
        float? OffsetLeft,
        float? OffsetRight,
        float? ScaleX,
        float? ScaleY);
}

internal sealed record ProviderCardBehaviorHints(
    IReadOnlyDictionary<string, AncientCardPortrait> AncientPortraits,
    IReadOnlyDictionary<string, CardPresentationDefinition> Presentations);
