using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// Session-owned thumbnails, not full-size originals. Closing a page must not
// dispose a texture another page can reuse. The process releases these on exit.
internal static class WorkshopCoverCache
{
    private static readonly WorkshopSessionCache<Texture2D?> Thumbnails = new();
    public static Task<Texture2D?> Get(string url, CancellationToken token) => Thumbnails.Get(url, () => Load(url), token);
    private static async Task<Texture2D?> Load(string url)
    {
        try
        {
            var bytes = await SkinWorkshopService.Cover(url, CancellationToken.None);
            if (bytes == null) return null;
            using var image = new Image();
            var error = bytes[0] == 137 ? image.LoadPngFromBuffer(bytes) : image.LoadJpgFromBuffer(bytes);
            if (error != Error.Ok || image.GetWidth() > 4096 || image.GetHeight() > 4096) return null;
            var factor = Math.Min(1d, Math.Min(144d / image.GetWidth(), 100d / image.GetHeight()));
            image.Resize(Math.Max(1, (int)(image.GetWidth() * factor)), Math.Max(1, (int)(image.GetHeight() * factor)), Image.Interpolation.Lanczos);
            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception ex) { ModLog.Info("工坊封面暂不可用，本次保留占位图：" + ex.Message); return null; }
    }
}
