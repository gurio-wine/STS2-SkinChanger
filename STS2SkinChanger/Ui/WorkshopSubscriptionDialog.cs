using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// Lives with the game, not the browser. Steam's native ItemInstalled callback is
// not guaranteed for an already-downloaded subscription, so own our result queue.
internal static class WorkshopSubscriptionDialog
{
    private static bool _showing;
    internal static void EnsurePolling()
    {
        var root = NGame.Instance?.GetTree()?.Root;
        if (root == null || root.GetNodeOrNull("SCWorkshopNotices") != null) return;
        var timer = new Godot.Timer { Name = "SCWorkshopNotices", WaitTime = .5, Autostart = true, ProcessMode = Node.ProcessModeEnum.Always };
        timer.Timeout += TryShow;
        root.AddChild(timer);
    }

    internal static void TryShow()
    {
        if (_showing || SkinWorkshopService.Notices.Peek() is not { } notice ||
            NModalContainer.Instance is not { OpenModal: null } container) return;
        _showing = true;
        _ = Show(container, notice.Key, notice.Value);
    }

    private static async Task Show(NModalContainer container, ulong id, WorkshopLoadReason reason)
    {
        NGenericPopup? popup = null;
        try
        {
            popup = NGenericPopup.Create();
            if (popup == null) return;
            SkinWorkshopPanel.SuspendForNotice(true);
            container.Add(popup);
            var confirmation = popup.WaitForConfirmation(
                new LocString("main_menu_ui", "MOD_NOT_LOADED_POPUP.description"),
                new LocString("main_menu_ui", "MOD_NOT_LOADED_POPUP.title"), null,
                new LocString("main_menu_ui", "GENERIC_POPUP.confirm"));
            var vertical = popup.GetNode<NVerticalPopup>("VerticalPopup");
            var incompatible = reason == WorkshopLoadReason.Version;
            var invalid = reason == WorkshopLoadReason.InvalidPackage;
            var title = WorkshopNoticeText.Get(incompatible || invalid ? WorkshopNoticeKey.BlockedTitle : WorkshopNoticeKey.RestartTitle);
            var name = SkinWorkshopService.Title(id).Replace('[', '(').Replace(']', ')').Replace('\n', ' ').Replace('\r', ' ');
            var body = invalid ? name + "\n" + WorkshopNoticeText.Reason(reason) :
                string.Format(WorkshopNoticeText.Get(incompatible ? WorkshopNoticeKey.BlockedBody : WorkshopNoticeKey.RestartBody), name, WorkshopNoticeText.Reason(reason));
            vertical.SetText(title, body);
            vertical.YesButton.SetText(ModLocalization.Get(ModText.Acknowledge));
            // A scene change may free the popup before a button is clicked. Keep the
            // notice queued and release this await so it can appear on the next screen.
            var exited = new TaskCompletionSource();
            popup.TreeExiting += () => exited.TrySetResult();
            await Task.WhenAny(confirmation, exited.Task);
            if (confirmation.IsCompletedSuccessfully)
            {
                SkinWorkshopService.Notices.Acknowledge(id);
                ModLog.Info($"玩家已确认工坊皮肤 {id} 的加载提醒：{reason}。");
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn("工坊订阅提醒暂未显示，将保留重试：" + ex.GetBaseException().Message);
            if (GodotObject.IsInstanceValid(popup)) popup!.QueueFree();
        }
        finally
        {
            // Clear only the popup owned by us, never another game's modal.
            if (GodotObject.IsInstanceValid(container) && ReferenceEquals(container.OpenModal, popup)) container.Clear();
            SkinWorkshopPanel.SuspendForNotice(false);
            _showing = false;
        }
    }
}
