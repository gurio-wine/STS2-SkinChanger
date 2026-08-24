using System.Diagnostics;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui.LoadOrderWarning;

internal static class LoadOrderWarningController
{
    private static bool _pending;
    private static bool _shownThisSession;

    public static void Schedule()
    {
        if (_shownThisSession)
        {
            return;
        }

        if (!SkinService.ShouldShowLoadOrderWarning(
                ManagedSkinModLoader.IsFirstInLoadOrder))
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
        TaskHelper.RunSafely(ShowWarning(container));
    }

    private static async Task ShowWarning(NModalContainer container)
    {
        try
        {
            var popup = NGenericPopup.Create();
            if (popup == null)
            {
                ModLog.Warn("游戏未能创建加载顺序提示框。");
                _pending = true; // 在下一个钩子重试
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
                ModLog.Error("加载顺序提示框缺少 VerticalPopup 节点，无法显示。");
                popup.QueueFree();
                _pending = true;
                return;
            }

            verticalPopup.SetText(
                ModLocalization.Get(ModText.LoadOrderTitle),
                ModLocalization.Get(ModText.LoadOrderMessage));
            verticalPopup.YesButton.SetText(ModLocalization.Get(ModText.Acknowledge));
            verticalPopup.NoButton.SetText(ModLocalization.Get(ModText.DoNotShowAgain));
            AddPrioritizeAndRestartButton(verticalPopup);
            _shownThisSession = true;
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
        catch (Exception exception)
        {
            ModLog.Error("显示加载顺序提示框失败：" + exception.GetBaseException().Message);
            if (!_shownThisSession)
            {
                _pending = true;
            }
        }
    }

    private static void AddPrioritizeAndRestartButton(NVerticalPopup popup)
    {
        var scene = ResourceLoader.Load<PackedScene>(
            "res://scenes/ui/abandon_run_yes_button.tscn");
        if (scene == null)
        {
            ModLog.Warn("无法加载置顶并重启按钮场景。");
            return;
        }

        var button = scene.Instantiate<NPopupYesNoButton>(PackedScene.GenEditState.Disabled);
        button.Name = "SkinChangerPrioritizeAndRestart";
        button.AnchorLeft = 0.5f;
        button.AnchorTop = 1f;
        button.AnchorRight = 0.5f;
        button.AnchorBottom = 1f;
        button.OffsetLeft = -110f;
        button.OffsetTop = -78f;
        button.OffsetRight = 110f;
        button.OffsetBottom = -6f;
        button.GrowHorizontal = Control.GrowDirection.Both;
        button.GrowVertical = Control.GrowDirection.Begin;
        popup.AddChild(button);
        button.SetText(ModLocalization.Get(ModText.PrioritizeAndRestart));
        button.DisconnectHotkeys();
        button.GetNodeOrNull<CanvasItem>("%HotkeyIcon")?.Hide();
        button.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NButton>(_ => PrioritizeAndRestart(popup)));
    }

    private static void PrioritizeAndRestart(NVerticalPopup popup)
    {
        try
        {
            MoveSelfToFirst();
            StartRestartHelper();
            ModLog.Info("已将皮肤切换器-Skin Changer 置顶，正在重启游戏。");
            popup.GetParent()?.QueueFree();
            Callable.From(() =>
            {
                if (NGame.Instance != null)
                {
                    NGame.Instance.Quit();
                }
                else
                {
                    (Engine.GetMainLoop() as SceneTree)?.Quit();
                }
            }).CallDeferred();
        }
        catch (Exception exception)
        {
            ModLog.Error("置顶并重启失败：" + exception.GetBaseException().Message);
            popup.SetText(
                ModLocalization.Get(ModText.LoadOrderTitle),
                ModLocalization.Get(ModText.LoadOrderFailure) + "\n\n" +
                exception.GetBaseException().Message);
        }
    }

    private static void MoveSelfToFirst()
    {
        var self = ModManager.Mods.FirstOrDefault(mod => Entry.IsSelfModId(mod.manifest?.id)) ??
                   throw new InvalidOperationException("当前 Mod 列表中找不到皮肤切换器-Skin Changer。");
        var settings = SaveManager.Instance.SettingsSave;
        settings.ModSettings ??= new ModSettings();
        var modList = settings.ModSettings.ModList;
        var wasEnabled = modList
            .FirstOrDefault(entry => Entry.IsSelfModId(entry.Id) && entry.Source == self.modSource)?
            .IsEnabled ?? true;
        modList.RemoveAll(entry => Entry.IsSelfModId(entry.Id));
        modList.Insert(0, new SettingsSaveMod(self) { IsEnabled = wasEnabled });
        SaveManager.Instance.SaveSettings();
    }

    private static void StartRestartHelper()
    {
        var executablePath = OS.GetExecutablePath();
        var workingDirectory = System.IO.Path.GetDirectoryName(executablePath) ?? string.Empty;
        var script =
            $"Wait-Process -Id {System.Environment.ProcessId}; " +
            $"Start-Process -FilePath '{EscapePowerShellLiteral(executablePath)}' " +
            $"-WorkingDirectory '{EscapePowerShellLiteral(workingDirectory)}'";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        if (Process.Start(startInfo) == null)
        {
            throw new InvalidOperationException("无法启动游戏重启辅助进程。");
        }
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");
}

[HarmonyPatch(typeof(NModalContainer), nameof(NModalContainer._Ready))]
internal static class LoadOrderWarningModalReadyPatch
{
    private static void Postfix() => LoadOrderWarningController.TryShow();
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
