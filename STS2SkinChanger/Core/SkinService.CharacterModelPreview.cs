namespace STS2SkinChanger.Core;

internal static partial class SkinService
{
    internal static (float X, float Y)? GetCharacterModelPreviewPosition()
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            return Config.CharacterModelPreviewX is { } x && Config.CharacterModelPreviewY is { } y &&
                   float.IsFinite(x) && float.IsFinite(y)
                ? (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)) : null;
        }
    }

    internal static void SetCharacterModelPreviewPosition(float x, float y)
        => SetCharacterModelPreviewPlacement(x, y, false);

    internal static bool IsCharacterModelPreviewDocked()
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            return Config.CharacterModelPreviewDocked;
        }
    }

    internal static void SetCharacterModelPreviewPlacement(float x, float y, bool docked)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return;
        lock (Sync)
        {
            EnsureConfigLoaded();
            Config.CharacterModelPreviewX = Math.Clamp(x, 0, 1);
            Config.CharacterModelPreviewY = Math.Clamp(y, 0, 1);
            Config.CharacterModelPreviewDocked = docked;
            Config.Save(ConfigPath);
        }
    }

    internal static void ResetCharacterModelPreviewPosition()
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            Config.CharacterModelPreviewX = Config.CharacterModelPreviewY = null;
            Config.CharacterModelPreviewDocked = false;
            Config.Save(ConfigPath);
        }
    }
}
