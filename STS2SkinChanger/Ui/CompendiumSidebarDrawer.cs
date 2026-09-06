using Godot;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace STS2SkinChanger.Ui;

internal static class CompendiumSidebarPolicy
{
    public static float X(float viewportWidth, float panelWidth, bool expanded) =>
        viewportWidth - panelWidth * (expanded ? 1f : .1f);
    public static bool ShouldExpand(bool pointerInside, bool popupOpen, bool keyboardFocus, bool allowed) =>
        allowed && (pointerInside || popupOpen || keyboardFocus);
}

// Native signals only: no generated Godot virtual bridge or permanent frame poll.
internal sealed class CompendiumSidebarDrawer
{
    private readonly Control _panel;
    private readonly Control _parent;
    private readonly Func<bool> _canInteract;
    private readonly HashSet<PopupMenu> _openPopups = [];
    private Window? _window;
    private Tween? _tween;
    private bool _expanded;
    private bool _keyboard;

    public CompendiumSidebarDrawer(Control panel, Func<bool> canInteract)
    {
        _panel = panel;
        _parent = panel.GetParent<Control>();
        _canInteract = canInteract;
        panel.TreeEntered += Connect;
        panel.TreeExiting += Disconnect;
        panel.VisibilityChanged += () => { if (panel.IsVisibleInTree()) Connect(); else Disconnect(); };
        _parent.Resized += Reposition;
        panel.Resized += Reposition;
        Connect();
    }

    public void KeepOpenFor(OptionButton selector)
    {
        var popup = selector.GetPopup();
        popup.AboutToPopup += () => { _openPopups.Add(popup); Refresh(); };
        popup.PopupHide += () => { _openPopups.Remove(popup); Refresh(); };
    }

    private void Connect()
    {
        if (_window != null || !_panel.IsInsideTree() || !_panel.IsVisibleInTree()) return;
        _window = _panel.GetWindow();
        _window.WindowInput += OnInput;
        _expanded = false;
        _keyboard = false;
        Reposition();
    }

    private void Disconnect()
    {
        if (GodotObject.IsInstanceValid(_window)) _window!.WindowInput -= OnInput;
        _window = null;
        _tween?.Kill();
        _tween = null;
        _openPopups.Clear();
        _expanded = false;
        _keyboard = false;
    }

    private void OnInput(InputEvent input)
    {
        if (input is InputEventMouse) _keyboard = false;
        else if (input is InputEventKey { Pressed: true } or InputEventJoypadButton { Pressed: true } ||
                 input is InputEventJoypadMotion { AxisValue: > .35f or < -.35f }) _keyboard = true;
        else return;
        Refresh();
    }

    public void Refresh()
    {
        if (_window == null || !_panel.IsVisibleInTree()) return;
        var focus = _panel.GetViewport().GuiGetFocusOwner();
        var expanded = CompendiumSidebarPolicy.ShouldExpand(
            _panel.GetGlobalRect().HasPoint(_panel.GetGlobalMousePosition()), _openPopups.Count > 0,
            _keyboard && focus != null && (_panel == focus || _panel.IsAncestorOf(focus)), _canInteract());
        if (expanded == _expanded) return;
        _expanded = expanded;
        _tween?.Kill();
        var target = TargetPosition();
        // Respect the game's instant-animation preference; don't introduce another setting.
        if (SaveManager.Instance?.PrefsSave?.FastMode == FastModeType.Instant)
        {
            _panel.Position = target;
            return;
        }
        _tween = _panel.CreateTween().SetIgnoreTimeScale().SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        _tween.TweenProperty(_panel, "position", target, expanded ? .20 : .15);
    }

    private Vector2 TargetPosition() => new(
        CompendiumSidebarPolicy.X(_parent.Size.X, _panel.Size.X, _expanded), 0);

    private void Reposition()
    {
        if (!GodotObject.IsInstanceValid(_panel) || !_panel.IsInsideTree()) return;
        _tween?.Kill();
        _tween = null;
        _panel.Position = TargetPosition();
    }
}
