using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Bestiary;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

/// <summary>
/// Finds every button which actually opens the game's Bestiary, including buttons created or
/// reparented by other UI mods.  The button's original Released handler is left untouched; the
/// stack push is redirected only while that button is in Ancient mode.
/// </summary>
internal static class BestiaryEntranceSwitcher
{
    private const string ToggleName = "STS2AncientCompendiumToggle";
    private const string ToggleHostName = "STS2AncientCompendiumToggleHost";
    private static readonly ConditionalWeakTable<NClickableControl, EntranceState> States = new();
    private static NClickableControl? _lastReleased;
    private static int _scanAttempts;
    private static bool _scanScheduled;

    internal static void ScanCompendium(NCompendiumSubmenu compendium)
    {
        if (!GodotObject.IsInstanceValid(compendium))
        {
            return;
        }

        try
        {
            foreach (var control in EnumerateDescendants(compendium).OfType<NClickableControl>())
            {
                if (control.Name.ToString().Equals(ToggleName, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    if (IsBestiaryEntrance(control))
                    {
                        EnsureAttached(control);
                    }
                }
                catch (Exception exception)
                {
                    // A foreign UI may expose a native/custom Callable which GodotSharp cannot
                    // fully materialize. One uninspectable button must never abort opening the
                    // Compendium; skip only that candidate and keep the game menu functional.
                    ModLog.Warn(
                        $"跳过无法检查的图鉴入口 {control.Name}：" +
                        exception.GetBaseException().Message);
                }
            }
        }
        catch (Exception exception)
        {
            // This hook runs inside OnSubmenuOpened. Never let optional entrance decoration
            // interrupt the game's own submenu stack after it has already pushed the page.
            ModLog.Warn("检查怪物图鉴入口失败，已保留原界面：" +
                        exception.GetBaseException().Message);
        }

        ScheduleScan(compendium);
    }

    internal static void RememberReleased(NClickableControl control)
    {
        _lastReleased = control;
        Callable.From(() =>
        {
            if (ReferenceEquals(_lastReleased, control))
            {
                _lastReleased = null;
            }
        }).CallDeferred();
    }

    internal static bool TryRedirectBestiaryOpen(NSubmenuStack stack, NSubmenu screen)
    {
        if (screen is not NBestiary || _lastReleased is not { } released ||
            !GodotObject.IsInstanceValid(released))
        {
            return false;
        }

        _lastReleased = null;
        if (!States.TryGetValue(released, out var state))
        {
            // This is a custom entrance which was not present during the initial scan.  It is
            // still registered now so the small toggle appears the next time the submenu opens.
            EnsureAttached(released);
            return false;
        }

        if (!state.UseAncient)
        {
            return false;
        }

        AncientCompendiumEntry.OpenFromStack(stack);
        return true;
    }

    private static void ScheduleScan(NCompendiumSubmenu compendium)
    {
        if (_scanScheduled || _scanAttempts >= 16)
        {
            return;
        }

        _scanScheduled = true;
        _scanAttempts++;
        Callable.From(() =>
        {
            _scanScheduled = false;
            if (GodotObject.IsInstanceValid(compendium))
            {
                ScanCompendium(compendium);
            }
        }).CallDeferred();
    }

    private static bool IsBestiaryEntrance(NClickableControl control)
    {
        var nodeName = control.Name.ToString();
        if (nodeName.Contains("Bestiary", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var connections = control.GetSignalConnectionList(NClickableControl.SignalName.Released);
        if (connections == null)
        {
            return false;
        }

        foreach (var connection in connections)
        {
            if (connection == null || !connection.TryGetValue("callable", out var raw))
            {
                continue;
            }

            var callable = raw.As<Callable>();
            var methodName = callable.Method?.ToString() ?? string.Empty;
            if (methodName.Contains("Bestiary", StringComparison.OrdinalIgnoreCase) ||
                methodName.Equals("OpenBestiary", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Delegate? callback;
            try
            {
                callback = callable.Delegate;
            }
            catch
            {
                callback = null;
            }

            if (callback?.Method.Name.Contains("Bestiary", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureAttached(NClickableControl control)
    {
        if (!GodotObject.IsInstanceValid(control))
        {
            return;
        }

        var state = States.GetOrCreateValue(control);
        if (state.Labels.Count == 0)
        {
            state.Labels = CaptureLabels(control);
        }

        var toggle = GodotObject.IsInstanceValid(state.Toggle)
            ? state.Toggle
            : EnumerateDescendants(control)
                .OfType<NCompendiumBottomButton>()
                .FirstOrDefault(candidate =>
                    candidate.Name.ToString().Equals(ToggleName, StringComparison.Ordinal));
        if (!GodotObject.IsInstanceValid(toggle))
        {
            toggle = CreateToggle(control, state);
            var host = CreateToggleHost();
            control.AddChild(host);
            host.AddChild(toggle);
            state.ToggleHost = host;
        }

        state.Toggle = toggle;
        state.ToggleHost ??= toggle.GetParent() as Control;
        PositionToggle(control, toggle, state.ToggleHost);
        ApplyMode(control, state);
    }

    private static Control CreateToggleHost() => new()
    {
        Name = ToggleHostName,
        MouseFilter = Control.MouseFilterEnum.Ignore,
        ZIndex = 40
    };

    private static NCompendiumBottomButton CreateToggle(
        NClickableControl target,
        EntranceState state)
    {
        var scenePath = SceneHelper.GetScenePath("screens/main_menu/compendium_bottom_button");
        NCompendiumBottomButton toggle = PreloadManager.Cache.GetScene(scenePath)
            .Instantiate<NCompendiumBottomButton>(PackedScene.GenEditState.Disabled)!;
        toggle.Name = ToggleName;
        toggle.FocusMode = Control.FocusModeEnum.All;
        toggle.MouseFilter = Control.MouseFilterEnum.Stop;
        toggle.ZIndex = 1;

        var icon = toggle.GetNodeOrNull<TextureRect>("Icon");
        if (icon != null)
        {
            icon.Visible = false;
        }

        toggle.Connect(
            NClickableControl.SignalName.Released,
            Callable.From((Action<NButton>)(_ =>
            {
                state.UseAncient = !state.UseAncient;
                ApplyMode(target, state);
            })));
        return toggle;
    }

    private static void PositionToggle(
        NClickableControl target,
        NCompendiumBottomButton toggle,
        Control? host)
    {
        if (host == null)
        {
            return;
        }

        toggle.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        toggle.Position = Vector2.Zero;
        if (toggle.Size.X <= 1f || toggle.Size.Y <= 1f)
        {
            toggle.Size = new Vector2(250f, 100f);
        }

        // NCompendiumBottomButton intentionally animates its own Scale to 1.05 on hover.
        // Scale the wrapper instead so that animation remains a small 5% pulse instead of
        // replacing our 42% compact size and suddenly restoring the full-sized button.
        var compactScale = new Vector2(0.42f, 0.42f);
        host.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        host.Size = toggle.Size;
        host.Scale = compactScale;
        var visibleSize = host.Size * compactScale;
        host.Position = new Vector2(
            Math.Max(target.Size.X - visibleSize.X - 24f, 0f),
            24f);
    }

    private static void ApplyMode(NClickableControl target, EntranceState state)
    {
        foreach (var label in state.Labels)
        {
            SetText(label.Node, state.UseAncient
                ? ModLocalization.Get(ModText.AncientCompendium)
                : label.OriginalText);
        }

        var toggleLabel = state.Toggle?.GetNodeOrNull<MegaLabel>("Label");
        if (toggleLabel != null)
        {
            toggleLabel.AutoSizeEnabled = false;
            toggleLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            toggleLabel.HorizontalAlignment = HorizontalAlignment.Center;
            toggleLabel.VerticalAlignment = VerticalAlignment.Center;
            toggleLabel.AddThemeFontSizeOverride("font_size", 54);
            toggleLabel.Text = "切换";
        }
    }

    private static List<LabelSnapshot> CaptureLabels(NClickableControl target)
    {
        var preferred = new List<Control>();
        if (target is NShortSubmenuButton shortButton)
        {
            var title = shortButton.GetNodeOrNull<MegaLabel>("%Title");
            if (title != null)
            {
                preferred.Add(title);
            }
        }

        if (target is NCompendiumBottomButton bottomButton)
        {
            var label = bottomButton.GetNodeOrNull<MegaLabel>("Label");
            if (label != null)
            {
                preferred.Add(label);
            }
        }

        if (preferred.Count == 0)
        {
            preferred.AddRange(EnumerateDescendants(target)
                .OfType<Control>()
                .Where(control => control is Label or MegaLabel or MegaRichTextLabel)
                .OrderByDescending(control =>
                    control.Name.ToString().Contains("title", StringComparison.OrdinalIgnoreCase) ||
                    control.Name.ToString().Equals("label", StringComparison.OrdinalIgnoreCase))
                .Take(1));
        }

        return preferred
            .Select(control => new LabelSnapshot(control, ReadText(control)))
            .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.OriginalText))
            .ToList();
    }

    private static string ReadText(Control control) => control.Get("text").AsString();

    private static void SetText(Control control, string text)
    {
        if (control is MegaLabel megaLabel)
        {
            megaLabel.SetTextAutoSize(text);
        }
        else
        {
            control.Set("text", text);
        }
    }

    private static IEnumerable<Node> EnumerateDescendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var descendant in EnumerateDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class EntranceState
    {
        public bool UseAncient { get; set; }
        public List<LabelSnapshot> Labels { get; set; } = [];
        public Control? ToggleHost { get; set; }
        public NCompendiumBottomButton? Toggle { get; set; }
    }

    private sealed record LabelSnapshot(Control Node, string OriginalText);
}

[HarmonyPatch(typeof(NClickableControl), "OnReleaseHandler")]
internal static class BestiaryEntranceReleasePatch
{
    private static void Prefix(NClickableControl __instance) =>
        BestiaryEntranceSwitcher.RememberReleased(__instance);
}

[HarmonyPatch(typeof(NClickableControl), nameof(NClickableControl.ForceClick))]
internal static class BestiaryEntranceForceClickPatch
{
    private static void Prefix(NClickableControl __instance) =>
        BestiaryEntranceSwitcher.RememberReleased(__instance);
}

[HarmonyPatch(typeof(NSubmenuStack), nameof(NSubmenuStack.Push))]
internal static class BestiaryEntrancePushPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NSubmenuStack __instance, NSubmenu screen) =>
        !BestiaryEntranceSwitcher.TryRedirectBestiaryOpen(__instance, screen);
}

[HarmonyPatch(typeof(NCompendiumSubmenu), nameof(NCompendiumSubmenu._Ready))]
internal static class BestiaryEntranceReadyPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCompendiumSubmenu __instance) =>
        BestiaryEntranceSwitcher.ScanCompendium(__instance);
}

[HarmonyPatch(typeof(NCompendiumSubmenu), nameof(NCompendiumSubmenu.OnSubmenuOpened))]
internal static class BestiaryEntranceOpenedPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCompendiumSubmenu __instance) =>
        BestiaryEntranceSwitcher.ScanCompendium(__instance);
}
