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

    public static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var preview = assembly.GetType("STS2SkinChanger.Core.FrameworkModelPreview")
            ?? throw new InvalidOperationException("小模型预览仍未接入按选择隔离的模型创建流程。");
        var create = AccessTools.Method(preview, "CreateVisuals");
        var animation = AccessTools.Method(preview, "ResolveAnimations");
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
        _visuals = (NCreatureVisuals)RuntimeHelpers.GetUninitializedObject(typeof(NCreatureVisuals));
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
            harmony.Patch(AccessTools.Method(typeof(CharacterModel), "CreateVisuals"),
                prefix: new HarmonyMethod(typeof(FrameworkModelPreviewTests), nameof(OriginalFactory)));
            _managed = true; _configured = false; _originalCalls = 0;
            var result = create.Invoke(null, [_character, "ironclad", "res://scenes/creature_visuals/ironclad.tscn"]);
            Require(ReferenceEquals(result, _visuals) && _configured && _originalCalls == 0,
                "原皮及受管理场景必须先使用隔离工厂并执行所选后处理，不能先进入会抛类型转换异常的共享缓存工厂。");
            _managed = false; _configured = false;
            result = create.Invoke(null, [_character, "ironclad", "res://scenes/creature_visuals/ironclad.tscn"]);
            Require(ReferenceEquals(result, _visuals) && !_configured && _originalCalls == 1,
                "纯运行时模型仍需原工厂，不能丢掉它的构造回调或重复应用后处理。");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        Console.WriteLine("Framework model preview passed: managed-before-cache, selected finishing, runtime fallback and animation names.");
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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
