using System.Reflection;
using System.Runtime.CompilerServices;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

internal static class CompanionSelectionTests
{
    internal static void RunPack(string gamePack, string skinPack)
    {
        var provider = Path.GetFileNameWithoutExtension(skinPack);
        using var catalog = SkinCatalog.Build(gamePack,
            [new SkinModDescriptor(provider, provider, skinPack, false, Path.GetDirectoryName(skinPack), false)]);
        var owner = catalog.Groups.Single(group => group.Id == "necrobinder");
        var pet = catalog.Groups.Single(group => group.Id == "osty");
        var option = owner.Options.Single(option => option.EffectiveProviderId == provider);
        var selections = new Dictionary<string, string>();
        for (var i = 0; i < 4; i++)
        {
            var selected = i % 2 == 0 ? option.Id : SkinCatalog.BaseOptionId;
            foreach (var update in catalog.BuildVisualSelectionTransaction(owner.Id, selected, selections))
                selections[update.Key] = update.Value;
            var overlay = catalog.BuildOverlay(selections, new HashSet<string> { pet.Id });
            var scene = overlay["res://scenes/creature_visuals/osty.tscn"];
            Require(scene.Archive.Path == (i % 2 == 0 ? skinPack : gamePack),
                "实包中的奥斯提场景没有随主人选择或恢复原版。");
            foreach (var resource in pet.Options.Single(candidate => candidate.Id == option.Id).Assets.Values)
            {
                foreach (var file in resource.Files)
                {
                    // Some optional provider-only dependencies do not exist in the base game.
                    if (i % 2 != 0 && !overlay.ContainsKey(file.Path)) continue;
                    Require(overlay.TryGetValue(file.Path, out var active) &&
                            active.Archive.Path == (i % 2 == 0 ? skinPack : gamePack),
                        $"实包依赖没有整体切换：{file.Path}");
                }
            }
        }
        Console.WriteLine($"Companion pack passed: {provider}, {pet.Options.Single(candidate => candidate.Id == option.Id).Assets.Count} Osty asset groups, repeated skin/base transitions.");
    }

