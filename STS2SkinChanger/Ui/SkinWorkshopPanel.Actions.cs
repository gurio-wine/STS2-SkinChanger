using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class SkinWorkshopPanel
{
    private bool _restartPending;
    private readonly Dictionary<ulong, string> _actionErrors = [];
    private void PerformPrimary(ulong id)
    {
        if (_closed || _restartPending) return;
        try
        {
            _actionErrors.Remove(id);
            var state = SkinWorkshopService.DownloadState(id);
            if (state?.Busy == true) return;
            var primary = WorkshopItemActions.Primary(SkinWorkshopService.IsSubscribed(id), SkinWorkshopService.IsActive(id),
                SkinWorkshopService.IsInstalled(id), SkinWorkshopService.Catalog.First(i => i.Id == id).RestartRequired, state?.State, false);
            if (primary == WorkshopTextKey.Restart)
            {
                _restartPending = true; Poll();
                // Same cross-platform normal shutdown path as the load-order dialog.
                // Never invoke this just because a download or query completed.
                Callable.From(() =>
                {
                    try
                    {
                        var game = NGame.Instance;
                        if (!GodotObject.IsInstanceValid(game)) throw new InvalidOperationException("Game is no longer available.");
                        OS.SetRestartOnExit(true);
                        ModLog.Info($"玩家点击工坊皮肤 {id} 的“需要重启”，按游戏正常退出流程重启。");
                        game!.Quit();
                    }
                    catch (Exception ex)
                    {
                        OS.SetRestartOnExit(false);
                        _restartPending = false;
                        ShowActionError(id, ex);
                    }
                }).CallDeferred();
            }
            else if (primary != null) _ = Subscribe(id);
        }
        catch (Exception ex) { ShowActionError(id, ex); }
    }
    private void OpenItem(ulong id)
    {
        if (_closed || !SkinWorkshopService.CanReadMetadata(id)) return;
        try
        {
            _actionErrors.Remove(id);
            WorkshopCommunityLinks.OpenItem(id);
        }
        catch (Exception ex) { ShowActionError(id, ex); }
    }
    private void OpenSubmission(Button button)
    {
        try
        {
            if (_submission != null) return;
            ResetHover();
            _submission=WorkshopSubmissionPanel.Show(this,()=> { _submission=null; if(!_closed)Poll(); });
            UpdateVisibleActions();
            button.Text = WorkshopCommunityText.Get(WorkshopCommunityTextKey.SubmitMod);
            button.TooltipText = "";
        }
        catch (Exception ex)
        {
            button.Text = WorkshopCommunityText.Get(WorkshopCommunityTextKey.OpenFailed);
            button.TooltipText = ex.GetBaseException().Message;
            ModLog.Warn("打开模组投稿区失败：" + ex.GetBaseException().Message);
        }
    }
    private void ShowActionError(ulong id, Exception ex)
    {
        ModLog.Warn($"工坊皮肤 {id} 操作失败：{ex.GetBaseException().Message}");
        if (_closed || !GodotObject.IsInstanceValid(this)) return;
        _actionErrors[id] = ex.GetBaseException().Message;
        var row = _actions.FirstOrDefault(item => item.Id == id);
        if (GodotObject.IsInstanceValid(row.Status)) { row.Status.Text = WorkshopText.Get(WorkshopTextKey.Failed); row.Status.TooltipText = ex.GetBaseException().Message; }
    }
}
