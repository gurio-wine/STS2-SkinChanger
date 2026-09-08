using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// Session-owned thumbnails, not full-size originals. Closing a page must not
// dispose a texture another page can reuse. The process releases these on exit.
internal static class WorkshopCoverCache
{
    private static readonly WorkshopSessionCache<Texture2D?> Thumbnails = new();
    public static Task<Texture2D?> Get(string url, CancellationToken token) => Thumbnails.Get(url, () => Load(url), token);
    private static readonly BoundedLruCache<string, Texture2D?> Previews = new(24, StringComparer.Ordinal);
    private static readonly Dictionary<string, Task<Texture2D?>> PendingPreviews = [];
    public static Task<Texture2D?> GetPreview(string url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Previews.TryGetValue(url, out var texture)) return Task.FromResult(texture);
        if (!PendingPreviews.TryGetValue(url, out var pending))
        {
            pending = LoadPreview(url); PendingPreviews[url] = pending;
            // A warm disk cache can finish before the producer is inserted.
            if (pending.IsCompleted) PendingPreviews.Remove(url);
        }
        return pending.WaitAsync(token);
    }
    private static async Task<Texture2D?> LoadPreview(string url)
    {
        try
        {
            var texture = await Load(url, 640, 360);
            // Drop only our strong reference on eviction. An on-screen TextureRect
            // may still own the evicted texture; never Dispose it under that node.
            Previews.Set(url, texture, out _);
            return texture;
        }
        finally { PendingPreviews.Remove(url); }
    }
    private static async Task<Texture2D?> Load(string url, int width = 144, int height = 100)
    {
        try
        {
            var bytes = await SkinWorkshopService.Cover(url, CancellationToken.None);
            if (bytes == null) return null;
            using var image = new Image();
            var error = bytes[0] == 137 ? image.LoadPngFromBuffer(bytes) : image.LoadJpgFromBuffer(bytes);
            if (error != Error.Ok || image.GetWidth() > 4096 || image.GetHeight() > 4096) return null;
            var factor = Math.Min(1d, Math.Min((double)width / image.GetWidth(), (double)height / image.GetHeight()));
            image.Resize(Math.Max(1, (int)(image.GetWidth() * factor)), Math.Max(1, (int)(image.GetHeight() * factor)), Image.Interpolation.Lanczos);
            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception ex) { ModLog.Info("工坊封面暂不可用，本次保留占位图：" + ex.Message); return null; }
    }
}
