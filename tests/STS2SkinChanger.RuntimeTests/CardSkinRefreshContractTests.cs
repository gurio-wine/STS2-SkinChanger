using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using STS2SkinChanger;

internal static class CardSkinRefreshContractTests
{
    public static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var controls = assembly.GetType("STS2SkinChanger.Ui.CardSkinControls", true)!;
        var refresh = controls.GetMethod("RefreshCardSkin", BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("卡牌换肤缺少完整刷新入口；只调用 Reload 会恢复不可打出牌的 -1 能耗图标。");
        var calls = Calls(refresh);
        var reloadIndex = Array.FindIndex(calls, call =>
            call.Name == "Invoke" && typeof(MethodBase).IsAssignableFrom(call.DeclaringType));
        var updateIndex = Array.FindIndex(calls, call =>
            call.DeclaringType == typeof(NCard) && call.Name == nameof(NCard.UpdateVisuals));
        Require(reloadIndex >= 0 && updateIndex > reloadIndex,
            "换肤必须先重载布局，再执行游戏 UpdateVisuals 恢复费用、描述和最终卡面。");
        Require(calls.Any(call => call.Name == "get_DisplayingPile"), "换肤不能丢失卡牌当前显示的牌堆上下文。");
        Require(calls.Any(call => call.Name == "GetRefreshPreviewMode"), "换肤不能把升级预览切回普通描述。");

        foreach (var (typeName, methodName) in new[]
        {
            ("CardSkinControls", "RefreshVisibleCards"),
            ("CardInspectSkinControls", "RestorePreview"),
            ("CardInspectSkinControls", "RefreshMatchingCards")
        })
        {
            var method = assembly.GetType("STS2SkinChanger.Ui." + typeName, true)!
                .GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)!;
            Require(Calls(method).Contains(refresh), $"{typeName}.{methodName} 未使用完整卡牌刷新。");
        }
        var previewClosureCalls = assembly.GetTypes()
            .Where(type => type.FullName?.StartsWith("STS2SkinChanger.Ui.CardInspectSkinControls+", StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            .Where(method => method.Name.Contains("<PreviewSelection>", StringComparison.Ordinal))
            .SelectMany(Calls);
        Require(previewClosureCalls.Contains(refresh), "悬浮单卡预览也必须使用完整刷新，且保留临时皮肤作用域。");

        var remember = controls.GetMethod("RememberPreviewMode", BindingFlags.Static | BindingFlags.Public)!;
        var stateType = controls.GetNestedType("CardPreviewState", BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(stateType, nonPublic: true)!;
        var resolve = stateType.GetMethod("GetMode")!;
        var modelField = stateType.GetField("Model")!;
        var modeField = stateType.GetField("Mode")!;
        // Identity-only models; no constructors, gameplay initialization or native Godot nodes.
        var firstModel = RuntimeHelpers.GetUninitializedObject(typeof(Wither));
        var nextModel = RuntimeHelpers.GetUninitializedObject(typeof(Wither));
        Require((CardPreviewMode)resolve.Invoke(state, [firstModel])! == CardPreviewMode.Normal,
            "未记录过预览的卡牌应使用普通显示。");
        modelField.SetValue(state, firstModel);
        modeField.SetValue(state, CardPreviewMode.Upgrade);
        Require((CardPreviewMode)resolve.Invoke(state, [firstModel])! == CardPreviewMode.Upgrade,
            "同一卡牌换肤必须保留升级预览。");
        Require((CardPreviewMode)resolve.Invoke(state, [nextModel])! == CardPreviewMode.Normal,
            "复用卡牌节点不能继承上一个模型的升级预览。");
        modelField.SetValue(state, nextModel);
        modeField.SetValue(state, CardPreviewMode.Normal);
        Require((CardPreviewMode)resolve.Invoke(state, [nextModel])! == CardPreviewMode.Normal,
            "关闭升级预览后不能恢复旧的预览模式。");

        var capturePatch = assembly.GetType("STS2SkinChanger.Ui.CardPreviewModeCapturePatch", true)!;
        Require(Calls(capturePatch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!).Contains(remember),
            "必须记录游戏实际调用的预览模式，而不是推测或固定为普通预览。");
        var nativeReload = AccessTools.Method(typeof(NCard), "Reload");
        var nativeUpdate = AccessTools.Method(typeof(NCard), nameof(NCard.UpdateVisuals));
        Require(!Calls(nativeReload).Any(call => call.Name == "UpdateEnergyCostVisuals") &&
                Calls(nativeUpdate).Any(call => call.Name == "UpdateEnergyCostVisuals"),
            "目标游戏版本的卡牌刷新职责发生变化，需要重新审查换肤顺序。");
        CheckRefreshDiagnostics(assembly, controls, refresh);
        Console.WriteLine("Card skin refresh contracts passed: library, presets, single-card selection, hover and pooled upgrade context.");
    }

    private static void CheckRefreshDiagnostics(Assembly assembly, Type controls, MethodInfo refresh)
    {
        var diagnostics = assembly.GetType("STS2SkinChanger.Ui.CardRefreshDiagnostics", true)!;
        var begin = diagnostics.GetMethod("Begin")!;
        var end = diagnostics.GetMethod("End")!;
        var record = diagnostics.GetMethod("Record")!;
        var library = Calls(controls.GetMethod("RefreshVisibleCards", BindingFlags.Static | BindingFlags.NonPublic)!);
        Require(Array.IndexOf(library, begin) >= 0 &&
                Array.IndexOf(library, begin) < Array.IndexOf(library, refresh) &&
                Array.IndexOf(library, end) > Array.IndexOf(library, refresh),
            "卡面诊断必须包围手动图鉴刷新，不能全局记录普通渲染。");

        var finalPatch = assembly.GetType("STS2SkinChanger.Ui.CardLayoutFinalPatch", true)!;
        var finalCalls = Calls(finalPatch.GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)!);
        var bridgeIndex = Array.FindIndex(finalCalls, call => call.Name == "SynchronizeProvider");
        Require(Array.FindIndex(finalCalls, call => call.Name == "ApplySelectedPortraitToNode") <
                Array.IndexOf(finalCalls, record) &&
                Array.IndexOf(finalCalls, record) < bridgeIndex &&
                Array.LastIndexOf(finalCalls, record) > bridgeIndex,
            "诊断必须区分本 Mod 呈现完成和外部卡面编辑器处理后的状态。");

        var diagnosticCalls = assembly.GetTypes()
            .Where(type => type == diagnostics || type.FullName?.StartsWith(diagnostics.FullName + "+", StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetMethodBody() != null)
            .SelectMany(Calls).ToArray();
        Require(!diagnosticCalls.Any(call =>
                call.DeclaringType == typeof(Godot.ResourceLoader) ||
                call.Name is "GetImage" or "get_Portrait" or "Dispose" or "QueueFree" ||
                call.Name.StartsWith("set_", StringComparison.Ordinal) &&
                typeof(Godot.GodotObject).IsAssignableFrom(call.DeclaringType)),
            "诊断只能观察已有节点；禁止重新加载资源或修改任何 Godot 显示属性。");
    }

    private static MethodInfo[] Calls(MethodInfo method) => PatchProcessor.GetOriginalInstructions(method)
        .Select(instruction => instruction.operand).OfType<MethodInfo>().ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
