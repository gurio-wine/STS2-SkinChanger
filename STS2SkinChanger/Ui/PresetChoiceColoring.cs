using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

/// <summary>Use the same native popup + colored ItemList pattern as the character bundle selector.</summary>
internal static class PresetChoiceColoring
{
    private const string ListName = "SCColoredChoices";

    internal static void Attach(OptionButton picker, Func<string, bool>? isAccented = null, bool colorSelection = true)
    {
        isAccented ??= BundlePresetPolicy.IsOwned;
        var popup = picker.GetPopup();
        // Skin choices are populated lazily and repeatedly. Reuse one native popup child and
        // refresh immediately too: the first Attach can run inside AboutToPopup itself.
        if (popup.GetNodeOrNull<ItemList>(ListName) is { } existing)
        {
            RefreshItems(picker, existing, isAccented);
            return;
        }
        var list = new ItemList
        {
            Name = ListName,
            MouseFilter = Control.MouseFilterEnum.Stop, AllowReselect = true,
            MaxColumns = 1, ZIndex = 100
        };
        popup.AddChild(list);
        list.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        list.AddThemeFontSizeOverride("font_size", colorSelection ? 19 : picker.GetThemeFontSize("font_size"));
        list.AddThemeColorOverride("font_color", new Color("fff6e2"));
        if (ContextualSkinControls.GameFont is { } font) list.AddThemeFontOverride("font", font);
        list.AddThemeStyleboxOverride("panel", ContextualSkinControls.CreateStyleBox(
            new Color("45104e"), new Color("79547e"), 2));
        list.AddThemeStyleboxOverride("hovered", ContextualSkinControls.CreateStyleBox(
            new Color("2c586f"), new Color("afcdde")));
        list.AddThemeStyleboxOverride("selected", ContextualSkinControls.CreateStyleBox(
            new Color("58205f"), new Color("efc850"), 2));
        ModThemeRuntime.ItemList(list);
        bool Owned(int index) => index >= 0 && index < picker.ItemCount &&
            isAccented(picker.GetItemMetadata(index).AsString());
        void StyleSelection()
        {
            if (!colorSelection) return;
            var color = Owned(picker.Selected) ? ModThemeRuntime.Accent : ModThemeRuntime.Text;
            foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                picker.AddThemeColorOverride(state, color);
        }
        void Choose(long index)
        {
            if (index < 0 || index >= picker.ItemCount || picker.IsItemDisabled((int)index)) return;
            picker.Select((int)index);
            popup.Hide();
            picker.EmitSignal(OptionButton.SignalName.ItemSelected, index);
        }
        list.ItemClicked += (index, _, button) => { if (button == (long)MouseButton.Left) Choose(index); };
        list.ItemActivated += Choose;
        // Preserve the native popup's focus contract, including single-card hover previews.
        var focused = -1;
        void Focus(int index)
        {
            if (index < 0 || index >= picker.ItemCount || focused == index) return;
            focused = index;
            popup.SetFocusedItem(index);
            popup.EmitSignal(PopupMenu.SignalName.IdFocused, picker.GetItemId(index));
        }
        if (!colorSelection)
        {
            list.GuiInput += ev =>
            {
                if (ev is InputEventMouseMotion) Focus(list.GetItemAtPosition(list.GetLocalMousePosition(), true));
            };
            list.ItemSelected += index => Focus((int)index);
        }
        picker.ItemSelected += _ => StyleSelection();
        popup.AboutToPopup += () =>
        {
            focused = -1;
            RefreshItems(picker, list, isAccented);
        };
        ModThemeRuntime.Bind(picker, "preset_colors", _ =>
        {
            StyleSelection();
            for (var i = 0; i < Math.Min(list.ItemCount, picker.ItemCount); i++)
                list.SetItemCustomFgColor(i, Owned(i) ? ModThemeRuntime.Accent : ModThemeRuntime.Text);
        });
        RefreshItems(picker, list, isAccented);
    }

    private static void RefreshItems(OptionButton picker, ItemList list, Func<string, bool> isAccented)
    {
        list.Clear();
        for (var i = 0; i < picker.ItemCount; i++)
        {
            list.AddItem(picker.GetItemText(i));
            list.SetItemDisabled(i, picker.IsItemDisabled(i));
            if (isAccented(picker.GetItemMetadata(i).AsString())) list.SetItemCustomFgColor(i, ModThemeRuntime.Accent);
        }
        if (picker.Selected >= 0 && picker.Selected < list.ItemCount) list.Select(picker.Selected);
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(list) || !GodotObject.IsInstanceValid(picker)) return;
            var popup = picker.GetPopup();
            if (!popup.Visible) return;
            list.Position = Vector2.Zero;
            list.Size = popup.Size;
            list.GrabFocus();
        }).CallDeferred();
    }
}
