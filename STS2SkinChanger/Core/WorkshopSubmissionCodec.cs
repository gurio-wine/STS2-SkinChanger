using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace STS2SkinChanger.Core;

internal static class WorkshopSubmissionCodec
{
    internal const int MaxCodeLength = 1800;
    private const int MaxExpandedBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All), MaxDepth = 8 };
    private static readonly Regex Header = new("^SCM2\\s+(?<name>\"(?:\\\\.|[^\"\\\\])*\")\\s+(?<id>[0-9]+)\\s+(?<group>[A-Fa-f0-9]{24})\\s+(?<part>[0-9]+)/(?<total>[0-9]+)\\s+(?<body>[A-Za-z0-9_-]+)\\.(?<sum>[A-Fa-f0-9]{8})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Header3 = new(@"^SCM3\s+(?<id>[0-9]+)\s+(?<group>[A-Fa-f0-9]{24})\s+(?<part>[0-9]+)/(?<total>[0-9]+)\s+(?<body>[A-Za-z0-9_-]+)\.(?<sum>[A-Fa-f0-9]{8})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Starts = new(@"(?<!\w)SCM\d+\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Recover = new("^SCM[0-9]+\\s+(?:(?<name>\"(?:\\\\.|[^\"\\\\])*\")\\s+)?(?<id>[0-9]+)?(?:\\s+(?<group>[A-Fa-f0-9]{24})(?:\\s+(?<part>[0-9]+)/(?<total>[0-9]+))?)?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private sealed record Part(ulong Id, int Format, string Group, int Index, int Total, string Body, WorkshopCodeSource Source);
    private sealed class CodeFailure(WorkshopCodeError error) : Exception { internal WorkshopCodeError Error => error; }
    internal static string Fingerprint(string text) => Hash(Encoding.UTF8.GetBytes(text), 12);
    private static string Hash(byte[] bytes, int chars) => Convert.ToHexString(SHA256.HashData(bytes))[..chars];

    internal static string[] Encode(WorkshopCatalogItem item, string scanner, string game)
    {
        var payload = new WorkshopCodePayload(3, WorkshopCatalogPolicy.AppId, scanner, game, item);
        Validate(payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        if (bytes.Length > MaxExpandedBytes) throw new InvalidDataException("Submission too large.");
        using var stream = new MemoryStream();
        using (var zip = new BrotliStream(stream, CompressionLevel.Optimal, true)) zip.Write(bytes);
        var packed = stream.ToArray();
        var body = Convert.ToBase64String(packed).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = $"SCM3 {item.Id} {Hash(packed, 24)} ";
        var size = MaxCodeLength - header.Length - 32;
        var total = (body.Length + size - 1) / size;
        if (total > 128) throw new InvalidDataException("Too many fragments.");
        return Enumerable.Range(0, total).Select(index =>
        {
            var raw = header + $"{index + 1}/{total} " + body.Substring(index * size, Math.Min(size, body.Length - index * size));
            return raw + "." + Hash(Encoding.UTF8.GetBytes(raw), 8);
        }).ToArray();
    }

    internal static WorkshopCodeRead Read(IEnumerable<WorkshopCodePost> posts)
    {
        var parts = new List<Part>(); var issues = new List<WorkshopCodeIssue>(); var order = 0; var codes = 0;
        foreach (var post in posts)
        {
            foreach (var line in post.Text.Replace('\r', '\n').Split('\n'))
            {
                var starts = CodeStarts(line).ToArray();
                for (var index = 0; index < starts.Length; index++)
                {
                    if (++codes > 10000) throw new InvalidDataException("Too many submission codes.");
                    var raw = line[starts[index].Index..(index + 1 < starts.Length ? starts[index + 1].Index : line.Length)].Trim();
                    var source = new WorkshopCodeSource(raw[..Math.Min(raw.Length, 4096)], post.Url, post.Page, post.Reply, order++);
                    if (raw.StartsWith("SCM1.", StringComparison.Ordinal))
                    {
                        var legacy = WorkshopSubmissionCode.ReadPosts([raw]);
                        if (legacy.Length == 0) issues.Add(Issue(source, WorkshopCodeError.Legacy));
                        else foreach (var item in legacy) issues.Add(Issue(source, WorkshopCodeError.Legacy, item.Id));
                        continue;
                    }
                    try { parts.Add(Parse(raw, source)); }
                    catch (CodeFailure ex) { issues.Add(RecoverIssue(source, ex.Error)); }
                    catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or OverflowException)
                    { issues.Add(RecoverIssue(source, WorkshopCodeError.Format)); }
                }
            }
        }
        var candidates = new List<WorkshopCodeCandidate>(); var expanded = 0;
        foreach (var group in parts.GroupBy(p => p.Group))
        {
            var first = group.First(); var sources = group.Select(p => p.Source).Distinct().ToArray();
            var distinct = group.DistinctBy(p => (p.Index, p.Body)).ToArray();
            var missing = Enumerable.Range(1, first.Total).Except(distinct.Select(p => p.Index)).ToArray();
            WorkshopCodeError? error = group.Any(p => p.Id != first.Id || p.Format != first.Format || p.Total != first.Total) ||
                distinct.GroupBy(p => p.Index).Any(g => g.Count() > 1) ? WorkshopCodeError.Conflict :
                missing.Length > 0 ? WorkshopCodeError.MissingParts : null;
            if (error == null)
            {
                try
                {
                    var body = string.Concat(distinct.OrderBy(p => p.Index).Select(p => p.Body));
                    var base64 = body.Replace('-', '+').Replace('_', '/');
                    var packed = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
                    if (Hash(packed, 24) != first.Group) throw new CodeFailure(WorkshopCodeError.Checksum);
                    using var input = new MemoryStream(packed); using var zip = new BrotliStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream(); var buffer = new byte[4096]; int count;
                    while ((count = zip.Read(buffer)) > 0)
                    {
                        if (output.Length + count > MaxExpandedBytes) throw new CodeFailure(WorkshopCodeError.TooLarge);
                        output.Write(buffer, 0, count);
                    }
                    expanded += (int)output.Length;
                    // SCM2's historical Name is ignored. SCM3 has no name field at all.
                    var payload = JsonSerializer.Deserialize<WorkshopCodePayload>(output.ToArray(), Json) ?? throw new CodeFailure(WorkshopCodeError.InvalidData);
                    Validate(payload);
                    if (payload.Item.Id != first.Id || payload.Format != first.Format) throw new CodeFailure(WorkshopCodeError.Conflict);
                    candidates.Add(new(payload, first.Group, sources));
                }
                catch (CodeFailure ex) { error = ex.Error; }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or FormatException or ArgumentException)
                { error = WorkshopCodeError.InvalidData; }
            }
            if (expanded > 32 * 1024 * 1024) throw new InvalidDataException("Submission expansion budget exceeded.");
            if (error is { } reason) issues.Add(new(first.Group, first.Id, "", "", reason, first.Group, first.Total, missing, sources));
        }
        return new(candidates.ToArray(), issues.GroupBy(i => (i.Key, i.Id, i.Error)).Select(g => g.First() with
        { Sources = g.SelectMany(i => i.Sources).Distinct().ToArray() }).ToArray());
    }

    private static IEnumerable<Match> CodeStarts(string line)
    {
        var cursor = 0; var quoted = false;
        foreach (Match match in Starts.Matches(line))
        {
            while (cursor < match.Index)
            {
                if (line[cursor] == '\\' && quoted) { cursor += 2; continue; }
                if (line[cursor] == '"') quoted = !quoted;
                cursor++;
            }
            if (!quoted) yield return match;
        }
    }

    private static Part Parse(string raw, WorkshopCodeSource source)
    {
        if (raw.Length > MaxCodeLength) throw new CodeFailure(WorkshopCodeError.TooLarge);
        var format = raw.StartsWith("SCM3 ", StringComparison.Ordinal) ? 3 : raw.StartsWith("SCM2 ", StringComparison.Ordinal) ? 2 : 0;
        if (format == 0) throw new CodeFailure(WorkshopCodeError.Version);
        var match = (format == 3 ? Header3 : Header).Match(raw);
        if (!match.Success) throw new CodeFailure(WorkshopCodeError.Format);
        var id = ulong.Parse(match.Groups["id"].Value);
        var index = int.Parse(match.Groups["part"].Value); var total = int.Parse(match.Groups["total"].Value);
        if (id is 0 or 3787302680 || total is < 1 or > 128 || index < 1 || index > total) throw new CodeFailure(WorkshopCodeError.Format);
        if (Hash(Encoding.UTF8.GetBytes(raw[..raw.LastIndexOf('.')]), 8) != match.Groups["sum"].Value.ToUpperInvariant()) throw new CodeFailure(WorkshopCodeError.Checksum);
        return new(id, format, match.Groups["group"].Value.ToUpperInvariant(), index, total, match.Groups["body"].Value, source with { Part = index });
    }
    private static WorkshopCodeIssue RecoverIssue(WorkshopCodeSource source, WorkshopCodeError error)
    {
        var match = Recover.Match(source.Code); var name = "";
        try { if (match.Groups["name"].Success) name = JsonSerializer.Deserialize<string>(match.Groups["name"].Value) ?? ""; } catch (JsonException) { }
        ulong.TryParse(match.Groups["id"].Value, out var id);
        int.TryParse(match.Groups["part"].Value, out var part); int.TryParse(match.Groups["total"].Value, out var total);
        if (total is < 1 or > 128 || part < 1 || part > total) part = total = 0;
        return Issue(source with { Part = part }, error, id, name, match.Groups["group"].Value) with { Total = total };
    }
    private static WorkshopCodeIssue Issue(WorkshopCodeSource source, WorkshopCodeError error, ulong id = 0, string name = "", string group = "") =>
        new(Fingerprint(source.Code), id, name, "", error, group, 0, [], [source]);
    private static void Validate(WorkshopCodePayload payload)
    {
        if (payload.Format is not (2 or 3)) throw new CodeFailure(WorkshopCodeError.Version);
        if (payload.App != WorkshopCatalogPolicy.AppId) throw new CodeFailure(WorkshopCodeError.WrongGame);
        if (payload.Scanner is not { Length: > 0 and <= 32 } ||
            payload.Game is not { Length: <= 64 } || !ValidItem(payload.Item))
            throw new CodeFailure(WorkshopCodeError.InvalidData);
    }
    internal static bool ValidItem(WorkshopCatalogItem? item) => item is { Id: not (0 or 3787302680), Targets.Length: > 0 and <= 512 } &&
        item.Targets.All(target => target != null && target.Kind is ("character" or "cards" or "monster" or "ancient" or "merchant" or "companion" or "event") &&
            target.Target is { Length: > 0 and <= 160 } && !target.Target.Any(c => char.IsControl(c) || "<>\"'\\/".Contains(c)));
}
