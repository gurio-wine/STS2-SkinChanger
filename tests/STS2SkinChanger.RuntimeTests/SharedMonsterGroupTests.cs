using System.Collections;
using System.Reflection;
using HarmonyLib;
using STS2SkinChanger;

internal static class SharedMonsterGroupTests
{
    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var controls = assembly.GetType("STS2SkinChanger.Ui.ContextualSkinControls", true)!;
        var matches = AccessTools.Method(controls, "MatchesGroupIdentity");
        var findGroup = AccessTools.Method(controls, "FindGroup");

        bool Match(string groupId, string modelId, string? typeName = null) =>
            (bool)matches.Invoke(null, [groupId, modelId, typeName])!;
        Require(Match("cultists", "damp_cultist") && Match("cultists", "calcified_cultist") &&
                Match("cultists", "unmapped_id", "DecimillipedeElite") == false &&
                Match("cultists", "weird_entry", "DampCultist") &&
                Match("decimillipede", "decimillipede_elite") &&
                Match("decimillipede", "decimillipede_segment_middle") &&
                Match("kaiser_crab", "kaiser_crab_boss") &&
                Match("bowlbug", "bowlbug_silk") &&
                Match("cultists", "ironclad") == false &&
                Match("damp_cultist", "damp_cultist"),
            "共享骨骼目录（cultists/decimillipede/kaiser_crab/bowlbug）必须映射到变体模型 ID，且不吞并无关模型。");

        // A provider naming its directory after the monster itself still wins over the shared
        // fallback: with both groups present the exact identity must resolve first.
        var catalogType = assembly.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
        var groupType = assembly.GetType("STS2SkinChanger.Catalog.SkinGroup", true)!;
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var catalogProperty = AccessTools.Property(service, "Catalog");
        var previousCatalog = catalogProperty.GetValue(null);
        var catalog = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(catalogType);
        var groupsField = AccessTools.Field(catalogType, "_groups");
        var groups = (IList)Activator.CreateInstance(groupsField.FieldType)!;
        groupsField.SetValue(catalog, groups);
        foreach (var id in new[] { "damp_cultist", "cultists" })
        {
            groups.Add(Activator.CreateInstance(groupType, id, id)!);
        }

        try
        {
            catalogProperty.SetValue(null, catalog);
            var viaProperty = findGroup.Invoke(null, ["damp_cultist", null]);
            var viaAlias = findGroup.Invoke(null, ["calcified_cultist", null]);
            Require(viaProperty != null &&
                    AccessTools.Property(groupType, "Id").GetValue(viaProperty!)!.Equals("damp_cultist") &&
                    viaAlias != null &&
                    AccessTools.Property(groupType, "Id").GetValue(viaAlias!)!.Equals("cultists"),
                "按怪物自身命名的组要精确优先，变体模型才回退到共享骨骼目录组。");
        }
        finally
        {
            catalogProperty.SetValue(null, previousCatalog);
        }

        Console.WriteLine("Shared monster groups passed: cultists/decimillipede/kaiser_crab/bowlbug aliasing with exact identity first.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
