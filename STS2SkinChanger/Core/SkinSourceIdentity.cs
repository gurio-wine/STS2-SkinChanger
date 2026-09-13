using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace STS2SkinChanger.Core;

// The Workshop browser keeps one identity space: every row is keyed by a ulong.
// A GitHub repository therefore gets a deterministic synthetic id derived from
// "owner/repo" instead of a second key space and an allocation table. Real Steam
// Workshop ids are far below 2^63, so the top bit alone separates the sources.
internal static class SkinSourceIdentity
{
    public const ulong GitHubMask = 1UL << 63;

    public static ulong GitHubId(string owner, string repo)
    {
        var name = owner.Trim().ToLowerInvariant() + "/" + repo.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return (BinaryPrimitives.ReadUInt64BigEndian(hash) & (GitHubMask - 1)) | GitHubMask;
    }

    public static bool IsGitHub(ulong id) => (id & GitHubMask) != 0;

    public static bool IsSteam(ulong id) => id != 0 && (id & GitHubMask) == 0;
}
