using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Steamworks;
using STS2SkinChanger;

// Explicit opt-in, read-only API smoke check. Never subscribes, publishes, launches
// the game, loads providers, or changes the player's files/settings.
internal static class WorkshopLiveMetadataCheck
{
    public static void Run(string nativeLibrary)
    {
        var library = Path.GetFullPath(nativeLibrary);
        if (!File.Exists(library)) throw new FileNotFoundException("Steam native library not found.", library);
        NativeLibrary.SetDllImportResolver(typeof(SteamAPI).Assembly, (name, _, _) =>
            name.Contains("steam_api", StringComparison.OrdinalIgnoreCase) ? NativeLibrary.Load(library) : IntPtr.Zero);
        Environment.SetEnvironmentVariable("SteamAppId", "2868840");
        Environment.SetEnvironmentVariable("SteamGameId", "2868840");
        if (!SteamAPI.Init()) throw new InvalidOperationException("Steam API unavailable; log in to the Steam client first.");
        var handle = UGCQueryHandle_t.Invalid;
        try
        {
            handle = SteamUGC.CreateQueryUGCDetailsRequest([new(3787302680)], 1);
            if (!SteamUGC.SetLanguage(handle, "schinese") || !SteamUGC.SetReturnLongDescription(handle, true) ||
                !SteamUGC.SetReturnAdditionalPreviews(handle, true)) throw new InvalidOperationException("Steam rejected query flags.");
            var completion = new TaskCompletionSource<SteamUGCQueryCompleted_t>();
            using var callback = CallResult<SteamUGCQueryCompleted_t>.Create((result, failed) =>
            {
                if (failed) completion.TrySetException(new IOException("Steam query IO failure."));
                else completion.TrySetResult(result);
            });
            callback.Set(SteamUGC.SendQueryUGCRequest(handle));
            var clock = Stopwatch.StartNew();
            while (!completion.Task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(25)) { SteamAPI.RunCallbacks(); Thread.Sleep(20); }
            if (!completion.Task.IsCompleted) throw new TimeoutException("Steam query timed out.");
            var result = completion.Task.GetAwaiter().GetResult();
            if (result.m_eResult != EResult.k_EResultOK || result.m_unNumResultsReturned != 1 ||
                !SteamUGC.GetQueryUGCResult(handle, 0, out var item) || item.m_eResult != EResult.k_EResultOK ||
                item.m_nPublishedFileId.m_PublishedFileId != 3787302680) throw new InvalidOperationException("Steam query result mismatch.");
            Func<EItemStatistic, ulong?> statistic = key => SteamUGC.GetQueryUGCStatistic(handle, 0, key, out var count) ? count : null;
            SteamUGC.GetQueryUGCPreviewURL(handle, 0, out var preview, 4096);
            var reader = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopMetadataReader", true)!;
            var details = reader.GetMethod("Read")!.Invoke(null, [item, preview, statistic])!;
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(details, details.GetType()));
            for (uint i = 0; i < SteamUGC.GetQueryUGCNumAdditionalPreviews(handle, 0); i++)
                if (!SteamUGC.GetQueryUGCAdditionalPreview(handle, 0, i, out var url, 4096, out _, 1024, out _) || string.IsNullOrEmpty(url))
                    throw new InvalidOperationException("Steam preview metadata unavailable.");
            Console.WriteLine($"Steam read-only metadata check passed: {SteamUGC.GetQueryUGCNumAdditionalPreviews(handle, 0)} previews, description {item.m_rgchDescription.Length} characters.");
        }
        finally
        {
            if (handle != UGCQueryHandle_t.Invalid) SteamUGC.ReleaseQueryUGCRequest(handle);
            SteamAPI.Shutdown();
        }
    }
}
