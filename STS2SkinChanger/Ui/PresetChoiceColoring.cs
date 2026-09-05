using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

/// <summary>Use the same native popup + colored ItemList pattern as the character bundle selector.</summary>
internal static class PresetChoiceColoring
{
    internal static void Attach(OptionButton picker)
    {
        var popup = picker.GetPopup();
        var list = new ItemList
        {
            MouseFilter = Control.MouseFilterEnum.Stop, AllowReselect = true,
            MaxColumns = 1, ZIndex = 100
        };
        popup.AddChild(list);
        list.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        list.AddThemeFontSizeOverride("font_size", 19);
        list.AddThemeColorOverride("font_color", new Color("fff6e2"));
        if (ContextualSkinControls.GameFont is { } font) list.AddThemeFontOverride("font", font);
        list.AddThemeStyleboxOverride("panel", ContextualSkinControls.CreateStyleBox(
            new Color("45104e"), new Color("79547e"), 2));
        list.AddThemeStyleboxOverride("hovered", ContextualSkinControls.CreateStyleBox(
            new Color("2c586f"), new Color("afcdde")));
        list.AddThemeStyleboxOverride("selected", ContextualSkinControls.CreateStyleBox(
            new Color("58205f"), new Color("efc850"), 2));
        bool Owned(int index) => index >= 0 && index < picker.ItemCount &&
            BundlePresetPolicy.IsOwned(picker.GetItemMetadata(index).AsString());
        void StyleSelection()
        {
            var color = new Color(Owned(picker.Selected) ? "efc850" : "fff6e2");
            foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                picker.AddThemeColorOverride(state, color);
        }
        void Choose(long index)
        {
            if (index < 0 || index >= picker.ItemCount) return;
            picker.Select((int)index);
            popup.Hide();
            picker.EmitSignal(OptionButton.SignalName.ItemSelected, index);
        }
        list.ItemClicked += (index, _, button) => { if (button == (long)MouseButton.Left) Choose(index); };
        list.ItemActivated += Choose;
        picker.ItemSelected += _ => StyleSelection();
        popup.AboutToPopup += () =>
        {
            list.Clear();
            for (var i = 0; i < picker.ItemCount; i++)
            {
                list.AddItem(picker.GetItemText(i));
                if (Owned(i)) list.SetItemCustomFgColor(i, new Color("efc850"));
            }
            if (picker.Selected >= 0) list.Select(picker.Selected);
            Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(list) || !popup.Visible) return;
                list.Position = Vector2.Zero;
                list.Size = popup.Size;
                list.GrabFocus();
            }).CallDeferred();
        };
        StyleSelection();
    }
}
