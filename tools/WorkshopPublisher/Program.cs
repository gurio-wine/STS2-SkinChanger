using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Steamworks;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: WorkshopPublisher <config.json>");
    return 2;
}

var configPath = Path.GetFullPath(args[0]);
var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};
var config = JsonSerializer.Deserialize<WorkshopConfig>(
    File.ReadAllText(configPath), jsonOptions)
    ?? throw new InvalidDataException("Workshop config is empty.");

var configDir = Path.GetDirectoryName(configPath)!;
var contentFolder = Path.GetFullPath(Path.Combine(configDir, config.ContentFolder));
var previewFile = Path.GetFullPath(Path.Combine(configDir, config.PreviewFile));

if (!Directory.Exists(contentFolder))
    throw new DirectoryNotFoundException(contentFolder);
if (!File.Exists(previewFile))
    throw new FileNotFoundException("Workshop preview image not found.", previewFile);

Environment.SetEnvironmentVariable("SteamAppId", config.AppId.ToString());
Environment.SetEnvironmentVariable("SteamGameId", config.AppId.ToString());

if (!SteamAPI.Init())
{
    Console.Error.WriteLine("SteamAPI.Init failed. Make sure Steam is running and the account owns the game.");
    return 3;
}

try
{
    if (!SteamUser.BLoggedOn())
    {
        Console.Error.WriteLine("Steam is not logged on.");
        return 4;
    }

    var appId = new AppId_t(config.AppId);
    PublishedFileId_t publishedFileId;
    if (config.PublishedFileId == 0)
    {
        publishedFileId = CreateItem(appId);
        config.PublishedFileId = publishedFileId.m_PublishedFileId;
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(config, jsonOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"Saved the new workshop item ID to {configPath}");
    }
    else
    {
        publishedFileId = new PublishedFileId_t(config.PublishedFileId);
    }

    Console.WriteLine($"Publishing workshop item {publishedFileId.m_PublishedFileId}...");
    var update = SteamUGC.StartItemUpdate(appId, publishedFileId);
    Require(SteamUGC.SetItemTitle(update, config.Title), "SetItemTitle");
    Require(SteamUGC.SetItemDescription(update, config.Description), "SetItemDescription");
    Require(SteamUGC.SetItemVisibility(update, config.Visibility), "SetItemVisibility");
    Require(SteamUGC.SetItemContent(update, contentFolder), "SetItemContent");
    Require(SteamUGC.SetItemPreview(update, previewFile), "SetItemPreview");

    var result = WaitForCallResult<SubmitItemUpdateResult_t>(
        SteamUGC.SubmitItemUpdate(update, config.ChangeNote));
    if (result.m_eResult != EResult.k_EResultOK)
        throw new InvalidOperationException($"SubmitItemUpdate failed: {result.m_eResult}");

    Console.WriteLine($"PUBLISHED_FILE_ID={publishedFileId.m_PublishedFileId}");
    Console.WriteLine($"LEGAL_AGREEMENT_REQUIRED={result.m_bUserNeedsToAcceptWorkshopLegalAgreement}");
}
finally
{
    SteamAPI.Shutdown();
}

return 0;

static PublishedFileId_t CreateItem(AppId_t appId)
{
    Console.WriteLine("Creating a new workshop item...");
    var result = WaitForCallResult<CreateItemResult_t>(
        SteamUGC.CreateItem(appId, EWorkshopFileType.k_EWorkshopFileTypeCommunity));
    if (result.m_eResult != EResult.k_EResultOK)
        throw new InvalidOperationException($"CreateItem failed: {result.m_eResult}");

    Console.WriteLine($"CREATED_FILE_ID={result.m_nPublishedFileId.m_PublishedFileId}");
    Console.WriteLine($"LEGAL_AGREEMENT_REQUIRED={result.m_bUserNeedsToAcceptWorkshopLegalAgreement}");
    return result.m_nPublishedFileId;
}

static T WaitForCallResult<T>(SteamAPICall_t call) where T : struct
{
    if (call == SteamAPICall_t.Invalid)
        throw new InvalidOperationException($"Steam returned an invalid API call for {typeof(T).Name}.");

    T value = default;
    bool finished = false;
    bool ioFailure = false;
    using var result = CallResult<T>.Create((data, failure) =>
    {
        value = data;
        ioFailure = failure;
        finished = true;
    });
    result.Set(call);

    var deadline = DateTime.UtcNow.AddMinutes(10);
    while (!finished && DateTime.UtcNow < deadline)
    {
        SteamAPI.RunCallbacks();
        Thread.Sleep(50);
    }

    if (!finished)
        throw new TimeoutException($"Timed out waiting for {typeof(T).Name}.");
    if (ioFailure)
        throw new IOException($"Steam I/O failure while waiting for {typeof(T).Name}.");
    return value;
}

static void Require(bool success, string operation)
{
    if (!success)
        throw new InvalidOperationException($"{operation} failed.");
}

internal sealed class WorkshopConfig
{
    public uint AppId { get; init; }
    public ulong PublishedFileId { get; set; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public ERemoteStoragePublishedFileVisibility Visibility { get; init; }
    public required string ContentFolder { get; init; }
    public required string PreviewFile { get; init; }
    public required string ChangeNote { get; init; }
}
