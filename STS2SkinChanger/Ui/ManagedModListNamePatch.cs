using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

[HarmonyPatch(typeof(NModMenuRow), nameof(NModMenuRow._Ready))]
internal static class ManagedModListNamePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NModMenuRow __instance)
    {
        if (!ManagedSkinModLoader.IsManagedProviderForDisplay(__instance.Mod))
        {
            return;
        }

        var title = __instance.GetNodeOrNull<Control>("Title");
        switch (title)
        {
            case RichTextLabel richTextLabel:
                richTextLabel.Text = ManagedModListNamePolicy.Format(
                    richTextLabel.Text,
                    isManagedProvider: true);
                break;
            case Label label:
                label.Text = ManagedModListNamePolicy.Format(
                    label.Text,
                    isManagedProvider: true);
                break;
        }
    }
}
