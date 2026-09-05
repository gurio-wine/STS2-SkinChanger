using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2SkinChanger;

internal static class FrameworkModelPreviewTests
{
    private static bool _managed, _configured;
    private static int _originalCalls;
    private static NCreatureVisuals _visuals = null!;
    private static CharacterModel _character = null!;
    private static MegaCrit.Sts2.Core.Entities.Creatures.Creature _creature = null!;
    private static bool _creatureConfigured, _ownerAttached;

    public static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var preview = assembly.GetType("STS2SkinChanger.Core.FrameworkModelPreview")
            ?? throw new InvalidOperationException("小模型预览仍未接入按选择隔离的模型创建流程。");
        var create = AccessTools.Method(preview, "CreateVisuals");
        var animation = AccessTools.Method(preview, "ResolveAnimations");
        var fit = AccessTools.Method(preview, "FitBounds")
            ?? throw new InvalidOperationException("模型预览没有按实际模型边界适配预览区，仍统一乘固定比例。");
        foreach (var (bounds, area, expectedScale, expectedPosition) in new (Rect2, Rect2, float, Vector2)[]
        {
            (new(-20, -60, 40, 60), new(-100, -300, 200, 300), 5, new(0, 0)),
            (new(100, 200, 800, 400), new(10, 20, 200, 300), .25f, new(-15, 170)),
            (new(-10, -40, 20, 40), new(0, 0, 200, 300), 7.5f, new(100, 300))
        })
        {
            var fitted = ((float Scale, Vector2 Position)?)fit.Invoke(null, [bounds, area]);
            Require(fitted is { } f && Mathf.IsEqualApprox(f.Scale, expectedScale) &&
                    f.Position.IsEqualApprox(expectedPosition),
                "预览应保持宽高比、居中落地，并按模型真实尺寸决定缩放，而不是按提供者固定补偿。");
        }
        Require(fit.Invoke(null, [new Rect2(), new Rect2(0, 0, 200, 300)]) == null,
            "未就绪的空边界不能产生无穷缩放。");
        CheckVisibleBounds(preview);
        var areaResolver = AccessTools.Method(preview, "PreviewArea")
            ?? throw new InvalidOperationException("原管理器根 Control 尺寸为零，预览区必须取实际背景框而非根尺寸。");
        var nativeArea = (Rect2)areaResolver.Invoke(null,
            [new Rect2(-161, -337, 308, 429), new Rect2(-142, 10, 275.8f, 52.5f)])!;
        Require(nativeArea.IsEqualApprox(new Rect2(-149, -325, 284, 323)),
            "实际原管理器使用负坐标背景和缩放过的页脚，适配必须避开名称与箭头。");
        var pathResolver = AccessTools.Method(preview, "ResolveCombatSpinePath")
            ?? throw new InvalidOperationException("预览没有消费按角色分发的战斗骨骼资源。");
        var routes = typeof(PreviewPathFixture).GetMethods(BindingFlags.Public | BindingFlags.Static);
        Require((string?)pathResolver.Invoke(null, [routes, "Silent"]) == "res://private/silent.tres" &&
                pathResolver.Invoke(null, [routes, "Defect"]) == null,
            "预览只能使用当前角色的战斗骨骼，不能借用休息处/商店或另一角色的路径。");
        foreach (var (names, entry, idle) in new (string[], string?, string?)[]
        {
            (["entry", "idle_loop"], "entry", "idle_loop"),
            (["entry", "Idle"], "entry", "Idle"),
            (["standing", "attack"], null, "standing"),
            (["entry"], "entry", null),
            (["attack", "die", "low_health_loop"], null, null),
            ([], null, null)
        })
        {
            var plan = ((string? Entry, string? Idle))animation.Invoke(null, [names])!;
            Require(plan == (entry, idle), "预览只播放存在的登场/待机动作，不能强制 idle_loop 或把死亡/残血当待机。");
        }

