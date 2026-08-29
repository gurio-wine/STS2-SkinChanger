using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class MultiplayerSkinFailureDialog
{
    private static bool _showing;

    internal static void TryShow()
    {
        if (_showing ||
            !OnlineSkinCache.TryPeekBlockingFailure(out var failure) ||
            NModalContainer.Instance is not { OpenModal: null } container)
        {
            return;
        }

        _showing = true;
        TaskHelper.RunSafely(Show(container, failure));
    }

    private static async Task Show(
        NModalContainer container,
        OnlineSkinCacheFailure failure)
    {
        try
        {
            var popup = NGenericPopup.Create();
            if (popup == null)
            {
                ModLog.Warn("游戏未能创建联机皮肤失败提示框，将在选角界面重试。");
                return;
            }

            container.Add(popup);
            var confirmation = popup.WaitForConfirmation(
                new LocString("main_menu_ui", "MOD_NOT_LOADED_POPUP.description"),
                new LocString("main_menu_ui", "MOD_NOT_LOADED_POPUP.title"),
                new LocString("main_menu_ui", "GENERIC_POPUP.cancel"),
                new LocString("main_menu_ui", "GENERIC_POPUP.confirm"));
            var verticalPopup = popup.GetNodeOrNull<NVerticalPopup>("VerticalPopup");
            if (verticalPopup == null)
            {
                ModLog.Warn("联机皮肤失败提示框缺少 VerticalPopup 节点，将在选角界面重试。");
                popup.QueueFree();
                return;
            }

            verticalPopup.SetText(
                ModLocalization.GetOnlineSkinFailureTitle(),
                ModLocalization.FormatOnlineSkinFailure(
                    EscapeBbCode(failure.ProviderId),
                    EscapeBbCode(failure.Detail)));
            verticalPopup.YesButton.SetText(ModLocalization.Get(ModText.Acknowledge));
            verticalPopup.NoButton.Hide();
            await confirmation;
            OnlineSkinCache.AcknowledgeBlockingFailure(failure.Key);
            ModLog.Info($"玩家已确认联机皮肤 {failure.ProviderId} 加载失败，本机将使用原皮继续。");
        }
        catch (Exception exception)
        {
            ModLog.Error("显示联机皮肤失败提示框失败：" + exception.GetBaseException().Message);
        }
        finally
        {
            _showing = false;
        }
    }

    private static string EscapeBbCode(string value) => value
        .Replace('[', '(')
        .Replace(']', ')')
        .Replace('\r', ' ')
        .Replace('\n', ' ');
}
