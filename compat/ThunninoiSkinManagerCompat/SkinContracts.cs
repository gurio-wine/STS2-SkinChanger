using Godot;
using MegaCrit.Sts2.Core.Models;

namespace thunninoiSkinManager.thunninoiSkinManagerCode.Patches;

public abstract class CharacterSkin
{
    public virtual ModelId? TargetCharId => null;
    public virtual string? CombatVisual => null;
    public virtual string? MerchantVisual => null;
    public virtual string? RestVisual => null;
    public virtual string? CharacterSelectBg => null;
    public virtual string? CharacterSelectPortrait => null;
    public virtual string? CharacterSelectTransition => null;
    public virtual string? CharacterIcon => null;
    public virtual string? CharacterIconOutline => null;
    public virtual string? CharacterIconScene => null;
    public virtual string? CharacterMapMarker => null;
    public virtual string? CardFrameMaterial => null;
    public virtual string? CardTrail => null;
    public virtual string? EnergyIcon => null;
    public virtual string[]? EnergyLayers => null;
    public virtual Color? EnergyLabelColor => null;
    public virtual Color? EnergyLabelOutlineColor => null;
    public virtual string? HandPoint => null;
    public virtual string? HandRock => null;
    public virtual string? HandPaper => null;
    public virtual string? HandScissors => null;
}

public class CharacterSkin<T> : CharacterSkin where T : CharacterModel
{
    public override ModelId TargetCharId => ModelDb.GetId<T>();
}

public abstract class OrbSkin
{
    public virtual ModelId? TargetOrbId => null;
    public virtual string? CustomIconPath => null;
    public virtual string? CustomSpritePath => null;
    public virtual Color? CustomDarkenedColor => null;
    public virtual Node2D? CreateCustomSprite() => null;
}

public abstract class OrbSkin<T> : OrbSkin where T : OrbModel
{
    public override ModelId TargetOrbId => ModelDb.GetId<T>();
}

public abstract class RelicSkin
{
    public virtual ModelId? TargetRelicId => null;
    public virtual string? PackedIconPath => null;
    public virtual string? PackedIconOutlinePath => null;
    public virtual string? BigIconPath => null;
}

public abstract class RelicSkin<T> : RelicSkin where T : RelicModel
{
    public override ModelId? TargetRelicId => ModelDb.GetId<T>();
}

public abstract class PotionSkin
{
    public virtual ModelId? TargetPotionId => null;
    public virtual string? CustomSpritePath => null;
    public virtual string? CustomSpriteOutlinePath => null;
    public virtual string? CustomThrownSpritePath => null;
}

public abstract class PotionSkin<T> : PotionSkin where T : PotionModel
{
    public override ModelId TargetPotionId => ModelDb.GetId<T>();
}

public abstract class PowerSkin
{
    public virtual ModelId? TargetPowerId => null;
    public virtual string? CustomIconPath => null;
    public virtual string? CustomBigIconPath => null;
}

public abstract class PowerSkin<T> : PowerSkin where T : PowerModel
{
    public override ModelId TargetPowerId => ModelDb.GetId<T>();
}
