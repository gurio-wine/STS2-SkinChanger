using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace STS2SkinChanger.Core;

/// <summary>
/// Adapts only version-drifted game calls when the GAME loads a functional framework. It does
/// not initialize the mod, bypass dependencies, suppress its PCK, or load the bundled substitute.
/// </summary>
internal static class NativeFrameworkAssemblyLoader
{
    private static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);
    public static Assembly? Find(string path) => Loaded.GetValueOrDefault(Path.GetFullPath(path));

    public static bool IsOriginalFrameworkPath(string path)
    {
        if (!FrameworkCompatibilityLayer.IsKnownFrameworkHost(Path.GetFileNameWithoutExtension(path))) return false;
        var fullPath = Path.GetFullPath(path);
        return ModManager.Mods.Any(mod => FrameworkCompatibilityLayer.IsKnownFrameworkHost(mod.manifest?.id) &&
            Path.GetFullPath(Path.Combine(mod.path, mod.manifest!.id + ".dll"))
                .Equals(fullPath, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryLoadCompatible(AssemblyLoadContext context, string path, out Assembly? assembly)
    {
        path = Path.GetFullPath(path);
        assembly = Find(path);
        if (assembly != null) return AssemblyLoadContext.GetLoadContext(assembly) == context;
        // Do not introduce a second copy when another loader already owns this identity.
        var identity = AssemblyName.GetAssemblyName(path).FullName;
        if (context.Assemblies.Any(candidate => candidate.FullName == identity)) return false;
        if (!ProviderAssemblyCompatibility.TryRewriteForCurrentGame(path, out var rewritten, out var calls, out var failure))
        {
            if (failure != null) ModLog.Warn("原管理器游戏接口检查失败，保留游戏加载路径：" + failure);
            return false;
        }
        using (rewritten)
        using (var located = PreserveEntryLocation(rewritten!, path))
            assembly = context.LoadFromStream(located);
        Loaded.Add(path, assembly);
        ModLog.Info($"原管理器按正常加载流程桥接 {calls} 处游戏版本接口：{Path.GetFileName(path)}；" +
                    "全部原功能保留，原 DLL/PCK 未修改。");
        return true;
    }

    private static MemoryStream PreserveEntryLocation(MemoryStream source, string path)
    {
        // Stream-loaded assemblies have an empty Location. Preserve the original initializer's
        // own-assembly location expression so its ModDirectory keeps pointing to its real pack.
        var cecil = typeof(Harmony).Assembly;
        var definition = cecil.GetType("Mono.Cecil.AssemblyDefinition", true)!
            .GetMethod("ReadAssembly", [typeof(Stream)])!.Invoke(null, [source])!;
        try
        {
            object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
            var module = Property(definition, "MainModule");
            var opCodes = cecil.GetType("Mono.Cecil.Cil.OpCodes", true)!;
            var instructionType = cecil.GetType("Mono.Cecil.Cil.Instruction", true)!;
            var createString = instructionType.GetMethods().Single(method => method.Name == "Create" &&
                method.GetParameters() is { Length: 2 } parameters && parameters[1].ParameterType == typeof(string));
            foreach (var type in ((IEnumerable)Property(module, "Types")).Cast<object>())
            {
                var isEntry = ((IEnumerable)Property(type, "CustomAttributes")).Cast<object>().Any(attribute =>
                    (string)Property(Property(attribute, "AttributeType"), "FullName") ==
                    "MegaCrit.Sts2.Core.Modding.ModInitializerAttribute");
                if (!isEntry) continue;
                foreach (var method in ((IEnumerable)Property(type, "Methods")).Cast<object>())
                {
                    if ((string)Property(method, "Name") != ".cctor") continue;
                    var body = Property(method, "Body");
                    var instructions = ((IEnumerable)Property(body, "Instructions")).Cast<object>().ToArray();
                    var processor = body.GetType().GetMethod("GetILProcessor")!.Invoke(body, null)!;
                    for (var index = 3; index < instructions.Length; index++)
                    {
                        var operand = Property(instructions[index], "Operand");
                        if (operand?.ToString() != "System.String System.Reflection.Assembly::get_Location()" ||
                            Property(instructions[index - 3], "Operand")?.ToString() != (string)Property(type, "FullName")) continue;
                        instructions[index].GetType().GetProperty("OpCode")!.SetValue(instructions[index], opCodes.GetField("Pop")!.GetValue(null));
                        instructions[index].GetType().GetProperty("Operand")!.SetValue(instructions[index], null);
                        var literal = createString.Invoke(null, [opCodes.GetField("Ldstr")!.GetValue(null), path]);
                        processor.GetType().GetMethod("InsertAfter", [instructionType, instructionType])!.Invoke(processor, [instructions[index], literal]);
                    }
                }
            }
            var result = new MemoryStream();
            definition.GetType().GetMethod("Write", [typeof(Stream)])!.Invoke(definition, [result]);
            result.Position = 0;
            return result;
        }
        finally { ((IDisposable)definition).Dispose(); }
    }
}

[HarmonyPatch(typeof(AssemblyLoadContext), nameof(AssemblyLoadContext.LoadFromAssemblyPath))]
internal static class NativeFrameworkAssemblyLoadPatch
{
    private static bool Prefix(AssemblyLoadContext __instance, string __0, ref Assembly __result)
    {
        if (!NativeFrameworkAssemblyLoader.IsOriginalFrameworkPath(__0)) return true;
        if (!NativeFrameworkAssemblyLoader.TryLoadCompatible(__instance, __0, out var assembly)) return true;
        __result = assembly!;
        return false;
    }
}
