using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui.LoadOrderWarning;

internal partial class LoadOrderWarningController : Node
{
    private static bool _scheduledThisSession;

    public static void Schedule(NMainMenu mainMenu)
    {
        if (_scheduledThisSession ||
            ManagedSkinModLoader.IsFirstInLoadOrder ||
            SkinService.Config.SuppressLoadOrderWarning)
        {
            return;
        }

        _scheduledThisSession = true;
        mainMenu.AddChild(new LoadOrderWarningController());
    }

    public override void _Process(double delta)
    {
        var container = NModalContainer.Instance;
        if (container == null || container.OpenModal != null)
        {
            return;
        }

        SetProcess(false);
        TaskHelper.RunSafely(ShowWarning(container));
    }

    private async Task ShowWarning(NModalContainer container)
    {
        var popup = NGenericPopup.Create();
        if (popup == null)
        {
            QueueFree();
            return;
        }

        container.Add(popup);
        var confirmation = popup.WaitForConfirmation(
            new LocString("main_menu_ui", "MOD_NOT_LOADED_POPUP.description"),
            new LocString("main_menu_ui", "MOD_NOT_LOADED_POPUP.title"),
            new LocString("main_menu_ui", "GENERIC_POPUP.cancel"),
            new LocString("main_menu_ui", "GENERIC_POPUP.confirm"));
        var verticalPopup = popup.GetNode<NVerticalPopup>("VerticalPopup");
        verticalPopup.SetText(
            "STS2 皮肤切换器加载顺序",
            "本 Mod 当前不在 Mod 加载顺序第一位。排在它前面的皮肤 Mod 会先加载自己的 DLL/PCK，" +
            "因此无法被完整接管。请在 Mod 管理界面把“STS2 皮肤切换器”移到第一位，然后重启游戏。");
        verticalPopup.YesButton.SetText("知道了");
        verticalPopup.NoButton.SetText("不再提示");

        var acknowledged = await confirmation;
        if (!acknowledged)
        {
            try
            {
                SkinService.SuppressLoadOrderWarning();
            }
            catch (Exception exception)
            {
                ModLog.Warn("保存加载顺序提示设置失败：" + exception.Message);
            }
        }

        QueueFree();
    }
}

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
internal static class MainMenuLoadOrderWarningPatch
{
    private static void Postfix(NMainMenu __instance) =>
        LoadOrderWarningController.Schedule(__instance);
}
