using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// Lease only the properties we change. No full layout restore, no global rarity patch.
internal static class CardSurfaceView
{
    private sealed record StretchLease(TextureRect Node, TextureRect.StretchModeEnum Original,
        TextureRect.StretchModeEnum Applied);
    private static readonly ConditionalWeakTable<NCard, StretchLease> Stretches = new();
    private sealed record BannerLease(CardModel Card, Control Node, Color Original)
    {
        public Color? Applied { get; set; }
    }
    private static readonly ConditionalWeakTable<NTinyCard, BannerLease> Banners = new();
    private static readonly System.Reflection.FieldInfo? BannerField = AccessTools.Field(typeof(NTinyCard), "_cardBanner");

    internal static void ApplyPortrait(NCard card, TextureRect target)
    {
        var mode = card.Model == null ? null : SkinService.GetCardSurface(card.Model)?.PortraitStretchMode;
        if (Stretches.TryGetValue(card, out var current) && ReferenceEquals(current.Node, target) &&
            mode == (int)current.Applied)
        {
            target.StretchMode = current.Applied;
            return;
        }
        Release(card);
        if (mode is not (>= 0 and <= 6)) return;
        var applied = (TextureRect.StretchModeEnum)mode.Value;
        Stretches.Add(card, new(target, target.StretchMode, applied));
        target.StretchMode = applied;
    }

    internal static void Release(NCard card)
    {
        if (!Stretches.TryGetValue(card, out var lease)) return;
        Stretches.Remove(card);
        if (GodotObject.IsInstanceValid(lease.Node) && lease.Node.StretchMode == lease.Applied)
            lease.Node.StretchMode = lease.Original;
    }

    internal static void BindTinyCard(NTinyCard tiny, CardModel card)
    {
        Banners.Remove(tiny);
        // SetCard has already rebuilt the native banner, so never reuse the prior model's color.
        if (BannerField?.GetValue(tiny) is not Control banner) return;
        var lease = new BannerLease(card, banner, banner.Modulate);
        Banners.Add(tiny, lease);
        ApplyBanner(lease);
    }

    internal static void ForgetTinyCard(NTinyCard tiny) => Banners.Remove(tiny);

    private static void ApplyBanner(BannerLease lease)
    {
        var surface = SkinService.GetCardSurface(lease.Card);
        if (surface?.TinyBannerColor is { } color && surface.ExcludedBannerRarity != (int)lease.Card.Rarity)
        {
            lease.Applied = new Color(color.R, color.G, color.B, color.A);
            lease.Node.Modulate = lease.Applied.Value;
        }
        else if (lease.Applied is { } previous)
        {
            if (lease.Node.Modulate == previous) lease.Node.Modulate = lease.Original;
            lease.Applied = null;
        }
    }

    internal static void RefreshTinyCards()
    {
        foreach (var pair in Banners)
        {
            if (!GodotObject.IsInstanceValid(pair.Key) || !GodotObject.IsInstanceValid(pair.Value.Node))
            { Banners.Remove(pair.Key); continue; }
            ApplyBanner(pair.Value);
        }
    }
}

[HarmonyPatch(typeof(NTinyCard), nameof(NTinyCard.SetCard))]
internal static class TinyCardSelectedSurfacePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NTinyCard __instance, CardModel card) => CardSurfaceView.BindTinyCard(__instance, card);
}

[HarmonyPatch(typeof(NTinyCard), nameof(NTinyCard.Set))]
internal static class TinyCardAnonymousSurfacePatch
{
    // Generic tiny icons carry no card identity and must always remain untouched.
    private static void Prefix(NTinyCard __instance) => CardSurfaceView.ForgetTinyCard(__instance);
}
