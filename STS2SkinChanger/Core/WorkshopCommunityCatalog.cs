using System.Text.Json;

namespace STS2SkinChanger.Core;

// Only completed, validated snapshots replace the previous list; failed refreshes don't clear it.
internal sealed class WorkshopCommunityCatalog
{
    private WorkshopCatalogItem[] _items = [];
    public WorkshopCatalogItem[] Items => _items;
    public async Task Replace(Func<Task<WorkshopCatalogItem[]>> load, Func<WorkshopCatalogItem[], Task<WorkshopCatalogItem[]>> verify, string cachePath)
    {
        var proposed = await load();
        var verified = await verify(proposed);
        var normalized = WorkshopSubmissionCode.Merge([], verified);
        var json = JsonSerializer.Serialize(normalized);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 4 * 1024 * 1024) throw new InvalidDataException("Community catalog cache too large.");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temp = cachePath + ".tmp";
        await File.WriteAllTextAsync(temp, json);
        File.Move(temp, cachePath, overwrite: true);
        _items = normalized;
    }
    public async Task Restore(string cachePath)
    {
        if (!File.Exists(cachePath)) return;
        if (new FileInfo(cachePath).Length > 4 * 1024 * 1024) throw new InvalidDataException("Community catalog cache too large.");
        _items = WorkshopCatalogPolicy.Parse(await File.ReadAllTextAsync(cachePath));
    }
}
