namespace STS2SkinChanger.Core;

internal readonly record struct MultiplayerSkinSyncParticipationDecision(
    bool AttachTransport,
    bool WriteCapabilityTrailer,
    bool ReadCapabilityTrailer,
    bool ApplyRemoteAppearance,
    bool SendLocalAppearance);

internal static class MultiplayerSkinSyncParticipationPolicy
{
    public static MultiplayerSkinSyncParticipationDecision Resolve(
        bool sendChanges,
        bool receiveChanges,
        bool isMultiplayer)
    {
        var participate = (sendChanges || receiveChanges) && isMultiplayer;
        return new MultiplayerSkinSyncParticipationDecision(
            AttachTransport: participate,
            WriteCapabilityTrailer: participate,
            ReadCapabilityTrailer: participate,
            ApplyRemoteAppearance: receiveChanges && isMultiplayer,
            SendLocalAppearance: sendChanges && isMultiplayer);
    }
}
