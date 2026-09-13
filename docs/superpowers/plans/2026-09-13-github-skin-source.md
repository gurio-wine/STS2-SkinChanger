# 创意工坊 GitHub 来源 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在现有皮肤创意工坊面板增加“来源”筛选，用 GitHub 主题 `sts2-sc-mod` 的仓库列表作为第二个皮肤来源，并把列出的皮肤一键安装到游戏 `mods/` 目录。

**Architecture:** 面板的身份键空间保持单一 `ulong`：GitHub 条目用 `owner/repo` 派生的合成 ID（置最高位），来源注册表按 ID 分派元数据、封面、安装状态与主操作，Steam 专用路径遇到合成 ID 直接跳过。仓库列表来自 GitHub 搜索接口并缓存 `pushed_at`/Release tag；识别在安装前完成——把 Release 资产解压到会话缓存里带 `github/<合成ID>/` 路径的暂存目录，复用投稿扫描的离线识别路径拿到类型/目标标签；安装把暂存目录复制进游戏 `mods/` 并在下次启动生效。

**Tech Stack:** C# / .NET 9（`net9.0` 主工程为 `netstandard2.1` 适配 Godot）、Godot 4 UI（`OptionButton`/`GridContainer`）、`System.Net.Http`、`System.Text.Json`、`System.IO.Compression`；测试为两个自建控制台套件（LogicTests 链接纯逻辑文件，RuntimeTests 直接引用主工程）。

**Spec:** `docs/superpowers/specs/2026-09-13-github-skin-source-design.md`

## Global Constraints

- 只在 `DSH-V41F` 分支提交，不切 master、不推 GitHub（用户明确要求时才推）。
- 每个提交把内测第四位加一，四处必须一致：`STS2SkinChanger/Entry.cs` 的 `InternalTestVersion`、`STS2SkinChanger/SkinChanger.json` 的 `internal_test_version`、`workshop/workshop.json` 的 `internalTestVersion`、`STS2SkinChanger/STS2SkinChanger.csproj` 的 `<Version>`/`<AssemblyVersion>`/`<FileVersion>`。公开三段版号（`1.0.8`）本次不动。
- 代码与配置使用 LF；不要批量改写整库换行；提交前确认没有 CRLF 混入。
- 新增的纯逻辑文件不得引用 Godot、Harmony、Steamworks 或游戏程序集，否则 LogicTests 无法链接。
- 面板文案必须同时提供 15 种语言（`zhs zht eng deu esp spa fra ita jpn kor pol ptb rus tha tur`），缺失语言回退 `eng`。
- 不启动游戏、不控制游戏做实机测试；构建与离线测试通过不等于实机表现。
- 游戏目录下 `mods/` 是安装目标；写入必须走“临时目录 + 改名”，失败不留下半个包，绝不删除既有目录。
- 出网请求主机白名单：`api.github.com`、`github.com`、`objects.githubusercontent.com`、`codeload.github.com`、`opengraph.githubassets.com`；`AllowAutoRedirect=false`，重定向逐跳校验。
- 未认证限额：搜索 10 次/分钟、核心 60 次/小时；受限时保留缓存并提示，不清空列表。

## File Structure

新增（主工程 `STS2SkinChanger/`）：

| 文件 | 职责 |
|---|---|
| `Core/SkinSourceIdentity.cs` | 合成 ID 生成与来源判定（纯逻辑） |
| `Core/GitHubSkinPolicy.cs` | 搜索/Release 响应解析、仓库筛选、主机白名单、资产选择（纯逻辑） |
| `Core/GitHubZipPolicy.cs` | zip 条目路径安全与体积上限（纯逻辑） |
| `Core/GitHubInstallPolicy.cs` | 安装目录、版本比较、安装状态、清单版本读取（纯逻辑） |
| `Core/GitHubSkinCatalog.cs` | 条目模型、状态枚举、缓存读写与校验、GitHub 排序（纯逻辑） |
| `Core/GitHubSkinClient.cs` | HTTP 客户端（注入 `HttpMessageHandler`，可测） |
| `Core/GitHubSkinService.cs` | 刷新管线、暂存、识别、发布、安装（用 Godot/游戏程序集） |
| `Core/GitHubSkinText.cs` | 15 语言文案表 |
| `Ui/SkinWorkshopPanel.Sources.cs` | 来源筛选、GitHub 行绑定与操作（面板 partial） |

修改：

| 文件 | 改动 |
|---|---|
| `Catalog/SkinCatalog.Workshop.cs:58-64` | `WorkshopSourceId` 增加 `github` 路径段标记 |
| `Core/SkinWorkshopService.cs:34,39-58,166-182,184-232` | 目录合并 GitHub 条目；Steam 专用路径按来源分派/跳过；封面主机白名单 |
| `Ui/SkinWorkshopPanel.cs:14-32,93-94,118-141,196-251` | 来源选择器、GitHub 排序/安装筛选、动作分派 |
| `Ui/SkinWorkshopPanel.Filters.cs:27-33,64-117,165-181` | 来源过滤、GitHub 模式筛选行 |
| `Ui/SkinWorkshopPanel.Rows.cs:100-121` | GitHub 行的标题/指标 |
| `Ui/SkinWorkshopPanel.Actions.cs:11-57` | GitHub 主操作（安装/更新）与打开仓库 |
| `Core/SkinChangerPaths.cs:23` | 新增 `GitHubCacheDirectory` |
| `tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj` | 链接新增纯逻辑文件 |
| `tests/STS2SkinChanger.LogicTests/Program.cs` | 调用新增测试入口 |
| `tests/STS2SkinChanger.RuntimeTests/Program.cs` | 新 audit 入口 |

---

### Task 1: 合成身份

**Files:**
- Create: `STS2SkinChanger/Core/SkinSourceIdentity.cs`
- Create: `tests/STS2SkinChanger.LogicTests/GitHubSourceIdentityTests.cs`
- Modify: `tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj`
- Modify: `tests/STS2SkinChanger.LogicTests/Program.cs`（在 `WorkshopPagePoolTests.Run();` 之前加一行 `GitHubSourceIdentityTests.Run();`）

**Interfaces:**
- Consumes: 无
- Produces: `SkinSourceIdentity.GitHubId(string owner, string repo) → ulong`；`SkinSourceIdentity.IsGitHub(ulong) → bool`；`SkinSourceIdentity.IsSteam(ulong) → bool`；`SkinSourceIdentity.GitHubMask`（`1UL << 63`）

- [ ] **Step 1: 写失败测试**

创建 `tests/STS2SkinChanger.LogicTests/GitHubSourceIdentityTests.cs`：

```csharp
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
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release`
Expected: 编译失败，`SkinSourceIdentity` 不存在。

- [ ] **Step 3: 最小实现**

创建 `STS2SkinChanger/Core/SkinSourceIdentity.cs`：

```csharp
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace STS2SkinChanger.Core;

// The Workshop browser keeps one identity space: every row is keyed by a ulong.
// A GitHub repository therefore gets a deterministic synthetic id derived from
// "owner/repo" instead of a second key space and an allocation table. Real Steam
// Workshop ids are far below 2^63, so the top bit alone separates the sources.
internal static class SkinSourceIdentity
{
    public const ulong GitHubMask = 1UL << 63;
    public static ulong GitHubId(string owner, string repo)
    {
        var name = owner.Trim().ToLowerInvariant() + "/" + repo.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return (BinaryPrimitives.ReadUInt64BigEndian(hash) & (GitHubMask - 1)) | GitHubMask;
    }
    public static bool IsGitHub(ulong id) => (id & GitHubMask) != 0;
    public static bool IsSteam(ulong id) => id != 0 && (id & GitHubMask) == 0;
}
```

在 `STS2SkinChanger.LogicTests.csproj` 的 `<ItemGroup>` 内加：

```xml
    <Compile Include="../../STS2SkinChanger/Core/SkinSourceIdentity.cs" Link="SkinSourceIdentity.cs" />
```

在 `Program.cs` 里 `WorkshopPagePoolTests.Run();` 上方加 `GitHubSourceIdentityTests.Run();`。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release`
Expected: `GitHub source identity passed: …` 且末尾 `Skin Changer logic policy tests passed.`，退出码 0。

- [ ] **Step 5: 提交**

按 Global Constraints 把第四位改为 `1.0.8.5`，然后：

```bash
git add STS2SkinChanger/Core/SkinSourceIdentity.cs tests/STS2SkinChanger.LogicTests/GitHubSourceIdentityTests.cs tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj tests/STS2SkinChanger.LogicTests/Program.cs STS2SkinChanger/Entry.cs STS2SkinChanger/SkinChanger.json workshop/workshop.json STS2SkinChanger/STS2SkinChanger.csproj
git commit -m "feat: derive a synthetic skin source id for GitHub repositories"
```

---

### Task 2: GitHub 响应解析与仓库筛选

**Files:**
- Create: `STS2SkinChanger/Core/GitHubSkinPolicy.cs`
- Create: `tests/STS2SkinChanger.LogicTests/GitHubSkinPolicyTests.cs`
- Modify: `tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj`
- Modify: `tests/STS2SkinChanger.LogicTests/Program.cs`

**Interfaces:**
- Consumes: `SkinSourceIdentity.GitHubId`
- Produces:
  - `record GitHubRepoRef(string Owner, string Name)`，成员 `FullName`、`Url`
  - `record GitHubRepoInfo(GitHubRepoRef Repo, string Description, int Stars, DateTimeOffset PushedAt)`
  - `record GitHubReleaseAsset(string Name, long Size, string Url)`
  - `record GitHubReleaseInfo(string Tag, string Title, DateTimeOffset PublishedAt, GitHubReleaseAsset[] Assets)`
  - `GitHubSkinPolicy.SelfRepo`（`"gurio-wine/STS2-SkinChanger"`）
  - `GitHubSkinPolicy.SearchUri(int page) → Uri`
  - `GitHubSkinPolicy.ParseSearch(string json) → GitHubRepoInfo[]`
  - `GitHubSkinPolicy.ParseRelease(string json) → GitHubReleaseInfo?`
  - `GitHubSkinPolicy.SelectAsset(GitHubReleaseInfo, string repoName) → GitHubReleaseAsset?`
  - `GitHubSkinPolicy.IsAllowedHost(string host) → bool`
  - `GitHubSkinPolicy.CoverUrl(GitHubRepoRef) → string`
  - `GitHubSkinPolicy.KeepWithoutTargets(ulong id, int targetCount, string kind) → bool`
  - `GitHubSkinPolicy.MaxAssetBytes`（`128L * 1024 * 1024`）

- [ ] **Step 1: 写失败测试**

创建 `tests/STS2SkinChanger.LogicTests/GitHubSkinPolicyTests.cs`：

```csharp
using STS2SkinChanger.Core;

internal static class GitHubSkinPolicyTests
{
    private const string Search = """
    {"total_count":3,"items":[
      {"full_name":"gurio-wine/STS2-SkinChanger","name":"STS2-SkinChanger","description":"self",
       "stargazers_count":9,"pushed_at":"2026-09-13T01:02:03Z","fork":false,"owner":{"login":"gurio-wine"}},
      {"full_name":"qbyoffice/SilentSkinMod","name":"SilentSkinMod","description":"silent skin",
       "stargazers_count":12,"pushed_at":"2026-09-10T04:05:06Z","fork":false,"owner":{"login":"qbyoffice"}},
      {"full_name":"someone/forked-skin","name":"forked-skin","description":"fork",
       "stargazers_count":1,"pushed_at":"2026-09-09T00:00:00Z","fork":true,"owner":{"login":"someone"}}]}
    """;

    private const string Release = """
    {"tag_name":"v0.1.4","name":"v0.1.4 Release","published_at":"2026-09-10T04:05:06Z","assets":[
      {"name":"SilentSkinMod.zip","size":16777216,"browser_download_url":"https://github.com/qbyoffice/SilentSkinMod/releases/download/v0.1.4/SilentSkinMod.zip"},
      {"name":"checksums.txt","size":128,"browser_download_url":"https://github.com/qbyoffice/SilentSkinMod/releases/download/v0.1.4/checksums.txt"}]}
    """;

    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        var repos = GitHubSkinPolicy.ParseSearch(Search);
        Require(repos.Length == 1 && repos[0].Repo.FullName == "qbyoffice/SilentSkinMod",
            "主题列表必须排除本 Mod 仓库和 fork，只保留真正的第三方仓库。");
        Require(repos[0].Stars == 12 && repos[0].PushedAt == DateTimeOffset.Parse("2026-09-10T04:05:06Z"),
            "星标与 pushed_at 必须按搜索结果原样解析，用于排序和缓存比对。");
        Require(GitHubSkinPolicy.ParseSearch("{").Length == 0 && GitHubSkinPolicy.ParseSearch("").Length == 0,
            "损坏的搜索响应必须当成空列表，不能抛出中断整次刷新。");

