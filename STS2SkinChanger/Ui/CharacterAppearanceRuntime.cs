using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal enum AppearanceSelectionRequestState
{
    Applied,
    Queued,
    Failed
}

internal sealed record AppearanceSelectionRequestResult(
    AppearanceSelectionRequestState State,
    string? Error = null);

internal sealed record CreatureAppearanceBinding(
    SkinGroup Group,
    string TransformKey,
    bool UsesMonsterScale,
    bool CanSelectSkin,
    bool SupportsIntent);

/// <summary>
/// Owns live, in-run character visual replacement. A selection is never changed while a game action is
/// executing: replacing a Spine controller halfway through a card action can strand the action's animation task.
/// </summary>
internal static class CharacterAppearanceRuntime
{
    internal const string TransformWrapperName = "SkinChangerCharacterTransform";
    internal const string HealthDisplayWrapperName = "SkinChangerHealthDisplayTransform";
    internal const string IntentDisplayWrapperName = "SkinChangerIntentDisplayTransform";
    private const string HealthBoundsProxyName = "SkinChangerHealthBoundsProxy";
    private const string BaseVisualScaleMeta = "skin_changer_character_base_visual_scale";
    private const string BaseDefaultScaleMeta = "skin_changer_character_base_default_scale";
    private const string CurrentScaleMeta = "skin_changer_character_current_scale";
    private const string CurrentOffsetXMeta = "skin_changer_character_current_offset_x";
    private const string CurrentOffsetYMeta = "skin_changer_character_current_offset_y";
    private const string CurrentHealthScaleMeta = "skin_changer_health_current_scale";
    private const string CurrentHealthOffsetXMeta = "skin_changer_health_current_offset_x";
    private const string CurrentHealthOffsetYMeta = "skin_changer_health_current_offset_y";
    private const string CurrentHealthFollowScaleMeta = "skin_changer_health_follow_scale";
    private const string CurrentHealthFollowMovementMeta = "skin_changer_health_follow_movement";
    private const string CurrentIntentScaleMeta = "skin_changer_intent_current_scale";
    private const string CurrentIntentOffsetXMeta = "skin_changer_intent_current_offset_x";
    private const string CurrentIntentOffsetYMeta = "skin_changer_intent_current_offset_y";
    private const string CurrentIntentFollowScaleMeta = "skin_changer_intent_follow_scale";
    private const string CurrentIntentFollowMovementMeta = "skin_changer_intent_follow_movement";
    private const string CurrentReticleScaleMeta = "skin_changer_reticle_current_scale";
    private const string CurrentReticleOffsetXMeta = "skin_changer_reticle_current_offset_x";
    private const string CurrentReticleOffsetYMeta = "skin_changer_reticle_current_offset_y";
    private const string CurrentReticleFollowScaleMeta = "skin_changer_reticle_follow_scale";
    private const string CurrentReticleFollowMovementMeta = "skin_changer_reticle_follow_movement";

    private static readonly FieldInfo? VisualsField =
        AccessTools.Field(typeof(NCreature), "<Visuals>k__BackingField");
    private static readonly FieldInfo? SpineAnimatorField =
        AccessTools.Field(typeof(NCreature), "_spineAnimator");
    private static readonly FieldInfo? SelectionReticleField =
        AccessTools.Field(typeof(NCreature), "_selectionReticle");
    private static readonly FieldInfo? SelectionReticleTweenField =
        AccessTools.Field(typeof(NSelectionReticle), "_currentTween");
    private static readonly FieldInfo? StateDisplayField =
        AccessTools.Field(typeof(NCreature), "_stateDisplay");
    private static readonly FieldInfo? TempScaleField =
        AccessTools.Field(typeof(NCreature), "_tempScale");
    private static readonly FieldInfo? RunStateField =
        AccessTools.Field(typeof(NRun), "_state");
    private static readonly FieldInfo? MapMarkerField =
        AccessTools.Field(typeof(NMapScreen), "_marker");
    private static readonly FieldInfo? OrbNodesField =
        AccessTools.Field(typeof(NOrbManager), "_orbs");
    private static readonly FieldInfo? EnergyCounterField =
        AccessTools.Field(typeof(NCombatUi), "_energyCounter");
    private static readonly FieldInfo? StarCounterField =
        AccessTools.Field(typeof(NCombatUi), "_starCounter");
    private static readonly MethodInfo? ConnectSpineAnimatorSignalsMethod =
        AccessTools.Method(typeof(NCreature), "ConnectSpineAnimatorSignals");
    private static readonly MethodInfo? UpdateBoundsMethod =
        AccessTools.Method(typeof(NCreature), "UpdateBounds", [typeof(Node)]);
    private static readonly MethodInfo? UpdatePhobiaModeMethod =
        AccessTools.Method(typeof(NCreature), "UpdatePhobiaMode");
    private static readonly MethodInfo? SetOrbManagerPositionMethod =
        AccessTools.Method(typeof(NCreature), "SetOrbManagerPosition");

    private static PendingSelection? _pendingSelection;
    private static WeakReference<NCombatRoom>? _playerLayoutRoom;
    private static float _playerLayoutScaling = 1f;
    private static bool _fullyCenterPlayers;

    internal static event Action<string, bool, string?>? QueuedSelectionFinished;

    internal static AppearanceSelectionRequestResult RequestSelection(string groupId, string optionId)
    {
        if (!CanApplySelectionNow())
        {
            _pendingSelection = new PendingSelection(groupId, optionId);
            NRun.Instance?
                .GetNodeOrNull<CharacterAppearanceRuntimeNode>("SkinChangerAppearanceRuntime")?
                .Wake();
            return new AppearanceSelectionRequestResult(AppearanceSelectionRequestState.Queued);
        }

        return ApplySelectionNow(groupId, optionId);
    }

