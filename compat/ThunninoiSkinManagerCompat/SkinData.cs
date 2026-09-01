using Godot;
using MegaCrit.Sts2.Core.Models;
using thunninoiSkinManager.thunninoiSkinManagerCode.Patches;

namespace thunninoiSkinManager.thunninoiSkinManagerCode;

public class SkinData
{
    public static class SkinConfigKey
    {
        public const string UseCardFrame = "UseCardFrame";
        public const string UseEnergy = "UseEnergy";
        public const string UseHands = "UseHands";
        public const string SilentRecolorShiv = "SilentRecolorShiv";
        public const string UseDefectOrbs = "UseDefectOrbs";
        public const string UseRegentBlade = "UseRegentBlade";
    }

    private readonly Dictionary<string, object> _customData = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<bool>> _configLoaded = new(StringComparer.Ordinal);

    public ModelId TargetCharacterId { get; }
    public string SkinId { get; }
    public string SkinName { get; }
    public bool IsDefault { get; private set; }
    internal CharacterSkin? CharacterSkinData { get; private set; }
    internal SkinData? FallbackSkin { get; private set; }
    internal Color? ShivTintColor { get; private set; }
    internal string? PreviewSkeletonData { get; private set; }
    internal Dictionary<ModelId, OrbSkin> OrbSkinDict { get; } = [];
    internal Dictionary<ModelId, PowerSkin> PowerSkinDict { get; } = [];
    internal Dictionary<ModelId, PotionSkin> PotionSkinDict { get; } = [];
    internal Dictionary<ModelId, RelicSkin> RelicSkinDict { get; } = [];

    public SkinData(ModelId targetCharacterId, string skinId, string skinName)
    {
        TargetCharacterId = targetCharacterId;
        SkinId = skinId;
        SkinName = skinName;
    }

    public SkinData RegisterConfig(string key, Func<bool> configVar)
    {
        _configLoaded[key] = configVar;
        return this;
    }

    public bool IsConfigEnabled(string key, bool defaultVar = true) =>
        _configLoaded.TryGetValue(key, out var value) ? value() : defaultVar;

    public SkinData AsDefault()
    {
        IsDefault = true;
        return this;
    }

    public SkinData RegisterCharacter(CharacterSkin characterSkin)
    {
        CharacterSkinData = characterSkin;
        return this;
    }

    public SkinData RegisterShivTint(Color color)
    {
        ShivTintColor = color;
        return this;
    }

    public SkinData RegisterOrb(OrbSkin orbSkinData)
    {
        if (orbSkinData.TargetOrbId is { } id)
        {
            OrbSkinDict[id] = orbSkinData;
        }
        return this;
    }

    public SkinData RegisterPower(PowerSkin powerSkinData)
    {
        if (powerSkinData.TargetPowerId is { } id)
        {
            PowerSkinDict[id] = powerSkinData;
        }
        return this;
    }

    public SkinData RegisterPotion(PotionSkin potionSkinData)
    {
        if (potionSkinData.TargetPotionId is { } id)
        {
            PotionSkinDict[id] = potionSkinData;
        }
        return this;
    }

    public SkinData RegisterRelic(RelicSkin relicSkinData)
    {
        if (relicSkinData.TargetRelicId is { } id)
        {
            RelicSkinDict[id] = relicSkinData;
        }
        return this;
    }

    public SkinData RegisterCustom(string key, object value)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            _customData[key] = value;
        }
        return this;
    }

    public T? GetCustom<T>(string key) where T : class =>
        _customData.TryGetValue(key, out var value) ? value as T : null;

    public object? GetCustom(string key) =>
        _customData.GetValueOrDefault(key);
}
