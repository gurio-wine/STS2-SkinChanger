using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace STS2SkinChanger.Core;

/// <summary>
/// A model's real ownership chain for cosmetic provider callbacks, without a combat node's
/// health/orb UI, gameplay subscriptions, global room membership or combat lifecycle.
/// </summary>
internal partial class FrameworkPreviewCreature : NCreature
{
    internal void Initialize(Player player, NCreatureVisuals visuals)
    {
        AccessTools.PropertySetter(typeof(NCreature), nameof(Entity)).Invoke(this, [player.Creature]);
        AccessTools.PropertySetter(typeof(NCreature), nameof(Visuals)).Invoke(this, [visuals]);
        AddChild(visuals);
    }

    public override void _EnterTree() { }
    public override void _Ready() { }
    public override void _ExitTree() => DeathAnimCancelToken.Cancel();
}
