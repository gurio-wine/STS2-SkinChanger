using Steamworks;

namespace STS2SkinChanger.Core;

internal static class WorkshopMetadataReader
{
    public static WorkshopDetails Read(SteamUGCDetails_t item, string preview, Func<EItemStatistic, ulong?> statistic) => new(item.m_rgchTitle, preview)
    {
        Subscriptions = statistic(EItemStatistic.k_EItemStatistic_NumSubscriptions),
        LifetimeSubscriptions = statistic(EItemStatistic.k_EItemStatistic_NumUniqueSubscriptions),
        Favorites = statistic(EItemStatistic.k_EItemStatistic_NumFavorites),
        Comments = statistic(EItemStatistic.k_EItemStatistic_NumComments),
        Score = float.IsFinite(item.m_flScore) ? Math.Clamp(item.m_flScore, 0, 1) : null,
        VotesUp = item.m_unVotesUp, VotesDown = item.m_unVotesDown,
        Created = item.m_rtimeCreated, Updated = item.m_rtimeUpdated
    };
}