        Require(GitHubSkinPolicy.IsAllowedHost("api.github.com") && GitHubSkinPolicy.IsAllowedHost("objects.githubusercontent.com") &&
            GitHubSkinPolicy.IsAllowedHost("opengraph.githubassets.com"),
            "接口、资产重定向与封面的主机必须在白名单内。");
        Require(!GitHubSkinPolicy.IsAllowedHost("evil.example.com") && !GitHubSkinPolicy.IsAllowedHost("github.com.evil.example.com"),
            "非白名单主机，包括后缀伪装的域名，必须被拒绝。");

        var release = GitHubSkinPolicy.ParseRelease(Release)!;
        Require(release.Tag == "v0.1.4" && release.Assets.Length == 2, "Release tag 与资产清单必须完整解析。");
        var asset = GitHubSkinPolicy.SelectAsset(release, "SilentSkinMod")!;
        Require(asset.Name == "SilentSkinMod.zip" && GitHubSkinPolicy.IsAllowedHost(new Uri(asset.Url).Host),
            "资产必须选 zip，且下载地址主机在白名单内。");
        Require(GitHubSkinPolicy.SelectAsset(new GitHubReleaseInfo("v1", "", default, []), "x") == null,
            "没有任何 zip 资产的 Release 必须判为无法安装。");
        Require(GitHubSkinPolicy.SelectAsset(release with { Assets = [asset with { Size = GitHubSkinPolicy.MaxAssetBytes + 1 }] }, "SilentSkinMod") == null,
            "超过体积上限的资产不能下载。");
        Require(GitHubSkinPolicy.SelectAsset(release, "SilentSkinMod")!.Url.StartsWith("https://github.com/"),
            "只接受 https 的资产地址。");

        Require(GitHubSkinPolicy.CoverUrl(repos[0].Repo) == "https://opengraph.githubassets.com/1/qbyoffice/SilentSkinMod",
            "封面地址必须走无接口限额的 opengraph 主机。");
        Require(GitHubSkinPolicy.SearchUri(1).Host == "api.github.com" &&
            GitHubSkinPolicy.SearchUri(1).Query.Contains("topic%3Asts2-sc-mod"),
            "搜索请求必须打在 topic 查询上。");

        var recognized = SkinSourceIdentity.GitHubId("o", "r");
        Require(GitHubSkinPolicy.KeepWithoutTargets(recognized, 0, "") && !GitHubSkinPolicy.KeepWithoutTargets(recognized, 0, "character"),
            "未识别的仓库只在没有类型筛选时列出，选了类型就不能假装它属于该类型。");
        Require(!GitHubSkinPolicy.KeepWithoutTargets(recognized, 2, "") && !GitHubSkinPolicy.KeepWithoutTargets(3787302680UL, 0, ""),
            "已识别条目与 Steam 条目不受这条规则影响。");
        Console.WriteLine("GitHub skin policy passed: search filter, host allowlist, release assets, cover, unrecognized listing.");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release`
Expected: 编译失败，`GitHubSkinPolicy` 不存在。

- [ ] **Step 3: 最小实现**

创建 `STS2SkinChanger/Core/GitHubSkinPolicy.cs`：

```csharp
using System.Text.Json;

namespace STS2SkinChanger.Core;

internal sealed record GitHubRepoRef(string Owner, string Name)
{
    public string FullName => Owner + "/" + Name;
    public string Url => "https://github.com/" + FullName;
}
internal sealed record GitHubRepoInfo(GitHubRepoRef Repo, string Description, int Stars, DateTimeOffset PushedAt);
internal sealed record GitHubReleaseAsset(string Name, long Size, string Url);
internal sealed record GitHubReleaseInfo(string Tag, string Title, DateTimeOffset PublishedAt, GitHubReleaseAsset[] Assets);

// Everything GitHub returns is untrusted input: parse defensively, cap every field,
// and never let one malformed repository abort the whole refresh.
internal static class GitHubSkinPolicy
{
    public const string SelfRepo = "gurio-wine/STS2-SkinChanger";
    public const string Topic = "sts2-sc-mod";
    public const long MaxAssetBytes = 128L * 1024 * 1024;
    private const int MaxSearchBytes = 4 * 1024 * 1024;
    public const int SearchPageSize = 100;
    public const int MaxSearchPages = 3;
    public static Uri SearchUri(int page) => new(
        $"https://api.github.com/search/repositories?q=topic%3A{Topic}&sort=updated&order=desc&per_page={SearchPageSize}&page={Math.Clamp(page, 1, MaxSearchPages)}");
    public static Uri LatestReleaseUri(GitHubRepoRef repo) => new($"https://api.github.com/repos/{repo.FullName}/releases/latest");
    public static string CoverUrl(GitHubRepoRef repo) => $"https://opengraph.githubassets.com/1/{repo.FullName}";

    private static readonly string[] Hosts = ["api.github.com", "github.com", "objects.githubusercontent.com", "codeload.github.com", "opengraph.githubassets.com"];
    public static bool IsAllowedHost(string? host) => host != null && Hosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    public static bool TryParseFullName(string? fullName, out GitHubRepoRef repo)
    {
        repo = null!;
        var parts = (fullName ?? "").Split('/');
        if (parts.Length != 2 || !SafeName(parts[0]) || !SafeName(parts[1])) return false;
        repo = new(parts[0], parts[1]);
        return true;
    }
    private static bool SafeName(string value) => value.Length is > 0 and <= 100 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static bool Eligible(GitHubRepoRef repo, bool fork) =>
        !fork && !repo.FullName.Equals(SelfRepo, StringComparison.OrdinalIgnoreCase) && repo.Owner != "." && repo.Name != ".";

    public static GitHubRepoInfo[] ParseSearch(string json)
    {
        if (json.Length is 0 or > MaxSearchBytes) return [];
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
            var result = new List<GitHubRepoInfo>();
            foreach (var item in items.EnumerateArray().Take(1000))
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("full_name", out var name) || !TryParseFullName(name.GetString(), out var repo) ||
                    !item.TryGetProperty("fork", out var fork) || fork.ValueKind != JsonValueKind.False || !Eligible(repo, false)) continue;
                var stars = item.TryGetProperty("stargazers_count", out var s) && s.TryGetInt32(out var value) ? Math.Max(0, value) : 0;
                var pushed = item.TryGetProperty("pushed_at", out var p) && p.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(p.GetString(), out var stamp) ? stamp : default;
                var description = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                    ? (d.GetString() ?? "") : "";
                result.Add(new(repo, description[..Math.Min(description.Length, 1024)], stars, pushed));
            }
            return result.DistinctBy(info => info.Repo.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException) { return []; }
    }

    public static GitHubReleaseInfo? ParseRelease(string json)
    {
        if (json.Length is 0 or > MaxSearchBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            var title = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            var published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(p.GetString(), out var stamp) ? stamp : default;
            var list = new List<GitHubReleaseAsset>();
            foreach (var asset in assets.EnumerateArray().Take(64))
            {
                if (asset.ValueKind != JsonValueKind.Object ||
                    !asset.TryGetProperty("name", out var an) || an.ValueKind != JsonValueKind.String ||
                    !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String) continue;
                var name = an.GetString() ?? "";
                var size = asset.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var bytes) ? bytes : 0;
                if (name.Length is 0 or > 128 || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(uri.Host)) continue;
                list.Add(new(name, size, uri.AbsoluteUri));
            }
            return new(tag[..Math.Min(tag.Length, 128)], title[..Math.Min(title.Length, 256)], published, list.ToArray());
        }
        catch (JsonException) { return null; }
    }

    public static GitHubReleaseAsset? SelectAsset(GitHubReleaseInfo release, string repoName)
    {
        var zips = release.Assets.Where(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            asset.Size is > 0 and <= MaxAssetBytes && !asset.Name.Contains("..", StringComparison.Ordinal)).ToArray();
        return zips.FirstOrDefault(asset => Path.GetFileNameWithoutExtension(asset.Name).Equals(repoName, StringComparison.OrdinalIgnoreCase))
            ?? zips.OrderByDescending(asset => asset.Size).FirstOrDefault();
    }

    // A repository that was never recognized still belongs in the unfiltered list,
    // but it must not pretend to be a character, card or monster skin.
    public static bool KeepWithoutTargets(ulong id, int targetCount, string kind) =>
        targetCount == 0 && kind.Length == 0 && SkinSourceIdentity.IsGitHub(id);
}
```

csproj 加两行（`WorkshopCatalogPolicy.cs` 供后续 Task 使用，一起加）：

```xml
    <Compile Include="../../STS2SkinChanger/Core/GitHubSkinPolicy.cs" Link="GitHubSkinPolicy.cs" />
    <Compile Include="../../STS2SkinChanger/Core/WorkshopCatalogPolicy.cs" Link="WorkshopCatalogPolicy.cs" />
```

`Program.cs` 加 `GitHubSkinPolicyTests.Run();`。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release`
Expected: `GitHub skin policy passed: …`，退出码 0。

- [ ] **Step 5: 提交**

第四位改 `1.0.8.6`，`git commit -m "feat: parse and filter the GitHub skin topic list"`（`git add` 同 Task 1 的文件集，换成本次两个新文件）。

---

### Task 3: 解压安全与安装判定

**Files:**
- Create: `STS2SkinChanger/Core/GitHubZipPolicy.cs`
- Create: `STS2SkinChanger/Core/GitHubInstallPolicy.cs`
- Create: `tests/STS2SkinChanger.LogicTests/GitHubInstallPolicyTests.cs`
- Modify: `tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj`、`Program.cs`

**Interfaces:**
- Consumes: 无（只用 BCL）
- Produces:
  - `enum GitHubZipEntry { Reject, Skip, Accept }`
  - `GitHubZipPolicy.Classify(string entryName, long length, int index, long totalSoFar, out string relative) → GitHubZipEntry`
  - `GitHubZipPolicy.MaxEntries`（20000）、`MaxEntryBytes`（256 MB）、`MaxTotalBytes`（512 MB）
  - `enum GitHubInstallState { NotInstalled, Installed, Updatable }`
  - `GitHubInstallPolicy.ModsDirectory(string executablePath) → string`
  - `GitHubInstallPolicy.FolderName(string packageId, string repoName) → string`
  - `GitHubInstallPolicy.CompareVersions(string? left, string? right) → int`
  - `GitHubInstallPolicy.Classify(string? installedVersion, string? availableVersion) → GitHubInstallState`
  - `GitHubInstallPolicy.ReadManifestVersion(string modRoot, string packageId) → string?`

- [ ] **Step 1: 写失败测试**

创建 `tests/STS2SkinChanger.LogicTests/GitHubInstallPolicyTests.cs`：

