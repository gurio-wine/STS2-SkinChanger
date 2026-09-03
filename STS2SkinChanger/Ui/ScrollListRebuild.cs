using Godot;

namespace STS2SkinChanger.Ui;

// Rebuild the rows, not the viewport. Keeping the ScrollContainer in the scene tree also
// keeps its scrollbars/ranges alive until Godot lays out the completed replacement rows.
// No delayed position setter can race a subsequent click, scroll or category change.
internal static class ScrollListRebuild
{
    private const string ScrollName = "SkinChangerRetainedListScroll";
    private const string ContextMeta = "skin_changer_list_context";

    internal static ScrollContainer Begin(VBoxContainer content, string contextId)
    {
        var scroll = content.GetNodeOrNull<ScrollContainer>(ScrollName);
        if (scroll == null)
        {
            scroll = new ScrollContainer { Name = ScrollName };
            content.AddChild(scroll);
        }

        foreach (var child in content.GetChildren())
        {
            if (child == scroll)
            {
                continue;
            }
            content.RemoveChild(child);
            child.QueueFree();
        }
        // Native scrollbars are internal children and are excluded by GetChildren().
        foreach (var child in scroll.GetChildren())
        {
            scroll.RemoveChild(child);
            child.QueueFree();
        }

        var previousContext = scroll.GetMeta(ContextMeta, string.Empty).AsString();
        if (!previousContext.Equals(contextId, StringComparison.OrdinalIgnoreCase))
        {
            scroll.ScrollHorizontal = 0;
            scroll.ScrollVertical = 0;
        }
        scroll.SetMeta(ContextMeta, contextId);
        return scroll;
    }

    internal static void PlaceAfterHeader(ScrollContainer scroll)
    {
        var content = scroll.GetParent();
        content.MoveChild(scroll, content.GetChildCount() - 1);
    }
}
