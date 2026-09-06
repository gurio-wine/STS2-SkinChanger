using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.addons.mega_text;

namespace STS2SkinChanger.Core;

internal static class EventSkinRuntime
{
    internal const string TextureSourceMeta = "sc_event_texture_source";
    internal const string VfxSourceMeta = "sc_event_vfx_source";
    private const string PortraitMeta = "sc_event_portrait";
    private const string PhobiaPortraitMeta = "sc_event_phobia_portrait";
    private static readonly System.Reflection.FieldInfo EventField = AccessTools.Field(typeof(NEventLayout), "_event");

    public static bool IsManaged(EventModel model) => model is not AncientEventModel &&
        SkinService.Catalog?.Groups.Any(group => group.Id == EventSkinPolicy.GroupId(model.Id.Entry)) == true;

    public static void RememberPortrait(NEventLayout layout, Texture2D portrait, Texture2D? phobia)
    {
        string Source(Texture2D texture) => texture.GetMeta(TextureSourceMeta, texture.ResourcePath).AsString();
        var source = Source(portrait);
        if (!EventSkinPolicy.IsEventGroup(SkinService.Catalog?.FindGroupIdForResourcePath(source) ?? "")) return;
        layout.SetMeta(PortraitMeta, source);
        layout.SetMeta(PhobiaPortraitMeta, phobia == null ? "" : Source(phobia));
    }

    // Called only after a successful selection transaction. Do not call SetEvent, BeginEvent,
    // RefreshEventState, AddOptions or OnRoomEnter: those can reset choices, votes and rewards.
    public static void RefreshCurrent(string groupId)
    {
        if (!EventSkinPolicy.IsEventGroup(groupId)) return;
        var layout = NEventRoom.Instance?.Layout;
        if (layout == null || !GodotObject.IsInstanceValid(layout) ||
            EventField.GetValue(layout) is not EventModel model ||
            EventSkinPolicy.GroupId(model.Id.Entry) != groupId) return;
        try
        {
            layout.SetTitle(model.Title.GetFormattedText());
            if (model.Description != null)
                layout.GetNodeOrNull<MegaRichTextLabel>("%EventDescription")?.SetTextAutoSize(model.Description.GetFormattedText());
            foreach (var button in layout.OptionButtons)
            {
                var title = button.Option.Title?.GetFormattedText() ?? "";
                var description = button.Option.Description?.GetFormattedText() ?? "";
                var color = button.Option.IsLocked ? "red" : "gold";
                var text = title.Length == 0 ? description : $"[{color}][b]{title}[/b][/{color}]\n{description}";
                button.GetNodeOrNull<MegaRichTextLabel>("%Text")?.SetTextAutoSize(text);
            }
            var portrait = layout.GetMeta(PortraitMeta, "").AsString();
            if (portrait.Length > 0)
            {
                var phobia = model.HasPhobiaModePortrait
                    ? $"res://images/events/{model.Id.Entry.ToLowerInvariant()}_phobia_mode.png" : "";
                layout.SetPortrait(LoadTexture(groupId, portrait), phobia.Length == 0 ? null : LoadTexture(groupId, phobia));
            }
            var hadVfx = RefreshVfx(layout, groupId, model.HasVfx);
            if (!hadVfx && model.HasVfx)
            {
                var vfx = model.CreateVfx();
                layout.AddVfxAnchoredToPortrait(vfx);
                vfx.Position = EventModel.VfxOffset;
            }
        }
        catch (Exception exception)
        {
            // A presentation failure must not propagate into event progression or poison the
            // saved selection transaction. Existing gameplay nodes remain intact.
            ModLog.Error($"刷新事件外观 {model.Id.Entry} 失败：{exception}");
        }
    }

    internal static Texture2D LoadTexture(string groupId, string path)
    {
        var texture = (Texture2D)SkinService.GetOrLoadRuntimeResource(groupId, path);
        texture.SetMeta(TextureSourceMeta, path);
        return texture;
    }

    private static bool RefreshVfx(Node parent, string groupId, bool enabled)
    {
        var found = false;
        foreach (var child in parent.GetChildren())
        {
            if (child is Node2D old && child.HasMeta(VfxSourceMeta))
            {
                found = true;
                if (enabled)
                {
                    var path = child.GetMeta(VfxSourceMeta).AsString();
                    var replacement = SkinService.InstantiateRuntimeScene<Node2D>(groupId, path);
                    replacement.SetMeta(VfxSourceMeta, path);
                    replacement.Transform = old.Transform;
                    replacement.ZIndex = old.ZIndex;
                    parent.AddChild(replacement);
                    parent.MoveChild(replacement, old.GetIndex());
                }
                parent.RemoveChild(old);
                old.QueueFree();
            }
            else found |= RefreshVfx(child, groupId, enabled);
        }
        return found;
    }
}

[HarmonyPatch]
internal static class EventSkinResourceAvailabilityPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
    [
        AccessTools.PropertyGetter(typeof(EventModel), nameof(EventModel.HasVfx)),
        AccessTools.PropertyGetter(typeof(EventModel), nameof(EventModel.HasPhobiaModePortrait))
    ];
    private static bool Prefix(EventModel __instance, System.Reflection.MethodBase __originalMethod, ref bool __result)
    {
        if (!EventSkinRuntime.IsManaged(__instance)) return true;
        var id = __instance.Id.Entry.ToLowerInvariant();
        var groupId = EventSkinPolicy.GroupId(id);
        var path = __originalMethod.Name == "get_HasVfx" ? $"res://scenes/vfx/events/{id}_vfx.tscn" :
            $"res://images/events/{id}_phobia_mode.png";
        __result = SkinService.Catalog!.HasEventResource(groupId, SkinService.Config.GetSelection(groupId), path);
        return false;
    }
}

[HarmonyPatch(typeof(LocTable), nameof(LocTable.GetRawText))]
internal static class EventSkinTextPatch
{
    [HarmonyPriority(Priority.Last)]
    private static bool Prefix(string ____name, string key, ref string __result)
    {
        if (____name != "events" || SkinService.Catalog == null) return true;
        if (!SkinService.Catalog.TryResolveEventText(key, ModLocalization.CurrentLanguage,
                SkinService.Config.Selections, out var text)) return true;
        __result = text;
        return false;
    }
}

[HarmonyPatch(typeof(EventModel), nameof(EventModel.CreateVfx))]
internal static class EventSkinVfxPatch
{
    private static bool Prefix(EventModel __instance, ref Node2D __result)
    {
        if (!EventSkinRuntime.IsManaged(__instance)) return true;
        var path = $"res://scenes/vfx/events/{__instance.Id.Entry.ToLowerInvariant()}_vfx.tscn";
        try
        {
            __result = SkinService.InstantiateRuntimeScene<Node2D>(EventSkinPolicy.GroupId(__instance.Id.Entry), path);
            __result.SetMeta(EventSkinRuntime.VfxSourceMeta, path);
            return false;
        }
        catch (Exception exception)
        {
            ModLog.Error($"事件特效加载失败 {__instance.Id.Entry}：{exception.Message}");
            return true;
        }
    }
}

[HarmonyPatch(typeof(NEventLayout), nameof(NEventLayout.SetPortrait))]
internal static class EventSkinPortraitTrackingPatch
{
    private static void Postfix(NEventLayout __instance, Texture2D portrait, Texture2D? phobiaModePortrait) =>
        EventSkinRuntime.RememberPortrait(__instance, portrait, phobiaModePortrait);
}
