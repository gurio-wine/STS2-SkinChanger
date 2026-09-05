using Godot;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

// Read/rewrite this assembly in a separate load context. These methods model the observed legacy
// command boundary; native scene access is dormant and is never used by an offline test.
public sealed class LegacyMerchantCommandFixture : AbstractConsoleCmd
{
    public static int WorldWrites;
    public static int ConfigReads;
    public static bool NativeAccess;
    static LegacyMerchantCommandFixture()
    {
        ConfigReads++;
        ApplyToExistingHands();
        UpdateLegVisibility(false);
    }
    public override string CmdName => "merchant-fixture";
    public override string Args => "point";
    public override string Description => "legacy fixture";
    public override bool IsNetworked => false;
    public override CmdResult Process(Player? player, string[] args) => new(true, "ok");
    public static int ApplyToExistingHands()
    {
        WorldWrites++;
        if (NativeAccess)
        {
            _ = Engine.GetMainLoop();
            var count = 0;
            FindAndApplyRecursive(null!, ref count);
        }
        return WorldWrites;
    }
    public static void UpdateLegVisibility(bool visible)
    {
        WorldWrites++;
        if (NativeAccess)
        {
            _ = Engine.GetMainLoop();
            UpdateLegVisibilityStatic(null!, visible);
        }
    }
    public static bool TryApplyToHand(NMerchantHand hand) => hand != null;
    public static void FindAndApplyRecursive(Node node, ref int count) => count++;
    public static void UpdateLegVisibilityStatic(Node node, bool visible)
    {
        if (NativeAccess) _ = node.GetNodeOrNull<Node>("MerchantInventoryLeg");
    }
}

public sealed class UnrelatedControlFixture : AbstractConsoleCmd
{
    public override string CmdName => "unrelated";
    public override string Args => "";
    public override string Description => "no legacy settings contract";
    public override bool IsNetworked => false;
    public override CmdResult Process(Player? player, string[] args) => new(true, "ok");
    public static int ApplyToExistingHands() => 17;
}
