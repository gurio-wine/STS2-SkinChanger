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
        RunAsync().GetAwaiter().GetResult();
        Console.WriteLine("GitHub skin client passed: headers, host allowlist, redirect guard, rate limit, bounded download.");

        async Task RunAsync()
        {
            var ok = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"items\":[]}") });
            using (var client = new GitHubSkinClient(ok, "SkinChanger/1.0.8.9"))
            {
                await client.SearchPageAsync(1, CancellationToken.None);
                var request = ok.Requests[0];
                Require(request.Headers.UserAgent.ToString().StartsWith("SkinChanger"), "请求必须带 User-Agent，否则 GitHub 直接拒绝。");
                Require(request.Headers.Accept.Any(value => value.MediaType == "application/vnd.github+json"),
                    "请求必须声明 GitHub 的 JSON 媒体类型。");
                Require(request.RequestUri!.Host == "api.github.com", "搜索请求只能打到 api.github.com。");

                var limited = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
                { Content = new StringContent("{\"message\":\"rate limit\"}") });
                using var limitedClient = new GitHubSkinClient(limited, "SkinChanger/1.0.8.9");
                Require(await Throws<GitHubRateLimitedException>(() => limitedClient.SearchPageAsync(1, CancellationToken.None)),
                    "403 必须转成限额异常，让上层保留缓存并提示稍后重试。");

                var missing = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
                using var missingClient = new GitHubSkinClient(missing, "SkinChanger/1.0.8.9");
                Require(await missingClient.LatestReleaseAsync(new GitHubRepoRef("o", "r"), CancellationToken.None) == null,
                    "没有 Release 的仓库要返回 null，而不是抛错中断整次刷新。");
                // sc.info 缺失是常态：没发布信息码的仓库占多数，404 不能中断整次刷新。
                Require(await missingClient.InfoFileAsync(new GitHubRepoRef("o", "r"), CancellationToken.None) == null &&
                    missing.Requests[^1].RequestUri!.Host == "raw.githubusercontent.com",
                    "没有 sc.info 的仓库要返回 null，并且只读 raw 主机上的仓库根文件。");

                var infoFile = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("SCM3 1 a 1/1 b.12345678") });
                using var infoClient = new GitHubSkinClient(infoFile, "SkinChanger/1.0.8.15");
                Require(await infoClient.InfoFileAsync(new GitHubRepoRef("o", "r"), CancellationToken.None) == "SCM3 1 a 1/1 b.12345678",
                    "sc.info 的内容必须原样返回给识别策略。");

                var hugeInfo = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(new string('x', GitHubInfoPolicy.MaxBytes + 1)) });
                using var hugeInfoClient = new GitHubSkinClient(hugeInfo, "SkinChanger/1.0.8.15");
                Require(await Throws<InvalidDataException>(() => hugeInfoClient.InfoFileAsync(new GitHubRepoRef("o", "r"), CancellationToken.None)),
                    "超过上限的 sc.info 必须在读进内存前拒绝，不能让一个仓库撑爆刷新。");

                var foreign = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
                using var foreignClient = new GitHubSkinClient(foreign, "SkinChanger/1.0.8.9");
                Require(await Throws<InvalidOperationException>(() => foreignClient.DownloadAsync(
                        new Uri("https://evil.example.com/x.zip"), Path.GetTempFileName(), CancellationToken.None)),
                    "白名单外的主机必须在发出任何请求前拒绝。");
                Require(foreign.Requests.Count == 0, "被拒绝的主机不能真的发出请求。");

                var hops = new Handler(request => request.RequestUri!.Host == "github.com"
                    ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example.com/x.zip") } }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[8]) });
                using var hopClient = new GitHubSkinClient(hops, "SkinChanger/1.0.8.9");
                var destination = Path.Combine(Path.GetTempPath(), "sc-gh-dl-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(destination);
                try
                {
                    Require(await Throws<InvalidOperationException>(() => hopClient.DownloadAsync(
                            new Uri("https://github.com/o/r/releases/download/v1/r.zip"), Path.Combine(destination, "r.zip"), CancellationToken.None)),
                        "重定向到白名单外的主机必须中止，不能逐跳放行。");
                }
                finally { Directory.Delete(destination, true); }

                // Real GitHub behaviour: an asset download answers 302 into release-assets.githubusercontent.com.
                var releaseAssets = new Handler(request => request.RequestUri!.Host == "github.com"
                    ? new HttpResponseMessage(HttpStatusCode.Redirect)
                    { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/r.zip?sig=abc") } }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[512]) });
                using var releaseClient = new GitHubSkinClient(releaseAssets, "SkinChanger/1.0.8.11");
                var redirected = Path.Combine(Path.GetTempPath(), "sc-gh-dl-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(redirected);
                try
                {
                    var written = await releaseClient.DownloadAsync(
                        new Uri("https://github.com/o/r/releases/download/v1/r.zip"), Path.Combine(redirected, "r.zip"), CancellationToken.None);
                    Require(written == 512 && releaseAssets.Requests.Count == 2 &&
                        releaseAssets.Requests[1].RequestUri!.Host == "release-assets.githubusercontent.com",
                        "Release 资产会 302 到 release-assets.githubusercontent.com；这条真实链路必须被放行，否则下载永远失败。");
                }
                finally { Directory.Delete(redirected, true); }

                var payload = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1024]) });                using var payloadClient = new GitHubSkinClient(payload, "SkinChanger/1.0.8.9");
                var target = Path.Combine(Path.GetTempPath(), "sc-gh-dl-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(target);
                try
                {
                    var written = await payloadClient.DownloadAsync(
                        new Uri("https://github.com/o/r/releases/download/v1/r.zip"), Path.Combine(target, "r.zip"), CancellationToken.None);
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
