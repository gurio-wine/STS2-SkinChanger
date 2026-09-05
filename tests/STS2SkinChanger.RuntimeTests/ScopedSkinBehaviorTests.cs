using System.Reflection;
using System.Collections;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using STS2SkinChanger;

internal static class ScopedSkinBehaviorTests
{
    public static void Run()
    {
        var bridgeType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ScopedSkinBehavior")
            ?? throw new InvalidOperationException("角色皮肤原设置的适用判断尚未接入实际选择归属。");
        var contractType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.SkinBehaviorContract", true)!;
        var find = contractType.GetMethod("Find")!;
        Require(((MethodInfo[])find.Invoke(null, [typeof(BehaviorProfile)])!).Length == 2,
            "角色资源合同应该发现 CharacterModel 和 Player 两个入口。");
        Require(((MethodInfo[])find.Invoke(null, [typeof(GameplayPredicate)])!).Length == 0,
            "普通游戏逻辑即使叫 AppliesTo 也不得接管。");
        Require(((MethodInfo[])find.Invoke(null, [typeof(UnrelatedResourceProfile)])!).Length == 0,
            "只有资源属性但没有角色与皮肤身份的类不得接管。");

        var character = (CharacterModel)RuntimeHelpers.GetUninitializedObject(typeof(Silent));
        var other = (CharacterModel)RuntimeHelpers.GetUninitializedObject(typeof(Ironclad));
        var local = PlayerFor(character);
        var remote = PlayerFor(character);
        bool? selected = false;
        Player? seenPlayer = null;
        var bridge = Activator.CreateInstance(bridgeType, [
            "sc-tests.behavior", (Func<Assembly, CharacterModel, Player?, bool?>)((_, _, player) =>
            {
                seenPlayer = player;
                return player == null ? selected : ReferenceEquals(player, remote);
            })])!;
        var install = bridgeType.GetMethod("Install")!;
        Require((int)install.Invoke(bridge, [typeof(BehaviorProfile).Assembly])! == 2,
            "只应安装明确的皮肤合同，不得修改普通游戏判断。");
        try
        {
            var profile = new BehaviorProfile();
            Require(!profile.AppliesTo(character), "未选中时，原作者只检查角色类型的结果必须被收窄。");
            selected = true;
            Require(profile.AppliesTo(character), "选中皮肤时必须保留原行为。");
            Require(!profile.AppliesTo(other), "不能覆盖原作者对其它角色返回的 false。");
            profile.Enabled = false;
            Require(!profile.AppliesTo(character), "原设置关闭的效果不得被 SC 强制开启。");
            profile.Enabled = true;
            Require(profile.AppliesTo(remote), "远端 Player 内的 CharacterModel 判断不能继承本机选择。");
            Require(!profile.AppliesTo(local), "同角色另一玩家不能继承远端皮肤。");
            Require(profile.AppliesTo(character) && seenPlayer == null, "调用结束必须清除玩家作用域。");
            profile.ThrowForPlayer = true;
            try { profile.AppliesTo(remote); } catch (InvalidOperationException) { }
            Require(profile.AppliesTo(character) && seenPlayer == null, "作者抛错也必须还原作用域。");
            profile.ThrowForPlayer = false;
            Action deferred = () => Require(!profile.AppliesTo(character), "延迟回调必须按执行时的选择重新判断。");
            selected = false;
            deferred();
            selected = null;
            Require(profile.AppliesTo(character), "未知归属不能擅自关闭原逻辑。");
            Require(new GameplayPredicate().AppliesTo(character), "非皮肤逻辑必须保持不变。");
            Require((int)install.Invoke(bridge, [typeof(BehaviorProfile).Assembly])! == 0,
                "重复激活不能叠加门控。");
        }
        finally { new Harmony("sc-tests.behavior").UnpatchAll("sc-tests.behavior"); }
        VerifyRegistrationAndSelection();
        Console.WriteLine("Scoped skin behavior passed: contract, author settings, live selection, player nesting and exception cleanup.");
    }

