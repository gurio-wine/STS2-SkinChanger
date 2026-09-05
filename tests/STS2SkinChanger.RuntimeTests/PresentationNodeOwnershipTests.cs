using HarmonyLib;
using STS2SkinChanger;

internal static class PresentationNodeOwnershipTests
{
    private sealed record Item(string Name, string NativeClass, params Item[] Children);

    internal static void Run()
    {
        var policy = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.PresentationNodeOwnership")
            ?? throw new InvalidOperationException("皮肤场景快照仍会把 Spine 自动生成的网格当成作者添加的节点。");
        var walk = AccessTools.Method(policy, "Walk").MakeGenericMethod(typeof(Item));
        var mesh = new Item("arbitrary-name", "SpineMesh2D", new Item("mesh-child", "Node2D"));
        var skeleton = new Item("actor", "SpineSprite", mesh, new Item("ribbon", "Sprite2D"));
        var root = new Item("root", "Node2D", skeleton, new Item("SpineMesh2D", "Node2D"));
        var visited = ((IEnumerable<Item>)walk.Invoke(null,
            [root, (Func<Item, string>)(item => item.NativeClass),
                (Func<Item, IEnumerable<Item>>)(item => item.Children)])!).Select(item => item.Name).ToArray();
        if (!visited.SequenceEqual(new[] { "root", "actor", "ribbon", "SpineMesh2D" }))
            throw new InvalidOperationException("只能排除引擎管理的网格子树，不能漏掉整个 Spine 模型、作者附件或仅名字相同的普通节点。");
        // Replacing skeleton data regenerates the renderer's meshes under the existing actor.
        // Those new object IDs must never enter the mod-added-node removal set.
        var changed = root with { Children = [skeleton with { Children =
            [new Item("new-mesh", "SpineMesh2D"), new Item("new-accessory", "Sprite2D")] }] };
        var after = ((IEnumerable<Item>)walk.Invoke(null,
            [changed, (Func<Item, string>)(item => item.NativeClass),
                (Func<Item, IEnumerable<Item>>)(item => item.Children)])!).Select(item => item.Name);
        if (!after.Except(visited).SequenceEqual(new[] { "new-accessory" }))
            throw new InvalidOperationException("换骨骼新增的内部网格不能归皮肤切换器删除；真正新增的外观附件仍必须可恢复。");
        Console.WriteLine("Presentation node ownership passed: native meshes excluded, provider actors/accessories retained.");
    }
}
