using STS2SkinChanger.Core;

namespace STS2SkinChanger;

/// <summary>Entry for in-memory adapted provider settings. Unknown providers have no writable targets.</summary>
public static class ProviderSettingsApi
{
    public static int Refresh(Type providerType) => ManagedMerchantSettingsBridge.RequestRefresh(providerType.Assembly);
}
