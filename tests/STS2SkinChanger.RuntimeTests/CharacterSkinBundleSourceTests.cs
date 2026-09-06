using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using STS2SkinChanger;

internal static class CharacterSkinBundleSourceTests
{
    private static string _savePath = string.Empty;

    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var configProperty = AccessTools.Property(service, "Config");
        var catalogProperty = AccessTools.Property(service, "Catalog");
        var previousConfig = configProperty.GetValue(null);
        var previousCatalog = catalogProperty.GetValue(null);
        var config = AccessTools.Method(configProperty.PropertyType, "Deserialize").Invoke(null, ["""
            {"Selections":{"watcher":"skin:missing","silent":"skin:other"},
             "CharacterSkinBundles":[
              {"Id":"watcher-package","Name":"观者包","CharacterGroupId":"watcher",
               "CharacterOptionId":"skin:missing","CardPresetNames":{"watcher":"手工卡牌"},
               "MonsterPresetNames":{"act:one":"手工怪物"}},
              {"Id":"silent-package","Name":"猎手包","CharacterGroupId":"silent","CharacterOptionId":"skin:other"}],
             "CardSkinPresets":[{"Name":"手工卡牌","CategoryId":"watcher"}],
             "MonsterSkinPresets":[{"Name":"手工怪物","CategoryId":"act:one"}]}
            """])!;
        var saved = ((IList)AccessTools.Property(config.GetType(), "CharacterSkinBundles").GetValue(config)!)[0]!;
        var before = Json(config);
        var catalog = RuntimeHelpers.GetUninitializedObject(catalogProperty.PropertyType);
        var groupType = assembly.GetType("STS2SkinChanger.Catalog.SkinGroup", true)!;
        var groups = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(groupType))!;
        IList AddGroup(string id)
        {
            var group = Activator.CreateInstance(groupType, [id, id])!;
            groups.Add(group);
            return (IList)AccessTools.Property(groupType, "Options").GetValue(group)!;
        }
        var watcher = AddGroup("watcher");
        var silent = AddGroup("silent");
        watcher.Add(Option(assembly, "skin:available"));
        watcher.Add(Option(assembly, "composition:one"));
        watcher.Add(Option(assembly, "session:one", session: true));
        // A source with the same ID on another character must not rescue a stale reference.
        silent.Add(Option(assembly, "skin:missing"));
        silent.Add(Option(assembly, "skin:other"));
        AccessTools.Field(catalog.GetType(), "_groups").SetValue(catalog, groups);
        var cardsField = AccessTools.Field(catalog.GetType(), "_cardGroups");
        cardsField.SetValue(catalog, Activator.CreateInstance(cardsField.FieldType));
        var identitiesField = AccessTools.Field(catalog.GetType(), "_providerInstanceIdentities");
        identitiesField.SetValue(catalog, Array.CreateInstance(identitiesField.FieldType.GenericTypeArguments[0], 0));
        var controls = assembly.GetType("STS2SkinChanger.Ui.CharacterSkinBundleControls", true)!;
        var editorType = controls.GetNestedType("EditorState", BindingFlags.NonPublic)!;
        var editor = Activator.CreateInstance(editorType, BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic, null, new object?[4], null)!;
        object Open(string group, object? bundle)
        {
            AccessTools.Field(editorType, "GroupId").SetValue(editor, group);
            AccessTools.Method(controls, "LoadDraft").Invoke(null, [editor, bundle]);
            return AccessTools.Field(editorType, "Draft").GetValue(editor)!;
        }
        string Selection(object bundle) => (string)AccessTools.Property(bundle.GetType(), "CharacterOptionId").GetValue(bundle)!;
        void SetSelection(object bundle, string id) => AccessTools.Property(bundle.GetType(), "CharacterOptionId").SetValue(bundle, id);
        string[] Sources(string group) => ((IEnumerable)AccessTools.Method(service, "GetCharacterSkinBundleSourceOptions")
            .Invoke(null, [group])!).Cast<object>().Select(option => (string)AccessTools.Property(option.GetType(), "Id")
                .GetValue(option)!).ToArray();

        try
        {
            configProperty.SetValue(null, config);
            catalogProperty.SetValue(null, catalog);
            var draft = Open("watcher", saved);
            Require(Selection(draft) == "__base__",
                "已禁用或删除的来源仍留在编辑草稿中，会被重新插入角色皮肤列表；应仅在草稿中回退原皮。");
            Require(Json(config) == before && Selection(saved) == "skin:missing",
                "仅打开编辑器不能删除旧引用或改写玩家配置。");
            Require(Json(AccessTools.Property(saved.GetType(), "CardPresetNames").GetValue(saved)!) ==
                    Json(AccessTools.Property(draft.GetType(), "CardPresetNames").GetValue(draft)!) &&
                    Json(AccessTools.Property(saved.GetType(), "MonsterPresetNames").GetValue(saved)!) ==
                    Json(AccessTools.Property(draft.GetType(), "MonsterPresetNames").GetValue(draft)!),
                "清理失效角色皮肤引用不能重置卡牌或怪物预设。");
            Require(Sources("watcher").SequenceEqual(new[] { "skin:available", "composition:one" }),
                "来源必须限定当前角色，允许已隐藏的合并原料，但排除会话皮肤。");

            var restored = Option(assembly, "skin:missing");
            watcher.Add(restored);
            Require(Selection(Open("WATCHER", saved)) == "skin:missing" && Json(config) == before,
                "关闭后重新启用原 Mod，尚未覆盖保存的包必须恢复原来源。");
            watcher.Remove(restored);
            Require(Selection(Open("watcher", saved)) == "__base__", "反复启用、禁用不能保留旧目录的选择。");
            Require(Selection(Open("watcher", null)) == "__base__" && Json(config) == before,
                "新建包不能从全局历史选择继承已失效的皮肤。");

            foreach (var id in new[] { "skin:available", "composition:one", "__base__" })
            {
                var available = Open("watcher", saved);
                SetSelection(available, id);
                Require(Selection(Open("watcher", available)) == id, "可用来源、合并皮肤和原皮不能被误清理：" + id);
            }
            var sessionDraft = Open("watcher", saved);
            SetSelection(sessionDraft, "session:one");
            Require(Selection(Open("watcher", sessionDraft)) == "__base__", "临时会话来源不能作为可保存的包来源。");
            var legacy = Open("watcher", saved);
            SetSelection(legacy, "skin:duplicate");
            watcher.Add(Option(assembly, "skin:duplicate::source:local-one"));
            var identityType = identitiesField.FieldType.GenericTypeArguments[0];
            var identities = Array.CreateInstance(identityType, 1);
            identities.SetValue(Activator.CreateInstance(identityType,
                ["skin:duplicate", "skin:duplicate::source:local-one", "同 ID 皮肤"]), 0);
            identitiesField.SetValue(catalog, identities);
            Require(Selection(Open("watcher", legacy)) == "skin:duplicate::source:local-one",
                "仍可解析的同 ID 旧选项必须先迁移，不能被当成失效来源清理。");
            var other = ((IList)AccessTools.Property(config.GetType(), "CharacterSkinBundles").GetValue(config)!)[1]!;
            Require(Selection(Open("silent", other)) == "skin:other" && Json(config) == before,
                "复用编辑器切换角色时不能携带前一个角色的来源或修改它的配置。");
            catalogProperty.SetValue(null, null);
            Require(Selection(Open("watcher", saved)) == "__base__" && Json(config) == before,
                "目录暂时不可用也只能回退草稿，不能破坏配置。");
            catalogProperty.SetValue(null, catalog);

            // Keep the production save transaction and serialization. Only redirect the
            // native user-data path boundary so no real player settings can be touched.
            var temporary = Directory.CreateTempSubdirectory("skin-changer-bundle-source-");
            var harmony = new Harmony("Gurio.SkinChanger.Tests.BundleSourceSave");
            _savePath = Path.Combine(temporary.FullName, "skin_changer.json");
            try
            {
                harmony.Patch(AccessTools.PropertyGetter(service, "ConfigPath"),
                    prefix: new HarmonyMethod(typeof(CharacterSkinBundleSourceTests), nameof(TemporaryConfigPath)));
                var replacement = Open("watcher", saved);
                Require((bool)AccessTools.Method(service, "OverwriteCharacterSkinBundle").Invoke(null,
                    ["观者包", replacement])!, "明确保存原皮回退应成功。");
                var loaded = AccessTools.Method(config.GetType(), "Load").Invoke(null, [_savePath])!;
                configProperty.SetValue(null, loaded);
                var loadedBundles = (IList)AccessTools.Property(loaded.GetType(), "CharacterSkinBundles").GetValue(loaded)!;
                Require(loadedBundles.Count == 2 && Selection(loadedBundles[0]!) == "__base__" &&
                        Json(loadedBundles[1]!) == Json(other), "保存并重读只能替换当前包的失效引用，不能改其它角色的包。");
                Require(Json(AccessTools.Property(saved.GetType(), "CardPresetNames").GetValue(saved)!) ==
                        Json(AccessTools.Property(saved.GetType(), "CardPresetNames").GetValue(loadedBundles[0])!) &&
                        Json(AccessTools.Property(saved.GetType(), "MonsterPresetNames").GetValue(saved)!) ==
                        Json(AccessTools.Property(saved.GetType(), "MonsterPresetNames").GetValue(loadedBundles[0])!),
                    "覆盖保存不能改变原有卡牌和怪物预设引用。");
                watcher.Add(restored);
                Require(Selection(Open("watcher", loadedBundles[0])) == "__base__",
                    "已经明确保存为原皮的包，重新启用旧 Mod 后不能反向覆盖玩家的新选择。");
            }
            finally
            {
                harmony.UnpatchAll(harmony.Id);
                File.Delete(_savePath);
                File.Delete(_savePath + ".bak");
                Directory.Delete(temporary.FullName);
                _savePath = string.Empty;
            }
        }
        finally
        {
            configProperty.SetValue(null, previousConfig);
            catalogProperty.SetValue(null, previousCatalog);
        }
        Console.WriteLine("Bundle source editing passed: missing sources, re-enable, draft isolation, save/reload, aliases, presets and character scope.");
    }

    private static object Option(Assembly assembly, string id, bool session = false)
    {
        var type = assembly.GetType("STS2SkinChanger.Catalog.SkinOption", true)!;
        var ctor = type.GetConstructors().Single(c => c.GetParameters().Length > 2);
        var args = ctor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue :
            p.ParameterType.IsGenericType ? Activator.CreateInstance(typeof(Dictionary<,>)
                .MakeGenericType(p.ParameterType.GenericTypeArguments)) : null).ToArray();
        args[0] = id;
        args[1] = id;
        var option = ctor.Invoke(args);
        AccessTools.Property(type, "IsSessionComposition").SetValue(option, session);
        if (id.StartsWith("composition:"))
            AccessTools.Property(type, "CompositionSourceOptionIds").SetValue(option, new[] { "skin:available" });
        return option;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, value.GetType());
    private static bool TemporaryConfigPath(ref string __result)
    {
        __result = _savePath;
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