    internal static string? GetRequestedOption(string groupId) =>
        _pendingSelection is { } pending &&
        pending.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase)
            ? pending.OptionId
            : null;

    internal static Player? GetLocalPlayer()
    {
        try
        {
            return RunStateField?.GetValue(NRun.Instance) is IRunState runState
                ? LocalContext.GetMe(runState)
                : null;
        }
        catch (Exception exception)
        {
            ModLog.Warn("读取当前玩家失败：" + exception.GetBaseException().Message);
            return null;
        }
    }

    internal static void FocusRuntimeProviderBehaviorsOnRunCharacters()
    {
        try
        {
            if (RunStateField?.GetValue(NRun.Instance) is not IRunState runState)
            {
                return;
            }

            var groupIds = runState.Players
                .Select(player => ContextualSkinControls.FindGroup(
                    player.Character.Id.Entry,
                    player.Character.GetType().Name)?.Id)
                .Where(groupId => groupId != null)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (groupIds.Count > 0)
            {
                SkinService.FocusRuntimeProviderBehaviorsOnCharacters(groupIds);
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("收窄当前对局角色皮肤行为失败：" + exception.GetBaseException().Message);
        }
    }

    internal static NCreature? GetCurrentCreature(Player? player)
    {
        if (player == null)
        {
            return null;
        }

        return NCombatRoom.Instance?.GetCreatureNode(player.Creature);
    }

    internal static bool ProcessPendingSelection()
    {
        var pending = _pendingSelection;
        if (pending == null)
        {
            return false;
        }
        if (!CanApplySelectionNow())
        {
            return true;
        }

        _pendingSelection = null;
        var result = ApplySelectionNow(pending.GroupId, pending.OptionId);
        QueuedSelectionFinished?.Invoke(
            pending.GroupId,
            result.State == AppearanceSelectionRequestState.Applied,
            result.Error);
        return false;
    }

    internal static void ClearPendingSelection() => _pendingSelection = null;

    internal static void OnCreatureReady(NCreature creature)
    {
        if (!GodotObject.IsInstanceValid(creature.Visuals))
        {
            return;
        }

        CaptureVisualBaseline(creature.Visuals);
        if (!TryGetCreatureAppearance(creature, out _))
        {
            return;
        }

        EnsureTransformWrapper(creature);
        EnsureHealthDisplayWrapper(creature);
        if (SupportsIntentAppearance(creature))
        {
            EnsureIntentDisplayWrapper(creature);
        }
        ApplyStoredTransform(creature);
    }

    internal static void ApplyStoredTransform(NCreature creature)
    {
        if (!TryGetCreatureAppearance(creature, out var binding))
        {
            return;
        }

        ApplyPreviewTransform(creature, GetCreatureCombatTransform(binding));
    }

    internal static CharacterCombatTransform GetCreatureCombatTransform(NCreature creature)
    {
        return TryGetCreatureAppearance(creature, out var binding)
            ? GetCreatureCombatTransform(binding)
            : new CharacterCombatTransform();
    }

    internal static CharacterCombatTransform SetCreatureCombatTransform(
        NCreature creature,
        CharacterCombatTransform value,
        bool save = true)
    {
        if (!TryGetCreatureAppearance(creature, out var binding))
        {
            return new CharacterCombatTransform();
        }

        var optionId = SkinService.Config.GetSelection(binding.Group.Id);
        if (!binding.UsesMonsterScale)
        {
            return SkinService.SetCharacterCombatTransform(
                binding.TransformKey,
                optionId,
                value,
                save);
        }

        var monsterScale = SkinService.SetSelectedMonsterScale(
            binding.Group.Id,
            value.Scale,
            save: false);
        var normalized = SkinService.SetCharacterCombatTransform(
            binding.TransformKey,
            optionId,
            value with { Scale = 1f },
            save);
        return normalized with { Scale = monsterScale };
    }

    internal static void ApplyTransformToKey(string transformKey)
    {
        var room = NCombatRoom.Instance;
        if (room == null)
        {
            return;
        }

        foreach (var creature in room.CreatureNodes.ToArray())
        {
            if (TryGetCreatureAppearance(creature, out var binding) &&
                binding.TransformKey.Equals(transformKey, StringComparison.OrdinalIgnoreCase))
            {
                ApplyStoredTransform(creature);
            }
        }
    }

    internal static void ApplyPreviewTransform(
        NCreature creature,
        CharacterCombatTransform value)
    {
        var wrapper = EnsureTransformWrapper(creature);
        if (wrapper == null)
        {
            return;
        }

        SetCurrentTransform(wrapper, value);
        wrapper.Position = new Vector2(value.OffsetX, value.OffsetY);
        if (TryGetCreatureAppearance(creature, out var binding) && binding.UsesMonsterScale)
        {
            ContextualSkinControls.ApplyMonsterScalePreview(creature.Visuals, value.Scale);
            wrapper.Scale = Vector2.One;
        }
        else
        {
            wrapper.Scale = Vector2.One * value.Scale;
        }

        RefreshCreatureAnchors(creature);
    }

    internal static Node2D? GetTransformWrapper(NCreature? creature)
    {
        if (creature == null || !GodotObject.IsInstanceValid(creature))
        {
            return null;
        }

        return creature.GetNodeOrNull<Node2D>(TransformWrapperName);
    }

    internal static Node2D? GetHealthDisplayWrapper(NCreature? creature)
    {
        if (creature == null || !GodotObject.IsInstanceValid(creature))
        {
            return null;
        }

        return creature.GetNodeOrNull<Node2D>(HealthDisplayWrapperName);
    }

    internal static Node2D? GetIntentDisplayWrapper(NCreature? creature)
    {
        if (creature == null || !GodotObject.IsInstanceValid(creature))
        {
            return null;
        }

        return creature.GetNodeOrNull<Node2D>(IntentDisplayWrapperName);
    }

    internal static NSelectionReticle? GetSelectionReticle(NCreature? creature)
    {
        if (creature == null || !GodotObject.IsInstanceValid(creature))
        {
            return null;
        }

        return SelectionReticleField?.GetValue(creature) as NSelectionReticle;
    }

    internal static void StopSelectionReticleAnimation(NSelectionReticle? reticle)
    {
        if (reticle == null || !GodotObject.IsInstanceValid(reticle))
        {
            return;
        }

        if (SelectionReticleTweenField?.GetValue(reticle) is Tween tween &&
            GodotObject.IsInstanceValid(tween))
        {
            tween.Kill();
        }
    }

    internal static bool SupportsIntentAppearance(NCreature? creature) =>
        creature != null &&
        GodotObject.IsInstanceValid(creature) &&
        TryGetCreatureAppearance(creature, out var binding) &&
        binding.SupportsIntent;

    internal static NCreatureStateDisplay? GetStateDisplay(NCreature? creature) =>
        creature != null && GodotObject.IsInstanceValid(creature)
            ? StateDisplayField?.GetValue(creature) as NCreatureStateDisplay
            : null;

    internal static Control? GetHealthBarBounds(NCreature? creature)
    {
        var stateDisplay = GetStateDisplay(creature);
        if (stateDisplay == null || !GodotObject.IsInstanceValid(stateDisplay))
        {
            return null;
        }

        var healthBar = stateDisplay.GetNodeOrNull<NHealthBar>("HealthBar");
        return healthBar != null && GodotObject.IsInstanceValid(healthBar.HpBarContainer)
            ? healthBar.HpBarContainer
            : null;
    }

    internal static Control? GetModelBounds(NCreature? creature)
    {
        if (creature == null ||
            !GodotObject.IsInstanceValid(creature) ||
            !GodotObject.IsInstanceValid(creature.Visuals))
        {
            return null;
        }

        return creature.Visuals.GetNodeOrNull<Control>("%Bounds");
    }

    internal static bool TryGetCreatureAppearance(
        NCreature creature,
        out CreatureAppearanceBinding binding)
    {
        binding = null!;
        if (!GodotObject.IsInstanceValid(creature) || creature.Entity == null)
        {
            return false;
        }

        var modelId = creature.Entity.ModelId.Entry;
        var modelTypeName = creature.Entity.Player?.Character.GetType().Name ??
                            creature.Entity.Monster?.GetType().Name;
        if (creature.Entity.PetOwner is { } owner)
        {
            var ownerCharacter = owner.Character;
            var ownerGroup = ContextualSkinControls.FindGroup(
                ownerCharacter.Id.Entry,
                ownerCharacter.GetType().Name);
            if (ownerGroup != null)
            {
                binding = new CreatureAppearanceBinding(
                    ownerGroup,
                    $"{ownerGroup.Id}::companion::{modelId}",
                    UsesMonsterScale: false,
                    CanSelectSkin: false,
                    SupportsIntent: false);
                return true;
            }
        }

        var directGroup = ContextualSkinControls.FindGroup(modelId, modelTypeName);
        if (directGroup != null)
        {
            var isCompanion = creature.Entity.PetOwner != null;
            binding = new CreatureAppearanceBinding(
                directGroup,
                directGroup.Id,
                creature.Entity.IsMonster && !isCompanion,
                CanSelectSkin: !isCompanion,
                SupportsIntent: creature.Entity.IsMonster && !isCompanion);
            return true;
        }

        return false;
    }

    private static CharacterCombatTransform GetCreatureCombatTransform(
        CreatureAppearanceBinding binding)
    {
        var optionId = SkinService.Config.GetSelection(binding.Group.Id);
        var value = SkinService.GetCharacterCombatTransform(binding.TransformKey, optionId);
        return binding.UsesMonsterScale
            ? value with { Scale = SkinService.GetSelectedMonsterScale(binding.Group.Id) }
            : value;
    }

    internal static void CorrectBoundsForVisualTransforms(NCreature creature, Node boundsContainer)
    {
        var wrapper = GetTransformWrapper(creature);
        if (wrapper == null)
        {
            CorrectMonsterBoundsForModelOnlyScale(creature);
            return;
        }

        try
        {
            var bounds = boundsContainer.GetNode<Control>("%Bounds");
            var tempScale = TempScaleField?.GetValue(creature) is float value && !Mathf.IsZeroApprox(value)
                ? value
                : 1f;
            var resourceScale = creature.Entity.IsMonster
                ? ContextualSkinControls.GetAppliedMonsterScaleFactor(creature.Visuals)
                : 1f;
            if (Mathf.IsZeroApprox(resourceScale))
            {
                resourceScale = 1f;
            }

            var wrapperScale = wrapper.Scale.X;
            var baseSize = bounds.Size * creature.Visuals.Scale / tempScale / resourceScale;
            var baseLocalPosition =
                (wrapper.GetGlobalTransform().AffineInverse() * bounds.GlobalPosition) /
                tempScale /
                resourceScale;
            var size = baseSize * resourceScale * wrapperScale;
            var globalPosition = wrapper.GetGlobalTransform() *
                                 (baseLocalPosition * resourceScale);
            var currentTransform = GetCurrentTransform(wrapper);

            creature.Hitbox.Size = size;
            creature.Hitbox.GlobalPosition = globalPosition;
            if (SupportsIntentAppearance(creature) &&
                GetSelectionReticle(creature) is { } reticle)
            {
                ApplySelectionReticleAndHitbox(
                    reticle,
                    creature.Hitbox,
                    currentTransform,
                    baseLocalPosition,
                    baseSize);
            }
            else if (SelectionReticleField?.GetValue(creature) is Control fallbackReticle)
            {
                fallbackReticle.Size = size;
                fallbackReticle.GlobalPosition = globalPosition;
                fallbackReticle.PivotOffset = size * 0.5f;
            }

            var intentMarker = boundsContainer.GetNode<Marker2D>("%IntentPos");
            var markerBaseLocal =
                (wrapper.GetGlobalTransform().AffineInverse() * intentMarker.GlobalPosition) /
                tempScale /
                resourceScale;
            var intentWrapper = SupportsIntentAppearance(creature)
                ? EnsureIntentDisplayWrapper(creature)
                : null;
            if (intentWrapper != null)
            {
                // Keep the HBox centred on its marker. Scaling the parent then changes only the intent's
                // own size; following model scale separately controls how far the marker moves from the
                // creature origin.
                creature.IntentContainer.Position = -creature.IntentContainer.Size * 0.5f;
                ApplyIntentDisplayTransform(intentWrapper, currentTransform, markerBaseLocal);
            }
            else
            {
                var markerGlobal = wrapper.GetGlobalTransform() *
                                   (markerBaseLocal * resourceScale);
                var markerLocal = creature.GetGlobalTransform().AffineInverse() * markerGlobal;
                creature.IntentContainer.Position =
                    markerLocal - creature.IntentContainer.Size * 0.5f;
            }

            var stateDisplay = GetStateDisplay(creature);
            var healthWrapper = EnsureHealthDisplayWrapper(creature);
            var healthBounds = EnsureHealthBoundsProxy(creature);
            if (stateDisplay != null && healthWrapper != null && healthBounds != null)
            {
                // The game's health-bar animation writes to the state display's local Position. Keeping our
                // customization on its parent makes vertical and horizontal movement behave consistently.
                healthWrapper.Position = Vector2.Zero;
                healthWrapper.Scale = Vector2.One;
                healthBounds.Size = baseSize;
                healthBounds.Scale = Vector2.One;
                healthBounds.GlobalPosition = creature.GetGlobalTransform() * baseLocalPosition;
                stateDisplay.SetCreatureBounds(healthBounds);
                ApplyHealthDisplayTransform(healthWrapper, currentTransform);
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("校正实战外观定位点失败：" + exception.GetBaseException().Message);
        }
    }

    private static void CorrectMonsterBoundsForModelOnlyScale(NCreature creature)
    {
        if (creature.Entity.IsPlayer || !GodotObject.IsInstanceValid(creature.Visuals))
        {
            return;
        }

        var factor = ContextualSkinControls.GetAppliedMonsterScaleFactor(creature.Visuals);
        if (Mathf.IsZeroApprox(factor) || Mathf.IsEqualApprox(factor, 1f))
        {
            return;
        }

        try
        {
            var size = creature.Hitbox.Size / factor;
            var globalPosition = creature.GlobalPosition +
                                 (creature.Hitbox.GlobalPosition - creature.GlobalPosition) / factor;
            creature.Hitbox.Size = size;
            creature.Hitbox.GlobalPosition = globalPosition;
            if (SelectionReticleField?.GetValue(creature) is Control reticle)
            {
                reticle.Size = size;
                reticle.GlobalPosition = globalPosition;
                reticle.PivotOffset = size * 0.5f;
            }

            creature.IntentContainer.Position = new Vector2(
                creature.IntentContainer.Position.X,
                creature.IntentContainer.Position.Y / factor);
            GetStateDisplay(creature)?.SetCreatureBounds(creature.Hitbox);
        }
        catch (Exception exception)
        {
            ModLog.Warn("校正怪物模型缩放后的战斗 UI 失败：" +
                        exception.GetBaseException().Message);
        }
    }

    internal static void CorrectOrbPositionForCharacterTransform(NCreature creature)
    {
        if (GetTransformWrapper(creature) == null ||
            !creature.Entity.IsPlayer ||
            creature.OrbManager == null)
        {
            return;
        }

        try
        {
            var localPosition = creature.GetGlobalTransform().AffineInverse() *
                                creature.Visuals.OrbPosition.GlobalPosition;
            creature.OrbManager.Position = localPosition;
            if (!creature.OrbManager.IsLocal)
            {
                creature.OrbManager.Position += Vector2.Up * 50f;
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("校正角色充能球定位点失败：" + exception.GetBaseException().Message);
        }
    }

    private static bool CanApplySelectionNow()
    {
        try
        {
            if (!RunManager.Instance.IsInProgress)
            {
                return true;
            }

            var executor = RunManager.Instance.ActionExecutor;
            if (executor != null &&
                (executor.IsRunning || executor.CurrentlyRunningAction != null))
            {
                return false;
            }

            return NCombatRoom.Instance?.CreatureNodes.All(creature =>
                       !creature.IsPlayingDeathAnimation) != false;
        }
        catch
        {
            // During run setup/teardown the executor can be unavailable. Waiting one frame is safer than
            // mutating the mounted resources while the room tree is changing.
            return false;
        }
    }

    private static AppearanceSelectionRequestResult ApplySelectionNow(string groupId, string optionId)
    {
        try
        {
            var previous = new Dictionary<string, string>(
                SkinService.Config.Selections,
                StringComparer.OrdinalIgnoreCase);
            if (!SkinService.ApplySelection(groupId, optionId))
            {
                return new AppearanceSelectionRequestResult(
                    AppearanceSelectionRequestState.Failed,
                    SkinService.LastError);
            }

            var affectedGroups = previous.Keys
                .Concat(SkinService.Config.Selections.Keys)
                .Where(key => previous.GetValueOrDefault(key) !=
                              SkinService.Config.Selections.GetValueOrDefault(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            affectedGroups.Add(groupId);

            var refreshErrors = RefreshLiveCreatures(affectedGroups);
            RefreshCurrentCharacterUi();
            if (refreshErrors.Count > 0)
            {
                var error = string.Join("; ", refreshErrors);
                ModLog.Warn("皮肤选择已保存，但部分实战外观将在下次创建时生效：" + error);
                return new AppearanceSelectionRequestResult(
                    AppearanceSelectionRequestState.Applied,
                    error);
            }

            return new AppearanceSelectionRequestResult(AppearanceSelectionRequestState.Applied);
        }
        catch (Exception exception)
        {
            ModLog.Error("实战切换角色外观失败：" + exception);
            return new AppearanceSelectionRequestResult(
                AppearanceSelectionRequestState.Failed,
                exception.GetBaseException().Message);
        }
    }

    private static List<string> RefreshLiveCreatures(IReadOnlySet<string> affectedGroups)
    {
        var errors = new List<string>();
        var room = NCombatRoom.Instance;
        if (room == null)
        {
            return errors;
        }

        var creatures = room.CreatureNodes.ToArray();
        var rebuiltPlayerOrPet = false;
        var affectedPlayers = creatures
            .Where(creature => creature.Entity.Player != null)
            .Where(creature =>
            {
                var character = creature.Entity.Player!.Character;
                var group = ContextualSkinControls.FindGroup(
                    character.Id.Entry,
                    character.GetType().Name);
                return group != null && affectedGroups.Contains(group.Id);
            })
            .Select(creature => creature.Entity.Player!)
            .ToHashSet();

        foreach (var creature in creatures)
        {
            var modelId = creature.Entity.ModelId.Entry;
            var typeName = creature.Entity.Player?.Character.GetType().Name ??
                           creature.Entity.Monster?.GetType().Name;
            var group = ContextualSkinControls.FindGroup(modelId, typeName);
            var directlyAffected = group != null && affectedGroups.Contains(group.Id);
            var ownerAffected = creature.Entity.PetOwner is { } owner &&
                                affectedPlayers.Contains(owner);
            if (!directlyAffected && !ownerAffected)
            {
                continue;
            }

            if (creature.IsPlayingDeathAnimation)
            {
                errors.Add(modelId + ": death animation");
                continue;
            }

            if (!TryRebuildCreatureVisuals(creature, out var error))
            {
                errors.Add(modelId + ": " + error);
            }
            else if (creature.Entity.IsPlayer || creature.Entity.PetOwner != null)
            {
                rebuiltPlayerOrPet = true;
            }
        }

        if (rebuiltPlayerOrPet)
        {
            RefreshPlayerAndPetLayout(room);
        }

        return errors;
    }

    internal static void CapturePlayerAndPetLayout(float scaling, bool fullyCenterPlayers)
    {
        var room = NCombatRoom.Instance;
        if (room == null)
        {
            return;
        }

        _playerLayoutRoom = new WeakReference<NCombatRoom>(room);
        _playerLayoutScaling = scaling;
        _fullyCenterPlayers = fullyCenterPlayers;
    }

    private static void RefreshPlayerAndPetLayout(NCombatRoom room)
    {
        try
        {
            if (_playerLayoutRoom == null ||
                !_playerLayoutRoom.TryGetTarget(out var capturedRoom) ||
                !ReferenceEquals(capturedRoom, room))
            {
                return;
            }

            var playersAndPets = room.CreatureNodes
                .Where(creature => creature.Entity.IsPlayer || creature.Entity.PetOwner != null)
                .ToList();
            if (playersAndPets.Count == 0)
            {
                return;
            }

            // Re-run the game's canonical layout first. This removes position offsets left by the
            // previously selected provider; Harmony then naturally runs only the newly selected
            // provider's current positioning postfixes (and keeps player/pet offsets coherent).
            NCombatRoom.PositionPlayersAndPets(
                playersAndPets,
                _playerLayoutScaling,
                _fullyCenterPlayers);
            foreach (var creature in playersAndPets)
            {
                RefreshCreatureAnchors(creature);
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("刷新实战玩家与宠物位置失败：" + exception.GetBaseException().Message);
        }
    }

    private static bool TryRebuildCreatureVisuals(NCreature creature, out string? error)
    {
        error = null;
        if (VisualsField == null || SpineAnimatorField == null)
        {
            error = "game visual fields unavailable";
            return false;
        }

        var oldVisuals = creature.Visuals;
        var desiredVisualName = oldVisuals.Name;
        var oldAnimator = SpineAnimatorField.GetValue(creature);
        var parent = oldVisuals.GetParent();
        NCreatureVisuals? newVisuals = null;
        var movedFormChildren = new List<Node>();
        try
        {
            newVisuals = creature.Entity.CreateVisuals() ??
                         throw new InvalidOperationException("CreateVisuals returned null");
            newVisuals.Name = oldVisuals.Name;
            newVisuals.Position = Vector2.Zero;
            newVisuals.Visible = oldVisuals.Visible;
            newVisuals.Modulate = oldVisuals.Modulate;
            newVisuals.SelfModulate = oldVisuals.SelfModulate;
            newVisuals.ZIndex = oldVisuals.ZIndex;
            newVisuals.ZAsRelative = oldVisuals.ZAsRelative;

            var oldBaseScale = oldVisuals.GetMeta(BaseVisualScaleMeta, oldVisuals.Scale).AsVector2();
            var oldBaseDefaultScale = oldVisuals
                .GetMeta(BaseDefaultScaleMeta, oldVisuals.DefaultScale)
                .AsSingle();
            var preserveRuntimeVisualScale =
                !ContextualSkinControls.IsMonsterScaleManaged(oldVisuals);
            var runtimeScale = preserveRuntimeVisualScale
                ? SafeDivide(oldVisuals.Scale, oldBaseScale)
                : Vector2.One;
            var runtimeDefaultScale = preserveRuntimeVisualScale &&
                                      !Mathf.IsZeroApprox(oldBaseDefaultScale)
                ? oldVisuals.DefaultScale / oldBaseDefaultScale
                : 1f;

            parent.AddChild(newVisuals);
            parent.MoveChild(newVisuals, oldVisuals.GetIndex());

            CreatureAnimator? newAnimator = null;
            if (newVisuals.HasSpineAnimation)
            {
                newAnimator = creature.Entity.Player != null
                    ? GenerateCharacterAnimator(
                        creature.Entity.Player.Character,
                        newVisuals.SpineBody!,
                        creature.Entity)
                    : creature.Entity.Monster!.GenerateAnimator(newVisuals.SpineBody!);
                if (creature.Entity.Monster != null)
                {
                    newVisuals.SetUpSkin(creature.Entity.Monster);
                }
            }

            VisualsField.SetValue(creature, newVisuals);
            SpineAnimatorField.SetValue(creature, newAnimator);
            ConnectSpineAnimatorSignalsMethod?.Invoke(creature, null);
            UpdatePhobiaModeMethod?.Invoke(creature, null);
            ReplaySelectedCreatureReady(creature);
            CaptureVisualBaseline(newVisuals);
            var newBaseScale = newVisuals.GetMeta(BaseVisualScaleMeta, newVisuals.Scale).AsVector2();
            var newBaseDefaultScale = newVisuals
                .GetMeta(BaseDefaultScaleMeta, newVisuals.DefaultScale)
                .AsSingle();
            newVisuals.Scale = newBaseScale * runtimeScale;
            newVisuals.DefaultScale = newBaseDefaultScale * runtimeDefaultScale;
            MigrateFormVfx(oldVisuals, newVisuals, movedFormChildren);
            ApplyStoredTransform(creature);
            if (creature.Entity.IsPlayer)
            {
                RebuildCurrentOrbVisuals(creature);
            }

            oldVisuals.GetParent()?.RemoveChild(oldVisuals);
            newVisuals.Name = desiredVisualName;
            oldVisuals.QueueFree();
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                VisualsField.SetValue(creature, oldVisuals);
                SpineAnimatorField.SetValue(creature, oldAnimator);
                foreach (var child in movedFormChildren.Where(GodotObject.IsInstanceValid))
                {
                    var oldHolder = oldVisuals.GetNodeOrNull<Control>("%FormVfx");
                    if (oldHolder != null)
                    {
                        child.Reparent(oldHolder, keepGlobalTransform: true);
                    }
                }

                ConnectSpineAnimatorSignalsMethod?.Invoke(creature, null);
                RefreshCreatureAnchors(creature);
                if (newVisuals != null && GodotObject.IsInstanceValid(newVisuals))
                {
                    newVisuals.GetParent()?.RemoveChild(newVisuals);
                    newVisuals.QueueFree();
                }
            }
            catch (Exception rollbackException)
            {
                ModLog.Error("回滚实战角色外观失败：" + rollbackException);
            }

            error = exception.GetBaseException().Message;
            ModLog.Error($"重建 {creature.Entity.ModelId.Entry} 的实战外观失败：{exception}");
            return false;
        }
    }

    private static Node2D? EnsureTransformWrapper(NCreature creature)
    {
        if (!GodotObject.IsInstanceValid(creature.Visuals))
        {
            return null;
        }

        var wrapper = GetTransformWrapper(creature);
        if (wrapper == null)
        {
            wrapper = new Node2D { Name = TransformWrapperName };
            var visualIndex = creature.Visuals.GetIndex();
            creature.AddChild(wrapper);
            creature.MoveChild(wrapper, Math.Max(0, visualIndex));
        }

        if (!ReferenceEquals(creature.Visuals.GetParent(), wrapper))
        {
            creature.Visuals.Reparent(wrapper, keepGlobalTransform: false);
        }

        return wrapper;
    }

    private static Node2D? EnsureHealthDisplayWrapper(NCreature creature)
    {
        var stateDisplay = GetStateDisplay(creature);
        if (stateDisplay == null || !GodotObject.IsInstanceValid(stateDisplay))
        {
            return null;
        }

        var wrapper = GetHealthDisplayWrapper(creature);
        if (wrapper == null)
        {
            wrapper = new Node2D { Name = HealthDisplayWrapperName };
            var displayIndex = stateDisplay.GetIndex();
            creature.AddChild(wrapper);
            creature.MoveChild(wrapper, Math.Max(0, displayIndex));
        }

        if (!ReferenceEquals(stateDisplay.GetParent(), wrapper))
        {
            stateDisplay.Reparent(wrapper, keepGlobalTransform: false);
        }

        return wrapper;
    }

    private static Node2D? EnsureIntentDisplayWrapper(NCreature creature)
    {
        var intentContainer = creature.IntentContainer;
        if (intentContainer == null || !GodotObject.IsInstanceValid(intentContainer))
        {
            return null;
        }

        var wrapper = GetIntentDisplayWrapper(creature);
        if (wrapper == null)
        {
            wrapper = new Node2D { Name = IntentDisplayWrapperName };
            var intentIndex = intentContainer.GetIndex();
            creature.AddChild(wrapper);
            creature.MoveChild(wrapper, Math.Max(0, intentIndex));
        }

        if (!ReferenceEquals(intentContainer.GetParent(), wrapper))
        {
            intentContainer.Reparent(wrapper, keepGlobalTransform: false);
        }

        return wrapper;
    }

    private static Control? EnsureHealthBoundsProxy(NCreature creature)
    {
        var proxy = creature.GetNodeOrNull<Control>(HealthBoundsProxyName);
        if (proxy != null)
        {
            return proxy;
        }

        proxy = new Control
        {
            Name = HealthBoundsProxyName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false
        };
        creature.AddChild(proxy);
        return proxy;
    }

    private static void SetCurrentTransform(Node2D wrapper, CharacterCombatTransform value)
    {
        wrapper.SetMeta(CurrentScaleMeta, value.Scale);
        wrapper.SetMeta(CurrentOffsetXMeta, value.OffsetX);
        wrapper.SetMeta(CurrentOffsetYMeta, value.OffsetY);
        wrapper.SetMeta(CurrentHealthScaleMeta, value.HealthBarScale);
        wrapper.SetMeta(CurrentHealthOffsetXMeta, value.HealthBarOffsetX);
        wrapper.SetMeta(CurrentHealthOffsetYMeta, value.HealthBarOffsetY);
        wrapper.SetMeta(CurrentHealthFollowScaleMeta, value.HealthBarFollowsModelScale);
        wrapper.SetMeta(CurrentHealthFollowMovementMeta, value.HealthBarFollowsModelMovement);
        wrapper.SetMeta(CurrentIntentScaleMeta, value.IntentScale);
        wrapper.SetMeta(CurrentIntentOffsetXMeta, value.IntentOffsetX);
        wrapper.SetMeta(CurrentIntentOffsetYMeta, value.IntentOffsetY);
        wrapper.SetMeta(CurrentIntentFollowScaleMeta, value.IntentFollowsModelScale);
        wrapper.SetMeta(CurrentIntentFollowMovementMeta, value.IntentFollowsModelMovement);
        wrapper.SetMeta(CurrentReticleScaleMeta, value.SelectionReticleScale);
        wrapper.SetMeta(CurrentReticleOffsetXMeta, value.SelectionReticleOffsetX);
        wrapper.SetMeta(CurrentReticleOffsetYMeta, value.SelectionReticleOffsetY);
        wrapper.SetMeta(CurrentReticleFollowScaleMeta, value.SelectionReticleFollowsModelScale);
        wrapper.SetMeta(CurrentReticleFollowMovementMeta, value.SelectionReticleFollowsModelMovement);
    }

    private static CharacterCombatTransform GetCurrentTransform(Node2D wrapper) =>
        new CharacterCombatTransform(
            wrapper.GetMeta(CurrentScaleMeta, 1f).AsSingle(),
            wrapper.GetMeta(CurrentOffsetXMeta, 0f).AsSingle(),
            wrapper.GetMeta(CurrentOffsetYMeta, 0f).AsSingle())
        {
            HealthBarScale = wrapper.GetMeta(CurrentHealthScaleMeta, 1f).AsSingle(),
            HealthBarOffsetX = wrapper.GetMeta(CurrentHealthOffsetXMeta, 0f).AsSingle(),
            HealthBarOffsetY = wrapper.GetMeta(CurrentHealthOffsetYMeta, 0f).AsSingle(),
            HealthBarFollowsModelScale =
                wrapper.GetMeta(CurrentHealthFollowScaleMeta, false).AsBool(),
            HealthBarFollowsModelMovement =
                wrapper.GetMeta(CurrentHealthFollowMovementMeta, true).AsBool(),
            IntentScale = wrapper.GetMeta(CurrentIntentScaleMeta, 1f).AsSingle(),
            IntentOffsetX = wrapper.GetMeta(CurrentIntentOffsetXMeta, 0f).AsSingle(),
            IntentOffsetY = wrapper.GetMeta(CurrentIntentOffsetYMeta, 0f).AsSingle(),
            IntentFollowsModelScale =
                wrapper.GetMeta(CurrentIntentFollowScaleMeta, false).AsBool(),
            IntentFollowsModelMovement =
                wrapper.GetMeta(CurrentIntentFollowMovementMeta, true).AsBool(),
            SelectionReticleScale = wrapper.GetMeta(CurrentReticleScaleMeta, 1f).AsSingle(),
            SelectionReticleOffsetX = wrapper.GetMeta(CurrentReticleOffsetXMeta, 0f).AsSingle(),
            SelectionReticleOffsetY = wrapper.GetMeta(CurrentReticleOffsetYMeta, 0f).AsSingle(),
            SelectionReticleFollowsModelScale =
                wrapper.GetMeta(CurrentReticleFollowScaleMeta, true).AsBool(),
            SelectionReticleFollowsModelMovement =
                wrapper.GetMeta(CurrentReticleFollowMovementMeta, true).AsBool()
        };

    private static void ApplyHealthDisplayTransform(
        Node2D wrapper,
        CharacterCombatTransform value)
    {
        var movement = value.HealthBarFollowsModelMovement
            ? new Vector2(value.OffsetX, value.OffsetY)
            : Vector2.Zero;
        wrapper.Position = movement +
                           new Vector2(value.HealthBarOffsetX, value.HealthBarOffsetY);
        var scale = value.HealthBarScale *
                    (value.HealthBarFollowsModelScale ? value.Scale : 1f);
        wrapper.Scale = Vector2.One * scale;
    }

    private static void ApplyIntentDisplayTransform(
        Node2D wrapper,
        CharacterCombatTransform value,
        Vector2 baseMarkerPosition)
    {
        var followedScale = value.IntentFollowsModelScale ? value.Scale : 1f;
        var movement = value.IntentFollowsModelMovement
            ? new Vector2(value.OffsetX, value.OffsetY)
            : Vector2.Zero;
        wrapper.Position = baseMarkerPosition * followedScale +
                           movement +
                           new Vector2(value.IntentOffsetX, value.IntentOffsetY);
        wrapper.Scale = Vector2.One * value.IntentScale * followedScale;
    }

    private static void ApplySelectionReticleAndHitbox(
        NSelectionReticle reticle,
        Control hitbox,
        CharacterCombatTransform value,
        Vector2 basePosition,
        Vector2 baseSize)
    {
        var followedScale = value.SelectionReticleFollowsModelScale ? value.Scale : 1f;
        var movement = value.SelectionReticleFollowsModelMovement
            ? new Vector2(value.OffsetX, value.OffsetY)
            : Vector2.Zero;
        var position = basePosition * followedScale +
                       movement +
                       new Vector2(
                           value.SelectionReticleOffsetX,
                           value.SelectionReticleOffsetY);
        var size = baseSize * value.SelectionReticleScale * followedScale;

        // Keep the game's original reticle hierarchy and animation state intact. Only its rectangle,
        // and the hitbox that drives mouse targeting, are customized.
        reticle.Position = position;
        reticle.Size = size;
        reticle.PivotOffset = size * 0.5f;
        hitbox.Position = position;
        hitbox.Size = size;
        hitbox.PivotOffset = size * 0.5f;
    }

    private static void CaptureVisualBaseline(NCreatureVisuals visuals)
    {
        if (!visuals.HasMeta(BaseVisualScaleMeta))
        {
            visuals.SetMeta(BaseVisualScaleMeta, visuals.Scale);
        }

        if (!visuals.HasMeta(BaseDefaultScaleMeta))
        {
            visuals.SetMeta(BaseDefaultScaleMeta, visuals.DefaultScale);
        }
    }

    private static void RefreshCreatureAnchors(NCreature creature)
    {
        UpdateBoundsMethod?.Invoke(creature, [creature.Visuals]);
        SetOrbManagerPositionMethod?.Invoke(creature, null);
    }

    private static void ReplaySelectedCreatureReady(NCreature creature)
    {
        var providerIds = new List<string>();
        var modelId = creature.Entity.ModelId.Entry;
        var typeName = creature.Entity.Player?.Character.GetType().Name ??
                       creature.Entity.Monster?.GetType().Name;
        var group = ContextualSkinControls.FindGroup(modelId, typeName);
        if (group != null)
        {
            AddSelectedFullRuntimeProvider(providerIds, group.Id);
        }

        // A character skin can own the complete presentation of its pets as well as the player model.
        // Replaying the owner's provider after the pet's own provider lets a suite skin deliberately
        // override that companion, while rebuilding the pet still clears the suite when switching away.
        if (creature.Entity.PetOwner is { } owner)
        {
            var ownerCharacter = owner.Character;
            var ownerGroup = ContextualSkinControls.FindGroup(
                ownerCharacter.Id.Entry,
                ownerCharacter.GetType().Name);
            if (ownerGroup != null)
            {
                AddSelectedFullRuntimeProvider(providerIds, ownerGroup.Id);
            }
        }

        foreach (var providerId in providerIds)
        {
            ManagedSkinModLoader.ReplaySelectedCreatureReady(providerId, creature);
        }
    }

    private static void AddSelectedFullRuntimeProvider(ICollection<string> providerIds, string groupId)
    {
        var providerId = SkinService.GetSelectedFullRuntimeProvider(groupId);
        if (providerId != null && !providerIds.Contains(providerId, StringComparer.OrdinalIgnoreCase))
        {
            providerIds.Add(providerId);
        }
    }

    private static void RebuildCurrentOrbVisuals(NCreature creature)
    {
        try
        {
            if (creature.OrbManager == null ||
                OrbNodesField?.GetValue(creature.OrbManager) is not IEnumerable<NOrb> orbs)
            {
                return;
            }

            foreach (var orb in orbs.ToArray())
            {
                if (GodotObject.IsInstanceValid(orb) && orb.Model != null)
                {
                    orb.ReplaceOrb(orb.Model);
                }
            }

            creature.OrbManager.UpdateVisuals(OrbEvokeType.None);
        }
        catch (Exception exception)
        {
            ModLog.Warn("刷新角色充能球外观失败：" + exception.GetBaseException().Message);
        }
    }

    private static void MigrateFormVfx(
        NCreatureVisuals oldVisuals,
        NCreatureVisuals newVisuals,
        ICollection<Node> movedChildren)
    {
        var oldHolder = oldVisuals.GetNodeOrNull<Control>("%FormVfx");
        var newHolder = newVisuals.GetNodeOrNull<Control>("%FormVfx");
        if (oldHolder == null || newHolder == null)
        {
            return;
        }

        foreach (var child in oldHolder.GetChildren())
        {
            movedChildren.Add(child);
            child.Reparent(newHolder, keepGlobalTransform: true);
        }
    }

    private static Vector2 SafeDivide(Vector2 value, Vector2 divisor) =>
        new(
            Mathf.IsZeroApprox(divisor.X) ? 1f : value.X / divisor.X,
            Mathf.IsZeroApprox(divisor.Y) ? 1f : value.Y / divisor.Y);

    private static CreatureAnimator GenerateCharacterAnimator(
        object character,
        object spineBody,
        object creature)
    {
        var methods = character.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == "GenerateAnimator")
            .OrderByDescending(method => method.GetParameters().Length)
            .ToArray();
        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            object?[] arguments = parameters.Length switch
            {
                2 => [spineBody, creature],
                1 => [spineBody],
                _ => []
            };
            if (arguments.Length == 0)
            {
                continue;
            }

            if (method.Invoke(character, arguments) is CreatureAnimator animator)
            {
                return animator;
            }
        }

        throw new MissingMethodException(character.GetType().FullName, "GenerateAnimator");
    }

    private static void RefreshCurrentCharacterUi()
    {
        var player = GetLocalPlayer();
        var run = NRun.Instance;
        if (player == null || run == null)
        {
            return;
        }

        try
        {
            var portrait = run.GlobalUi.TopBar.Portrait;
            foreach (var child in portrait.GetChildren())
            {
                portrait.RemoveChild(child);
                child.QueueFree();
            }

            portrait.AddChild(player.Character.Icon);
        }
        catch (Exception exception)
        {
            ModLog.Warn("刷新实战左上角角色头像失败：" + exception.GetBaseException().Message);
        }

        try
        {
            if (MapMarkerField?.GetValue(NMapScreen.Instance) is TextureRect marker)
            {
                marker.Texture = player.Character.MapMarker;
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("刷新地图角色标记失败：" + exception.GetBaseException().Message);
        }

        RefreshCurrentEnergyCounter(player);
    }

    private static void RefreshCurrentEnergyCounter(Player player)
    {
        var combatUi = NCombatRoom.Instance?.Ui;
        if (combatUi == null ||
            EnergyCounterField?.GetValue(combatUi) is not NEnergyCounter oldCounter)
        {
            return;
        }

        NEnergyCounter? newCounter = null;
        NStarCounter? starCounter = null;
        var oldIndex = oldCounter.GetIndex();
        var oldDetached = false;
        try
        {
            newCounter = NEnergyCounter.Create(player);
            if (newCounter == null)
            {
                return;
            }

            var desiredName = oldCounter.Name;
            newCounter.Name = desiredName;
            newCounter.Visible = oldCounter.Visible;
            newCounter.Modulate = oldCounter.Modulate;
            newCounter.SelfModulate = oldCounter.SelfModulate;
            combatUi.EnergyCounterContainer.AddChild(newCounter);
            combatUi.EnergyCounterContainer.MoveChild(newCounter, oldIndex);
            if (StarCounterField?.GetValue(combatUi) is NStarCounter currentStarCounter)
            {
                starCounter = currentStarCounter;
                starCounter.Reparent(newCounter, keepGlobalTransform: true);
            }

            EnergyCounterField.SetValue(combatUi, newCounter);
            oldCounter.GetParent()?.RemoveChild(oldCounter);
            oldDetached = true;
            newCounter.Name = desiredName;
            oldCounter.QueueFree();
        }
        catch (Exception exception)
        {
            EnergyCounterField.SetValue(combatUi, oldCounter);
            if (starCounter != null &&
                GodotObject.IsInstanceValid(starCounter) &&
                GodotObject.IsInstanceValid(oldCounter))
            {
                starCounter.Reparent(oldCounter, keepGlobalTransform: true);
            }

            if (oldDetached && GodotObject.IsInstanceValid(oldCounter))
            {
                combatUi.EnergyCounterContainer.AddChild(oldCounter);
                combatUi.EnergyCounterContainer.MoveChild(oldCounter, oldIndex);
            }

            if (newCounter != null && GodotObject.IsInstanceValid(newCounter))
            {
                newCounter.GetParent()?.RemoveChild(newCounter);
                newCounter.QueueFree();
            }

            ModLog.Warn("刷新实战能量计数器失败：" + exception.GetBaseException().Message);
        }
    }

    private sealed record PendingSelection(string GroupId, string OptionId);
}

internal partial class CharacterAppearanceRuntimeNode : Node
{
    public override void _Ready() => SetProcess(false);

    public void Wake() => SetProcess(true);

    public override void _Process(double delta)
    {
        if (!CharacterAppearanceRuntime.ProcessPendingSelection())
        {
            SetProcess(false);
        }
    }

    public override void _ExitTree() =>
        CharacterAppearanceRuntime.ClearPendingSelection();
}

[HarmonyPatch(typeof(NRun), nameof(NRun._Ready))]
internal static class InRunCharacterAppearanceRuntimePatch
{
    private static void Postfix(NRun __instance)
    {
        CharacterAppearanceRuntime.FocusRuntimeProviderBehaviorsOnRunCharacters();
        if (__instance.GetNodeOrNull<CharacterAppearanceRuntimeNode>("SkinChangerAppearanceRuntime") != null)
        {
            return;
        }

        __instance.AddChild(new CharacterAppearanceRuntimeNode
        {
            Name = "SkinChangerAppearanceRuntime",
            ProcessMode = Node.ProcessModeEnum.Always
        });
    }
}

[HarmonyPatch(typeof(NCreature), nameof(NCreature._Ready))]
internal static class CharacterAppearanceCreatureReadyPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCreature __instance) =>
        CharacterAppearanceRuntime.OnCreatureReady(__instance);
}

[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.PositionPlayersAndPets))]
internal static class CharacterAppearancePlayerLayoutPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(float scaling, bool fullyCenterPlayers) =>
        CharacterAppearanceRuntime.CapturePlayerAndPetLayout(scaling, fullyCenterPlayers);
}

[HarmonyPatch]
internal static class CharacterAppearanceBoundsPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(NCreature), "UpdateBounds", [typeof(Node)]);

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCreature __instance, Node boundsContainer) =>
        CharacterAppearanceRuntime.CorrectBoundsForVisualTransforms(__instance, boundsContainer);
}

[HarmonyPatch]
internal static class CharacterAppearanceOrbPositionPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(NCreature), "SetOrbManagerPosition");

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCreature __instance) =>
        CharacterAppearanceRuntime.CorrectOrbPositionForCharacterTransform(__instance);
}
