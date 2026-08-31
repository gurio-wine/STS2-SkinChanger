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
    private static bool _autoReorderedThisSession;

    public static void Schedule()
    {
        if (_shownThisSession)
        {
            return;
        }

        if (!SkinService.ShouldShowLoadOrderWarning(
                ManagedSkinModLoader.IsBeforeAllSkinProviders))
        {
            return;
        }

        if (!_autoReorderedThisSession)
        {
            try
            {
                MoveSelfBeforeSkinProviders();
                _autoReorderedThisSession = true;
                ModLog.Info("已自动将皮肤切换器-Skin Changer 移到所有皮肤 Mod 之前；需要重启游戏后才能开始接管。");
            }
            catch (Exception exception)
            {
                ModLog.Error("自动调整皮肤 Mod 加载顺序失败：" + exception.GetBaseException().Message);
                return;
            }
        }

        _pending = true;
        ModLog.Info("已准备显示“需要重启才能生效”的加载顺序提示。");
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
                BuildLoadOrderMessage());
            verticalPopup.YesButton.SetText(ModLocalization.Get(ModText.PrioritizeAndRestart));
            verticalPopup.NoButton.SetText(ModLocalization.Get(ModText.Acknowledge));
            _shownThisSession = true;
            ModLog.Info("已显示需要重启才能生效的加载顺序提示框。");

            var restartRequested = await confirmation;
            if (restartRequested)
            {
                RestartGame(verticalPopup);
            }
            else
            {
                ModLog.Info("玩家选择“知道了”，已自动调整加载顺序；等待玩家手动重启后生效。");
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

    private static void RestartGame(NVerticalPopup popup)
    {
        try
        {
            // Let Godot relaunch the same exported project after the game's normal shutdown
            // path completes. This is supported by both game versions and avoids depending on
            // PowerShell (which also made the button unusable on macOS).
            OS.SetRestartOnExit(true);
            ModLog.Info("玩家选择“重启”，正在重启游戏以应用新的 Mod 加载顺序。");
            popup.QueueFree();
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
            ModLog.Error("按玩家请求重启游戏失败：" + exception.GetBaseException().Message);
            popup.SetText(
                ModLocalization.Get(ModText.LoadOrderTitle),
                ModLocalization.Get(ModText.LoadOrderFailure) + "\n\n" +
                exception.GetBaseException().Message);
        }
    }

    private static string BuildLoadOrderMessage()
    {
        var providers = ManagedSkinModLoader.SkinProvidersBeforeSelf
            .Select(mod => mod.manifest?.name ?? mod.manifest?.id ?? mod.path)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Select(name => "• " + EscapeBbCode(name))
            .ToArray();
        return ModLocalization.Get(ModText.LoadOrderMessage)
            .Replace("{0}", string.Join("\n", providers), StringComparison.Ordinal);
    }

    private static string EscapeBbCode(string value) => value
        .Replace('[', '(')
        .Replace(']', ')')
        .Replace('\r', ' ')
        .Replace('\n', ' ');

    private static void MoveSelfBeforeSkinProviders()
    {
        var self = ModManager.Mods.FirstOrDefault(mod => Entry.IsSelfModId(mod.manifest?.id)) ??
                   throw new InvalidOperationException("当前 Mod 列表中找不到皮肤切换器-Skin Changer。");
        var skinProviders = ManagedSkinModLoader.SkinProvidersInLoadOrder;
        if (skinProviders.Count == 0)
        {
            throw new InvalidOperationException("当前 Mod 列表中找不到需要调整顺序的皮肤 Mod。");
        }

        var settings = SaveManager.Instance.SettingsSave;
        settings.ModSettings ??= new ModSettings();
        var modList = settings.ModSettings.ModList;
        var wasEnabled = modList
            .FirstOrDefault(entry => Entry.IsSelfModId(entry.Id) && entry.Source == self.modSource)?
            .IsEnabled ?? true;
        modList.RemoveAll(entry => Entry.IsSelfModId(entry.Id));
        var insertionIndex = modList.FindIndex(entry => skinProviders.Any(provider =>
            string.Equals(entry.Id, provider.manifest?.id, StringComparison.OrdinalIgnoreCase) &&
            entry.Source == provider.modSource));
        if (insertionIndex < 0)
        {
            // Older settings can omit the source on a retained entry. Fall back to the manifest
            // id while preserving every unrelated mod's relative position.
            insertionIndex = modList.FindIndex(entry => skinProviders.Any(provider =>
                string.Equals(entry.Id, provider.manifest?.id, StringComparison.OrdinalIgnoreCase)));
        }

        if (insertionIndex < 0)
        {
            throw new InvalidOperationException("无法在已保存的 Mod 顺序中定位皮肤 Mod。");
        }

        modList.Insert(insertionIndex, new SettingsSaveMod(self) { IsEnabled = wasEnabled });
        SaveManager.Instance.SaveSettings();
    }

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
