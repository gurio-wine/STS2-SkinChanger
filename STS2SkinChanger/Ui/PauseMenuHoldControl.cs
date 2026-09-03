using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class PauseMenuHoldControl : Node
{
    private static readonly ConditionalWeakTable<NClickableControl, PauseMenuHoldControl> Bindings = new();
    private readonly PauseMenuHoldGesture _gesture = new();
    private NClickableControl _button = null!;
    private Action _onHold = null!;

    internal static void Attach(NPauseMenuButton button, Action onHold, ModText tooltip)
    {
        if (Bindings.TryGetValue(button, out _))
        {
            return;
        }

        var binding = new PauseMenuHoldControl
        {
            Name = "SkinChangerHoldGesture",
            _button = button,
            _onHold = onHold,
            ProcessMode = ProcessModeEnum.Always
        };
        button.AddChild(binding);
        binding.SetProcess(false);
        Bindings.Add(button, binding);
        button.Connect(NClickableControl.SignalName.Unfocused, Callable.From<NClickableControl>(
            _ => binding.Cancel()));
        button.VisibilityChanged += () =>
        {
            if (!button.IsVisibleInTree())
            {
                binding.Cancel();
            }
        };
        ModLocalization.Bind(button, () => button.TooltipText = ModLocalization.Get(tooltip));
    }

    internal static void Begin(NClickableControl button)
    {
        if (Bindings.TryGetValue(button, out var binding) &&
            button.IsEnabled && button.IsVisibleInTree())
        {
            binding._gesture.Begin(Time.GetTicksMsec());
            binding.SetProcess(true);
        }
    }

    internal static bool ConsumeClick(NClickableControl button)
    {
        if (!Bindings.TryGetValue(button, out var binding))
        {
            return false;
        }

        // A long frame can deliver release before _Process gets a chance to run.
        binding.UpdateHold();
        binding.SetProcess(false);
        return binding._gesture.ConsumeRelease();
    }

    public override void _Process(double delta) => UpdateHold();

    private void UpdateHold()
    {
        if (!_button.IsEnabled || !_button.IsVisibleInTree() || !_button.GetWindow().HasFocus())
        {
            Cancel();
            return;
        }
        if (!_gesture.Advance(Time.GetTicksMsec()))
        {
            return;
        }
        SetProcess(false);
        try
        {
            _onHold();
        }
        catch (Exception exception)
        {
            ModLog.Error("切换外观入口可见性失败：" + exception);
        }
    }

    private void Cancel()
    {
        _gesture.Cancel();
        SetProcess(false);
    }

    public override void _ExitTree() => Cancel();
}

// Track the game's accepted press, not an independent mouse poll. This also supports
// keyboard/controller selection and leaves all unregistered game buttons unchanged.
[HarmonyPatch(typeof(NClickableControl), "OnPressHandler")]
internal static class PauseMenuAppearanceHoldPressPatch
{
    private static void Postfix(NClickableControl __instance) => PauseMenuHoldControl.Begin(__instance);
}

[HarmonyPatch(typeof(NPauseMenu), "OnBackOrResumeButtonPressed")]
internal static class PauseMenuAppearanceResumeHoldPatch
{
    private static bool Prefix(NButton __0) => !PauseMenuHoldControl.ConsumeClick(__0);
}