```csharp
using STS2SkinChanger.Core;

internal static class GitHubInstallPolicyTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        void Accept(string name, string expected, long length = 16)
        {
            Require(GitHubZipPolicy.Classify(name, length, 0, 0, out var relative) == GitHubZipEntry.Accept && relative == expected,
                $"正常条目 {name} 必须被接受，并给出用 / 分隔的相对路径 {expected}。");
        }
        Accept("SilentSkinMod-0.1.4/KaguyaSilentRavenSkin.json", "SilentSkinMod-0.1.4/KaguyaSilentRavenSkin.json");
        Accept("KaguyaSilentRavenSkin.pck", "KaguyaSilentRavenSkin.pck");
        Accept(@"sub\win\style.dll", "sub/win/style.dll");
        Require(GitHubZipPolicy.Classify("../evil.dll", 16, 0, 0, out _) == GitHubZipEntry.Reject &&
            GitHubZipPolicy.Classify("a/../../evil.dll", 16, 0, 0, out _) == GitHubZipEntry.Reject,
            "路径穿越条目必须拒绝，不能写到暂存目录之外。");
        Require(GitHubZipPolicy.Classify("/etc/passwd", 16, 0, 0, out _) == GitHubZipEntry.Reject &&
            GitHubZipPolicy.Classify(@"C:\Windows\x.dll", 16, 0, 0, out _) == GitHubZipEntry.Reject,
            "绝对路径与盘符路径必须拒绝。");
        Require(GitHubZipPolicy.Classify("__MACOSX/x", 16, 0, 0, out _) == GitHubZipEntry.Skip &&
            GitHubZipPolicy.Classify(".git/config", 16, 0, 0, out _) == GitHubZipEntry.Skip &&
            GitHubZipPolicy.Classify("mod/", 0, 0, 0, out _) == GitHubZipEntry.Skip,
            "打包者系统的垃圾条目与目录占位要跳过，不算识别失败。");
        Require(GitHubZipPolicy.Classify("big.bin", GitHubZipPolicy.MaxEntryBytes + 1, 0, 0, out _) == GitHubZipEntry.Reject &&
            GitHubZipPolicy.Classify("more.bin", 16, 1, GitHubZipPolicy.MaxTotalBytes, out _) == GitHubZipEntry.Reject &&
            GitHubZipPolicy.Classify("many.bin", 16, GitHubZipPolicy.MaxEntries, 0, out _) == GitHubZipEntry.Reject,
            "单文件、总解压体积和条目数超限都必须拒绝，避免解压炸弹。");

        Require(GitHubInstallPolicy.ModsDirectory(@"C:\Games\SlayTheSpire2\SlayTheSpire2.exe") == @"C:\Games\SlayTheSpire2\mods",
            "Windows 安装目标是与可执行文件同级的 mods 目录。");
        Require(GitHubInstallPolicy.ModsDirectory("/Applications/Game.app/Contents/MacOS/Game") ==
            Path.GetFullPath("/Applications/Game.app/Contents/Resources/mods"),
            "macOS 应用包的 mods 目录在 Contents/Resources 下，与主资源包同层。");
        Require(GitHubInstallPolicy.FolderName("KaguyaSilentRavenSkin", "SilentSkinMod") == "KaguyaSilentRavenSkin",
            "安装目录优先用清单 ID，与游戏加载本地 Mod 的布局一致。");
        Require(GitHubInstallPolicy.FolderName("../evil", "SilentSkinMod") == "SilentSkinMod" &&
            GitHubInstallPolicy.FolderName("", "SilentSkinMod") == "SilentSkinMod" &&
            GitHubInstallPolicy.FolderName("m", "a/b\\c") == "abc",
            "非法清单 ID 必须回退到仓库名，且不允许路径分隔符。");

        Require(GitHubInstallPolicy.CompareVersions("0.1.10", "0.1.4") > 0 && GitHubInstallPolicy.CompareVersions("v0.1.4", "0.1.4") == 0 &&
            GitHubInstallPolicy.CompareVersions("1.0", "1.0.0") == 0 && GitHubInstallPolicy.CompareVersions("", "1.0") < 0,
            "版本比较按数字段，忽略前导 v，缺段按 0。");
        Require(GitHubInstallPolicy.Classify(null, "0.1.4") == GitHubInstallState.NotInstalled &&
            GitHubInstallPolicy.Classify("0.1.4", "0.1.4") == GitHubInstallState.Installed &&
            GitHubInstallPolicy.Classify("0.1.4", "0.1.10") == GitHubInstallState.Updatable &&
            GitHubInstallPolicy.Classify("0.1.4", null) == GitHubInstallState.Installed,
            "安装状态只按已安装版本与可用版本判定；拿不到可用版本时不能谎报可更新。");

        var root = Path.Combine(Path.GetTempPath(), "sc-gh-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "KaguyaSilentRavenSkin.json"),
                """{"id":"KaguyaSilentRavenSkin","name":"Silent","version":"0.1.4","has_pck":true,"affects_gameplay":false}""");
            Require(GitHubInstallPolicy.ReadManifestVersion(root, "KaguyaSilentRavenSkin") == "0.1.4",
                "已安装版本必须从清单 json 的 version 字段读取。");
            File.WriteAllText(Path.Combine(root, "broken.json"), "{");
            Require(GitHubInstallPolicy.ReadManifestVersion(root, "broken") == null &&
                GitHubInstallPolicy.ReadManifestVersion(root, "../escape") == null,
                "损坏清单与非法 ID 都要返回 null，不能抛异常或读到目录外。");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("GitHub install policy passed: zip guards, install path, version state, manifest version.");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release`
Expected: 编译失败，`GitHubZipPolicy` / `GitHubInstallPolicy` 不存在。

- [ ] **Step 3: 最小实现**

`STS2SkinChanger/Core/GitHubZipPolicy.cs`：

```csharp
namespace STS2SkinChanger.Core;

internal enum GitHubZipEntry { Reject, Skip, Accept }

// A release archive is untrusted: entries may try to escape the staging folder,
// carry another platform's junk, or expand into a decompression bomb.
internal static class GitHubZipPolicy
{
    public const int MaxEntries = 20000;
    public const long MaxEntryBytes = 256L * 1024 * 1024;
    public const long MaxTotalBytes = 512L * 1024 * 1024;
    public static GitHubZipEntry Classify(string entryName, long length, int index, long totalSoFar, out string relative)
    {
        relative = "";
        if (index >= MaxEntries || length > MaxEntryBytes || totalSoFar + length > MaxTotalBytes) return GitHubZipEntry.Reject;
        var name = entryName.Replace('\\', '/').TrimStart('/');
        if (name.Length == 0 || name.EndsWith('/')) return GitHubZipEntry.Skip;
        var parts = name.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or "..") || name.Contains(':') ||
            parts.Any(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) || name.Length > 240) return GitHubZipEntry.Reject;
        if (parts[0].StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase) || parts[0] == ".git" ||
            parts[^1].Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)) return GitHubZipEntry.Skip;
        relative = string.Join('/', parts);
        return GitHubZipEntry.Accept;
    }
}
```

`STS2SkinChanger/Core/GitHubInstallPolicy.cs`：

```csharp
using System.Text.Json;

namespace STS2SkinChanger.Core;

internal enum GitHubInstallState { NotInstalled, Installed, Updatable }

internal static class GitHubInstallPolicy
{
    // Mirrors GamePackLocator: a macOS bundle keeps game data in Contents/Resources,
    // while OS.GetExecutablePath() points into Contents/MacOS.
    public static string ModsDirectory(string executablePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        if (Path.GetFileName(directory).Equals("MacOS", StringComparison.OrdinalIgnoreCase))
            directory = Path.GetFullPath(Path.Combine(directory, "..", "Resources"));
        return Path.Combine(directory, "mods");
    }
    public static string FolderName(string packageId, string repoName)
    {
        var safe = new string(packageId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray()).Trim('.');
        if (safe.Length is > 0 and <= 64) return safe;
        var fallback = new string(repoName.Where(char.IsAsciiLetterOrDigit).ToArray());
        return fallback.Length is > 0 and <= 64 ? fallback : "Skin";
    }
    public static int CompareVersions(string? left, string? right)
    {
        var a = Segments(left);
        var b = Segments(right);
        if (a.Length == 0 || b.Length == 0) return a.Length == b.Length ? 0 : a.Length == 0 ? -1 : 1;
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
    private static int[] Segments(string? version) => (version ?? "").Trim().TrimStart('v', 'V').Split('.', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => int.TryParse(new string(part.TakeWhile(char.IsAsciiDigit).ToArray()), out var value) ? value : -1).ToArray();
    public static GitHubInstallState Classify(string? installedVersion, string? availableVersion) =>
        string.IsNullOrWhiteSpace(installedVersion) ? GitHubInstallState.NotInstalled :
        !string.IsNullOrWhiteSpace(availableVersion) && CompareVersions(installedVersion, availableVersion) < 0
            ? GitHubInstallState.Updatable : GitHubInstallState.Installed;
    public static string? ReadManifestVersion(string modRoot, string packageId)
    {
        if (packageId.Length is 0 or > 64 || packageId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) return null;
        var path = Path.Combine(modRoot, packageId + ".json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path).TrimStart('\uFEFF'));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
}
```

csproj 与 `Program.cs` 同前两个任务各加一行。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release`
Expected: `GitHub install policy passed: …`，退出码 0。

- [ ] **Step 5: 提交**

第四位改 `1.0.8.7`，`git commit -m "feat: guard GitHub asset extraction and decide install state"`。

---

### Task 4: 条目模型与目录缓存

**Files:**
- Create: `STS2SkinChanger/Core/GitHubSkinCatalog.cs`
- Create: `tests/STS2SkinChanger.LogicTests/GitHubSkinCatalogTests.cs`
- Modify: `LogicTests.csproj`、`Program.cs`

**Interfaces:**
- Consumes: `WorkshopTarget`、`WorkshopCatalogItem`（`Core/WorkshopCatalogPolicy.cs`）、`GitHubRepoInfo`、`GitHubReleaseAsset`
- Produces:
  - `enum GitHubRecognition { Pending, Recognized, NoPackage, NoResources, Failed }`
  - `record GitHubSkinEntry(string FullName, string Name, string Description, int Stars, DateTimeOffset PushedAt, string ReleaseTag, string PackageId, string PackageName, string PackageVersion, WorkshopTarget[] Targets, bool RestartRequired, GitHubRecognition Recognition, string Error, string AssetUrl, long AssetSize)`，成员 `Id`、`Repo`（由 `FullName` 解析）
  - `enum GitHubSort { Updated, Stars, Name }`
  - `GitHubSortPolicy.OrderIds(IEnumerable<ulong>, IReadOnlyDictionary<ulong, GitHubSkinEntry>, GitHubSort, bool reverse) → ulong[]`
  - `GitHubSortPolicy.Metric(GitHubSkinEntry?, GitHubSort) → string`
  - `GitHubSkinCatalog.State` / `.Entries` / `.Restore(string path)` / `.Save(string path, Action<string, Action>? cacheWriter)` / `.Merge(IEnumerable<GitHubSkinEntry>)` / `.Publish(GitHubSkinEntry)` / `static GitHubSkinEntry FromRepo(GitHubRepoInfo)`

- [ ] **Step 1: 写失败测试**

创建 `tests/STS2SkinChanger.LogicTests/GitHubSkinCatalogTests.cs`：

```csharp
using STS2SkinChanger.Core;

internal static class GitHubSkinCatalogTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        var repo = new GitHubRepoInfo(new GitHubRepoRef("qbyoffice", "SilentSkinMod"), "silent", 12, DateTimeOffset.Parse("2026-09-10T04:05:06Z"));
        var pending = GitHubSkinCatalog.FromRepo(repo);
        Require(pending.Recognition == GitHubRecognition.Pending && pending.Targets.Length == 0 &&
            pending.Id == SkinSourceIdentity.GitHubId("qbyoffice", "SilentSkinMod"),
            "搜索阶段先建立待识别条目，ID 与仓库一一对应。");
        Require(pending.ReleaseTag.Length == 0, "尚未查询 Release 时不能伪造 tag，否则会跳过识别。");

        var recognized = pending with { ReleaseTag = "v0.1.4", PackageId = "KaguyaSilentRavenSkin", PackageName = "Silent",
            PackageVersion = "0.1.4", Recognition = GitHubRecognition.Recognized, RestartRequired = true,
            Targets = [new("character", "silent")] };
        var state = new GitHubSkinCatalog();
        state.Merge([pending]);
        Require(state.Entries.Length == 1 && state.Entries[0].Recognition == GitHubRecognition.Pending, "搜索结果必须并入目录。");
        state.Merge([pending with { Stars = 20 }]);
        Require(state.Entries[0].Stars == 20, "同一 pushed_at 的重复搜索必须就地更新星标，不重复建条目。");
        state.Publish(recognized);
        state.Merge([pending with { Stars = 21 }]);
        Require(state.Entries[0].Targets.Length == 1 && state.Entries[0].PackageId == "KaguyaSilentRavenSkin",
            "pushed_at 未变时，重新搜索不能丢掉已识别的结果。");
        state.Merge([pending with { PushedAt = DateTimeOffset.Parse("2026-09-12T00:00:00Z") }]);
        Require(state.Entries[0].Recognition == GitHubRecognition.Pending && state.Entries[0].Targets.Length == 0,
            "pushed_at 变化后必须重新识别，旧的类型标签不能继续挂着。");

        var path = Path.Combine(Path.GetTempPath(), "sc-gh-catalog-" + Guid.NewGuid().ToString("N"), "github_skins.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            state.Publish(recognized);
            state.Save(path, null);
            var restored = new GitHubSkinCatalog();
            restored.Restore(path);
            Require(restored.Entries.Length == 1 && restored.Entries[0].PackageVersion == "0.1.4" &&
                restored.Entries[0].Targets.Single().Target == "silent", "目录缓存必须能跨会话完整恢复。");
            File.WriteAllText(path, """{"format":99,"entries":[]}""");
            Require(Throws(() => restored.Restore(path)), "未知格式的缓存必须拒绝，不能按半懂的数据继续跑。");
            File.WriteAllText(path, """{"format":1,"entries":[{"fullName":"../evil","name":"x"}]}""");
            Require(Throws(() => restored.Restore(path)), "缓存里的仓库名同样要按不可信输入校验。");
            File.WriteAllText(path, "{");
            Require(Throws(() => restored.Restore(path)), "损坏缓存必须抛错，由调用方保留空目录并重新拉取。");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }

        // 不同仓库才有不同的合成 ID；同一仓库的两次快照会得到同一个 ID。
        var newer = recognized with { FullName = "someone/NewerSkin", Name = "NewerSkin", Stars = 5,
            PushedAt = DateTimeOffset.Parse("2026-09-12T00:00:00Z") };
        Require(newer.Id != recognized.Id, "测试夹具必须用两个不同仓库，否则排序断言会退化成同一个条目。");
        var entries = new Dictionary<ulong, GitHubSkinEntry> { [recognized.Id] = recognized, [newer.Id] = newer };
        Require(GitHubSortPolicy.OrderIds([recognized.Id, newer.Id], entries, GitHubSort.Updated)[0] == newer.Id,
            "按最近更新排序时 pushed_at 新的在前。");
        Require(GitHubSortPolicy.OrderIds([recognized.Id, newer.Id], entries, GitHubSort.Stars)[0] == recognized.Id,
            "按星标排序时星标高的在前。");
        Require(GitHubSortPolicy.OrderIds([recognized.Id, newer.Id], entries, GitHubSort.Name)[0] == newer.Id &&
            GitHubSortPolicy.OrderIds([recognized.Id, newer.Id], entries, GitHubSort.Name, reverse: true)[0] == recognized.Id,
            "按名称排序及其反向必须真的反过来，且不依赖字典顺序。");
        Require(GitHubSortPolicy.Metric(recognized, GitHubSort.Stars).Contains("12") &&
            GitHubSortPolicy.Metric(null, GitHubSort.Stars) == "—",
            "指标文本要显示星标，缺失条目显示占位符。");
        Console.WriteLine("GitHub skin catalog passed: merge keeps recognition, cache round-trip, sorting.");
    }
    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release`
Expected: 编译失败，`GitHubSkinCatalog` 不存在。

- [ ] **Step 3: 最小实现**

`STS2SkinChanger/Core/GitHubSkinCatalog.cs`：

```csharp
using System.Text.Json;

