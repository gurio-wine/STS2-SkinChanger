using STS2SkinChanger.Core;

// The game loads Mod assemblies with AssemblyLoadContext.LoadFromAssemblyPath, which probes the game
// folder only, and its AssemblyResolve fallback answers for sts2 and 0Harmony alone. A Mod that
// ships a private dependency beside its own DLL (the "3D Card Effects" ONNX runtime) therefore never
// loads it. These rules decide when Skin Changer may step in.
internal static class ModPrivateDependencyPolicyTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        var gameDirectory = Path.Combine(Path.GetTempPath(), "sc-game");
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), "sc-runtime");
        var modDirectory = Path.Combine(Path.GetTempPath(), "sc-workshop", "3800243712");
        var gameAssembly = Path.Combine(gameDirectory, "sts2.dll");
        var runtimeAssembly = Path.Combine(runtimeDirectory, "System.Private.CoreLib.dll");
        var modAssembly = Path.Combine(modDirectory, "ThreeDCardEffects.dll");
        var bundled = Path.Combine(modDirectory, "Microsoft.ML.OnnxRuntime.dll");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bundled };
        bool Exists(string path) => existing.Contains(path);
        string? Candidate(string? requester, string? name) =>
            ModPrivateDependencyPolicy.CandidatePath(requester, name, gameDirectory, runtimeDirectory, Exists);

        Require(Candidate(modAssembly, "Microsoft.ML.OnnxRuntime") == bundled,
            "随包自带依赖必须按请求方自己的目录解析，否则这类依赖永远加载不到。");
        Require(ModPrivateDependencyPolicy.IsModOwned(modAssembly, gameDirectory, runtimeDirectory) &&
            !ModPrivateDependencyPolicy.IsModOwned(gameAssembly, gameDirectory, runtimeDirectory) &&
            !ModPrivateDependencyPolicy.IsModOwned(runtimeAssembly, gameDirectory, runtimeDirectory),
            "只有游戏目录与 .NET 运行时之外的程序集才算 Mod 代码。");
        Require(Candidate(gameAssembly, "Microsoft.ML.OnnxRuntime") == null,
            "游戏自己的程序集不参与这条解析路径。");
        Require(Candidate(runtimeAssembly, "Microsoft.ML.OnnxRuntime") == null,
            "运行时目录里的程序集不参与这条解析路径。");

        existing.Add(Path.Combine(gameDirectory, "Shared.Library.dll"));
        existing.Add(Path.Combine(modDirectory, "Shared.Library.dll"));
        Require(Candidate(modAssembly, "Shared.Library") == null,
            "游戏自带同名程序集时绝不能换成 Mod 的副本，身份冲突会破坏游戏代码。");

        Require(Candidate(modAssembly, "NotBundled") == null &&
            Candidate(modAssembly, null) == null &&
            Candidate(modAssembly, "") == null,
            "Mod 没有随包提供这个依赖时必须让它照旧失败，不能假装成功。");
        Require(Candidate(null, "Microsoft.ML.OnnxRuntime") == null &&
            Candidate("", "Microsoft.ML.OnnxRuntime") == null,
            "没有请求方就没有归属，不能凭名字猜。");
        Require(Candidate("::not a path::", "Microsoft.ML.OnnxRuntime") == null,
            "非法路径必须安静放弃，不能在依赖解析里抛异常。");
        Require(Candidate(Path.Combine(modDirectory, "nested", "Inner.dll"), "Microsoft.ML.OnnxRuntime") == null,
            "只探测请求方自己所在的目录，不去猜子目录。");

        Console.WriteLine("Mod private dependency policy passed: only a Mod's own bundled file is used, the game's copies are never shadowed.");
    }
}
