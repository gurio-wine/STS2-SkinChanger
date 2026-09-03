using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using STS2SkinChanger;

internal static class CharacterSkinPopupContractTests
{
    public static void Run()
    {
        var controls = typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.ContextualSkinControls", true)!;
        var configure = controls.GetMethod("ConfigureCharacterBundlePopupList", BindingFlags.Static | BindingFlags.NonPublic)!;
        var instructions = PatchProcessor.GetOriginalInstructions(configure);
        var subscribeIndex = instructions.FindIndex(instruction =>
            instruction.operand is MethodInfo { Name: "add_AboutToPopup" });
        Require(subscribeIndex >= 0, "皮肤包文字显示层必须在每次打开下拉列表时刷新。");
        var openHandler = instructions.Take(subscribeIndex).Last(instruction =>
            instruction.opcode == OpCodes.Ldftn).operand as MethodInfo;
        Require(openHandler != null && PatchProcessor.GetOriginalInstructions(openHandler).Any(instruction =>
                instruction.operand is MethodInfo { Name: "RefreshCharacterBundlePopupList" }),
            "打开列表不能无条件显示旧的皮肤包文字层；必须重新读取当前角色的选项并决定是否显示。");

        var refresh = controls.GetMethod("RefreshCharacterBundlePopupList", BindingFlags.Static | BindingFlags.NonPublic)!;
        var refreshInstructions = PatchProcessor.GetOriginalInstructions(refresh);
        Require(refreshInstructions.Any(instruction => instruction.operand is MethodInfo called &&
                    called.DeclaringType == typeof(Godot.ItemList) && called.Name == "Clear") &&
                refreshInstructions.Any(instruction => instruction.operand is MethodInfo called &&
                    called.DeclaringType == typeof(Godot.OptionButton) && called.Name == "GetItemText") &&
                refreshInstructions.Any(instruction => instruction.operand is MethodInfo { Name: "GetItemMetadata" }),
            "每次刷新必须清除旧文字，从当前下拉选项同时读取名称与皮肤包标记，不能复用上一角色的缓存。");

        // Follow the actual emitted handlers as well as their registration methods. Neither the
        // native PopupMenu nor its colored ItemList overlay may initiate a temporary skin load.
        var setup = controls.GetMethod("EnsureCharacterSelector", BindingFlags.Static | BindingFlags.NonPublic)!;
        var visited = new HashSet<MethodInfo>();
        var pending = new Stack<MethodInfo>([setup, configure]);
        while (pending.TryPop(out var method))
        {
            if (!visited.Add(method)) continue;
            foreach (var instruction in PatchProcessor.GetOriginalInstructions(method))
            {
                if (instruction.operand is not MethodInfo called) continue;
                Require(called.Name != "ApplyCharacterPreviewSelection",
                    "角色选择器的鼠标悬浮或键盘焦点仍能触发临时皮肤加载，必须只在确认选择后加载。");
                if (called.DeclaringType == controls || called.DeclaringType?.DeclaringType == controls)
                {
                    pending.Push(called);
                }
            }
        }
        Console.WriteLine("Character skin popup contracts passed: current option names, bundle overlay refresh, no hover loading.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