namespace STS2SkinChanger.Core;

internal enum GitHubRecognition { Pending, Recognized, NoPackage, NoResources, Failed }

internal sealed record GitHubSkinEntry(
    string FullName, string Name, string Description, int Stars, DateTimeOffset PushedAt, string ReleaseTag,
    string PackageId, string PackageName, string PackageVersion, WorkshopTarget[] Targets, bool RestartRequired,
    GitHubRecognition Recognition, string Error, string AssetUrl, long AssetSize)
{
    [System.Text.Json.Serialization.JsonIgnore] public ulong Id => SkinSourceIdentity.GitHubId(Owner, RepoName);
    [System.Text.Json.Serialization.JsonIgnore] public string Owner => FullName.Split('/')[0];
    [System.Text.Json.Serialization.JsonIgnore] public string RepoName => FullName.Split('/')[1];
    [System.Text.Json.Serialization.JsonIgnore] public GitHubRepoRef Repo => new(Owner, RepoName);
    [System.Text.Json.Serialization.JsonIgnore] public string? CoverUrl => GitHubSkinPolicy.CoverUrl(Repo);
}

internal sealed record GitHubSkinState(int Format, GitHubSkinEntry[] Entries)
{
    public static GitHubSkinState Empty => new(1, []);
}

// Recognized results are cached across sessions; the search list alone would make the
// player download and rescan every repository on each start.
internal sealed class GitHubSkinCatalog
{
    public const int Format = 1;
    public GitHubSkinState State { get; private set; } = GitHubSkinState.Empty;
    public GitHubSkinEntry[] Entries => State.Entries;

    public static GitHubSkinEntry FromRepo(GitHubRepoInfo info) => new(info.Repo.FullName, info.Repo.Name,
        info.Description, info.Stars, info.PushedAt, "", "", "", "", [], true, GitHubRecognition.Pending, "", "", 0);

    public void Merge(IEnumerable<GitHubSkinEntry> entries)
    {
        var next = State.Entries.ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (next.TryGetValue(entry.FullName, out var known) && known.PushedAt == entry.PushedAt)
            {
                // Keep the recognition result; only the volatile search fields are refreshed.
                next[entry.FullName] = known with { Stars = entry.Stars, Description = entry.Description, PushedAt = entry.PushedAt };
                continue;
            }
            // A new commit invalidates the previous recognition entirely.
            next[entry.FullName] = entry.Recognition == GitHubRecognition.Pending ? entry : known == null ? entry :
                entry with { Recognition = GitHubRecognition.Pending, Targets = [], PackageId = "", PackageName = "", PackageVersion = "", ReleaseTag = "" };
        }
        Publish(next.Values.OrderBy(entry => entry.PushedAt, Comparer<DateTimeOffset>.Default).Reverse().ToArray());
    }
    public void Publish(GitHubSkinEntry entry)
    {
        var list = State.Entries.ToList();
        var index = list.FindIndex(known => known.FullName.Equals(entry.FullName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) list[index] = entry; else list.Add(entry);
        Publish(list.ToArray());
    }
    private void Publish(GitHubSkinEntry[] entries) => State = new(Format, entries);

    public void Restore(string path)
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("GitHub catalog cache too large.");
        var state = JsonSerializer.Deserialize<GitHubSkinState>(File.ReadAllText(path), new JsonSerializerOptions { MaxDepth = 16 });
        Validate(state);
        State = state!;
    }
    public void Save(string path, Action<string, Action>? cacheWriter)
    {
        var json = JsonSerializer.Serialize(State);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 4 * 1024 * 1024) json = JsonSerializer.Serialize(GitHubSkinState.Empty with { Entries = State.Entries.Take(999).ToArray() });
        void Write()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        if (cacheWriter == null) Write(); else cacheWriter(path, Write);
    }
    private static void Validate(GitHubSkinState? state)
    {
        if (state is not { Format: Format, Entries: not null } || state.Entries.Length > 1000 ||
            state.Entries.Any(entry => entry == null || !GitHubSkinPolicy.TryParseFullName(entry.FullName, out _) ||
                entry.Name is not { Length: <= 128 } || entry.Description is not { Length: <= 1024 } ||
                entry.Stars < 0 || entry.ReleaseTag is not { Length: <= 128 } || entry.PackageId is not { Length: <= 64 } ||
                entry.PackageName is not { Length: <= 256 } || entry.PackageVersion is not { Length: <= 64 } ||
                entry.Error is not { Length: <= 512 } || entry.AssetSize < 0 ||
                (entry.AssetUrl.Length > 0 && (!Uri.TryCreate(entry.AssetUrl, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || !GitHubSkinPolicy.IsAllowedHost(uri.Host))) ||
                !Enum.IsDefined(entry.Recognition) || entry.Targets is null || entry.Targets.Length > 64 ||
                entry.Targets.Any(target => target == null || string.IsNullOrWhiteSpace(target.Kind) || target.Kind.Length > 32 ||
                    string.IsNullOrWhiteSpace(target.Target) || target.Target.Length > 128) ||
                entry.Recognition == GitHubRecognition.Recognized && entry.Targets.Length == 0))
            throw new InvalidDataException("Unverified or invalid GitHub catalog cache.");
    }
}

internal enum GitHubSort { Updated, Stars, Name }

internal static class GitHubSortPolicy
{
    public static ulong[] OrderIds(IEnumerable<ulong> ids, IReadOnlyDictionary<ulong, GitHubSkinEntry> entries, GitHubSort sort, bool reverse = false)
        => ids.OrderBy(id => reverse ? 1 : 0)
            .ThenBy(id => entries.TryGetValue(id, out var entry) ? Key(entry, sort) : null, Comparer<string>.Default)
            .ThenBy(id => id).ToArray();
    private static string? Key(GitHubSkinEntry entry, GitHubSort sort) => sort switch
    {
        GitHubSort.Updated => entry.PushedAt.ToString("O"),
        GitHubSort.Stars => entry.Stars.ToString("D10"),
        _ => entry.Name.ToUpperInvariant()
    };
    public static string Metric(GitHubSkinEntry? entry, GitHubSort sort) => entry == null ? "—" : sort switch
    {
        // 纯数字，不带语言后缀：指标列只显示数值，标签由面板的语言表提供。
        GitHubSort.Stars => entry.Stars.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
        GitHubSort.Updated => entry.PushedAt.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        _ => entry.PackageVersion.Length > 0 ? entry.PackageVersion : entry.ReleaseTag
    };
}
```

注意：`OrderIds` 里 `Comparer<string>.Default` 的升降序通过 `OrderBy`/`OrderByDescending` 表达。把上面两行替换成显式实现：

```csharp
    public static ulong[] OrderIds(IEnumerable<ulong> ids, IReadOnlyDictionary<ulong, GitHubSkinEntry> entries, GitHubSort sort, bool reverse = false)
    {
        var keyed = ids.Select(id => (Id: id, Key: entries.TryGetValue(id, out var entry) ? Key(entry, sort) : null)).ToArray();
        var ordered = reverse
            ? keyed.OrderByDescending(item => item.Key != null).ThenByDescending(item => item.Key, StringComparer.Ordinal)
            : keyed.OrderBy(item => item.Key == null).ThenBy(item => item.Key, StringComparer.Ordinal);
        return ordered.ThenBy(item => item.Id).Select(item => item.Id).ToArray();
    }
```

（`OrderIds` 的升降序由下面的显式实现表达，不用 `Comparer<string>.Default` 的隐式方向。）

- [ ] **Step 4: 运行确认通过**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release`
Expected: `GitHub skin catalog passed: …`，退出码 0。

- [ ] **Step 5: 提交**

第四位改 `1.0.8.8`，`git commit -m "feat: cache GitHub skin entries across sessions"`。

---

### Task 5: 出网客户端

**Files:**
- Create: `STS2SkinChanger/Core/GitHubSkinClient.cs`
- Create: `tests/STS2SkinChanger.LogicTests/GitHubSkinClientTests.cs`
- Modify: `LogicTests.csproj`、`Program.cs`

**Interfaces:**
- Consumes: `GitHubSkinPolicy`、`GitHubSkinClient` 自身
- Produces:
  - `class GitHubRateLimitedException : Exception`
  - `GitHubSkinClient(HttpMessageHandler handler, string userAgent)`
  - `GitHubSkinClient.SearchPageAsync(int page, CancellationToken) → Task<string>`
  - `GitHubSkinClient.LatestReleaseAsync(GitHubRepoRef repo, CancellationToken) → Task<string?>`（404 返回 null）
  - `GitHubSkinClient.DownloadAsync(Uri uri, string destination, CancellationToken) → Task<long>`
  - `GitHubSkinClient.Dispose()`

- [ ] **Step 1: 写失败测试**

创建 `tests/STS2SkinChanger.LogicTests/GitHubSkinClientTests.cs`：

