using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui.LoadOrderWarning;

internal static class LoadOrderWarningController
{
    private static bool _pending;
    private static bool _shownThisSession;

    public static void Schedule()
    {
        if (_shownThisSession ||
            ManagedSkinModLoader.IsFirstInLoadOrder ||
            SkinService.Config.SuppressLoadOrderWarning)
        {
            return;
        }

        _pending = true;
        ModLog.Info("检测到本 Mod 不在加载顺序第一位，准备显示顺序提示。");
        TryShow();
    }

    public static void TryShow()
    {
        var container = NModalContainer.Instance;
        if (!_pending || _shownThisSession ||
            container == null || container.OpenModal != null)
        {
            return;
        }

        _pending = false;
        _shownThisSession = true;
        TaskHelper.RunSafely(ShowWarning(container));
    }

    private static async Task ShowWarning(NModalContainer container)
    {
        var popup = NGenericPopup.Create();
        if (popup == null)
        {
            ModLog.Warn("游戏未能创建加载顺序提示框。");
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
        ModLog.Info("已显示加载顺序提示框。");

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

    }
}

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
internal static class MainMenuLoadOrderWarningPatch
{
    private static void Postfix() => LoadOrderWarningController.Schedule();
}

[HarmonyPatch(typeof(NModalContainer), nameof(NModalContainer.Clear))]
internal static class LoadOrderWarningModalClosedPatch
{
    private static void Postfix() => LoadOrderWarningController.TryShow();
}
