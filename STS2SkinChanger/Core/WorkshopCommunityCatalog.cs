using System.Text.Json;

namespace STS2SkinChanger.Core;

// Only completed, validated snapshots replace the previous list; failed refreshes don't clear it.
internal sealed class WorkshopCommunityCatalog
{
    public WorkshopCommunityState State { get; private set; } = WorkshopCommunityState.Empty;
    public WorkshopCatalogItem[] Items => State.Entries.Select(e => e.Item).ToArray();
    public async Task Replace(Func<Task<WorkshopCommunityState>> load, string cachePath, Action<string, Action>? cacheWriter = null)
    {
        var state = await load();
        Validate(state);
        var json = JsonSerializer.Serialize(state);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 32 * 1024 * 1024) throw new InvalidDataException("Community catalog cache too large.");
        void Write()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temp = cachePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, cachePath, overwrite: true);
        }
        await Task.Run(() => { if (cacheWriter == null) Write(); else cacheWriter(cachePath, Write); });
        State = state;
    }
    public async Task Restore(string cachePath)
    {
        if (!File.Exists(cachePath)) return;
        if (new FileInfo(cachePath).Length > 32 * 1024 * 1024) throw new InvalidDataException("Community catalog cache too large.");
        var state = JsonSerializer.Deserialize<WorkshopCommunityState>(await File.ReadAllTextAsync(cachePath), new JsonSerializerOptions { MaxDepth = 16 });
        Validate(state);
        // Names no longer gate inclusion. Drop retired name-only diagnostics from old caches;
        // the background refresh rechecks their source codes by Workshop ID.
        State = state! with { Issues = state!.Issues.Where(i => i.Error is not (WorkshopCodeError.MissingName or WorkshopCodeError.NameMismatch)).ToArray() };
    }
    private static void Validate(WorkshopCommunityState? state)
    {
        if (state is not { Format: 2, Entries: not null, Issues: not null } || state.Entries.Length > 20000 || state.Issues.Length > 10000 ||
            state.Entries.Any(e => e == null || e.Name is not { Length: <= 1024 } || e.Group is not { Length: 24 } || e.Item == null ||
                !Hex(e.Group, 24) || !WorkshopSubmissionCodec.ValidItem(e.Item) || e.Reply is < 0 or > 30000) ||
            state.Issues.Any(i => i == null || !Hex(i.Key, 12) && !Hex(i.Key, 24) || !Enum.IsDefined(i.Error) ||
                i.Name is not { Length: <= 4096 } || i.ActualName is not { Length: <= 1024 } || i.Group == null ||
                i.Group.Length != 0 && !Hex(i.Group, 24) || i.Total is < 0 or > 128 || i.Missing is not { Length: <= 128 } ||
                i.Missing.Any(p => p < 1 || p > i.Total) || i.Sources is not { Length: > 0 and <= 10000 } ||
                i.Sources.Any(s => s == null || s.Code is not { Length: > 0 and <= 4096 } || s.Url == null ||
                    !WorkshopCodeDiagnostics.SafeSource(s.Url) || s.Page < 1 || s.Reply < 0 || s.Order < 0 || s.Part is < 0 or > 128)))
            throw new InvalidDataException("Unverified or invalid community catalog cache.");
    }
    private static bool Hex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
}