```csharp
using System.Net;
using System.Net.Http;
using STS2SkinChanger.Core;

internal static class GitHubSkinClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        _ = RunAsync().GetAwaiter().GetResult();
        Console.WriteLine("GitHub skin client passed: headers, host allowlist, redirect guard, rate limit, bounded download.");

        async Task RunAsync()
        {
            var ok = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"items\":[]}") });
            using (var client = new GitHubSkinClient(ok, "SkinChanger/1.0.8.8"))
            {
                await client.SearchPageAsync(1, CancellationToken.None);
                var request = ok.Requests[0];
                Require(request.Headers.UserAgent.ToString().StartsWith("SkinChanger"), "请求必须带 User-Agent，否则 GitHub 直接拒绝。");
                Require(request.Headers.Accept.Any(value => value.MediaType == "application/vnd.github+json"),
                    "请求必须声明 GitHub 的 JSON 媒体类型。");
                Require(request.RequestUri!.Host == "api.github.com", "搜索请求只能打到 api.github.com。");

                var limited = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
                { Content = new StringContent("{\"message\":\"rate limit\"}") });
                using var limitedClient = new GitHubSkinClient(limited, "SkinChanger/1.0.8.8");
                Require(await Throws<GitHubRateLimitedException>(() => limitedClient.SearchPageAsync(1, CancellationToken.None)),
                    "403 必须转成限额异常，让上层保留缓存并提示稍后重试。");
                var missing = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
                using var missingClient = new GitHubSkinClient(missing, "SkinChanger/1.0.8.8");
                Require(await missingClient.LatestReleaseAsync(new GitHubRepoRef("o", "r"), CancellationToken.None) == null,
                    "没有 Release 的仓库要返回 null，而不是抛错中断整次刷新。");
                var foreign = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
                using var foreignClient = new GitHubSkinClient(foreign, "SkinChanger/1.0.8.8");
                Require(await Throws<InvalidOperationException>(() => foreignClient.DownloadAsync(new Uri("https://evil.example.com/x.zip"), Path.GetTempFileName(), CancellationToken.None)),
                    "白名单外的主机必须在发出任何请求前拒绝。");
                Require(foreign.Requests.Count == 0, "被拒绝的主机不能真的发出请求。");

                var hops = new Handler(request => request.RequestUri!.Host == "github.com"
                    ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example.com/x.zip") } }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[8]) });
                using var hopClient = new GitHubSkinClient(hops, "SkinChanger/1.0.8.8");
                var destination = Path.Combine(Path.GetTempPath(), "sc-gh-dl-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(destination);
                try
                {
                    Require(await Throws<InvalidOperationException>(() => hopClient.DownloadAsync(
                        new Uri("https://github.com/o/r/releases/download/v1/r.zip"), Path.Combine(destination, "r.zip"), CancellationToken.None)),
                        "重定向到白名单外的主机必须中止，不能逐跳放行。");
                }
                finally { Directory.Delete(destination, true); }

                var payload = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1024]) });
                using var payloadClient = new GitHubSkinClient(payload, "SkinChanger/1.0.8.8");
                var target = Path.Combine(Path.GetTempPath(), "sc-gh-dl-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(target);
                try
                {
                    var written = await payloadClient.DownloadAsync(new Uri("https://github.com/o/r/releases/download/v1/r.zip"), Path.Combine(target, "r.zip"), CancellationToken.None);
                    Require(written == 1024 && File.Exists(Path.Combine(target, "r.zip")) && !File.Exists(Path.Combine(target, "r.zip.part")),
                        "下载先写 .part 再改名，失败不会留下半个包。");
                }
                finally { Directory.Delete(target, true); }
            }
        }
    }
    private static async Task<bool> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
        catch { return false; }
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release`
Expected: 编译失败，`GitHubSkinClient` 不存在。

- [ ] **Step 3: 最小实现**

`STS2SkinChanger/Core/GitHubSkinClient.cs`：

```csharp
using System.Net;
using System.Net.Http;

namespace STS2SkinChanger.Core;

internal sealed class GitHubRateLimitedException(string message) : Exception(message);

// Unauthenticated GitHub access is rate limited (search 10/min, core 60/h). A limited
// reply must never clear the list; it is reported and the cached catalog stays.
internal sealed class GitHubSkinClient : IDisposable
{
    private const int MaxJsonBytes = 4 * 1024 * 1024;
    private const long MaxDownloadBytes = GitHubSkinPolicy.MaxAssetBytes;
    private const int MaxRedirects = 5;
    private readonly HttpClient _client;
    public GitHubSkinClient(HttpMessageHandler handler, string userAgent)
    {
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }
    public static GitHubSkinClient CreateDefault() => new(new HttpClientHandler { AllowAutoRedirect = false },
        $"STS2-SkinChanger/{Entry.InternalTestVersion}");

    public Task<string> SearchPageAsync(int page, CancellationToken token) => GetJsonAsync(GitHubSkinPolicy.SearchUri(page), token);
    public async Task<string?> LatestReleaseAsync(GitHubRepoRef repo, CancellationToken token)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, GitHubSkinPolicy.LatestReleaseUri(repo)), token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadTextAsync(response, token);
    }
    private async Task<string> GetJsonAsync(Uri uri, CancellationToken token)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), token);
        return await ReadTextAsync(response, token);
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri == null || !GitHubSkinPolicy.IsAllowedHost(request.RequestUri.Host))
            throw new InvalidOperationException("Refused a request outside the GitHub allowlist: " + request.RequestUri);
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests &&
            (response.Headers.TryGetValues("x-ratelimit-remaining", out var values) && values.FirstOrDefault() == "0" ||
             response.StatusCode == HttpStatusCode.TooManyRequests))
        {
            response.Dispose();
            throw new GitHubRateLimitedException("GitHub API rate limit reached.");
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException("GitHub request failed: " + status);
        }
        return response;
    }
    private static async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaxJsonBytes) throw new InvalidDataException("GitHub response too large.");
        var text = await response.Content.ReadAsStringAsync(token);
        if (text.Length > MaxJsonBytes) throw new InvalidDataException("GitHub response too large.");
        return text;
    }

    public async Task<long> DownloadAsync(Uri uri, string destination, CancellationToken token)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !GitHubSkinPolicy.IsAllowedHost(uri.Host))
            throw new InvalidOperationException("Refused a download outside the GitHub allowlist: " + uri);
        var current = uri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.SeeOther)
            {
                var location = response.Headers.Location ?? throw new HttpRequestException("GitHub redirect without a target.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                // Every hop is validated: a redirect must never leave the allowlist.
                if (current.Scheme != Uri.UriSchemeHttps || !GitHubSkinPolicy.IsAllowedHost(current.Host))
                    throw new InvalidOperationException("Refused a redirect outside the GitHub allowlist: " + current);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new GitHubRateLimitedException("GitHub download limit reached.");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("GitHub download failed: " + (int)response.StatusCode);
            if (response.Content.Headers.ContentLength is { } length && length > MaxDownloadBytes) throw new InvalidDataException("GitHub asset too large.");
            var part = destination + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            long written = 0;
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using (var output = File.Create(part))
                {
                    var buffer = new byte[1 << 16];
                    int count;
                    while ((count = await input.ReadAsync(buffer, token)) > 0)
                    {
                        written += count;
                        if (written > MaxDownloadBytes) throw new InvalidDataException("GitHub asset too large.");
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                    }
                }
                File.Move(part, destination, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
                throw;
            }
            return written;
        }
        throw new InvalidOperationException("Too many GitHub redirects.");
    }
    public void Dispose() => _client.Dispose();
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release`
Expected: `GitHub skin client passed: …`，退出码 0。

- [ ] **Step 5: 提交**

第四位改 `1.0.8.9`，`git commit -m "feat: add an allowlisted GitHub client with cache-safe limits"`。

---

### Task 6: 刷新、识别与安装服务

**Files:**
- Create: `STS2SkinChanger/Core/GitHubSkinService.cs`
- Modify: `STS2SkinChanger/Core/SkinChangerPaths.cs`（新增 `GitHubCacheDirectory`、`GitHubCatalogPath`）
- Modify: `STS2SkinChanger/Catalog/SkinCatalog.Workshop.cs:58-64`（`WorkshopSourceId` 增加 `github` 段）
- Modify: `tests/STS2SkinChanger.RuntimeTests/Program.cs`（新增 `--audit-github-recognition` 分支）
- Create: `tests/STS2SkinChanger.RuntimeTests/GitHubRecognitionAudit.cs`

**Interfaces:**
- Consumes: `GitHubSkinClient`、`GitHubSkinCatalog`、`GitHubSkinPolicy`、`GitHubZipPolicy`、`GitHubInstallPolicy`、`SkinService.CaptureWorkshopScanContext()`、`SkinService.RunWorkshopScan(Func<T>, CancellationToken)`、`WorkshopSubmissionScanner.Scan/ReadDescriptors`、`SkinChangerPaths`
- Produces:
  - `SkinChangerPaths.GitHubCacheDirectory`（`<会话缓存>/github`）、`SkinChangerPaths.GitHubCatalogPath`（`<配置根>/github_skins.json`）
  - `SkinCatalog.WorkshopSourceId` 识别 `github/<id>` 路径
  - `GitHubSkinService`：`Revision`、`Busy`、`Progress`、`Failed`、`RateLimited`、`Items`、`EntriesById`、`Entry(ulong) → GitHubSkinEntry?`、`Title(ulong)`、`CoverUrl(ulong)`、`RepoUrl(ulong)`、`InstallState(ulong)`、`IsInstalling(ulong)`、`StartRefresh()`、`Task RefreshAsync(bool restore)`、`Task InstallAsync(ulong, CancellationToken)`

- [ ] **Step 1: 写失败测试（识别 audit）**

创建 `tests/STS2SkinChanger.RuntimeTests/GitHubRecognitionAudit.cs`：

```csharp
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

/// <summary>
/// Read-only check that a GitHub release folder is recognized through the same offline path
/// the Workshop submission scan uses: the folder is staged below a "github/&lt;id&gt;" path so the
/// catalog binds the synthetic source id, and the exported targets must match the package.
/// No provider initializer runs and no game process is started.
/// </summary>
internal static class GitHubRecognitionAudit
{
    internal static void Run(string gamePck, string root, string owner, string repo)
    {
        var id = SkinSourceIdentity.GitHubId(owner, repo);
        Console.WriteLine($"source id={id} github={SkinSourceIdentity.IsGitHub(id)} root={root}");
        Console.WriteLine($"workshop source id={SkinCatalog.WorkshopSourceId(root)}");
        var cards = CardArtPackRecognitionAudit.CardEntries(gamePck);
        var result = WorkshopSubmissionScanner.Scan(new(id, repo, root), gamePck, cards, "0.107.1", new Dictionary<string, string>());
        Console.WriteLine(result.Item == null
            ? $"recognition failed: {result.Error}"
            : $"recognition targets=" + string.Join("|", result.Item.Targets.Select(target => target.Kind + ":" + target.Target)) +
              $" restart={result.Item.RestartRequired}");
        Console.WriteLine("descriptors: " + string.Join(", ", WorkshopSubmissionScanner.ReadDescriptors(root).Select(mod => mod.Id + "@" + mod.RootPath)));
    }
}
```

在 `RuntimeTests/Program.cs` 里仿照 `--audit-art-pack-recognition` 加：

```csharp
if (args.Length == 5 && args[0] == "--audit-github-recognition")
{
    GitHubRecognitionAudit.Run(args[1], args[2], args[3], args[4]);
    return;
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/STS2SkinChanger.RuntimeTests -c Release -- --audit-github-recognition <游戏PCK> <解压后的Mod目录> qbyoffice SilentSkinMod`
Expected: 编译通过但输出 `workshop source id=0` 与 `recognition failed: No recognized skin resources.`（路径还没有 `github` 段标记）。

- [ ] **Step 3: 实现路径标记与服务**

`Catalog/SkinCatalog.Workshop.cs` 的 `WorkshopSourceId` 改为：

```csharp
    internal static ulong WorkshopSourceId(string? path)
    {
        var parts = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i++)
            // Steam installs, the formal-version snapshot cache and the GitHub staging root
            // all encode the source id in the same position, so one lookup serves every source.
            if ((parts[i] == "2868840" || parts[i] == "_workshop_formal_cache" || parts[i] == "github") &&
                ulong.TryParse(parts[i + 1], out var id)) return id;
        return 0;
    }
```

`Core/SkinChangerPaths.cs` 增加：

```csharp
    public static string GitHubCacheDirectory => Path.Combine(CacheDirectory, "github");
    // Recognized results must outlive a session: rebuilding them costs a download per repository.
    public static string GitHubCatalogPath => Configuration("github_skins.json");
```

`Core/GitHubSkinService.cs`（关键实现，完整文件）：

