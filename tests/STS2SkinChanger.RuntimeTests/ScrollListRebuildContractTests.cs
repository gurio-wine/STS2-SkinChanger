using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using STS2SkinChanger;

internal static class ScrollListRebuildContractTests
{
    public static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var builders = new[]
        {
            ("CardSkinControls", "BuildPriorityOverlay"),
            ("ContextualSkinControls", "BuildMonsterPriorityOverlay"),
            ("CharacterSkinCompositionControls", "BuildEditor"),
            ("CardSkinControls", "BuildPresetOverlay"),
            ("ContextualSkinControls", "BuildMonsterPresetOverlay")
        };
        foreach (var (typeName, methodName) in builders)
        {
            var type = assembly.GetType("STS2SkinChanger.Ui." + typeName, true)!;
            var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)!;
            var instructions = PatchProcessor.GetOriginalInstructions(method);
            if (instructions.Any(instruction => instruction.opcode == OpCodes.Newobj &&
                    instruction.operand is ConstructorInfo { DeclaringType: var constructedType } &&
                    constructedType == typeof(ScrollContainer)))
            {
                throw new InvalidOperationException($"{typeName}.{methodName} 重建了滚动容器，会丢失当前滚动位置。");
            }
            foreach (var expectedCall in new[] { "Begin", "PlaceAfterHeader" })
            {
                if (!instructions.Any(instruction => instruction.operand is MethodInfo called &&
                        called.DeclaringType?.FullName == "STS2SkinChanger.Ui.ScrollListRebuild" &&
                        called.Name == expectedCall))
                {
                    throw new InvalidOperationException($"{typeName}.{methodName} 未接入列表滚动保留：{expectedCall}。");
                }
            }
        }

        var bundleControls = assembly.GetType("STS2SkinChanger.Ui.CharacterSkinBundleControls", true)!;
        var createState = bundleControls.GetMethod("CreateState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var buildEditor = bundleControls.GetMethod("BuildEditor", BindingFlags.Static | BindingFlags.NonPublic)!;
        Require(PatchProcessor.GetOriginalInstructions(createState).Any(instruction =>
                instruction.opcode == OpCodes.Newobj &&
                instruction.operand is ConstructorInfo { DeclaringType: var type } &&
                type == typeof(ScrollContainer)),
            "皮肤包管理器必须在创建界面时建立固定视口滚动容器，不能在第二次打开时重建丢失。");
        Require(PatchProcessor.GetOriginalInstructions(buildEditor).All(instruction =>
                instruction.operand is not MethodInfo called ||
                called.DeclaringType?.FullName != "STS2SkinChanger.Ui.ScrollListRebuild"),
            "皮肤包管理器应只重建固定滚动视口内的内容。");
        Console.WriteLine("Scroll list rebuild contracts passed: priorities, compositions, presets and bundles.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