        _character = (CharacterModel)RuntimeHelpers.GetUninitializedObject(typeof(Ironclad));
        AccessTools.Field(typeof(AbstractModel), "<Id>k__BackingField")
            .SetValue(_character, new ModelId("CHARACTER", "IRONCLAD"));
        var playerFactory = AccessTools.Method(preview, "CreatePreviewPlayer")
            ?? throw new InvalidOperationException("小预览没有独立角色归属，依赖 NCreature.Entity.Player 的皮肤初始化仍会跳过。");
        var constructorBoundary = new Harmony("tests.framework-preview-save-boundary");
        MegaCrit.Sts2.Core.Entities.Players.Player player;
        try
        {
            // The real Player constructor reads the native save account for ascension only.
            constructorBoundary.Patch(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Saves.SaveManager), "Instance"),
                prefix: new HarmonyMethod(typeof(FrameworkModelPreviewTests), nameof(NoSaveAccount)));
            player = (MegaCrit.Sts2.Core.Entities.Players.Player)playerFactory.Invoke(null, [_character, null])!;
        }
        finally { constructorBoundary.UnpatchAll(constructorBoundary.Id); }
        Require(ReferenceEquals(player.Character, _character) && ReferenceEquals(player.Creature.Player, player) &&
                player.Creature.CurrentHp == player.Creature.MaxHp && player.Creature.CurrentHp > 0 &&
                player.Deck.Cards.Count == 0 && player.Relics.Count == 0 && player.Creature.CombatState == null,
            "预览必须有正确满血的所属角色，但不能创建卡组、遗物或加入战斗。");
        _visuals = (NCreatureVisuals)RuntimeHelpers.GetUninitializedObject(typeof(NCreatureVisuals));
        _creature = player.Creature;
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var controls = assembly.GetType("STS2SkinChanger.Ui.ContextualSkinControls", true)!;
        var harmony = new Harmony("tests.framework-model-preview");
        try
        {
            // Only the engine-dependent factory and provider callbacks are replaced. Exercise
            // the actual preview router, including the configure callback and fallback branch.
            harmony.Patch(AccessTools.Method(service, "TryInstantiateSelectedCharacterCreatureVisuals"),
                prefix: new HarmonyMethod(typeof(FrameworkModelPreviewTests), nameof(ManagedFactory)));
            harmony.Patch(AccessTools.Method(controls, "ApplySelectedProviderVisualPostfix"),
                prefix: new HarmonyMethod(typeof(FrameworkModelPreviewTests), nameof(Configure)));
            harmony.Patch(AccessTools.Method(controls, "ApplySelectedCreatureVisualPostfix"),
                prefix: new HarmonyMethod(typeof(FrameworkModelPreviewTests), nameof(ConfigureCreature)));
            harmony.Patch(AccessTools.Method(typeof(CharacterModel), "CreateVisuals"),
                prefix: new HarmonyMethod(typeof(FrameworkModelPreviewTests), nameof(OriginalFactory)));
            _managed = true; _configured = false; _originalCalls = 0;
            var result = create.Invoke(null, [_character, "ironclad", "res://scenes/creature_visuals/ironclad.tscn", null]);
            Require(ReferenceEquals(result, _visuals) && _configured && _originalCalls == 0,
                "原皮及受管理场景必须先使用隔离工厂并执行所选后处理，不能先进入会抛类型转换异常的共享缓存工厂。");
            _managed = false; _configured = false;
            result = create.Invoke(null, [_character, "ironclad", "res://scenes/creature_visuals/ironclad.tscn", null]);
            Require(ReferenceEquals(result, _visuals) && !_configured && _originalCalls == 1,
                "纯运行时模型仍需原工厂，不能丢掉它的构造回调或重复应用后处理。");
            _managed = true; _configured = false; _creatureConfigured = false;
            result = create.Invoke(null, [_character, "ironclad", "res://scenes/creature_visuals/ironclad.tscn", _creature]);
            Require(ReferenceEquals(result, _visuals) && _configured && _creatureConfigured,
                "独立预览需要同时完成角色级和生物级所选皮肤处理，不能只处理角色资源。");
            harmony.Patch(AccessTools.Method(typeof(Node), nameof(Node.AddChild)),
                prefix: new HarmonyMethod(typeof(FrameworkModelPreviewTests), nameof(ObserveOwnerAttachment)));
            var ownerType = assembly.GetType("STS2SkinChanger.Core.FrameworkPreviewCreature", true)!;
            var owner = (NCreature)RuntimeHelpers.GetUninitializedObject(ownerType);
            _ownerAttached = false;
            AccessTools.Method(ownerType, "Initialize").Invoke(owner, [player, _visuals]);
            Require(_ownerAttached, "完整预览树必须把模型挂在 NCreature 所属节点下面。");
            // Preview lifecycle must not touch the game's native UI or combat subscriptions.
            owner._EnterTree();
            owner._Ready();
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        Console.WriteLine("Framework model preview passed: managed-before-cache, selected finishing, runtime fallback and animation names.");
    }

    private static bool ObserveOwnerAttachment(Node __instance, Node node)
    {
        Require(__instance is NCreature owner && ReferenceEquals(owner.Entity, _creature) &&
                ReferenceEquals(owner.Visuals, _visuals) && ReferenceEquals(node, _visuals) &&
                ReferenceEquals(owner.Entity.Player?.Character, _character),
            "作者的模型 Ready 触发前，父级就必须暴露正确的 Entity、Player 和最终 Visuals。");
        _ownerAttached = true;
        return false;
    }

    private static bool ConfigureCreature(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, ref NCreatureVisuals? visuals)
    {
        Require(_configured && ReferenceEquals(creature, _creature) && ReferenceEquals(visuals, _visuals),
            "生物级收尾必须沿用同一个模型和角色，且在角色级处理之后执行。");
        _creatureConfigured = true;
        return false;
    }

    private static void CheckVisibleBounds(Type preview)
    {
        var measure = AccessTools.Method(preview, "MeasureWithoutHiddenAttachments")
            ?? throw new InvalidOperationException("透明附件仍计入预览边界，人物会被不可见部件挤小。");
        var method = measure.MakeGenericMethod(typeof(int));
        var attached = new HashSet<int> { 1, 2, 3 };
        var boxes = new[] { new Rect2(), new Rect2(-100, -300, 200, 300),
            new Rect2(1000, -100, 500, 100), new Rect2(-800, -100, 500, 100) };
        Func<int, Action> exclude = index =>
        {
            attached.Remove(index);
            return () => attached.Add(index);
        };
        Func<Rect2> bounds = () => attached.Select(index => boxes[index]).Aggregate((a, b) => a.Merge(b));
        var result = (Rect2)method.Invoke(null, [new[] { 2, 3 }, exclude, bounds])!;
        Require(result.IsEqualApprox(new Rect2(-100, -300, 200, 300)) && attached.SetEquals([1, 2, 3]),
            "测量应排除透明部件，但测量结束必须恢复完整附件状态，不能改变动画。");
        try
        {
            method.Invoke(null, [new[] { 2, 3 }, exclude, (Func<Rect2>)(() => throw new InvalidOperationException("render unavailable"))]);
            throw new Exception("测量异常应保留给上层安全回退。");
        }
        catch (TargetInvocationException exception) when (exception.InnerException?.Message == "render unavailable") { }
        Require(attached.SetEquals([1, 2, 3]), "测量失败也不能遗留缺失部件。");
    }

    private static bool ManagedFactory(string groupId, string scenePath,
        Func<NCreatureVisuals, NCreatureVisuals>? configureVisuals, out NCreatureVisuals visuals, ref bool __result)
    {
        Require(groupId == "ironclad" && scenePath == "res://scenes/creature_visuals/ironclad.tscn",
            "预览必须使用当前角色的资源范围。");
        __result = _managed;
        visuals = _managed ? configureVisuals!(_visuals) : null!;
        return false;
    }

    private static bool Configure(string modelId, string? modelTypeName, object model, ref NCreatureVisuals visuals)
    {
        Require(modelId == "IRONCLAD" && modelTypeName == nameof(Ironclad) &&
                ReferenceEquals(model, _character) && ReferenceEquals(visuals, _visuals),
            "必须把刚创建的预览模型交给当前角色的后处理。");
        _configured = true;
        return false;
    }

    private static bool OriginalFactory(ref NCreatureVisuals __result)
    {
        _originalCalls++;
        if (_managed) throw new InvalidCastException("stale plain Node2D in shared cache");
        __result = _visuals;
        return false;
    }

    private static bool NoSaveAccount(ref MegaCrit.Sts2.Core.Saves.SaveManager __result)
    {
        __result = null!;
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static class PreviewPathFixture
    {
        public static string GetCombatSkinPath(string character) => character == "Silent" ? "res://private/silent.tres" : "";
        public static string GetRestSiteSkinPath(string character) => "res://rest/other.tres";
        public static string GetMerchantSkinPath(string character) => "res://merchant/other.tres";
    }
}