```csharp
using System.IO.Compression;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using Godot;

namespace STS2SkinChanger.Core;

// The second skin source. Listing comes from the GitHub topic search, recognition from the
// same offline package scan the Workshop submission flow uses, installation from a plain
// copy into the game's mods folder.
internal static class GitHubSkinService
{
    private const int MaxReleaseQueriesPerRefresh = 30;
    private static readonly GitHubSkinCatalog Catalog = new();
    private static GitHubSkinClient? _client;
    private static Task? _running;
    private static readonly HashSet<ulong> Installing = [];
    private static WorkshopCatalogItem[] _items = [];
    private static GitHubSkinEntry[] _recognized = [];
    private static IReadOnlyDictionary<ulong, GitHubSkinEntry> _entriesById = new Dictionary<ulong, GitHubSkinEntry>();
    public static int Revision { get; private set; }
    public static bool Busy { get; private set; }
    public static bool Failed { get; private set; }
    public static bool RateLimited { get; private set; }
    public static string Progress { get; private set; } = "";
    public static WorkshopCatalogItem[] Items => _items;
    public static IReadOnlyDictionary<ulong, GitHubSkinEntry> EntriesById => _entriesById;
    public static GitHubSkinEntry? Entry(ulong id) => _entriesById.GetValueOrDefault(id);
    public static string Title(ulong id) => Entry(id) is { } entry ? DisplayName(entry) : "";
    public static string? CoverUrl(ulong id) => Entry(id)?.CoverUrl;
    public static string RepoUrl(ulong id) => Entry(id)?.Repo.Url ?? "";
    public static GitHubInstallState InstallState(ulong id) => Entry(id) is { Recognition: GitHubRecognition.Recognized } entry
        ? GitHubInstallPolicy.Classify(InstalledVersion(entry), entry.PackageVersion) : GitHubInstallState.NotInstalled;
    public static bool IsInstalling(ulong id) => Installing.Contains(id);
    private static string DisplayName(GitHubSkinEntry entry) => entry.PackageName.Length > 0 ? entry.PackageName : entry.Name;

    public static void StartRefresh()
    {
        if (_running != null) return;
        _running = RefreshAsync(restore: true);
    }
    public static async Task RefreshAsync(bool restore)
    {
        if (Busy) return;
        Busy = true; Failed = false; RateLimited = false;
        try
        {
            if (restore)
            {
                try { Catalog.Restore(SkinChangerPaths.GitHubCatalogPath); }
                catch (Exception ex) { ModLog.Warn("GitHub 皮肤缓存不可用，将重新拉取：" + ex.Message); }
                PublishItems();
            }
            _client ??= GitHubSkinClient.CreateDefault();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var token = timeout.Token;
            var repos = await FetchReposAsync(token);
            Catalog.Merge(repos.Select(GitHubSkinCatalog.FromRepo));
            PublishItems();
            SaveCatalog();
            await RecognizePendingAsync(token);
            ModLog.Info($"GitHub 皮肤列表刷新完成：{Catalog.Entries.Length} 个仓库，已识别 {_recognized.Length} 个。");
        }
        catch (GitHubRateLimitedException ex)
        {
            RateLimited = true; Failed = true;
            ModLog.Warn("GitHub 接口受限，保留缓存列表：" + ex.Message);
        }
        catch (Exception ex)
        {
            Failed = true;
            ModLog.Warn("GitHub 皮肤列表刷新失败，保留缓存列表：" + ex.GetBaseException().Message);
        }
        finally { Busy = false; Progress = ""; }
    }
    private static async Task<GitHubRepoInfo[]> FetchReposAsync(CancellationToken token)
    {
        var result = new List<GitHubRepoInfo>();
        for (var page = 1; page <= GitHubSkinPolicy.MaxSearchPages; page++)
        {
            Progress = $"{page}/{GitHubSkinPolicy.MaxSearchPages}";
            var json = await _client!.SearchPageAsync(page, token);
            var pageItems = GitHubSkinPolicy.ParseSearch(json);
            result.AddRange(pageItems);
            if (pageItems.Length < GitHubSkinPolicy.SearchPageSize) break;
        }
        return result.DistinctBy(info => info.Repo.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task RecognizePendingAsync(CancellationToken token)
    {
        var pending = Catalog.Entries.Where(entry => entry.Recognition == GitHubRecognition.Pending)
            .OrderByDescending(entry => entry.PushedAt).Take(MaxReleaseQueriesPerRefresh).ToArray();
        for (var index = 0; index < pending.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            Progress = $"{index + 1}/{pending.Length}";
            var entry = pending[index];
            try { Catalog.Publish(await RecognizeAsync(entry, token)); }
            catch (GitHubRateLimitedException) { RateLimited = true; throw; }
            catch (Exception ex)
            {
                Catalog.Publish(entry with { Recognition = GitHubRecognition.Failed, Error = ex.GetBaseException().Message[..Math.Min(512, ex.GetBaseException().Message.Length)] });
            }
            PublishItems();
            SaveCatalog();
        }
    }

    private static async Task<GitHubSkinEntry> RecognizeAsync(GitHubSkinEntry entry, CancellationToken token)
    {
        var json = await _client!.LatestReleaseAsync(entry.Repo, token);
        if (json == null) return entry with { Recognition = GitHubRecognition.NoPackage, Error = "release" };
        var release = GitHubSkinPolicy.ParseRelease(json);
        if (release == null) return entry with { Recognition = GitHubRecognition.NoPackage, Error = "release" };
        var asset = GitHubSkinPolicy.SelectAsset(release, entry.RepoName);
        var tagged = entry with { ReleaseTag = release.Tag, AssetUrl = asset?.Url ?? "", AssetSize = asset?.Size ?? 0 };
        if (asset == null) return tagged with { Recognition = GitHubRecognition.NoPackage };
        var package = await StageAsync(tagged, token);
        if (package == null) return tagged with { Recognition = GitHubRecognition.NoResources };
        var scan = package.Value;
        return scan.Item == null
            ? tagged with { Recognition = GitHubRecognition.NoResources, Error = scan.Error[..Math.Min(512, scan.Error.Length)] }
            : tagged with { Recognition = GitHubRecognition.Recognized, PackageId = scan.PackageId, PackageName = scan.PackageName,
                PackageVersion = scan.PackageVersion, Targets = scan.Item.Targets, RestartRequired = true };
    }

    private readonly record struct StagedPackage(string Directory, string PackageId, string PackageName, string PackageVersion, WorkshopCatalogItem? Item, string Error);

    private static async Task<StagedPackage?> StageAsync(GitHubSkinEntry entry, CancellationToken token)
    {
        var root = Path.Combine(SkinChangerPaths.GitHubCacheDirectory, entry.Id.ToString());
        if (!PrepareStaging(root)) return null;
        var zip = Path.Combine(root, "package.zip");
        if (!File.Exists(zip) || new FileInfo(zip).Length != entry.AssetSize)
            await _client!.DownloadAsync(new Uri(entry.AssetUrl), zip, token);
        var package = Path.Combine(root, "package");
        Extract(zip, package);
        // The scan below runs on the game thread through the same snapshot the submission
        // scanner uses, so a GitHub package is recognized exactly like a Workshop one.
        var snapshot = SkinService.CaptureWorkshopScanContext();
        var gameVersion = AccessTools.Field(typeof(ModManager), "_gameVersion")?.GetValue(null)?.ToString() ?? "";
        var dependencies = ModManager.Mods.Where(mod => mod.state == ModLoadState.Loaded && mod.manifest?.id != null)
            .GroupBy(mod => mod.manifest!.id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().manifest!.version ?? "", StringComparer.OrdinalIgnoreCase);
        var descriptors = WorkshopSubmissionScanner.ReadDescriptors(package);
        if (descriptors.Length == 0) return null;
        var result = await SkinService.RunWorkshopScan(() => WorkshopSubmissionScanner.Scan(
            new(entry.Id, entry.Name, package), snapshot.GamePack, snapshot.Cards, gameVersion, dependencies, snapshot.Baselines), token);
        var descriptor = descriptors.FirstOrDefault(mod => result.Item != null &&
            result.Item.Targets.Length > 0 && mod.Id.Equals(result.Item.PackageId, StringComparison.OrdinalIgnoreCase)) ?? descriptors[0];
        return new(package, descriptor.Id, descriptor.Name,
            GitHubInstallPolicy.ReadManifestVersion(descriptor.RootPath ?? package, descriptor.Id) ?? entry.ReleaseTag,
            result.Item, result.Error);
    }

    private static bool PrepareStaging(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Directory.CreateDirectory(root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ModLog.Warn("GitHub 暂存目录不可用：" + ex.Message); return false; }
    }
    private static void Extract(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(zipPath);
        long total = 0; var index = 0;
        foreach (var item in archive.Entries)
        {
            var verdict = GitHubZipPolicy.Classify(item.FullName, item.Length, index++, total, out var relative);
            if (verdict == GitHubZipEntry.Reject) throw new InvalidDataException("Unsafe archive entry: " + item.FullName);
            if (verdict == GitHubZipEntry.Skip) continue;
            total += item.Length;
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Archive entry escapes the staging folder.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            item.ExtractToFile(target, overwrite: true);
        }
    }

    public static async Task InstallAsync(ulong id, CancellationToken token)
    {
        if (!Installing.Add(id)) return;
        try
        {
            var entry = Entry(id) ?? throw new InvalidOperationException("Unknown GitHub skin.");
            if (entry.Recognition != GitHubRecognition.Recognized) throw new InvalidOperationException("Skin is not recognized.");
            var package = await StageAsync(entry, token) ?? throw new InvalidOperationException("Release package is unavailable.");
            var mods = GitHubInstallPolicy.ModsDirectory(OS.GetExecutablePath());
            Directory.CreateDirectory(mods);
            var folder = GitHubInstallPolicy.FolderName(package.PackageId, entry.RepoName);
            var target = Path.Combine(mods, folder);
            var staged = Path.Combine(mods, "." + folder + ".installing");
            if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
            CopyDirectory(package.Directory, staged);
            // Replace the previous package only after the new one is complete on disk.
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staged, target);
            ModLog.Info($"GitHub 皮肤 {entry.FullName} 已安装到 {target}；下次启动游戏生效。");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(ex.GetBaseException().Message, ex);
        }
        finally { Installing.Remove(id); }
    }
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith(".git", StringComparison.OrdinalIgnoreCase))) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string? InstalledVersion(GitHubSkinEntry entry)
    {
        try
        {
            var mods = GitHubInstallPolicy.ModsDirectory(OS.GetExecutablePath());
            var folder = GitHubInstallPolicy.FolderName(entry.PackageId, entry.RepoName);
            return GitHubInstallPolicy.ReadManifestVersion(Path.Combine(mods, folder), entry.PackageId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void PublishItems()
    {
        _recognized = Catalog.Entries.Where(entry => entry.Recognition == GitHubRecognition.Recognized).ToArray();
        _entriesById = Catalog.Entries.ToDictionary(entry => entry.Id);
        _items = Catalog.Entries.Select(entry => new WorkshopCatalogItem(entry.Id,
            entry.Recognition == GitHubRecognition.Recognized ? entry.Targets : [], true)).ToArray();
        Revision++;
    }
    private static void SaveCatalog()
    {
        try { Catalog.Save(SkinChangerPaths.GitHubCatalogPath, null); }
        catch (Exception ex) { ModLog.Warn("GitHub 皮肤缓存写入失败：" + ex.Message); }
    }
}
```

`WorkshopScanResult` 需要暴露包 ID/名称/版本，做法是两处追加式改动（不改动既有行为）：

1. `Catalog/SkinCatalog.cs` 增加一个只读查询，把源 ID 映射回提供者 ID：

```csharp
    /// <summary>
    /// Which provider Mod a source id was bound to. The Workshop scan knows only the source
    /// id it was called with, while an installer or audit needs the provider's own id, folder
    /// and manifest version to place the package.
    /// </summary>
    internal string? ProviderForSourceId(ulong sourceId) => _workshopSourceIds
        .Where(pair => pair.Value == sourceId)
        .Select(pair => pair.Key)
        .FirstOrDefault(providerId => Groups.SelectMany(group => group.Options).Any(option => option.EffectiveProviderId == providerId) ||
            CardGroups.SelectMany(group => group.Options).Any(option => (option.ProviderId ?? option.Id) == providerId));
```

2. `Core/WorkshopSubmissionScanner.cs` 的 `WorkshopScanResult` 追加两个可选字段，并在 `Scan` 成功返回时填入：

```csharp
internal sealed record WorkshopScanResult(WorkshopCatalogItem? Item, string Name, string Error, string ProviderId = "", string ProviderName = "");
```

```csharp
            var providerId = catalog.ProviderForSourceId(local.Id) ?? "";
            return new(own with { RestartRequired = !hot }, local.Name, "", providerId,
                mods.FirstOrDefault(mod => mod.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase))?.Name ?? local.Name);
```

包版本仍由 `GitHubInstallPolicy.ReadManifestVersion(descriptor.RootPath ?? package, descriptor.Id)` 从暂存目录的清单里读，不需要给 `SkinModDescriptor` 加字段。

**安装复用暂存目录**：`StageAsync` 在 `package.zip` 大小与 `entry.AssetSize` 一致时跳过下载，安装不再重复下载或重复识别。

- [ ] **Step 4: 运行确认通过**

