namespace STS2SkinChanger.Ui.LoadOrderWarning;

// F35：自动排序不能反复撤销玩家的手动排序。上次自动排序后仍留在本 Mod 之上的提供者是玩家主动
// 上移的选择，继续尊重；只有新出现在上面的（新安装、或玩家上移了未记录项）才需要再次自动排序
// 并提醒重启。
internal static class LoadOrderRespectPolicy
{
    internal static (IReadOnlyList<string> Deliberate, IReadOnlyList<string> Actionable) Resolve(
        IEnumerable<string> providersAboveSelf,
        IEnumerable<string> rememberedProvidersAboveSelf)
    {
        var remembered = new HashSet<string>(rememberedProvidersAboveSelf, StringComparer.OrdinalIgnoreCase);
        var deliberate = new List<string>();
        var actionable = new List<string>();
        foreach (var provider in providersAboveSelf)
        {
            if (remembered.Remove(provider))
            {
                deliberate.Add(provider);
            }
            else
            {
                actionable.Add(provider);
            }
        }

        return (deliberate, actionable);
    }
}
