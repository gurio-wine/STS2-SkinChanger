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
            if (SkinSourceIdentity.IsGitHub(id))
            {
                // Installing writes into the game's mods folder; the game loads it on next start.
                if (GitHubSkinService.IsInstalling(id) || GitHubSkinService.IsScanning(id)) return;
                PerformGitHub(id);
                return;
            }
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
        if (_closed) return;
        if (SkinSourceIdentity.IsGitHub(id))
        {
            try
            {
                _actionErrors.Remove(id);
                var url = GitHubSkinService.RepoUrl(id);
                if (url.Length > 0) OS.ShellOpen(url);
            }
            catch (Exception ex) { ShowActionError(id, ex); }
            return;
        }
        if (!SkinWorkshopService.CanReadMetadata(id)) return;
        try
        {
            _actionErrors.Remove(id);
            WorkshopCommunityLinks.OpenItem(id);
        }
        catch (Exception ex) { ShowActionError(id, ex); }
    }

    // One button, three meanings: an installed skin is removed, a waiting update is installed,
    // an installable skin is installed, and a repository without a code is scanned instead -
    // that scan is what produces the code its author publishes as sc.info.
    private void PerformGitHub(ulong id)
    {
        var state = GitHubSkinService.InstallState(id);
        if (state == GitHubInstallState.Installed) { _ = UninstallGitHub(id); return; }
        if (state == GitHubInstallState.Updatable || GitHubSkinService.IsRecognized(id)) { _ = InstallGitHub(id); return; }
        _ = ScanGitHub(id);
    }

    private async Task ScanGitHub(ulong id)
    {
        var polling = BusyPolling();
        try
        {
            using var token = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var result = await GitHubSkinService.ScanAsync(id, token.Token);
            if (_closed || !GodotObject.IsInstanceValid(this) || !IsInsideTree()) return;
            OpenScanResult(id, result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowActionError(id, ex); }
        finally
        {
            polling.QueueFree();
            if (!_closed && GodotObject.IsInstanceValid(this) && IsInsideTree()) Poll();
        }
    }

    // A download and a scan take seconds, and this panel only refreshes on demand. Without its
    // own ticker the row would sit on the old text until the work is already done.
    private Godot.Timer BusyPolling()
    {
        var timer = new Godot.Timer { WaitTime = .25, ProcessMode = ProcessModeEnum.Always };
        timer.Timeout += () =>
        {
            if (_closed || !GodotObject.IsInstanceValid(this) || !IsInsideTree()) { timer.QueueFree(); return; }
            Poll();
        };
        AddChild(timer); timer.Start();
        return timer;
    }

    // The codes live only in this window: nothing is saved, so a repository stays "not
    // recognized" until its author actually publishes them as sc.info.
    private void OpenScanResult(ulong id, GitHubScanResult result)
    {
        try
        {
            if (_scan != null) return;
            ResetHover();
            var entry = GitHubSkinService.Entry(id);
            var details = result.Item is { } item
                ? string.Join(" · ", item.Targets.Select(target => _names.Name(target.Kind, target.Target)).Distinct())
                : "";
            // The same handle the card wears, so the window and the row it came from read alike.
            _scan = GitHubScanPanel.Show(this, entry is null ? "" : GitHubSkinPolicy.Handle(entry.FullName),
                details, result, () => { _scan = null; if (!_closed) Poll(); });
            UpdateVisibleActions();
        }
        catch (Exception ex) { ShowActionError(id, ex); }
    }

    private void PerformGitHubSecondary(ulong id)
    {
        if (_closed || _restartPending || GitHubSkinService.IsInstalling(id)) return;
        // The button is only shown while an update is waiting; that is when removing the
        // installed copy is the alternative the player might actually want.
        if (GitHubSkinService.InstallState(id) != GitHubInstallState.Updatable) return;
        _actionErrors.Remove(id);
        _ = UninstallGitHub(id);
    }

    private async Task UninstallGitHub(ulong id)
    {
        try { await GitHubSkinService.UninstallAsync(id); }
        catch (Exception ex) { ShowActionError(id, ex); }
        finally { if (!_closed && GodotObject.IsInstanceValid(this) && IsInsideTree()) Poll(); }
    }

    private async Task InstallGitHub(ulong id)
    {
        var polling = BusyPolling();
        try
        {
            using var token = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await GitHubSkinService.InstallAsync(id, token.Token);
        }
        catch (Exception ex) { ShowActionError(id, ex); }
        finally
        {
            polling.QueueFree();
            if (!_closed && GodotObject.IsInstanceValid(this) && IsInsideTree()) Poll();
        }
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
