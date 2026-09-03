using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// On-demand diagnostics for an explicit library skin change, not a per-frame card logger.
// Inspect existing nodes/resources only: calling Model.Portrait or loading an image here would
// itself change the caches we are trying to observe.
internal static class CardRefreshDiagnostics
{
    private static readonly ConditionalWeakTable<NCard, TraceState> Traces = new();
    private static long _sequence;

    public static void Begin(NCard card, bool enabled)
    {
        if (!enabled || !GodotObject.IsInstanceValid(card) || card.Model == null)
        {
            return;
        }

        try
        {
            var state = new TraceState(
                ++_sequence, card.Model, SkinService.GetEffectiveCardSelection(card.Model));
            Traces.Remove(card);
            Traces.Add(card, state);
            Record(card, "before");
        }
        catch (Exception exception)
        {
            ModLog.Warn("启动卡面切换诊断失败：" + exception.GetBaseException().Message);
        }
    }

    public static void Record(NCard card, string stage, MethodBase? source = null)
    {
        if (!Traces.TryGetValue(card, out var state) || !state.Active ||
            !ReferenceEquals(card.Model, state.Model))
        {
            return;
        }

        WriteSnapshot(card, state, source == null ? stage : source.Name + "/" + stage);
    }

    public static void End(NCard card)
    {
        if (!Traces.TryGetValue(card, out var state) || !state.Active)
        {
            return;
        }

        Record(card, "complete");
        state.Active = false;
        try
        {
            var tree = card.GetTree();
            if (tree == null) return;
            var weakCard = new WeakReference<NCard>(card);
            tree.CreateTimer(0.2).Timeout += () =>
            {
                if (weakCard.TryGetTarget(out var current) && GodotObject.IsInstanceValid(current) &&
                    !current.IsQueuedForDeletion() && ReferenceEquals(current.Model, state.Model) &&
                    Traces.TryGetValue(current, out var latest) && ReferenceEquals(latest, state))
                {
                    WriteSnapshot(current, state, "settled-200ms");
                }
            };
        }
        catch (Exception exception)
        {
            ModLog.Warn("安排卡面延迟诊断失败：" + exception.GetBaseException().Message);
        }
    }

    private static void WriteSnapshot(NCard card, TraceState state, string stage)
    {
        try
        {
            var nodes = new[]
            {
                "%PortraitCanvasGroup", "%Portrait", "%AncientPortrait",
                "%Frame", "%AncientBorder", "%AncientTextBg"
            };
            ModLog.Info($"[card-refresh:{state.Sequence}] card={state.Model.Id}, " +
                $"node={card.GetInstanceId()}, selected={state.Selection}, stage={stage}; " +
                string.Join("; ", nodes.Select(path => NodeSnapshot(card, path))));
        }
        catch (Exception exception)
        {
            // Optional diagnostics must never interrupt a card refresh.
            ModLog.Warn("记录卡面切换状态失败：" + exception.GetBaseException().Message);
        }
    }

    private static string NodeSnapshot(NCard card, string path)
    {
        var node = card.GetNodeOrNull<CanvasItem>(path);
        if (node == null || !GodotObject.IsInstanceValid(node)) return path + "=missing";
        var description = $"{path}:visible={node.Visible}/{node.IsVisibleInTree()}, " +
            $"color={node.Modulate}/{node.SelfModulate}, material={node.Material?.ResourcePath}";
        if (node is Control control)
        {
            description += $", pos={control.Position}, size={control.Size}, scale={control.Scale}";
        }
        if (node is TextureRect rect)
        {
            description += ", texture=" + TextureSnapshot(rect.Texture);
        }
        if (node is CanvasGroup && node.Material is ShaderMaterial shader)
        {
            description += ", mask=" + TextureSnapshot(shader.GetShaderParameter("mask_texture").AsGodotObject() as Texture2D) +
                ", mask_region=" + shader.GetShaderParameter("mask_region");
        }
        return description;
    }

    private static string TextureSnapshot(Texture2D? texture)
    {
        if (texture == null) return "null";
        if (!GodotObject.IsInstanceValid(texture)) return "freed";
        var description = $"{texture.GetType().Name}#{texture.GetInstanceId()} " +
            $"{texture.GetWidth()}x{texture.GetHeight()} {texture.ResourcePath}";
        if (texture is AtlasTexture atlas)
        {
            description += $" region={atlas.Region} margin={atlas.Margin}, atlas=";
            var image = atlas.Atlas;
            description += image != null && GodotObject.IsInstanceValid(image)
                ? $"{image.GetType().Name}#{image.GetInstanceId()} {image.GetWidth()}x{image.GetHeight()} {image.ResourcePath}"
                : "null/freed";
        }
        return description;
    }

    private sealed class TraceState(long sequence, CardModel model, string selection)
    {
        public long Sequence { get; } = sequence;
        public CardModel Model { get; } = model;
        public string Selection { get; } = selection;
        public bool Active { get; set; } = true;
    }
}
