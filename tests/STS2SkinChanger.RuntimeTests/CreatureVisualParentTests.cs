using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2SkinChanger;

internal static class CreatureVisualParentTests
{
    public static void Run()
    {
        var bridge = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.CreatureVisualParentBridge")
            ?? throw new InvalidOperationException("自定义生物不能穿过 SC 的缩放容器找到所属生物。");
        var rewrite = bridge.GetMethod("Rewrite", BindingFlags.Static | BindingFlags.NonPublic)!;
        var method = typeof(CreatureVisualParentTests).GetMethod(nameof(Lookups), BindingFlags.Static | BindingFlags.NonPublic)!;
        var input = PatchProcessor.GetOriginalInstructions(method);
        var output = ((IEnumerable<CodeInstruction>)rewrite.Invoke(null, [input])!).ToArray();
        Require(output.Count(i => i.operand is MethodInfo m && m.DeclaringType == bridge) == 3,
            "as、显式转换和 GetParent<NCreature> 都应识别。");
        Require(output.Count(i => i.operand is MethodInfo m && m.Name == "GetParent" && m.DeclaringType == typeof(Node)) == 2,
            "不能改变普通 Node/Node2D 父节点查询。");
        Require(((IEnumerable<CodeInstruction>)rewrite.Invoke(null, [output])!).Count(i =>
            i.operand is MethodInfo m && m.DeclaringType == bridge) == 3, "重复处理不能重复改写。");
        var resolve = bridge.GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(object));
        var child = new object(); var wrapper = new object(); var owner = new object(); var other = new object();
        object? Resolve(object current, object tracked, object ownerVisual) => resolve.Invoke(null,
            [child, current, tracked, owner, ownerVisual]);
        Require(ReferenceEquals(Resolve(wrapper, wrapper, child), owner), "只穿透自己的有效缩放容器。");
        Require(ReferenceEquals(Resolve(other, wrapper, child), other), "被其它 Mod 移动后不能返回陈旧所有者。");
        Require(ReferenceEquals(Resolve(wrapper, wrapper, other), wrapper), "换肤后旧模型不能冒用新模型所有者。");
        Console.WriteLine("Creature visual parent bridge passed: narrow call sites and wrapper/owner identity.");
    }

    public static void Audit(string path)
    {
        var provider = Assembly.LoadFrom(Path.GetFullPath(path));
        Type[] types;
        try { types = provider.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { types = exception.Types.OfType<Type>().ToArray(); }
        var bridge = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.CreatureVisualParentBridge", true)!;
        var rewrite = bridge.GetMethod("Rewrite", BindingFlags.Static | BindingFlags.NonPublic)!;
        var matches = new List<string>();
        var harmony = new Harmony("sc.tests.actual_visual_parent");
        try
        {
            foreach (var type in types.Where(type => typeof(NCreatureVisuals).IsAssignableFrom(type) ||
                type.Name == "PetVisualFactory" || type.FullName?.Contains(".PetVisualFactory+", StringComparison.Ordinal) == true))
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                var input = PatchProcessor.GetOriginalInstructions(method);
                var output = ((IEnumerable<CodeInstruction>)rewrite.Invoke(null, [input])!).ToArray();
                if (!output.Any(i => i.operand is MethodInfo target && target.DeclaringType == bridge)) continue;
                // Install the real transpiler, without executing the provider method or any game lifecycle.
                harmony.Patch(method, transpiler: new HarmonyMethod(rewrite));
                matches.Add(type.FullName + "." + method.Name);
                Console.WriteLine($"Verified actual custom visual owner lookup: {type.FullName}.{method.Name}");
            }
            Require(new[] { "ShouldRenderInCombat", "UpdateDeathTint", "Register", "PlayAttack" }
                    .All(name => matches.Any(match => match.Contains(name, StringComparison.Ordinal))),
                "宠物显示、死亡着色、注册和攻击的全部父节点查询都必须保留。");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static object[] Lookups(Node node) =>
        [(node.GetParent() as NCreature)!, (NCreature)node.GetParent(), node.GetParent<NCreature>(), node.GetParent(), node.GetParent<Node2D>()];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
