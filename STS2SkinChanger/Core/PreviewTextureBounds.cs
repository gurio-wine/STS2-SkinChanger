using Godot;

namespace STS2SkinChanger.Core;

/// <summary>One preview's read-only framing measurements; never retains texture data globally.</summary>
internal sealed class PreviewTextureBounds : IDisposable
{
    private readonly Dictionary<Texture2D, Image?> _images = [];
    private readonly Dictionary<(Texture2D, Rect2), Rect2?> _bounds = [];
    private long _readPixels;
    private const long MaxReadPixels = 16 * 1024 * 1024;

    internal Rect2? Read(Texture2D texture, Rect2 source, int depth = 0)
    {
        if (_bounds.TryGetValue((texture, source), out var cached)) return cached;
        Rect2? result = null;
        try
        {
            result = ReadCore(texture, source, depth);
        }
        catch (Exception exception)
        {
            ModLog.Warn("读取小预览透明边界失败，保留原始取景：" + exception.GetBaseException().Message);
        }
        _bounds[(texture, source)] = result;
        return result;
    }

    private Rect2? ReadCore(Texture2D texture, Rect2 source, int depth)
    {
        if (depth > 16 || !source.HasArea()) return null;
        if (texture is AtlasTexture atlas)
        {
            if (atlas.Atlas == null) return null;
            // AtlasTexture.GetImage omits the margin and does not correctly compose nested
            // atlas views. Follow the renderer's region/margin mapping down to the real image.
            var region = atlas.Region;
            if (region.Size.X == 0) region.Size = new(atlas.Atlas.GetWidth(), region.Size.Y);
            if (region.Size.Y == 0) region.Size = new(region.Size.X, atlas.Atlas.GetHeight());
            var translation = region.Position - atlas.Margin.Position;
            var mapped = new Rect2(source.Position + translation, source.Size).Intersection(region);
            if (!mapped.HasArea()) return new Rect2();
            var used = Read(atlas.Atlas, mapped, depth + 1);
            return used is { } r && r.HasArea() ? new Rect2(r.Position - translation, r.Size) : used;
        }

        // A live viewport/procedural texture is not a static picture. Do not read it back or
        // cache an animation frame produced by another renderer.
        if (texture is not (ImageTexture or CompressedTexture2D or PortableCompressedTexture2D)) return null;
        if (!_images.TryGetValue(texture, out var image))
        {
            var pixels = (long)texture.GetWidth() * texture.GetHeight();
            if (pixels <= 0 || pixels > MaxReadPixels - _readPixels) return null;
            _readPixels += pixels;
            image = texture.GetImage();
            _images[texture] = image;
            if (image is { } && image.IsCompressed() && image.Decompress() != Error.Ok) return null;
        }
        if (image == null || image.IsEmpty() || image.IsCompressed() ||
            image.GetWidth() != texture.GetWidth() || image.GetHeight() != texture.GetHeight()) return null;
        var full = new Rect2(Vector2.Zero, texture.GetSize());
        // Repeating/out-of-range regions have sampler-dependent geometry. Keep the old bounds.
        if (!full.Encloses(source)) return null;
        if (source.IsEqualApprox(full)) return image.GetUsedRect();
        var pixelsRect = new Rect2I((Vector2I)source.Position.Floor(),
            (Vector2I)source.End.Ceil() - (Vector2I)source.Position.Floor());
        using var crop = image.GetRegion(pixelsRect);
        var rect = (Rect2)crop.GetUsedRect();
        return rect.HasArea()
            ? new Rect2(rect.Position + pixelsRect.Position, rect.Size).Intersection(source)
            : new Rect2();
    }

    internal static Rect2 ToLocal(Rect2 source, Rect2? visible, Vector2 offset,
        bool centered, bool flipH, bool flipV)
    {
        var used = (visible ?? source).Intersection(source);
        if (!used.HasArea()) return new Rect2();
        var relative = used.Position - source.Position;
        if (flipH) relative.X = source.Size.X - relative.X - used.Size.X;
        if (flipV) relative.Y = source.Size.Y - relative.Y - used.Size.Y;
        return new Rect2(offset - (centered ? source.Size / 2 : Vector2.Zero) + relative, used.Size);
    }

    public void Dispose()
    {
        foreach (var image in _images.Values) image?.Dispose();
        _images.Clear();
        _bounds.Clear();
    }
}
