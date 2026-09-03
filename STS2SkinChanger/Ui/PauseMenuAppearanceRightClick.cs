using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class PauseMenuAppearanceRightClick
{
    internal static void Attach(NPauseMenuButton button, Action onRightClick, ModText tooltip)
    {
        if (button.HasMeta("skin_changer_right_click"))
        {
            return;
        }
        button.SetMeta("skin_changer_right_click", true);
        button.Connect(NClickableControl.SignalName.MousePressed, Callable.From<InputEvent>(input =>
        {
            if (input is not InputEventMouseButton mouse ||
                !PauseMenuAppearanceClickPolicy.ShouldToggleVisibility(
                    mouse.ButtonIndex == MouseButton.Right, mouse.Pressed))
            {
                return;
            }
            try
            {
                onRightClick();
            }
            catch (Exception exception)
            {
                ModLog.Error("切换外观入口可见性失败：" + exception);
            }
            button.AcceptEvent();
        }));
        ModLocalization.Bind(button, () => button.TooltipText = ModLocalization.Get(tooltip));
    }
}
