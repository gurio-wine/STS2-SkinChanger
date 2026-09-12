using Godot;

namespace STS2SkinChanger.Core;

/// <summary>
/// A style/UI package draws its version of a group while the game builds that group's scene: its
/// background, its merchant and its inventory frames are attached once, from the scene's own
/// initialization (see <see cref="PartTakeoverScope"/>). Choosing that part inside a scene that is
/// already standing therefore has to repeat that initialization on the live scene.
///
/// Only the package's own drawing is repeated — the game's scene is never rebuilt and the game's
/// own methods are never called again — so the run, the open inventory and everything the player
/// already bought stay exactly as they are.
///
/// The repeat can only happen while the package still owns the callback that draws this scene.
/// A package whose parts are taken over is therefore never stripped of its own patches
/// (see <see cref="VisualPatchGuard"/>); they wait here, bound to the group's selection, until the
/// scene they draw is standing again or the player picks their part.
///
/// The repeat is only ever built for the part this group's selection resolves to, which is what
/// keeps it inside the selection: the package's own callbacks are held back by
/// <see cref="PartTakeoverScope"/> when their part is not in effect.
/// </summary>
internal static class PartTakeoverPresentation
{
    private static readonly HashSet<string> ReportedEmptyReplays = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Redraws the live scene for whichever part of this group is selected, and reports how many of
    /// the package's own entry points drew it. Zero means this group is not taken over right now,
    /// the package is not loaded, or it draws this scene from somewhere other than its
    /// initialization.
    /// </summary>
    internal static int ReapplySelectedOwner(Node scene, string groupId)
    {
        if (!GodotObject.IsInstanceValid(scene))
        {
            return 0;
        }

        try
        {
            PartTakeoverScope.EnsureInstalled();
            if (PartTakeoverScope.ResolveSelectedOwner(groupId) is not { } owner)
            {
                return 0;
            }

            // Hand back whatever this part drew in this scene first: the scene may already carry
            // its art from an earlier visit, or from the part being chosen twice.
            PartTakeoverVisualScope.ReleaseOwner(scene, owner);
            var redrawn = 0;
            foreach (var patch in PartTakeoverScope.ResolveScenePresentation(owner, scene.GetType()))
            {
                if (!PartTakeoverPolicy.TryBuildPresentationArguments(
                        patch.Callback,
                        patch.Target,
                        scene,
                        out var arguments))
                {
                    continue;
                }

                try
                {
                    patch.Callback.Invoke(null, arguments);
                    redrawn++;
                }
                catch (Exception exception)
                {
                    ModLog.Warn(
                        $"重画部件 {owner.OptionId} 的商店画面入口 " +
                        $"{patch.Callback.DeclaringType?.Name}.{patch.Callback.Name} 失败：" +
                        exception.GetBaseException().Message);
                }
            }

            if (redrawn > 0)
            {
                ModLog.Info($"已按当前选择重画部件 {owner.OptionId} 的商店画面：{redrawn} 个入口。");
            }
            else if (ReportedEmptyReplays.Add(owner.GroupId + "|" + owner.OptionId))
            {
                // Say why nothing was repeated instead of leaving the player with the game's own
                // shop and no clue: an entry point the game never reaches again cannot be repeated
                // from here, and that has to be visible in the log.
                var (total, onScene) = PartTakeoverScope.CountGatedPatches(owner, scene.GetType());
                ModLog.Info(
                    $"部件 {owner.OptionId} 在已建好的 {scene.GetType().Name} 上没有可重画的画面入口：" +
                    $"已按选择接管 {total} 个入口，其中 {onScene} 个属于该场景自己的初始化。");
            }

            return redrawn;
        }
        catch (Exception exception)
        {
            ModLog.Warn("按当前选择重画部件画面失败，保留作者画面：" + exception.GetBaseException().Message);
            return 0;
        }
    }
}