先构建：`dotnet build STS2SkinChanger/STS2SkinChanger.csproj -c Release`
再跑 audit（用真实包目录）：
`dotnet run --project tests/STS2SkinChanger.RuntimeTests -c Release -- --audit-github-recognition "<游戏PCK>" "%TEMP%\sc-gh-probe\SilentSkinMod-0.1.4" qbyoffice SilentSkinMod`
Expected: `workshop source id=<合成ID>`（非 0）且 `recognition targets=character:silent`（分组按实包内容，与 SilentSkinMod v0.1.4 一致）。

- [ ] **Step 5: 提交**

第四位改 `1.0.8.10`，`git commit -m "feat: recognize GitHub release packages through the offline scan"`。

---

### Task 7: 面板目录与 Steam 专用路径分派

**Files:**
- Modify: `STS2SkinChanger/Core/SkinWorkshopService.cs`
- Modify: `STS2SkinChanger/Ui/WorkshopCoverCache.cs`（无需改，`SkinWorkshopService.Cover` 白名单在服务里）
- Test: `tests/STS2SkinChanger.LogicTests/GitHubSourceRoutingTests.cs` + 新文件 `STS2SkinChanger/Core/SkinSourceRoutingPolicy.cs`

**Interfaces:**
- Consumes: `SkinSourceIdentity`、`GitHubSkinService`
- Produces:
  - `SkinSourceRoutingPolicy.IsSteamItem(ulong) → bool`（= `SkinSourceIdentity.IsSteam`）
  - `SkinSourceRoutingPolicy.CatalogUnion(IReadOnlyList<WorkshopCatalogItem> steam, IReadOnlyList<WorkshopCatalogItem> github) → WorkshopCatalogItem[]`（按 ID 去重、Steam 优先）
  - `SkinWorkshopService.Catalog` 返回并集；`IsSubscribed/IsInstalled/Progress/LoadTag` 按来源分派

- [ ] **Step 1: 写失败测试**

创建 `tests/STS2SkinChanger.LogicTests/GitHubSourceRoutingTests.cs`：

```csharp
using STS2SkinChanger.Core;

internal static class GitHubSourceRoutingTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        var steam = new WorkshopCatalogItem(3787302680UL, [new("character", "silent")]);
        var github = new WorkshopCatalogItem(SkinSourceIdentity.GitHubId("o", "r"), [new("cards", "silent")]);
        var union = SkinSourceRoutingPolicy.CatalogUnion([steam], [github]);
        Require(union.Length == 2 && union[0].Id == steam.Id, "并集必须同时包含两种来源，Steam 条目在前保持既有顺序。");
        var duplicate = new WorkshopCatalogItem(steam.Id, [new("cards", "defect")]);
        Require(SkinSourceRoutingPolicy.CatalogUnion([steam], [duplicate]).Single().Targets.Single().Kind == "character",
            "同一 ID 冲突时以 Steam 条目为准，合成 ID 不会与工坊 ID 相同，冲突只可能来自坏数据。");
        Require(SkinSourceRoutingPolicy.IsSteamItem(steam.Id) && !SkinSourceRoutingPolicy.IsSteamItem(github.Id),
            "Steam 专用路径必须只对真实工坊 ID 生效。");
        Require(SkinSourceRoutingPolicy.SteamOnly(github.Id) is null, "GitHub 条目没有 Steam 订阅状态，必须返回 null 而不是 false。");
        Require(SkinSourceRoutingPolicy.SteamOnly(steam.Id) == true, "Steam 条目保留原有订阅状态读取。");
        Console.WriteLine("GitHub source routing passed: union catalog and Steam-only guards.");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release`
Expected: 编译失败，`SkinSourceRoutingPolicy` 不存在。

- [ ] **Step 3: 实现分派与目录合并**

创建 `STS2SkinChanger/Core/SkinSourceRoutingPolicy.cs`：

```csharp
namespace STS2SkinChanger.Core;

// One identity space, two sources. Steam-only reads (subscription state, UGC metadata,
// hot registration) must be skipped for synthetic GitHub ids instead of being asked
// about an item Steam has never heard of.
internal static class SkinSourceRoutingPolicy
{
    public static bool IsSteamItem(ulong id) => SkinSourceIdentity.IsSteam(id);
    public static bool? SteamOnly(ulong id) => SkinSourceIdentity.IsSteam(id) ? true : null;
    public static WorkshopCatalogItem[] CatalogUnion(IReadOnlyList<WorkshopCatalogItem> steam, IReadOnlyList<WorkshopCatalogItem> github) =>
        steam.Concat(github).GroupBy(item => item.Id).Select(group => group.First()).ToArray();
}
```

`Core/SkinWorkshopService.cs` 改动：

```csharp
    private static WorkshopCatalogItem[]? _unionItems;
    private static (int Community, int GitHub) _unionRevision = (-1, -1);
    public static IReadOnlyList<WorkshopCatalogItem> Catalog
    {
        get
        {
            var revision = (_communityRevision, GitHubSkinService.Revision);
            if (_unionItems != null && _unionRevision == revision) return _unionItems;
            _unionRevision = revision;
            _unionItems = SkinSourceRoutingPolicy.CatalogUnion(_combinedItems ?? Items.Value, GitHubSkinService.Items);
            return _unionItems;
        }
    }
    internal static bool CanReadMetadata(ulong id) => SkinSourceIdentity.IsSteam(id) &&
        (Catalog.Any(item => item.Id == id) || CommunityIssues.Any(issue => issue.Id == id));
    public static bool IsSubscribed(ulong id) => SkinSourceIdentity.IsSteam(id) &&
        ((EItemState)SteamUGC.GetItemState(new(id)) & EItemState.k_EItemStateSubscribed) != 0;
    public static bool IsInstalled(ulong id) => SkinSourceIdentity.IsSteam(id) && TryInstalled(id, out _) ||
        !SkinSourceIdentity.IsSteam(id) && GitHubSkinService.InstallState(id) != GitHubInstallState.NotInstalled;
    public static double? Progress(ulong id) => SkinSourceIdentity.IsSteam(id) && SteamUGC.GetItemDownloadInfo(new(id), out var bytes, out var total) && total > 0
        ? Math.Clamp(100d * bytes / total, 0, 100) : null;
    public static string LoadTag(WorkshopCatalogItem item) => SkinSourceIdentity.IsSteam(item.Id)
        ? ClassifySteam(item)
        : GitHubSkinService.InstallState(item.Id) == GitHubInstallState.NotInstalled ? "restart" : "restart";
    public static async Task Subscribe(ulong id)
    {
        if (!SkinSourceIdentity.IsSteam(id)) return;   // GitHub rows install through their own path.
        …既有实现…
    }
    public static async Task Unsubscribe(ulong id)
    {
        if (!SkinSourceIdentity.IsSteam(id)) return;
        …既有实现…
    }
```

`LoadTag` 的 GitHub 分支固定 `"restart"`：GitHub 包只能由游戏启动时加载，安装前后都需要重启，这与 spec「不提供免重启标记」一致；保留 `LoadTag` 的调用点不变。

`Cover` 的主机白名单追加一行：

```csharp
              uri.Host.EndsWith(".akamaihd.net", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("opengraph.githubassets.com", StringComparison.OrdinalIgnoreCase))) return null;
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release` 与 `dotnet build STS2SkinChanger/STS2SkinChanger.csproj -c Release`
Expected: 新用例通过；主工程 0 警告 0 错误。

- [ ] **Step 5: 提交**

第四位改 `1.0.8.11`，`git commit -m "feat: merge GitHub skins into the workshop catalog with source routing"`。

---

### Task 8: 面板 UI 与 15 语言文案

**Files:**
- Create: `STS2SkinChanger/Core/GitHubSkinText.cs`（完整 15 语言）
- Create: `STS2SkinChanger/Ui/SkinWorkshopPanel.Sources.cs`
- Modify: `Ui/SkinWorkshopPanel.Filters.cs`、`Ui/SkinWorkshopPanel.cs`、`Ui/SkinWorkshopPanel.Rows.cs`、`Ui/SkinWorkshopPanel.Actions.cs`
- Test: `tests/STS2SkinChanger.LogicTests/GitHubSkinTextTests.cs`

**Interfaces:**
- Consumes: `GitHubSkinService`、`GitHubSortPolicy`、`SkinSourceRoutingPolicy`
- Produces:
  - `enum GitHubSkinTextKey { Source, Steam, GitHub, Install, Reinstall, Update, Installed, NotInstalled, Updatable, Unrecognized, OpenRepo, Refresh, Refreshing, RefreshFailed, RateLimited, Downloading, Installing, InstallFailed, All, Stars, Updated, Name, Empty }`
  - `GitHubSkinText.Get(GitHubSkinTextKey)`、`GitHubSkinText.ForLanguage(string, GitHubSkinTextKey)`
  - 面板新增 `_source`、`_githubSort`、`_installFilter` 状态与对应控件

- [ ] **Step 1: 写失败测试**

创建 `tests/STS2SkinChanger.LogicTests/GitHubSkinTextTests.cs`：

```csharp
using STS2SkinChanger.Core;

internal static class GitHubSkinTextTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        var keys = Enum.GetValues<GitHubSkinTextKey>();
        foreach (var language in new[] { "zhs", "zht", "eng", "deu", "esp", "spa", "fra", "ita", "jpn", "kor", "pol", "ptb", "rus", "tha", "tur" })
            foreach (var key in keys)
                Require(!string.IsNullOrWhiteSpace(GitHubSkinText.ForLanguage(language, key)),
                    $"语言 {language} 的 {key} 不能为空，玩家界面不允许出现空白按钮。");
        Require(GitHubSkinText.ForLanguage("eng", GitHubSkinTextKey.Steam) == "Steam" &&
            GitHubSkinText.ForLanguage("eng", GitHubSkinTextKey.GitHub) == "GitHub",
            "来源名称在各语言保持原文，便于玩家对照。");
        Require(GitHubSkinText.ForLanguage("unknown", GitHubSkinTextKey.Install) == GitHubSkinText.ForLanguage("eng", GitHubSkinTextKey.Install),
            "未知语言必须回退英文，而不是抛异常或空白。");
        Console.WriteLine("GitHub skin text passed: 15 languages, no empty labels.");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release`
Expected: 编译失败，`GitHubSkinText` 未定义或键不足。

- [ ] **Step 3: 实现文案与面板**

`Core/GitHubSkinText.cs` 按 `WorkshopText` 的结构实现（`Dictionary<string, string[]>`，键顺序见 Interfaces；`zhs`/`eng` 两行示例）：

```csharp
        ["zhs"] = "来源|Steam|GitHub|安装|重新安装|更新|已安装|未安装|可更新|未识别|打开仓库|刷新|刷新中…|刷新失败|接口受限，稍后重试|下载中…|安装中…|安装失败|全部|星标|最近更新|名称|没有匹配的 GitHub 皮肤".Split('|'),
        ["eng"] = "Source|Steam|GitHub|Install|Reinstall|Update|Installed|Not installed|Update available|Not recognized|Open repository|Refresh|Refreshing…|Refresh failed|Rate limited, try later|Downloading…|Installing…|Install failed|All|Stars|Recently updated|Name|No matching GitHub skins".Split('|'),
```

其余 13 种语言按同样 23 个位置翻译（`zht/deu/esp/spa/fra/ita/jpn/kor/pol/ptb/rus/tha/tur`），未翻译位置回退 `eng` 会立刻被上面的测试抓到。

`Ui/SkinWorkshopPanel.Sources.cs`（新增 partial，实现要点）：

