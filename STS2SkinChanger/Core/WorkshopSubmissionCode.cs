using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STS2SkinChanger.Core;

// Public DATA format, not an authenticity signature or permission to execute a Mod.
internal sealed record WorkshopSubmission(int Format, uint App, string Scanner, string Game, WorkshopCatalogItem[] Items);
internal static class WorkshopSubmissionCode
{
    public const string Prefix = "SCM1.";
    public const int MaxCodeLength = 1800;
    private const int MaxDecodedBytes = 128 * 1024;
    private static readonly HashSet<string> Kinds = ["character", "cards", "monster", "ancient", "merchant", "companion", "event"];
    private static readonly Regex Codes = new(@"(?<![A-Za-z0-9_])SCM1\.[A-Za-z0-9_-]+\.[A-Fa-f0-9]{8}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string[] Encode(IEnumerable<WorkshopCatalogItem> items, string scanner, string game)
    {
        var normalized = Normalize(items);
        var lines = new List<string>();
        var batch = new List<WorkshopCatalogItem>();
        bool Fits(WorkshopCatalogItem[] part, out string code)
        {
            code = Pack(new(1, WorkshopCatalogPolicy.AppId, scanner, game, part)) ?? "";
            return part.Length <= 64 && code.Length is > 0 and <= MaxCodeLength;
        }
        IEnumerable<WorkshopCatalogItem> Split(WorkshopCatalogItem item)
        {
            if (Fits([item], out _)) { yield return item; yield break; }
            if (item.Targets.Length < 2) throw new InvalidDataException("One classification target exceeds the code limit.");
            var half = item.Targets.Length / 2;
            foreach (var fragment in Split(item with { Targets = item.Targets[..half] })) yield return fragment;
            foreach (var fragment in Split(item with { Targets = item.Targets[half..] })) yield return fragment;
        }
        foreach (var item in normalized.SelectMany(Split))
        {
            batch.Add(item);
            if (Fits(batch.ToArray(), out _)) continue;
            batch.RemoveAt(batch.Count - 1);
            Fits(batch.ToArray(), out var code);
            lines.Add(code);
            batch = [item];
        }
        if (batch.Count > 0) { Fits(batch.ToArray(), out var code); lines.Add(code); }
        return lines.ToArray();
    }

    private static string? Pack(WorkshopSubmission submission)
    {
        Validate(submission);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(submission);
        if (bytes.Length > MaxDecodedBytes) return null;
        using var output = new MemoryStream();
        using (var zip = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true)) zip.Write(bytes);
        var packed = output.ToArray();
        return Prefix + Convert.ToBase64String(packed).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "." +
            Convert.ToHexString(SHA256.HashData(packed).AsSpan(0, 4));
    }

    public static WorkshopSubmission Decode(string code)
    {
        if (code.Length > MaxCodeLength || !code.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unknown or oversized submission code.");
        var parts = code.Split('.');
        if (parts.Length != 3 || parts[2].Length != 8) throw new InvalidDataException("Invalid submission code.");
        var body = parts[1].Replace('-', '+').Replace('_', '/');
        var packed = Convert.FromBase64String(body.PadRight((body.Length + 3) / 4 * 4, '='));
        if (!Convert.ToHexString(SHA256.HashData(packed).AsSpan(0, 4)).Equals(parts[2], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Submission checksum mismatch.");
        using var input = new MemoryStream(packed);
        using var zip = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = zip.Read(buffer)) > 0)
        {
            if (output.Length + count > MaxDecodedBytes) throw new InvalidDataException("Submission expansion limit.");
            output.Write(buffer, 0, count);
        }
        var submission = JsonSerializer.Deserialize<WorkshopSubmission>(output.ToArray(), new JsonSerializerOptions { MaxDepth = 8 })
            ?? throw new InvalidDataException("Empty submission.");
        Validate(submission);
        return submission with { Items = Normalize(submission.Items) };
    }

    public static WorkshopCatalogItem[] ReadPosts(IEnumerable<string> posts)
    {
        var items = new List<WorkshopCatalogItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targets = 0;
        foreach (var post in posts)
        {
            if (post.Length > 2 * 1024 * 1024) throw new InvalidDataException("Discussion post too large.");
            foreach (Match match in Codes.Matches(post))
            {
                if (match.Length > MaxCodeLength || !seen.Add(match.Value)) continue;
                if (seen.Count > 10000) throw new InvalidDataException("Too many submission codes.");
                try
                {
                    var decoded = Decode(match.Value).Items;
                    targets += decoded.Sum(i => i.Targets.Length);
                    items.AddRange(decoded);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or FormatException or ArgumentException) { }
                if (items.Count > 20000 || targets > 100000) throw new InvalidDataException("Too many submitted Mods or targets.");
            }
        }
        return Normalize(items);
    }

    public static WorkshopCatalogItem[] Merge(IEnumerable<WorkshopCatalogItem> builtIn, IEnumerable<WorkshopCatalogItem> community)
    {
        var original = builtIn.ToArray();
        var known = original.Select(i => i.Id).ToHashSet();
        return original.Concat(Normalize(community).Where(i => !known.Contains(i.Id))).ToArray();
    }

    private static WorkshopCatalogItem[] Normalize(IEnumerable<WorkshopCatalogItem> items) => items.GroupBy(i => i.Id).OrderBy(g => g.Key)
        .Select(g => new WorkshopCatalogItem(g.Key, g.SelectMany(i => i.Targets).Select(t => new WorkshopTarget(t.Kind, t.Target.ToLowerInvariant())).Distinct()
            .OrderBy(t => t.Kind, StringComparer.Ordinal).ThenBy(t => t.Target, StringComparer.Ordinal).ToArray(), g.Any(i => i.RestartRequired))).ToArray();

    private static void Validate(WorkshopSubmission s)
    {
        if (s.Format != 1 || s.App != WorkshopCatalogPolicy.AppId || s.Scanner is not { Length: > 0 and <= 32 } ||
            s.Game is not { Length: <= 64 } || s.Items is not { Length: > 0 and <= 65 }) throw new InvalidDataException("Invalid submission schema.");
        foreach (var item in s.Items)
        {
            if (item == null || item.Id == 0 || item.Id == 3787302680 || item.Targets is not { Length: > 0 and <= 512 })
                throw new InvalidDataException("Invalid submitted Mod.");
            foreach (var t in item.Targets)
                if (t == null || !Kinds.Contains(t.Kind) || t.Target is not { Length: > 0 and <= 160 } ||
                    t.Target.Any(c => char.IsControl(c) || "<>\"'\\/".Contains(c))) throw new InvalidDataException("Invalid classification target.");
        }
    }
}
