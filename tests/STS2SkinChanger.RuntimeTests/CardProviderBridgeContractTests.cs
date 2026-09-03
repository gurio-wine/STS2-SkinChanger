using System.Reflection;
using Godot;
using HarmonyLib;
using STS2SkinChanger;

internal static class CardProviderBridgeContractTests
{
    public static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var bridge = assembly.GetType("STS2SkinChanger.Core.ExternalCardVisualBridge", true)!;
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var identity = service.GetMethod("GetExternalCardProviderIdentity")!;
        var identityCalls = Calls(identity);
        Require(identity.ReturnType == typeof(string) &&
                identityCalls.Any(call => call.Name == "ResolveCardPortraitRequest") &&
                identityCalls.Any(call => call.Name == "TryGetValue") &&
                identityCalls.Count(call => call.Name == "GetInstanceId") == 2,
            "编辑器交接只能导出已验证为当前卡牌皮肤缓存的贴图，不能从占位图/文件名推测身份。");
        Require(!identityCalls.Any(call => call.Name == "set_ResourcePath"),
            "导出编辑器身份不能重命名真正的卡图资源，否则会污染资源缓存。");

        var prepare = Method(bridge, "PrepareManagedProviderIdentity");
        var prepareCalls = Calls(prepare);
        Require(prepareCalls.Any(call => call.Name == "get_Visible") && prepareCalls.Contains(identity),
            "必须从可见卡图中找当前皮肤，不能用隐藏的透明占位图作为外部来源。");
        var createView = Method(bridge, "CreateProviderView");
        Require(prepareCalls.Contains(createView), "真实资源必须通过无像素拷贝的视图交接给编辑器。");
        var viewCalls = Calls(createView);
        foreach (var property in new[] { "Atlas", "Region", "Margin", "FilterClip" })
        {
            Require(viewCalls.Any(call => call.Name == "get_" + property) &&
                    viewCalls.Any(call => call.Name == "set_" + property),
                "已有图集导出不能丢失裁切/边距，也不能再嵌套一层：" + property);
        }
        Require(!viewCalls.Any(call => call.DeclaringType == typeof(ResourceLoader) ||
                                      call.Name is "GetImage" or "CreateFromImage" or "Load"),
            "同步不得重新解码/复制卡图像素。");

        var scope = bridge.GetNestedType("ProviderCaptureScope", BindingFlags.NonPublic)!;
        var constructorCalls = PatchProcessor.GetOriginalInstructions(scope.GetConstructors().Single())
            .Select(instruction => instruction.operand).OfType<MethodInfo>().ToArray();
        Require(constructorCalls.Count(call => call.Name == "set_Texture") == 2,
            "普通和异画两个缓存都应交接当前来源，避免另一个缓存遗留空白图。");
        var sync = Method(bridge, "SynchronizeProvider");
        Require(sync.GetMethodBody()!.ExceptionHandlingClauses.Any(clause =>
                    clause.Flags == ExceptionHandlingClauseOptions.Finally) &&
                Calls(sync).Contains(scope.GetMethod("Restore")!),
            "交接后必须在 finally 恢复卡图节点，再让编辑器应用玩家设置。");
        Console.WriteLine("Card provider bridge contracts passed: verified identity, both layers, immutable sources and atlas geometry.");
    }

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

    private static MethodInfo[] Calls(MethodInfo method) => PatchProcessor.GetOriginalInstructions(method)
        .Select(instruction => instruction.operand).OfType<MethodInfo>().ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
