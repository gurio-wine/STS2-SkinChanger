using Godot;
using MegaCrit.Sts2.Core.Nodes;
using Steamworks;
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
        if (_closed || !SkinWorkshopService.Catalog.Any(i => i.Id == id)) return;
        try
        {
            _actionErrors.Remove(id);
            if (SteamUtils.IsOverlayEnabled()) SteamFriends.ActivateGameOverlayToWebPage(WorkshopItemActions.ItemUrl(id, false));
            else if (OS.ShellOpen(WorkshopItemActions.ItemUrl(id, true)) != Error.Ok)
                throw new IOException("Steam could not open the Workshop item.");
        }
        catch (Exception ex) { ShowActionError(id, ex); }
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
