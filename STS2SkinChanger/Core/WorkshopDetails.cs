using System.Globalization;
using System.Text.RegularExpressions;

namespace STS2SkinChanger.Core;

internal sealed record WorkshopDetails(string Title, string PreviewUrl)
{
    // Null means unavailable (including caches written by older releases), not zero.
    public ulong? Subscriptions { get; init; }
    public ulong? LifetimeSubscriptions { get; init; }
    public ulong? Favorites { get; init; }
    public ulong? Comments { get; init; }
    public float? Score { get; init; }
    public uint? VotesUp { get; init; }
    public uint? VotesDown { get; init; }
    public uint? Created { get; init; }
    public uint? Updated { get; init; }
}

internal enum WorkshopSort { Subscriptions, LifetimeSubscriptions, Rating, Published, Updated, Comments, Favorites }

internal static class WorkshopSortPolicy
{
    public static ulong[] OrderIds(IEnumerable<ulong> ids, IReadOnlyDictionary<ulong, WorkshopDetails> details, WorkshopSort sort) =>
        ids.OrderByDescending(id => Value(details.GetValueOrDefault(id), sort)).ThenBy(id => id).ToArray();

    private static decimal? Value(WorkshopDetails? detail, WorkshopSort sort) => sort switch
    {
        WorkshopSort.Subscriptions => detail?.Subscriptions,
        WorkshopSort.LifetimeSubscriptions => detail?.LifetimeSubscriptions,
        WorkshopSort.Rating => detail?.Score is { } score && float.IsFinite(score) ? (decimal)Math.Clamp(score, 0, 1) : null,
        WorkshopSort.Published => detail?.Created,
        WorkshopSort.Updated => detail?.Updated,
        WorkshopSort.Comments => detail?.Comments,
        WorkshopSort.Favorites => detail?.Favorites,
        _ => null
    };

    public static string Metric(WorkshopDetails? detail, WorkshopSort sort, string language)
    {
        if (Value(detail, sort) is not { } value) return "—";
        var key = (WorkshopDetailsTextKey)((int)WorkshopDetailsTextKey.SubscriptionCount + (int)sort);
        if (sort == WorkshopSort.Rating)
            return string.Format(CultureInfo.CurrentCulture, WorkshopDetailsText.ForLanguage(language, key), value * 100,
                detail?.VotesUp is { } up && detail.VotesDown is { } down ? ((ulong)up + down).ToString("N0", CultureInfo.CurrentCulture) : "—");
        var display = sort is WorkshopSort.Published or WorkshopSort.Updated
            ? DateTimeOffset.FromUnixTimeSeconds((long)value).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.ToString("N0", CultureInfo.CurrentCulture);
        return string.Format(CultureInfo.CurrentCulture, WorkshopDetailsText.ForLanguage(language, key), display);
    }
}

internal sealed record WorkshopIntroduction(string Description, string[] Images, bool Available = true);

internal static class WorkshopIntroductionPolicy
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(80);
    public static string Summary(string? text, int maxCharacters = 360)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        // Display plain text only. Workshop descriptions are untrusted BBCode,
        // never a source of executable markup or image-fetch instructions.
        text = text[..Math.Min(text.Length, 32000)].Replace("\r\n", "\n").Replace('\r', '\n');
        try
        {
            text = Regex.Replace(text, @"\[(img|previewyoutube|previewicon)(?:=[^\]]*)?\].*?\[/\1\]", "", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
            text = Regex.Replace(text, @"\[/?(?:h[1-6]|list|olist|\*)\]", "\n", RegexOptions.IgnoreCase, RegexTimeout);
            text = Regex.Replace(text, @"\[/?[a-z][^\]\n]*\]", "", RegexOptions.IgnoreCase, RegexTimeout);
            text = Regex.Replace(text, @"[\t ]+", " ", RegexOptions.None, RegexTimeout);
            text = Regex.Replace(text, @"\n[ \n]+", "\n\n", RegexOptions.None, RegexTimeout).Trim();
        }
        catch (RegexMatchTimeoutException) { return ""; }
        var elements = StringInfo.ParseCombiningCharacters(text);
        var limit = Math.Clamp(maxCharacters, 1, 1000);
        return elements.Length <= limit ? text : text[..elements[limit - 1]].TrimEnd() + "…";
    }

    public static string[] Images(string cover, IEnumerable<string> previews)
    {
        var images = previews.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal).Take(64).ToArray();
        return images.Length > 0 ? images : string.IsNullOrWhiteSpace(cover) ? [] : [cover];
    }
}
