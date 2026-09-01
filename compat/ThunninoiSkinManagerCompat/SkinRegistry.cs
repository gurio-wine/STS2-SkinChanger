using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using thunninoiSkinManager.thunninoiSkinManagerCode.Patches;

namespace thunninoiSkinManager.thunninoiSkinManagerCode;

public class SkinRegistry
{
    private static readonly Dictionary<ModelId, List<SkinData>> Skins = [];
    private static readonly Dictionary<ModelId, string> ActiveSkins = [];

    public static void SkinDbSetup()
    {
        // Deliberately does not install UI, save files or global visual patches. Provider
        // Harmony postfixes may still use this declaration hook to call Register(SkinData).
    }

    public static void finializeSetup()
    {
    }

    public static void Register(SkinData skin)
    {
        if (string.IsNullOrWhiteSpace(skin.SkinId))
        {
            return;
        }

        if (!Skins.TryGetValue(skin.TargetCharacterId, out var values))
        {
            values = [];
            Skins[skin.TargetCharacterId] = values;
        }

        if (values.All(candidate => !candidate.SkinId.Equals(
                skin.SkinId,
                StringComparison.OrdinalIgnoreCase)))
        {
            values.Add(skin);
        }
    }

    public static SkinData? GetActiveSkin(ModelId characterId)
    {
        if (!ActiveSkins.TryGetValue(characterId, out var skinId) ||
            !Skins.TryGetValue(characterId, out var values))
        {
            return null;
        }

        return values.FirstOrDefault(candidate => candidate.SkinId.Equals(
            skinId,
            StringComparison.OrdinalIgnoreCase));
    }

    public static int GetSkinIndex(ModelId characterId, string skindId) =>
        Skins.TryGetValue(characterId, out var values)
            ? values.FindIndex(candidate => candidate.SkinId.Equals(
                skindId,
                StringComparison.OrdinalIgnoreCase))
            : -1;

    public static List<SkinData> GetAllSkins(ModelId characterId) =>
        Skins.TryGetValue(characterId, out var values) ? values : [];

    public static bool IsUsingSkin(ModelId characterId, string skinId) =>
        ActiveSkins.TryGetValue(characterId, out var active) &&
        active.Equals(skinId, StringComparison.OrdinalIgnoreCase);

    public static void SetActiveSkin(ModelId characterId, string skinId)
    {
        ActiveSkins[characterId] = skinId;
    }

    public static void SetActiveSkin(ModelId characterId, int skinIndex)
    {
        if (Skins.TryGetValue(characterId, out var values) &&
            skinIndex >= 0 && skinIndex < values.Count)
        {
            ActiveSkins[characterId] = values[skinIndex].SkinId;
        }
    }

    public static void CycleNext(ModelId characterId) => Cycle(characterId, 1);
    public static void CyclePrevious(ModelId characterId) => Cycle(characterId, -1);

    public static Dictionary<ModelId, string> GetAllActiveSkins() => ActiveSkins;

    public static void Load()
    {
    }

    public static void Save()
    {
    }

    internal static bool Resolve<T>(ModelId characterId, Func<SkinData, T?> selector, out T? value)
    {
        var skin = GetActiveSkin(characterId);
        if (skin == null || skin.IsDefault)
        {
            value = default;
            return false;
        }
        value = selector(skin);
        return true;
    }

    internal static Color? ResolveColor(ModelId characterId, Func<SkinData, Color?> selector) =>
        Resolve(characterId, selector, out Color? value) ? value : null;

    internal static string? ResolvePath(ModelId characterId, Func<SkinData, string?> selector) =>
        Resolve(characterId, selector, out string? value) ? value : null;

    internal static Texture2D? ResolveTexture(ModelId characterId, Func<SkinData, string?> selector)
    {
        var path = ResolvePath(characterId, selector);
        return path == null ? null : PreloadManager.Cache.GetTexture2D(path);
    }

    internal static PowerSkin? ResolvePower(ModelId powerId) =>
        ActiveValues().SelectMany(value => value.PowerSkinDict)
            .FirstOrDefault(pair => pair.Key.Equals(powerId)).Value;

    internal static PotionSkin? ResolvePotion(ModelId potionId) =>
        ActiveValues().SelectMany(value => value.PotionSkinDict)
            .FirstOrDefault(pair => pair.Key.Equals(potionId)).Value;

    internal static RelicSkin? ResolveRelic(ModelId relicId) =>
        ActiveValues().SelectMany(value => value.RelicSkinDict)
            .FirstOrDefault(pair => pair.Key.Equals(relicId)).Value;

    internal static OrbSkin? ResolveOrb(ModelId orbId)
    {
        var defectId = ModelDb.GetId<Defect>();
        var skin = GetActiveSkin(defectId);
        return skin != null && skin.OrbSkinDict.TryGetValue(orbId, out var orb) ? orb : null;
    }

    internal static bool ResolveConfig(ModelId charId, string key, bool defaultValue = true) =>
        Resolve(charId, skin => skin.IsConfigEnabled(key, defaultValue), out bool value)
            ? value
            : defaultValue;

    private static IEnumerable<SkinData> ActiveValues() =>
        ActiveSkins.Keys.Select(GetActiveSkin).Where(value => value != null).Cast<SkinData>();

    private static void Cycle(ModelId characterId, int delta)
    {
        if (!Skins.TryGetValue(characterId, out var values) || values.Count == 0)
        {
            return;
        }
        var current = ActiveSkins.TryGetValue(characterId, out var skinId)
            ? values.FindIndex(value => value.SkinId.Equals(skinId, StringComparison.OrdinalIgnoreCase))
            : 0;
        var next = (Math.Max(current, 0) + delta + values.Count) % values.Count;
        ActiveSkins[characterId] = values[next].SkinId;
    }
}
