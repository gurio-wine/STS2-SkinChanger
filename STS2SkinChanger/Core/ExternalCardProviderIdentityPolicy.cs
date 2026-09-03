using System.Security.Cryptography;
using System.Text;

namespace STS2SkinChanger.Core;

internal readonly record struct ExternalCardVisualOwnershipState(
    bool Portrait,
    bool Frame,
    bool Text);

internal static class ExternalCardProviderIdentityPolicy
{
    public static ExternalCardVisualOwnershipState ResolveEditorOwnership(
        bool hasOverride,
        bool fullArt,
        bool ancientTextOutside)
    {
        if (!hasOverride)
        {
            return default;
        }

        return new ExternalCardVisualOwnershipState(
            Portrait: true,
            Frame: fullArt,
            Text: fullArt || ancientTextOutside);
    }

    public static string BuildSyntheticPath(string cardId, string stableKey)
    {
        var normalizedCardId = NormalizeCardId(cardId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stableKey));
        return $"user://skin_changer/card_provider/{normalizedCardId}_" +
               $"{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}.png";
    }

    private static string NormalizeCardId(string cardId)
    {
        var builder = new StringBuilder(cardId.Length);
        var previousUnderscore = false;
        foreach (var character in cardId.ToLowerInvariant())
        {
            var isAsciiLetterOrDigit = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (isAsciiLetterOrDigit)
            {
                builder.Append(character);
                previousUnderscore = false;
            }
            else if (!previousUnderscore && builder.Length > 0)
            {
                builder.Append('_');
                previousUnderscore = true;
            }
        }

        var result = builder.ToString().Trim('_');
        return result.Length == 0 ? "card" : result;
    }
}
