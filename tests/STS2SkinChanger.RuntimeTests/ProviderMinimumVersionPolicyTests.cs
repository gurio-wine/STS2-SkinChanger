using STS2SkinChanger;

// A package may only be used on a game version its manifest does not declare when there is evidence
// that its code still runs; the declaration itself is never guessed away.
internal static class ProviderMinimumVersionPolicyTests
{
    public static void Run()
    {
        var policy = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ProviderMinimumVersionPolicy", true);
        string Decide(string? declared, string? game, bool hasDll, Func<bool> audit) =>
            policy!.GetMethod("Decide")!.Invoke(null, [declared, game, hasDll, audit])!.ToString()!;

        var audits = 0;
        bool Verified() { audits++; return true; }
        bool Refused() { audits++; return false; }
        bool Never() { throw new InvalidOperationException("不需要核验时不能额外编译皮肤 DLL。"); }

        audits = 0;
        Require(Decide("0.111.0", "0.107.1", true, Verified) == "VerifiedTakeOver" && audits == 1,
            "声明更高游戏版本、但代码已核验可运行的皮肤必须被接管。");
        audits = 0;
        Require(Decide("0.111.0", "0.107.1", true, Refused) == "Unsupported" && audits == 1,
            "代码无法在当前版本运行的皮肤必须保持游戏原加载器的拒绝。");
        audits = 0;
        Require(Decide("0.111.0", "0.107.1", false, Never) == "VerifiedTakeOver",
            "纯资源包没有代码可依赖新版本，不需要核验 DLL 即可接管。");
        audits = 0;
        Require(Decide("0.107.1", "0.107.1", true, Never) == "GameDecides",
            "声明已被满足时保持原有加载路径，不能改成接管。");
        audits = 0;
        Require(Decide("0.99.0", "0.107.1", true, Never) == "GameDecides",
            "声明低于当前版本时保持原有加载路径。");
        audits = 0;
        Require(Decide(null, "0.107.1", true, Never) == "GameDecides",
            "没有版本声明时游戏自行加载，与既有行为一致。");
        audits = 0;
        Require(Decide("", "0.107.1", true, Never) == "GameDecides",
            "空白声明等同于没有声明。");
        audits = 0;
        Require(Decide("banana", "0.107.1", true, Never) == "UnreadableRule",
            "读不懂的版本约束不能靠接管绕过，保持游戏原加载器的判断。");
        audits = 0;
        Require(Decide("0.111.0", null, true, Never) == "GameDecides",
            "拿不到当前游戏版本时不能自行接管，交由游戏判断。");
        audits = 0;
        Require(Decide("0.111.0", "", true, Never) == "GameDecides",
            "空白的游戏版本同样交由游戏判断。");
        Console.WriteLine("Provider minimum version policy passed: evidence-based takeover only.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