    private static void VerifyRegistrationAndSelection()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var assembly = typeof(Entry).Assembly;
        var loader = assembly.GetType("STS2SkinChanger.Core.ManagedSkinModLoader", true)!;
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var catalogProperty = service.GetProperty("Catalog", flags)!;
        var configProperty = service.GetProperty("Config", flags)!;
        var oldCatalog = catalogProperty.GetValue(null);
        var oldConfig = configProperty.GetValue(null);
        var catalogType = catalogProperty.PropertyType;
        // Identity/selection-only catalog. No archive, scene or engine calls are needed by this
        // route; real group/option/config classes are retained, not mocked selection results.
        var catalog = RuntimeHelpers.GetUninitializedObject(catalogType);
        var groupType = assembly.GetType("STS2SkinChanger.Catalog.SkinGroup", true)!;
        var optionType = assembly.GetType("STS2SkinChanger.Catalog.SkinOption", true)!;
        var groups = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(groupType))!;
        var group = Activator.CreateInstance(groupType, ["silent", "猎手"])!;
        groups.Add(group);
        var options = (IList)groupType.GetProperty("Options")!.GetValue(group)!;
        var optionCtor = optionType.GetConstructors().Single(c => c.GetParameters().Length > 2);
        var optionArgs = optionCtor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        optionArgs[0] = "selected";
        optionArgs[1] = "selected";
        var assetsType = optionCtor.GetParameters()[2].ParameterType;
        optionArgs[2] = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(assetsType.GenericTypeArguments));
        optionArgs[3] = true;
        options.Add(optionCtor.Invoke(optionArgs));
        optionArgs[0] = "merged-alias";
        optionArgs[8] = "selected";
        options.Add(optionCtor.Invoke(optionArgs));
        AccessTools.Field(catalogType, "_groups").SetValue(catalog, groups);
        var config = Activator.CreateInstance(configProperty.PropertyType)!;
        var selections = (IDictionary)configProperty.PropertyType.GetProperty("Selections")!.GetValue(config)!;
        var character = (CharacterModel)RuntimeHelpers.GetUninitializedObject(typeof(Silent));
        AccessTools.Field(typeof(AbstractModel), "<Id>k__BackingField").SetValue(character, new ModelId("character", "SILENT"));
        var logs = new Harmony("sc-tests.behavior-logs");
        foreach (var method in new[] { "Info", "Warn" })
            logs.Patch(assembly.GetType("STS2SkinChanger.Core.ModLog", true)!.GetMethod(method, flags)!,
                prefix: new HarmonyMethod(typeof(ScopedSkinBehaviorTests), nameof(SkipEngineLog)));
        var providerAssembly = typeof(RegisteredSkinProfile).Assembly;
        try
        {
            catalogProperty.SetValue(null, catalog);
            configProperty.SetValue(null, config);
            loader.GetMethod("RegisterProviderAssembly", flags)!.Invoke(null, ["selected", providerAssembly]);
            var profile = new RegisteredSkinProfile();
            Require(!profile.AppliesTo(character), "注册皮肤时必须真实安装归属门控，不能只有未接线的工具类。");
            selections["silent"] = "selected";
            Require(profile.AppliesTo(character), "实际选择服务必须允许当前来源。");
            selections["silent"] = "__base__";
            Require(!profile.AppliesTo(character), "切到原皮必须撤销旧作者行为权限。");
            selections["silent"] = "selected";
            Require(profile.AppliesTo(character), "再次选回不能被旧的停用状态阻断。");
            selections["silent"] = "merged-alias";
            Require(profile.AppliesTo(character), "合并或皮肤包选项应按实际运行时来源归属，而非比较选项 ID。");
            var scopeField = assembly.GetType("STS2SkinChanger.Core.MultiplayerSkinSync", true)!
                .GetField("_selectionScopes", flags)!;
            var oldScopes = scopeField.GetValue(null);
            var scopes = new Stack<IReadOnlyDictionary<string, string>>();
            scopes.Push(new Dictionary<string, string> { ["silent"] = "__base__" });
            try
            {
                scopeField.SetValue(null, scopes);
                Require(!profile.AppliesTo(character), "玩家的实际换肤作用域必须优先于本机同角色选择。");
            }
            finally { scopeField.SetValue(null, oldScopes); }
            selections["silent"] = "missing";
            Require(!profile.AppliesTo(character), "缺失皮肤回退时不得借用已载入的旧 DLL。");
        }
        finally
        {
            catalogProperty.SetValue(null, oldCatalog);
            configProperty.SetValue(null, oldConfig);
            ((IDictionary)loader.GetField("ProviderIdsByAssembly", flags)!.GetValue(null)!).Remove(providerAssembly);
            var guard = new Harmony(Entry.ModId + ".skin-behavior-ownership");
            guard.Unpatch(typeof(RegisteredSkinProfile).GetMethod("AppliesTo")!, HarmonyPatchType.All, guard.Id);
            logs.UnpatchAll(logs.Id);
        }
    }

    private static bool SkipEngineLog() => false;

    public static void Audit(string path)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(path));
        var find = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.SkinBehaviorContract", true)!.GetMethod("Find")!;
        var methods = assembly.GetTypes().SelectMany(type => (MethodInfo[])find.Invoke(null, [type])!).ToArray();
        Require(methods.Length >= 2, "实包没有找到已验证的角色皮肤适用合同。");
        foreach (var method in methods) Console.WriteLine("Skin behavior contract: " + method.DeclaringType!.FullName + "." + method);
        Console.WriteLine("Metadata audit only: no Mod initializer, settings write or game node construction.");
    }

    private static Player PlayerFor(CharacterModel character)
    {
        var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
        AccessTools.Field(typeof(Player), "<Character>k__BackingField").SetValue(player, character);
        return player;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Mirrors the observed contract, without referring to any provider's namespace or ID.
    private sealed class BehaviorProfile
    {
        public string ProfileId => "test-skin";
        public Type TargetCharacterType => typeof(Silent);
        public string BodyTexturePath => "res://test/body.png";
        public string BodySkeletonDataPath => "res://test/body.tres";
        public bool Enabled = true;
        public bool ThrowForPlayer;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool AppliesTo(CharacterModel character) => Enabled && TargetCharacterType.IsInstanceOfType(character);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool AppliesTo(Player player)
        {
            var result = AppliesTo(player.Character);
            if (ThrowForPlayer) throw new InvalidOperationException("original failure");
            return result;
        }
    }

    private sealed class GameplayPredicate
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool AppliesTo(CharacterModel character) => true;
    }

    private sealed class UnrelatedResourceProfile
    {
        public string BodyTexturePath => "res://test/body.png";
        public string BodySkeletonDataPath => "res://test/body.tres";
        public bool AppliesTo(CharacterModel character) => true;
    }
}