```csharp
using Godot;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal partial class SkinWorkshopPanel
{
    private string _source = "steam";
    private WorkshopPagedChoice _sourcePicker = null!;
    private WorkshopPagedChoice _installPicker = null!;
    private GitHubSort _githubSort = GitHubSort.Updated;
    private const string InstallFilterAll = "";
    private string _installFilter = InstallFilterAll;
    // Only "GitHub" is an explicit option: the picker's built-in "all" entry carries the Steam
    // label, so the row reads exactly "Steam | GitHub" without a meaningless "all sources".
    private static readonly string[] SourceOptions = ["github"];
    private static readonly string[] InstallOptions = ["missing", "installed", "updatable"];
    private bool GitHubMode => _source == "github";
    private static string SourceName(string value) => GitHubSkinText.Get(GitHubSkinTextKey.GitHub);
    private static string InstallName(string value) => value switch
    {
        "missing" => GitHubSkinText.Get(GitHubSkinTextKey.NotInstalled),
        "installed" => GitHubSkinText.Get(GitHubSkinTextKey.Installed),
        "updatable" => GitHubSkinText.Get(GitHubSkinTextKey.Updatable),
        _ => GitHubSkinText.Get(GitHubSkinTextKey.All)
    };

    private void BuildSourceFilters(HFlowContainer filters)
    {
        _sourcePicker = new(SourceName, value => { _source = value; _page = 0; _listScroll.ScrollVertical = 0; RefreshFilters(); Rebuild(); },
            () => GitHubSkinText.Get(GitHubSkinTextKey.Steam));
        filters.AddChild(_sourcePicker.Picker); KeepHoverBelow(_sourcePicker.Picker);
        _installPicker = new(InstallName, value => { _installFilter = value; _page = 0; RefreshFilters(); Rebuild(); });
        filters.AddChild(_installPicker.Picker); KeepHoverBelow(_installPicker.Picker);
    }
    private IEnumerable<WorkshopCatalogItem> SourceFiltered(IEnumerable<WorkshopCatalogItem> items) => items
        .Where(item => GitHubMode ? SkinSourceIdentity.IsGitHub(item.Id) : SkinSourceRoutingPolicy.IsSteamItem(item.Id))
        .Where(item => !GitHubMode || _installFilter.Length == 0 || (_installFilter switch
        {
            // "已安装" 覆盖“已安装”和“可更新”两种行；“可更新”只看版本比较的结果。
            "missing" => !SkinWorkshopService.IsInstalled(item.Id),
            "installed" => SkinWorkshopService.IsInstalled(item.Id),
            "updatable" => GitHubSkinService.InstallState(item.Id) == GitHubInstallState.Updatable,
            _ => true
        }));
    private string GitHubMetric(ulong id) => GitHubSortPolicy.Metric(GitHubSkinService.Entry(id), _githubSort);
}
```

`WorkshopPagedChoice` 的构造签名以 `Filters.cs:71-77` 的现有用法为准（`new(显示名函数, 选中回调)`，随后 `SetOptions(值集合, 当前值)`、`Picker`）。`RefreshFilters()` 里增加：

```csharp
        _sourcePicker.SetOptions(SourceOptions, _source);
        _sourcePicker.Picker.Visible = !CodeErrors;
        // 只有订阅状态是 Steam 专有的；类型、地区、目标与重启标签两种来源通用，继续沿用。
        _subscriptionPicker.Picker.Visible = !CodeErrors && !GitHubMode;
        _installPicker.SetOptions(InstallOptions, _installFilter);
        _installPicker.Picker.Visible = !CodeErrors && GitHubMode;
```

`BuildSort`（`SkinWorkshopPanel.cs:38-62`）在 GitHub 模式复用同一个 `_sortPicker`：`RefreshFilters()` 里按模式重填选项——GitHub 模式填 `GitHubSort` 三项（`Updated/Stars/Name`，文本取自 `GitHubSkinTextKey.Updated/Stars/Name`），Steam 模式保持现有 `WorkshopSort` 列表；`_sortPicker.ItemSelected` 回调按当前模式写入 `_githubSort` 或 `_sort`。

`FilteredItems()`（`Filters.cs:27-33`）改为：

```csharp
        var items = SourceFiltered(WorkshopBrowserPolicy.Filter(SkinWorkshopService.Catalog, _kind, _target, RegionMembers)
            .Where(item => WorkshopLoadTags.Matches(_loadFilter, SkinWorkshopService.LoadTag(item)))
            .Where(item => item.Targets.Length > 0 || GitHubSkinPolicy.KeepWithoutTargets(item.Id, item.Targets.Length, _kind)));
        var filtered = (GitHubMode ? items : _subscriptions.Filter(items)).ToDictionary(item => item.Id);
        return GitHubMode
            ? GitHubSortPolicy.OrderIds(filtered.Keys, GitHubSkinService.EntriesById, _githubSort, _reverseSort).Select(id => filtered[id]).ToArray()
            : WorkshopSortPolicy.OrderIds(filtered.Keys, SkinWorkshopService.CachedMetadata, _sort, _reverseSort, SkinWorkshopService.CommunityReplies)
                .Select(id => filtered[id]).ToArray();
```

（`GitHubSkinService` 相应暴露 `IReadOnlyDictionary<ulong, GitHubSkinEntry> EntriesById`，在 `PublishItems()` 里重建。）

`SkinWorkshopPanel.cs` 的 `Initialize()`：`BuildFilters(content)` 内 `BuildSourceFilters(filters)` 先于其它筛选器；`Poll()` 增加 GitHub 刷新按钮状态与进度（`_communityRefresh` 旁边新增一个“刷新”按钮，仅在 GitHub 模式可见，按下调用 `GitHubSkinService.StartRefresh()`，文本用 `Refreshing/Refresh/RefreshFailed/RateLimited`）。

`Rows.cs` 的 `BindRows` 增加：

```csharp
            if (SkinSourceIdentity.IsGitHub(item.Id))
            {
                view.Title.Text = GitHubSkinService.Title(item.Id);
                view.Metric.Text = GitHubMetric(item.Id);
                view.Cancel.Text = GitHubSkinText.Get(GitHubSkinTextKey.OpenRepo);
            }
```

（Steam 分支保持原样；这三行必须放在 `BindRows` 已有的 `view.Title.Text = …CachedDetails… / view.Metric.Text = …ItemMetric… / view.Cancel.Text = …Unsubscribe…` 之后，用来覆盖 Steam 文案。`LoadDetails` 只对 Steam ID 拿到详情：`SkinWorkshopService.Query` 内部已按 `CanReadMetadata` 过滤合成 ID，不会去问 Steam 要一个它不认识的物品。）

`Actions.cs`：`PerformPrimary` 开头按来源分派：

```csharp
        if (SkinSourceIdentity.IsGitHub(id))
        {
            if (GitHubSkinService.IsInstalling(id)) return;
            _actionErrors.Remove(id);
            _ = InstallGitHub(id);
            return;
        }
```

并新增：

```csharp
    private async Task InstallGitHub(ulong id)
    {
        try
        {
            using var token = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await GitHubSkinService.InstallAsync(id, token.Token);
        }
        catch (Exception ex) { ShowActionError(id, ex); }
        finally { if (!_closed && GodotObject.IsInstanceValid(this) && IsInsideTree()) Poll(); }
    }
```

`OpenItem(id)` 对 GitHub 行改为打开仓库：`if (SkinSourceIdentity.IsGitHub(id)) { OS.ShellOpen(GitHubSkinService.RepoUrl(id)); return; }`（`SkinSourceIdentity.IsGitHub` 为真但仓库为空时直接返回，不做任何事）。

`UpdateVisibleActions`（`SkinWorkshopPanel.cs:210-251`）的 GitHub 分支：

```csharp
            if (SkinSourceIdentity.IsGitHub(id))
            {
                var entry = GitHubSkinService.Entry(id);
                var state = GitHubSkinService.InstallState(id);
                var busy = GitHubSkinService.IsInstalling(id);
                var recognized = entry is { Recognition: GitHubRecognition.Recognized };
                // 未识别的仓库没有可安装的包：主按钮不出现，只留“打开仓库”和状态文字。
                action.Visible = recognized;
                action.Disabled = busy || _restartPending;
                action.Text = state == GitHubInstallState.Updatable ? GitHubSkinText.Get(GitHubSkinTextKey.Update)
                    : state == GitHubInstallState.Installed ? GitHubSkinText.Get(GitHubSkinTextKey.Reinstall)
                    : GitHubSkinText.Get(GitHubSkinTextKey.Install);
                action.TooltipText = busy ? GitHubSkinText.Get(GitHubSkinTextKey.Installing) : entry?.Error ?? "";
                cancel.Visible = true; cancel.Disabled = busy;
                cancel.Text = GitHubSkinText.Get(GitHubSkinTextKey.OpenRepo);
                cancel.TooltipText = cancel.Text;
                label.Text = !recognized
                    ? GitHubSkinText.Get(entry is { Recognition: GitHubRecognition.Pending }
                        ? GitHubSkinTextKey.Refreshing : GitHubSkinTextKey.Unrecognized)
                    : state == GitHubInstallState.Updatable ? GitHubSkinText.Get(GitHubSkinTextKey.Updatable)
                    : WorkshopText.Get(WorkshopTextKey.Restart);
                label.TooltipText = entry is { Error.Length: > 0 } ? entry.Error : label.Text;
                continue;
            }
```

未识别行的重启标签仍由 `LoadTag` 返回 `"restart"`（GitHub 包只能由游戏启动时加载），因此 `BindTags` 的第一个标签槽在两种状态下都显示“需要重启 / 可免重启”中的正确一项。

`EmptyText`（`Filters.cs:21`）在 GitHub 模式返回 `GitHubSkinText.Get(GitHubSkinTextKey.Empty)`。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release` 与 `dotnet build STS2SkinChanger/STS2SkinChanger.csproj -c Release`
Expected: 文案用例通过；主工程 0 警告 0 错误。

- [ ] **Step 5: 提交**

第四位改 `1.0.8.12`，`git commit -m "feat: add the GitHub source filter, listing and install actions to the workshop panel"`。

---

### Task 9: 文档、构建与部署

**Files:**
- Modify: `README.md`（工坊一节补来源筛选与 GitHub 安装说明，含“安装后需要重启”）
- Modify: `PRODUCT.md`（如其中描述了工坊来源，补一句 GitHub 来源）
- Modify: 四个版本文件（第四位到本轮最后一位）

- [ ] **Step 1: 全量回归**

Run（按顺序，任一失败即停下修复）：
1. `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release`
2. `dotnet run --project tests/STS2SkinChanger.RuntimeTests -c Release`
3. `dotnet build STS2SkinChanger/STS2SkinChanger.csproj -c Release`
4. `dotnet run --project tests/STS2SkinChanger.RuntimeTests -c Release -- --test-workshop`
Expected: 全部通过，主工程 0 警告 0 错误。

- [ ] **Step 2: 文档**

README 的工坊段落补一条：来源筛选（Steam / GitHub）、GitHub 列表来自 `github.com/topics/sts2-sc-mod`、安装位置为游戏 `mods/`、安装与更新都需要重启游戏、未识别的仓库会列出但不能安装。PRODUCT.md 同步一句来源说明。

- [ ] **Step 3: 构建并部署**

- 确认游戏已退出（检查游戏进程）。
- `dotnet build STS2SkinChanger/STS2SkinChanger.csproj -c Release`
- 把 `STS2SkinChanger/bin/Release/` 下的 `Gurio.SkinChanger.dll`、`thunninoiSkinManager.dll`、`SkinChanger.json`、`README.md`（部署为 `README`）复制到三处：`workshop/content`、测试版工坊目录 `workshop/content/2868840/3787302680`、正式版快照缓存 `_workshop_formal_cache/3787302680`。（清单里原有的 `SkinChanger.CacheCleanup.exe` 自 1.0.12.1 起不再存在，见规格文末“不再随包发布退出助手”。）
- 逐个核对四处 SHA-256 与构建源一致，并确认四处版本标记都是本轮最后一位。

- [ ] **Step 4: 提交**

Run: `git add -A; git commit -m "docs: document the GitHub skin source"`（含版本第四位最后一次递增）。不上传工坊、不推 GitHub。

---

## 自检结论

- **Spec 覆盖**：来源筛选（Task 8）、搜索与排除本仓库/fork（Task 2）、`pushed_at`/tag 缓存与限额降级（Task 4、5、6）、出网与重定向白名单（Task 2、5）、包内清单元数据（Task 6）、opengraph 封面（Task 2、7）、安装前识别与未识别分支（Task 6、8）、安装与更新判定（Task 3、6）、UI 复用现有筛选/排序/行/封面（Task 8）、15 语言与两版本兼容（Task 8、9）、测试（Task 1-5 纯逻辑 + Task 6 实包 audit）均落到具体任务。
- **与 spec 的两处偏差（已在计划中实现并需在 spec 里回填）**：① 列表首屏不阻塞在下载上——先出仓库列表，识别在后台逐项进行并实时刷新标签；② 单次刷新最多查询 30 个 Release、单资产上限 128 MB，超出的留在待识别状态，避免一次刷新打满未认证限额与带宽。
- **命名一致性**：`SkinSourceIdentity.IsGitHub/IsSteam`、`GitHubSkinEntry.Id`、`GitHubSkinService.Items/Entry/InstallState/EntriesById`、`GitHubSortPolicy.OrderIds/Metric`、`GitHubSkinText.ForLanguage` 在各任务间保持一致。
