using STS2SkinChanger.Core;

internal static class GitHubSourceIdentityTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        var a = SkinSourceIdentity.GitHubId("gurio-wine", "SilentSkinMod");
        var b = SkinSourceIdentity.GitHubId("Gurio-Wine", "silentskinmod");
        Require(a == b, "同一仓库的大小写差异必须得到同一个合成 ID，否则缓存和选中项会分裂。");
        Require(a != SkinSourceIdentity.GitHubId("gurio-wine", "OtherMod"), "不同仓库不能得到同一个合成 ID。");
        Require(SkinSourceIdentity.IsGitHub(a) && !SkinSourceIdentity.IsSteam(a), "合成 ID 必须被判为 GitHub 来源。");
        Require((a & SkinSourceIdentity.GitHubMask) != 0 && (a & (SkinSourceIdentity.GitHubMask - 1)) != 0,
            "合成 ID 必须置最高位且使用低位哈希，不能退化成常量。");
        Require(SkinSourceIdentity.IsSteam(2868840UL) && SkinSourceIdentity.IsSteam(3787302680UL),
            "真实工坊 ID 远小于 2^63，不能被误判为 GitHub 来源。");
        Require(!SkinSourceIdentity.IsSteam(0) && !SkinSourceIdentity.IsGitHub(0), "0 既不是工坊物品也不是仓库。");
        var ids = new HashSet<ulong>();
        foreach (var name in new[] { "a", "b", "c", "d", "e" })
            Require(ids.Add(SkinSourceIdentity.GitHubId("owner", name)), "同一 owner 下的不同仓库不能碰撞。");
        Console.WriteLine("GitHub source identity passed: deterministic, case-insensitive, no Steam overlap.");
    }
}
