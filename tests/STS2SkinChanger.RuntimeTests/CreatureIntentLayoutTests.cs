using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2SkinChanger;

internal static class CreatureIntentLayoutTests
{
    private static readonly Type Runtime = typeof(Entry).Assembly.GetType(
        "STS2SkinChanger.Ui.CharacterAppearanceRuntime", true)!;
    private static readonly Type BoundsPatch = typeof(Entry).Assembly.GetType(
        "STS2SkinChanger.Ui.CharacterAppearanceBoundsPatch", true)!;
    private static readonly MethodInfo UpdateBounds = AccessTools.Method(
        typeof(NCreature), "UpdateBounds", [typeof(Node)]);
    private static float _intentY;
    private static float _correctedY;
    private static readonly List<string> Calls = [];

    public static void Run()
    {
        var failures = new List<string>();
        foreach (var test in new Action[] { CheckCurrentStanceLookup, CheckProviderGuardOrder })
        {
            try { test(); }
            catch (Exception error) { failures.Add(error.GetBaseException().Message); }
        }
        if (failures.Count != 0)
            throw new InvalidOperationException(string.Join(System.Environment.NewLine, failures));
        Console.WriteLine("Creature intent layout passed: current stance anchors and provider visibility guards after SC transforms.");
    }

    private static void CheckCurrentStanceLookup()
    {
        // Check the actual path passed across the Godot lookup boundary. A '%' lookup
        // resolves the scene owner's unique root marker, not this stance's child.
        var correction = AccessTools.Method(Runtime, "CorrectBoundsForVisualTransforms");
        var paths = PatchProcessor.GetOriginalInstructions(correction)
            .Where(instruction => instruction.opcode == OpCodes.Ldstr)
            .Select(instruction => instruction.operand as string)
            .Where(path => path?.EndsWith("IntentPos", StringComparison.Ordinal) == true)
            .ToArray();
        Require(paths.Length == 1 && paths[0] == "IntentPos",
            "意图定位必须读取当前姿态的 IntentPos 子节点；%IntentPos 会把飞行/蓄能姿态恢复为默认位置。");

        // Both supported game assemblies use a direct child for intent, but retain the
        // unique Bounds lookup. Do not change the native hitbox contract along with it.
        var nativePaths = PatchProcessor.GetOriginalInstructions(UpdateBounds)
            .Where(instruction => instruction.opcode == OpCodes.Ldstr)
            .Select(instruction => instruction.operand as string).ToArray();
        Require(nativePaths.Contains("IntentPos") && nativePaths.Contains("%Bounds"),
            "游戏的姿态定位合同发生变化，需要重新核对意图与选框，不能静默沿用旧假设。");
    }

    private static void CheckProviderGuardOrder()
    {
        // Run real Harmony registration and dispatch on the real game target. Only
        // engine-dependent layout writes are substituted: the production callback,
        // its attributes and its call into the transform correction remain intact.
        // A normal-priority author guard models CZN's existing top-edge correction.
        var boundary = new Harmony("SkinChanger.Tests.Intent.EngineBoundary");
        var manager = new Harmony("SkinChanger.Tests.Intent.Manager");
        var author = new Harmony("SkinChanger.Tests.Intent.Author");
        try
        {
            boundary.Patch(UpdateBounds, prefix: new HarmonyMethod(
                typeof(CreatureIntentLayoutTests), nameof(SkipNativeLayout)));
            boundary.Patch(AccessTools.Method(Runtime, "CorrectBoundsForVisualTransforms"),
                prefix: new HarmonyMethod(typeof(CreatureIntentLayoutTests), nameof(CorrectEngineLayout)));
            foreach (var authorFirst in new[] { true, false })
            {
                if (authorFirst) RegisterAuthor(author);
                manager.CreateClassProcessor(BoundsPatch).Patch();
                if (!authorFirst) RegisterAuthor(author);
                foreach (var (position, expected) in new[] { (-900f, 80f), (220f, 220f), (-30f, 80f) })
                {
                    Calls.Clear();
                    _intentY = 0;
                    _correctedY = position;
                    UpdateBounds.Invoke(RuntimeHelpers.GetUninitializedObject(typeof(NCreature)), [null]);
                    Require(_intentY == expected && Calls.SequenceEqual(new[] { "transform", "guard" }),
                        $"作者的防越界修正被 SC 覆盖：注册顺序 authorFirst={authorFirst}，" +
                        $"输入={position}，结果={_intentY}，应为={expected}；执行={string.Join(",", Calls)}。");
                }
                author.UnpatchAll(author.Id);
                manager.UnpatchAll(manager.Id);
            }
        }
        finally
        {
            author.UnpatchAll(author.Id);
            manager.UnpatchAll(manager.Id);
            boundary.UnpatchAll(boundary.Id);
        }
    }

    private static void RegisterAuthor(Harmony author) => author.Patch(UpdateBounds,
        postfix: new HarmonyMethod(typeof(CreatureIntentLayoutTests), nameof(AuthorVisibilityGuard)));

    private static bool SkipNativeLayout() => false;

    private static bool CorrectEngineLayout()
    {
        Calls.Add("transform");
        _intentY = _correctedY;
        return false;
    }

    private static void AuthorVisibilityGuard()
    {
        Calls.Add("guard");
        _intentY = Math.Max(80f, _intentY);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
