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
            ("ContextualSkinControls", "BuildMonsterPresetOverlay"),
            ("CharacterSkinBundleControls", "BuildEditor")
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
        Console.WriteLine("Scroll list rebuild contracts passed: priorities, compositions, presets and bundles.");
    }
}
