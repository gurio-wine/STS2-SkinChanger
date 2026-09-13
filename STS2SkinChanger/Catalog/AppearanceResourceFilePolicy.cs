namespace STS2SkinChanger.Catalog;

/// <summary>
/// Which loose files in a package are something the game can draw with. Used only to tell a package
/// that ships appearance resources from one that ships nothing but code and manifests; the game and
/// the catalog loaders keep their own, stricter resource rules.
/// </summary>
internal static class AppearanceResourceFilePolicy
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images and textures.
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tga", ".svg", ".dds", ".ktx", ".ctex",
        // Godot resources: scenes, materials and the exported payload of imported files.
        ".tres", ".res", ".tscn", ".scn", ".escn",
        // Spine skeletons and atlases.
        ".atlas", ".skel", ".spine",
        // Audio and fonts ship with appearance overhauls as well.
        ".ogg", ".wav", ".mp3", ".ttf", ".otf", ".fnt"
    };

    internal static bool IsAppearanceResourceFile(string path) =>
        Extensions.Contains(Path.GetExtension(path));
}
