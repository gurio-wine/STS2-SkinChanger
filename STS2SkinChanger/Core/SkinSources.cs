namespace STS2SkinChanger.Core;

// The browser's source switch. Exactly two options, Steam first: there is no meaningful
// "all sources" entry here, and a picker that lists a third value ends up showing two
// entries with the same label, which is how a player selects the wrong source.
internal static class SkinSources
{
    public const string Steam = "steam";
    public const string GitHub = "github";
    public static readonly string[] Options = [Steam, GitHub];

    public static string Normalize(string? value) =>
        string.Equals(value, GitHub, StringComparison.OrdinalIgnoreCase) ? GitHub : Steam;

    public static int IndexOf(string? value) => Normalize(value) == GitHub ? 1 : 0;

    public static string OptionAt(int index) => index >= 0 && index < Options.Length ? Options[index] : Steam;

    public static string Name(string language, string? value) => GitHubSkinText.ForLanguage(language,
        Normalize(value) == GitHub ? GitHubSkinTextKey.GitHub : GitHubSkinTextKey.Steam);
}
