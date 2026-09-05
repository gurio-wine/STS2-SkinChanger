using System.Reflection;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using STS2SkinChanger;

internal static class ProviderSettingsTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var controls = assembly.GetType("STS2SkinChanger.Core.ProviderSettingsControls")
            ?? throw new InvalidOperationException("接管后的皮肤命令没有独立注册路径，原生控制台无法发现它们。");
        var register = controls.GetMethod("TryRegisterCommand", Static)!;
        var commands = new Dictionary<string, AbstractConsoleCmd>();
        bool Register(AbstractConsoleCmd command, bool debug = true) =>
            (bool)register.Invoke(null, [commands, command, debug])!;
        var first = new SettingsCommand("merchant-test", debug: true);
        Require(!Register(first, false) && commands.Count == 0, "不能放开原游戏禁用的调试命令。");
        Require(Register(first) && ReferenceEquals(commands["merchant-test"], first), "接管命令必须能晚于控制台创建注册。");
        Require(!Register(first) && commands.Count == 1, "重复激活不能重复注册命令。");
        Require(!Register(new SettingsCommand("merchant-test")) && ReferenceEquals(commands["merchant-test"], first),
            "不得覆盖游戏或其它 Mod 的同名命令。");
        Require(!Register(new SettingsCommand("MERCHANT-TEST")), "同名冲突需忽略大小写，输入会被原控制台转小写。");
        Require(Register(new SettingsCommand("other", debug: false), false), "非调试命令不能因调试开关关闭而丢失。");

        var targetsType = assembly.GetType("STS2SkinChanger.Core.ProviderSettingsTargets`1", true)!.MakeGenericType(typeof(Target));
        var targets = Activator.CreateInstance(targetsType)!;
        var bind = targetsType.GetMethod("Bind")!;
        var refresh = targetsType.GetMethod("Refresh")!;
        var selected = new Dictionary<string, string?> { ["merchant"] = "a", ["fake"] = "b" };
        var live = new Target("merchant");
        var wrong = new Target("fake");
        var stale = new Target("merchant") { Alive = false };
        void Bind(Target target, string provider = "a") => bind.Invoke(targets, [target, target.Group, provider]);
        int Refresh() => (int)refresh.Invoke(targets, [
            (Func<Target, bool>)(target => target.Alive),
            (Func<Target, string?>)(target => target.Group),
            (Func<string, string?>)(group => selected.GetValueOrDefault(group)),
            (Action<Target>)(target => target.Writes++)])!;
        Bind(live); Bind(wrong); Bind(stale);
        Require(Refresh() == 1 && live.Writes == 1 && wrong.Writes == 0 && stale.Writes == 0,
            "只能刷新存活且当前实际选择该提供者的对象，真/假商人必须隔离。");
        selected["merchant"] = "b";
        Require(Refresh() == 0 && live.Writes == 1, "切走后到达的延迟刷新不能污染新皮肤。");
        selected["merchant"] = "a";
        Bind(live, "b");
        Require(Refresh() == 0, "同一对象重新绑定后，旧提供者记录必须失效。");
        Bind(live); Bind(live);
        Require(Refresh() == 1 && live.Writes == 2, "重复 Ready/绑定不能累积刷新次数。");
        live.Group = "fake";
        selected["fake"] = "a";
        Require(Refresh() == 1 && live.Writes == 2 && wrong.Writes == 1,
            "节点换属另一场景后，不能沿用原分组绑定。");
        VerifyRewrittenInitializer();
        Console.WriteLine("Provider settings passed: late console registration, collisions, debug policy and per-target ownership.");
    }

    public static void Audit(string path)
    {
        // Metadata only: never run the provider initializer, command constructor or ConfigStore.
        using var source = File.OpenRead(Path.GetFullPath(path));
        using var rewritten = Rewrite(source, out var count);
        Require(count == 2 && rewritten != null, "实包必须在加载前替换两个全场景入口。");
        var provider = Assembly.Load(rewritten!.ToArray());
        var contractType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.MerchantSettingsContract", true)!;
        var contract = contractType.GetMethod("TryCreate", Static)!.Invoke(null, [provider]);
        Require(contract != null, "实包的商人设置接口未被识别。");
        var commandType = (Type)contractType.GetProperty("CommandType")!.GetValue(contract)!;
        Require(typeof(AbstractConsoleCmd).IsAssignableFrom(commandType), "必须使用原命令类，不创造另一套配置。");
        var worldMethods = (IReadOnlyList<MethodInfo>)contractType.GetProperty("WorldApplyMethods")!.GetValue(contract)!;
        Require(worldMethods.Select(method => method.Name).Order().SequenceEqual(
            new[] { "ApplyToExistingHands", "UpdateLegVisibility" }), "必须截断手和腿的全部全场景入口，包括延迟重试。");
        Console.WriteLine("Verified actual merchant settings contract: " + commandType.FullName);
    }

    public static void AuditPreserved(string path)
    {
        using var source = File.OpenRead(Path.GetFullPath(path));
        using var output = Rewrite(source, out var count);
        Require(count == 0 && output == null, "非商人设置接口不应被此旧接口适配器修改。");
        Console.WriteLine("Verified settings assembly left intact: " + Path.GetFileName(path));
    }

    private static MemoryStream? Rewrite(Stream source, out int count)
    {
        var method = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.MerchantSettingsAssemblyCompatibility", true)!
            .GetMethod("Rewrite", Static)!;
        object?[] args = [source, 0];
        var result = (MemoryStream?)method.Invoke(null, args);
        count = (int)args[1]!;
        return result;
    }

    private static void VerifyRewrittenInitializer()
    {
        using var source = File.OpenRead(typeof(LegacyMerchantCommandFixture).Assembly.Location);
        using var rewritten = Rewrite(source, out var count);
        Require(count == 2 && rewritten != null, "旧式命令必须在静态构造之前截断全树修改。");
        var copy = Assembly.Load(rewritten!.ToArray());
        var fixture = copy.GetType("LegacyMerchantCommandFixture", true)!;
        _ = Activator.CreateInstance(fixture); // Runs the ACTUAL rewritten .cctor.
        Require((int)fixture.GetField("WorldWrites")!.GetValue(null)! == 0,
            "注册命令的静态构造不得触及世界；只有执行命令时拦截已经太晚。");
        Require((int)fixture.GetField("ConfigReads")!.GetValue(null)! == 1,
            "必须保留原配置加载，不得删掉整个静态构造。");
        fixture.GetMethod("ApplyToExistingHands")!.Invoke(null, null);
        fixture.GetMethod("UpdateLegVisibility")!.Invoke(null, [true]);
        Require((int)fixture.GetField("WorldWrites")!.GetValue(null)! == 0,
            "延迟重试和直接调用也不能绕过作用对象检查。");
        var untouched = copy.GetType("UnrelatedControlFixture", true)!;
        Require((int)untouched.GetMethod("ApplyToExistingHands")!.Invoke(null, null)! == 17,
            "同名方法但没有已验证能力合同的其它 Mod 不能被改写。");
        using var secondPass = Rewrite(rewritten, out var secondCount);
        Require(secondCount == 0 && secondPass == null, "重复加载检查不能重复叠加桥接代码。");
        Console.WriteLine("Provider settings IL passed: real static initialization, late calls, unrelated code and idempotence.");
    }

    private sealed class Target(string group)
    {
        public string Group = group;
        public bool Alive = true;
        public int Writes;
    }

    private sealed class SettingsCommand(string name, bool debug = false) : AbstractConsoleCmd
    {
        public override string CmdName => name;
        public override string Args => "status";
        public override string Description => "settings test";
        public override bool IsNetworked => false;
        public override bool DebugOnly => debug;
        public override CmdResult Process(Player? player, string[] args) => new(true, "ok");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
