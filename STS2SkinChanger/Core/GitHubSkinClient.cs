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
    private readonly string _userAgent;

    public GitHubSkinClient(HttpMessageHandler handler, string userAgent)
    {
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        _userAgent = userAgent;
    }

    public Task<string> SearchPageAsync(int page, CancellationToken token) =>
        GetJsonAsync(GitHubSkinPolicy.SearchUri(page), token);

    public async Task<string?> LatestReleaseAsync(GitHubRepoRef repo, CancellationToken token)
    {
        using var response = await SendAsync(Request(GitHubSkinPolicy.LatestReleaseUri(repo)), token, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadTextAsync(response, token);
    }

    private async Task<string> GetJsonAsync(Uri uri, CancellationToken token)
    {
        using var response = await SendAsync(Request(uri), token);
        return await ReadTextAsync(response, token);
    }

    // Headers go on every request rather than on the client defaults, so what the
    // handler receives is exactly what was configured.
    private HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token, bool allowNotFound = false)
    {
        if (request.RequestUri == null || !GitHubSkinPolicy.IsAllowedHost(request.RequestUri.Host))
            throw new InvalidOperationException("Refused a request outside the GitHub allowlist: " + request.RequestUri);
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        // GitHub answers 403 or 429 when the unauthenticated budget is gone; either way the
        // cached list must survive, so both are reported as a limit instead of a failure.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            response.Dispose();
            throw new GitHubRateLimitedException("GitHub API rate limit reached.");
        }
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return response;
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
            using var request = Request(current);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.SeeOther)
            {
                var location = response.Headers.Location ?? throw new HttpRequestException("GitHub redirect without a target.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                // Every hop is validated: a redirect must never leave the allowlist.
                if (current.Scheme != Uri.UriSchemeHttps || !GitHubSkinPolicy.IsAllowedHost(current.Host))
                    throw new InvalidOperationException("Refused a redirect outside the GitHub allowlist: " + current);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new GitHubRateLimitedException("GitHub download limit reached.");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("GitHub download failed: " + (int)response.StatusCode);
            if (response.Content.Headers.ContentLength is { } length && length > MaxDownloadBytes)
                throw new InvalidDataException("GitHub asset too large.");
            var part = destination + ".part";
            var directory = Path.GetDirectoryName(destination);
            if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);
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
