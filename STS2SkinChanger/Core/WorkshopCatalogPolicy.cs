using System.Text.Json;

namespace STS2SkinChanger.Core;

internal sealed record WorkshopTarget(string Kind, string Target);
// Only an explicit, complete package audit may promise hot loading.
internal sealed record WorkshopCatalogItem(ulong Id, WorkshopTarget[] Targets, bool RestartRequired = true);

internal static class WorkshopCatalogPolicy
{
    public const string CommandId = "__workshop__";
    public const uint AppId = 2868840;
    private static readonly HashSet<string> Kinds = ["character", "cards", "monster", "ancient", "merchant", "companion", "event"];

    public static WorkshopCatalogItem[] Parse(string json)
    {
        if (json.Length > 4 * 1024 * 1024) throw new InvalidDataException("Workshop catalog too large.");
        var items = JsonSerializer.Deserialize<WorkshopCatalogItem[]>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 8 }) ?? [];
        return items.Where(item => item != null && item.Id > 0)
            .GroupBy(item => item.Id).Select(group => new WorkshopCatalogItem(group.Key,
                group.SelectMany(item => item.Targets ?? []).Where(t => t != null && Kinds.Contains(t.Kind) && !string.IsNullOrWhiteSpace(t.Target))
                    .Select(t => new WorkshopTarget(t.Kind, t.Target.ToLowerInvariant())).Distinct().ToArray(), group.Any(item => item.RestartRequired)))
            .Where(item => item.Targets.Length > 0).ToArray();
    }
    public static ulong[] FilterIds(string json, string kind, string target) =>
        Filter(Parse(json), kind, target).Select(item => item.Id).ToArray();
    public static IEnumerable<WorkshopCatalogItem> Filter(IEnumerable<WorkshopCatalogItem> items, string kind, string target) =>
        items.Where(item => item.Targets.Any(t => (kind.Length == 0 || t.Kind == kind) &&
            (target.Length == 0 || t.Target.Equals(target, StringComparison.OrdinalIgnoreCase))));
    public static bool IsSkinChoice(string id) => !id.Equals(CommandId, StringComparison.OrdinalIgnoreCase);
    public static bool CanUseInstalledFiles(bool installed, bool needsUpdate, bool downloading, bool pending) =>
        installed && !needsUpdate && !downloading && !pending;

    public static bool IsSafeCover(byte[] data)
    {
        bool Size(uint width, uint height) => width is > 0 and <= 4096 && height is > 0 and <= 4096 && (ulong)width * height <= 8_388_608;
        if (data.Length < 24 || data.Length > 2 * 1024 * 1024) return false;
        if (data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            return Size(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20)));
        if (data[0] != 0xff || data[1] != 0xd8) return false;
        var p = 2;
        while (p + 4 < data.Length)
        {
            if (data[p++] != 0xff) return false;
            while (p < data.Length && data[p] == 0xff) p++;
            if (p + 3 >= data.Length) return false;
            var marker = data[p++];
            if (marker is 0xd9 or 0xda) return false;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            var size = (data[p] << 8) | data[p + 1];
            if (size < 2 || p + size > data.Length) return false;
            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
                return size >= 7 && Size((uint)((data[p + 5] << 8) | data[p + 6]), (uint)((data[p + 3] << 8) | data[p + 4]));
            p += size;
        }
        return false;
    }
}