    internal static void Run()
    {
        // Exercise the real catalog transaction without loading Godot or installing a Mod.
        var owner = new SkinGroup("necrobinder", "亡灵契约师");
        var pet = new SkinGroup("osty", "奥斯提");
        var unrelated = new SkinGroup("neow", "涅奥");
        foreach (var group in new[] { owner, pet, unrelated })
        {
            group.Options.Add(new("resource-a", "A", new Dictionary<string, ResourceAsset>
            {
                ["res://shared"] = new("res://a"),
                [$"res://animations/characters/{group.Id}/test.png"] = new("res://test")
            }));
            group.Options.Add(new("resource-b", "B", new Dictionary<string, ResourceAsset>
            {
                ["res://shared"] = new("res://b"),
                ["res://extra"] = new("res://extra")
            }));
        }
        owner.Options.Add(new("owner-only", "No pet resources", new Dictionary<string, ResourceAsset>()));
        var catalog = (SkinCatalog)RuntimeHelpers.GetUninitializedObject(typeof(SkinCatalog));
        Set("_groups", new List<SkinGroup> { owner, pet, unrelated });
        Set("_fullRuntimeProviders", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Set("_fullRuntimeProviderGroups", new Dictionary<string, IReadOnlyList<string>>());
        Set("_characterAppearanceGroupIds", new HashSet<string> { owner.Id });
        var selections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [owner.Id] = "resource-b", [pet.Id] = "resource-b", [unrelated.Id] = "resource-b"
        };
        for (var i = 0; i < 8; i++)
        {
            var option = i % 2 == 0 ? "resource-a" : "resource-b";
            var updates = Apply(option);
            Require(updates.GetValueOrDefault(pet.Id) == option,
                "纯资源皮肤切换必须同时更新奥斯提，不能只联动带 DLL 的皮肤。");
            Require(!updates.ContainsKey(unrelated.Id) && selections[unrelated.Id] == "resource-b",
                "同一提供者的无关生物不能一起切换。");
        }
        Require(Apply("owner-only").GetValueOrDefault(pet.Id) == SkinCatalog.BaseOptionId,
            "新角色皮肤没有奥斯提资源时必须恢复原版，不能残留旧皮肤。");
        Apply("resource-a");
        Require(Apply(SkinCatalog.BaseOptionId).GetValueOrDefault(pet.Id) == SkinCatalog.BaseOptionId,
            "角色恢复默认时奥斯提也必须恢复默认。");

        var combination = new CharacterSkinComposition
        {
            Id = "composition:test", GroupId = owner.Id, Name = "A + B",
            SourceOptionIds = ["resource-a", "resource-b"]
        };
        catalog.SynchronizeCharacterSkinCompositions([combination]);
        var mergedId = Apply(combination.Id).GetValueOrDefault(pet.Id);
        var merged = pet.Options.SingleOrDefault(option => option.Id == mergedId);
        Require(merged != null && merged.IsComposition &&
                merged.CompositionSourceOptionIds.SequenceEqual(combination.SourceOptionIds) &&
                ReferenceEquals(merged.Assets["res://shared"], pet.Options.Single(option => option.Id == "resource-a").Assets["res://shared"]) &&
                ReferenceEquals(merged.Assets["res://extra"], pet.Options.Single(option => option.Id == "resource-b").Assets["res://extra"]),
            $"合并皮肤的奥斯提必须沿用来源优先级：前者优先、后者补充。实际={mergedId}; " +
            $"主人选项={string.Join(',', owner.Options.Select(option => option.Id))}; " +
            $"随从来源={string.Join(',', merged?.CompositionSourceOptionIds ?? [])}");
        var beforeCount = pet.Options.Count;
        Apply(combination.Id);
        Require(pet.Options.Count == beforeCount, "重复选择不能无限累积衍生选项。");
        catalog.SynchronizeCharacterSkinCompositions([combination]);
        Require(pet.Options.Any(option => option.Id == mergedId), "保存其它合并皮肤不能移除正在使用的奥斯提合并资源。");
        Require(catalog.TryCreateSessionCharacterComposition(owner.Id, combination.SourceOptionIds, out var sessionId),
            "多人会话应能构造相同来源的合并皮肤。");
        var sessionPetId = Apply(sessionId)[pet.Id];
        Require(sessionPetId != mergedId, "会话和本机合并选项不能共享清理生命周期。");
        catalog.ClearSessionCharacterCompositions();
        Require(pet.Options.Any(option => option.Id == mergedId) &&
                pet.Options.All(option => option.Id != sessionPetId), "退出会话不得删除本机保存的随从合并皮肤。");
        Apply(combination.Id);
        var stale = new Dictionary<string, string>(selections) { [pet.Id] = "resource-b" };
        Require(catalog.BuildCompanionSelectionUpdates(stale)[pet.Id] == mergedId && stale[pet.Id] == "resource-b",
            "启动时应能修复旧独立选择，且计算不能擅自修改传入配置。");
        stale.Remove(owner.Id);
        Require(catalog.BuildCompanionSelectionUpdates(stale)[pet.Id] == SkinCatalog.BaseOptionId,
            "尚未选择主人皮肤时，不能从旧独立奥斯提设置继承皮肤。");
        Require(catalog.BuildVisualSelectionTransaction(unrelated.Id, "resource-a", selections).Count == 1,
            "修改先古皮肤不得触发角色或奥斯提的联动。");

        Set("_fullRuntimeProviders", new HashSet<string> { "resource-a" });
        Set("_fullRuntimeProviderGroups", new Dictionary<string, IReadOnlyList<string>>
        {
            ["resource-a"] = new[] { owner.Id, pet.Id }
        });
        Apply("resource-a");
        Require(catalog.IsFullRuntimeProviderFullySelected("resource-a", selections),
            "原有 DLL 皮肤完整联动必须继续成立。");
        Require(Apply("resource-b")[pet.Id] == "resource-b", "离开 DLL 套装也必须切到新奥斯提皮肤。");
        Console.WriteLine("Companion selection passed: raw resources, reset, composition priority, runtime suites.");

        void Set(string name, object value) => typeof(SkinCatalog)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(catalog, value);
        IReadOnlyDictionary<string, string> Apply(string option)
        {
            var updates = catalog.BuildVisualSelectionTransaction(owner.Id, option, selections);
            foreach (var pair in updates) selections[pair.Key] = pair.Value;
            return updates;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
