using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using STS2SkinChanger;

internal static class FrameworkCardVisualTests
{
    private static readonly Assembly Mod = typeof(Entry).Assembly;
    private static readonly Type Service = Mod.GetType("STS2SkinChanger.Core.SkinService", true)!;
    private static object? _characterOverrides;
    private static bool _throwPortrait;

    public static void Run(string frameworkPath)
    {
        // Load the selected host-version assembly, but never run its framework initializer.
        var framework = Assembly.LoadFrom(Path.GetFullPath(frameworkPath));
        var helper = framework.GetType("STS2RitsuLib.Scaffolding.Content.Patches.ModCharacterOwnedVisualOverrideHelper", true)!;
        var resolve = AccessTools.Method(helper, "TryGetOwningCharacterOverrides", [typeof(CardModel)]);
        Require(resolve != null, "该框架快照缺少已审计的按卡牌解析角色外观入口。");
        var guard = Mod.GetType("STS2SkinChanger.Core.FrameworkCardVisualGuard");
        Require(guard != null, "缺少框架卡面隔离：角色卡图替换会污染原皮路径，异画外形也会越过单卡选择。");
        var baseline = AccessTools.Method(guard, "GetBaselinePortraitPath");
        var ensure = AccessTools.Method(guard, "EnsureInstalled");
        var proxy = DispatchProxy.Create(resolve!.ReturnType, typeof(CharacterProfileProxy));
        _characterOverrides = proxy;
        var profileType = framework.GetType("STS2RitsuLib.Scaffolding.Content.CardAssetProfile", true)!;
        var constructor = profileType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var profileArgs = constructor.GetParameters().Select(parameter =>
            parameter.HasDefaultValue ? parameter.DefaultValue : parameter.ParameterType.IsValueType
                ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray();
        var profile = constructor.Invoke(profileArgs);
        profileType.GetProperty("PortraitPath")!.SetValue(profile, "res://context/previous-character.png");
        var styleProperty = profileType.GetProperty("VisualStyle")!;
        styleProperty.SetValue(profile, Enum.Parse(styleProperty.PropertyType, "Ancient"));
        ((CharacterProfileProxy)proxy).Profile = profile;

        var harmony = new Harmony("SkinChanger.Tests.FrameworkCardVisuals");
        var catalogProperty = AccessTools.Property(Service, "Catalog");
        var configProperty = AccessTools.Property(Service, "Config");
        var cache = AccessTools.Field(Service, "_cardLookupCache");
        var oldCatalog = catalogProperty.GetValue(null);
        var oldConfig = configProperty.GetValue(null);
        var oldCache = cache.GetValue(null);
        var card = Initialize(new Infection(), "INFECTION");
        var uncovered = Initialize(new Burn(), "BURN");
        var original = card.PortraitPath;
        try
        {
            foreach (var name in new[] { "Info", "Warn", "Error" })
                harmony.Patch(AccessTools.Method(Mod.GetType("STS2SkinChanger.Core.ModLog", true)!, name),
                    prefix: new HarmonyMethod(typeof(FrameworkCardVisualTests), nameof(LogPrefix)));
            catalogProperty.SetValue(null, null);
            cache.SetValue(null, Activator.CreateInstance(cache.FieldType));
            // Only replace native owner/resource lookups. The framework's path/style resolution,
            // our ownership lookup, scope and installed Harmony guard all execute for real.
            harmony.Patch(resolve, prefix: new HarmonyMethod(typeof(FrameworkCardVisualTests), nameof(CharacterPrefix))
                { priority = Priority.Last });
            var exists = AccessTools.GetDeclaredMethods(framework.GetType("STS2RitsuLib.Utils.AssetPathDiagnostics", true)!)
                .Single(method => method.Name == "Exists" && method.GetParameters().Length == 3);
            harmony.Patch(exists, prefix: new HarmonyMethod(typeof(FrameworkCardVisualTests), nameof(ExistsPrefix)));
            var pathPatch = framework.GetType("STS2RitsuLib.Scaffolding.Content.Patches.CardPortraitPathPatch", true)!;
            harmony.Patch(AccessTools.PropertyGetter(typeof(CardModel), "PortraitPath"),
                prefix: new HarmonyMethod(AccessTools.Method(pathPatch, "Prefix")));
            var style = AccessTools.Method(helper, "TryCardVisualStyle");
            bool HasContextStyle(CardModel model)
            {
                object?[] args = [model, null];
                return (bool)style.Invoke(null, args)!;
            }
            Require(card.PortraitPath == "res://context/previous-character.png" && HasContextStyle(card),
                "前置条件失败：必须先重现框架角色配置同时替换卡图和先古外形。");
            ensure.Invoke(null, null);
            Require((string)baseline.Invoke(null, [card])! == original,
                "原皮路径读取不能把角色提供的其它卡图当成基线。");
            Require(card.PortraitPath == "res://context/previous-character.png" && HasContextStyle(card),
                "基线读取结束后必须恢复未接管卡牌的框架行为。");
            var modCard = Initialize(CreateModCard(), "MOD_CARD");
            Require((string)baseline.Invoke(null, [modCard])! == "res://context/previous-character.png" && HasContextStyle(modCard),
                "不能把 Mod 定义的卡牌强制读成游戏图集路径；它的框架基线仍需保留。");
            harmony.Patch(AccessTools.PropertyGetter(typeof(CardModel), "PortraitPath"),
                prefix: new HarmonyMethod(typeof(FrameworkCardVisualTests), nameof(ThrowPortraitPrefix))
                    { priority = Priority.First });
            _throwPortrait = true;
            try
            {
                baseline.Invoke(null, [card]);
                throw new InvalidOperationException("前置条件失败：应触发卡图 getter 异常。");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is TestPortraitException) { }
            finally { _throwPortrait = false; }
            Require(card.PortraitPath == "res://context/previous-character.png" && HasContextStyle(card),
                "卡图 getter 抛异常后也必须退出基线作用域，不能继续压制未接管卡牌。");

            var catalog = CreateCatalog();
            catalogProperty.SetValue(null, catalog);
            cache.SetValue(null, Activator.CreateInstance(cache.FieldType));
            foreach (var selection in new[] { "__base__", "skin:portrait", "skin:frame", "skin:raw", "__base__" })
            {
                configProperty.SetValue(null, AccessTools.Method(configProperty.PropertyType, "Deserialize").Invoke(null,
                    ["{\"Selections\":{\"cards:item:card.infection\":\"" + selection + "\"}}"]));
                Require(card.PortraitPath == original && !HasContextStyle(card),
                    "已接管卡牌不能继续接受角色级卡图/先古外形覆盖：" + selection);
                var request = AccessTools.Method(Service, "ResolveCardPortraitRequest").Invoke(null, [card])!;
                var path = (string)AccessTools.Property(request.GetType(), "ResourcePath").GetValue(request)!;
                Require(path == (selection == "skin:portrait" ? "res://selected/infection.png" : original),
                    "原皮、纯卡图、仅外框的资源请求必须分别来自对应所有者：" + selection);
                Require(uncovered.PortraitPath == "res://context/previous-character.png" && HasContextStyle(uncovered),
                    "同分类未接管的牌必须保留原框架的卡图与外形。");
                Require(card.Rarity == MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Status,
                    "外观隔离不能修改卡牌真实稀有度。");
            }
            Console.WriteLine("Framework card visuals passed with " + framework.GetName().Version +
                ": clean baseline, default/portrait/frame ownership, uncovered cards and unchanged rarity.");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            catalogProperty.SetValue(null, oldCatalog);
            configProperty.SetValue(null, oldConfig);
            cache.SetValue(null, oldCache);
            _characterOverrides = null;
            _throwPortrait = false;
        }
    }

    private static CardModel Initialize(CardModel card, string id)
    {
        AccessTools.Field(typeof(AbstractModel), "<Id>k__BackingField").SetValue(card, new ModelId("CARD", id));
        AccessTools.Field(typeof(CardModel), "_pool").SetValue(card, new StatusCardPool());
        return card;
    }

    private static CardModel CreateModCard()
    {
        // Identity-only foreign CardModel. Abstract gameplay methods are never invoked; emitting
        // them avoids binding this visual test to one version's OnPlay signature.
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FrameworkCardVisualFixture"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Cards").DefineType("FixtureModCard", TypeAttributes.Public, typeof(CardModel));
        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var constructorIl = constructor.GetILGenerator();
        constructorIl.Emit(OpCodes.Newobj, typeof(NotSupportedException).GetConstructor(Type.EmptyTypes)!);
        constructorIl.Emit(OpCodes.Throw);
        foreach (var method in typeof(CardModel).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(method => method.IsAbstract))
        {
            var implementation = type.DefineMethod(method.Name,
                (method.Attributes & ~MethodAttributes.Abstract) | MethodAttributes.Virtual,
                method.ReturnType, method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
            var il = implementation.GetILGenerator();
            il.Emit(OpCodes.Newobj, typeof(NotSupportedException).GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Throw);
            type.DefineMethodOverride(implementation, method);
        }
        return (CardModel)RuntimeHelpers.GetUninitializedObject(type.CreateType()!);
    }

    private static object CreateCatalog()
    {
        var type = Mod.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
        var catalog = RuntimeHelpers.GetUninitializedObject(type);
        foreach (var name in new[] { "_cardGroups", "_configuredCardGroups", "_pckCardOptions", "_providerInstanceIdentities" })
        {
            var field = AccessTools.Field(type, name);
            field.SetValue(catalog, Activator.CreateInstance(typeof(List<>).MakeGenericType(field.FieldType.GenericTypeArguments[0])));
        }
        var groupType = Mod.GetType("STS2SkinChanger.Catalog.CardSkinGroup", true)!;
        var group = Activator.CreateInstance(groupType, ["status", "status"])!;
        ((IList)AccessTools.Field(type, "_cardGroups").GetValue(catalog)!).Add(group);
        var optionType = Mod.GetType("STS2SkinChanger.Catalog.CardSkinOption", true)!;
        var ctor = optionType.GetConstructors().Single();
        foreach (var id in new[] { "skin:portrait", "skin:frame", "skin:raw" })
        {
            var args = ctor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            args[0] = id; args[1] = id;
            args[2] = id == "skin:portrait" ? new Dictionary<string, string> { ["Infection"] = "res://selected/infection.png" }
                : new Dictionary<string, string>();
            args[3] = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),
                Mod.GetType("STS2SkinChanger.Catalog.AncientCardPortrait", true)!));
            var option = ctor.Invoke(args);
            if (id == "skin:raw")
            {
                const string path = "res://images/atlases/card_atlas.sprites/status/infection.tres";
                var asset = Activator.CreateInstance(Mod.GetType("STS2SkinChanger.Catalog.ResourceAsset", true)!, [path]);
                ((IDictionary)AccessTools.Property(optionType, "Assets").GetValue(option)!).Add(path, asset);
            }
            if (id == "skin:frame")
            {
                var presentation = Mod.GetType("STS2SkinChanger.Catalog.CardPresentationDefinition", true)!.GetConstructors().Single();
                var parameters = presentation.GetParameters().Select(p => p.DefaultValue).ToArray();
                parameters[0] = true;
                ((IDictionary)AccessTools.Property(optionType, "CardPresentations").GetValue(option)!)
                    .Add("Infection", presentation.Invoke(parameters));
            }
            ((IList)AccessTools.Property(groupType, "Options").GetValue(group)!).Add(option);
        }
        return catalog;
    }

    private static bool CharacterPrefix(ref object __result) { __result = _characterOverrides!; return false; }
    private static bool ExistsPrefix(ref bool __result) { __result = true; return false; }
    private static bool LogPrefix(string __0) { Console.WriteLine(__0); return false; }
    private static void ThrowPortraitPrefix() { if (_throwPortrait) throw new TestPortraitException(); }
    private sealed class TestPortraitException : Exception;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public class CharacterProfileProxy : DispatchProxy
    {
        public object? Profile;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == "TryGetVanillaCardVisualOverrideForContext" ? Profile :
            method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
