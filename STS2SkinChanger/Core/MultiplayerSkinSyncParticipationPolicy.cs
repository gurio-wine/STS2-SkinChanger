namespace STS2SkinChanger.Core;

internal readonly record struct MultiplayerSkinSyncParticipationDecision(
    bool AttachTransport,
    bool WriteCapabilityTrailer,
    bool ReadCapabilityTrailer,
    bool ApplyRemoteAppearance);

internal static class MultiplayerSkinSyncParticipationPolicy
{
    public static MultiplayerSkinSyncParticipationDecision Resolve(
        bool enabled,
        bool isMultiplayer)
    {
        var participate = enabled && isMultiplayer;
        return new MultiplayerSkinSyncParticipationDecision(
            AttachTransport: participate,
            WriteCapabilityTrailer: participate,
            ReadCapabilityTrailer: participate,
            ApplyRemoteAppearance: participate);
    }
}
