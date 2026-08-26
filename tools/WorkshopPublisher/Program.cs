using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
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
var additionalPreviewFiles = (config.PreviewFiles ?? [])
    .Select(path => Path.GetFullPath(Path.Combine(configDir, path)))
    .ToArray();

if (!Directory.Exists(contentFolder))
    throw new DirectoryNotFoundException(contentFolder);
if (!File.Exists(previewFile))
    throw new FileNotFoundException("Workshop preview image not found.", previewFile);
if (new FileInfo(previewFile).Length >= 1_000_000)
    throw new InvalidDataException(
        $"Workshop primary preview image must be under 1 MB: {previewFile}");
foreach (var additionalPreviewFile in additionalPreviewFiles)
{
    if (!File.Exists(additionalPreviewFile))
        throw new FileNotFoundException(
            "Workshop additional preview image not found.",
            additionalPreviewFile);
    if (new FileInfo(additionalPreviewFile).Length >= 1_000_000)
        throw new InvalidDataException(
            $"Workshop additional preview image must be under 1 MB: {additionalPreviewFile}");
}

List<WorkshopLocalization> localizations = [];
if (!string.IsNullOrWhiteSpace(config.LocalizationsFile))
{
    var localizationsPath = Path.GetFullPath(
        Path.Combine(configDir, config.LocalizationsFile));
    localizations = JsonSerializer.Deserialize<List<WorkshopLocalization>>(
        File.ReadAllText(localizationsPath), jsonOptions)
        ?? throw new InvalidDataException("Workshop localizations are empty.");
}

// Validate every generated description before making the first Steam update so a
// malformed localization cannot leave the Workshop item only partly updated.
_ = ComposeDescription(
    config.Description,
    config.StatementHeading,
    config.Limitations,
    config.Version,
    JoinFeatureUpdates(config.FeatureUpdate, config.CardPriorityUpdate));
foreach (var localization in localizations)
{
    _ = ComposeDescription(
        localization.Description,
        localization.StatementHeading,
        localization.Limitations,
        localization.Version,
        JoinFeatureUpdates(localization.FeatureUpdate, localization.CardPriorityUpdate));
}

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
    Require(
        SteamUGC.SetItemDescription(
            update,
            ComposeDescription(
                config.Description,
                config.StatementHeading,
                config.Limitations,
                config.Version,
                JoinFeatureUpdates(config.FeatureUpdate, config.CardPriorityUpdate))),
        "SetItemDescription");
    Require(SteamUGC.SetItemVisibility(update, config.Visibility), "SetItemVisibility");
    Require(SteamUGC.SetItemContent(update, contentFolder), "SetItemContent");
    Require(SteamUGC.SetItemPreview(update, previewFile), "SetItemPreview");
    SyncAdditionalPreviews(update, publishedFileId, additionalPreviewFiles);

    var result = WaitForCallResult<SubmitItemUpdateResult_t>(
        SteamUGC.SubmitItemUpdate(update, config.ChangeNote));
    if (result.m_eResult != EResult.k_EResultOK)
        throw new InvalidOperationException($"SubmitItemUpdate failed: {result.m_eResult}");

    foreach (var localization in localizations)
        PublishLocalization(appId, publishedFileId, localization);

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

static void SyncAdditionalPreviews(
    UGCUpdateHandle_t update,
    PublishedFileId_t publishedFileId,
    IReadOnlyList<string> previewFiles)
{
    var existingCount = GetAdditionalPreviewCount(publishedFileId);
    var sharedCount = Math.Min(existingCount, (uint)previewFiles.Count);
    for (uint index = 0; index < sharedCount; index++)
    {
        Require(
            SteamUGC.UpdateItemPreviewFile(update, index, previewFiles[(int)index]),
            $"UpdateItemPreviewFile({index})");
    }

    for (var index = (int)sharedCount; index < previewFiles.Count; index++)
    {
        Require(
            SteamUGC.AddItemPreviewFile(
                update,
                previewFiles[index],
                EItemPreviewType.k_EItemPreviewType_Image),
            $"AddItemPreviewFile({index})");
    }

    for (var index = existingCount; index > previewFiles.Count; index--)
    {
        Require(
            SteamUGC.RemoveItemPreview(update, index - 1),
            $"RemoveItemPreview({index - 1})");
    }

    Console.WriteLine(
        $"Synchronized {previewFiles.Count} additional preview image(s)." +
        (existingCount == previewFiles.Count ? string.Empty : $" Previous count: {existingCount}."));
}

static uint GetAdditionalPreviewCount(PublishedFileId_t publishedFileId)
{
    var query = SteamUGC.CreateQueryUGCDetailsRequest([publishedFileId], 1);
    if (query == UGCQueryHandle_t.Invalid)
        throw new InvalidOperationException("CreateQueryUGCDetailsRequest failed.");

    try
    {
        Require(
            SteamUGC.SetReturnAdditionalPreviews(query, true),
            "SetReturnAdditionalPreviews");
        var result = WaitForCallResult<SteamUGCQueryCompleted_t>(
            SteamUGC.SendQueryUGCRequest(query));
        if (result.m_eResult != EResult.k_EResultOK || result.m_unNumResultsReturned == 0)
            throw new InvalidOperationException(
                $"Workshop preview query failed: {result.m_eResult}");

        return SteamUGC.GetQueryUGCNumAdditionalPreviews(query, 0);
    }
    finally
    {
        SteamUGC.ReleaseQueryUGCRequest(query);
    }
}

static void PublishLocalization(
    AppId_t appId,
    PublishedFileId_t publishedFileId,
    WorkshopLocalization localization)
{
    Console.WriteLine($"Publishing localization: {localization.Language}");
    var update = SteamUGC.StartItemUpdate(appId, publishedFileId);
    Require(
        SteamUGC.SetItemUpdateLanguage(update, localization.Language),
        $"SetItemUpdateLanguage({localization.Language})");
    Require(
        SteamUGC.SetItemTitle(update, localization.Title),
        $"SetItemTitle({localization.Language})");
    Require(
        SteamUGC.SetItemDescription(
            update,
            ComposeDescription(
                localization.Description,
                localization.StatementHeading,
                localization.Limitations,
                localization.Version,
                JoinFeatureUpdates(localization.FeatureUpdate, localization.CardPriorityUpdate))),
        $"SetItemDescription({localization.Language})");

    var result = WaitForCallResult<SubmitItemUpdateResult_t>(
        SteamUGC.SubmitItemUpdate(update, string.Empty));
    if (result.m_eResult != EResult.k_EResultOK)
        throw new InvalidOperationException(
            $"Localization update failed for {localization.Language}: {result.m_eResult}");
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

static string ComposeDescription(
    string description,
    string? statementHeading,
    string? limitations,
    string? version,
    string? featureUpdate)
{
    var composed = description.TrimEnd();
    if (!string.IsNullOrWhiteSpace(version))
    {
        var versionPattern = new Regex(
            @"(?m)^\d+\.\d+\.\d+$",
            RegexOptions.CultureInvariant);
        if (versionPattern.Matches(composed).Count != 1)
            throw new InvalidDataException(
                "The Workshop description must contain exactly one standalone semantic version line.");

        composed = versionPattern.Replace(composed, version.Trim(), 1);
    }

    if (!string.IsNullOrWhiteSpace(featureUpdate))
    {
        const string sectionMarker = "\n\n[h2]";
        var firstSection = composed.IndexOf(sectionMarker, StringComparison.Ordinal);
        var insertionPoint = firstSection < 0
            ? -1
            : composed.IndexOf(
                sectionMarker,
                firstSection + sectionMarker.Length,
                StringComparison.Ordinal);
        if (insertionPoint < 0)
        {
            insertionPoint = !string.IsNullOrWhiteSpace(statementHeading)
                ? composed.LastIndexOf("\n\n", StringComparison.Ordinal)
                : composed.Length;
        }

        if (insertionPoint < 0)
            throw new InvalidDataException(
                "Could not find a safe insertion point for the Workshop feature description.");

        composed = composed[..insertionPoint]
            + "\n\n"
            + featureUpdate.Trim()
            + composed[insertionPoint..];
    }

    if (!string.IsNullOrWhiteSpace(statementHeading))
    {
        var statementStart = composed.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (statementStart < 0)
            throw new InvalidDataException(
                "A statement heading requires the statement to be the final description paragraph.");

        composed = composed[..statementStart]
            + $"\n\n[h2]{statementHeading.Trim()}[/h2]\n"
            + composed[(statementStart + 2)..];
    }

    return string.IsNullOrWhiteSpace(limitations)
        ? composed
        : composed + "\n\n" + limitations.Trim();
}

static string? JoinFeatureUpdates(params string?[] sections)
{
    var populated = sections
        .Where(section => !string.IsNullOrWhiteSpace(section))
        .Select(section => section!.Trim())
        .ToArray();
    return populated.Length == 0 ? null : string.Join("\n\n", populated);
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
    public string? StatementHeading { get; init; }
    public required string Description { get; init; }
    public string? Limitations { get; init; }
    public string? Version { get; init; }
    public string? FeatureUpdate { get; init; }
    public string? CardPriorityUpdate { get; init; }
    public ERemoteStoragePublishedFileVisibility Visibility { get; init; }
    public required string ContentFolder { get; init; }
    public required string PreviewFile { get; init; }
    public IReadOnlyList<string>? PreviewFiles { get; init; }
    public required string ChangeNote { get; init; }
    public string? LocalizationsFile { get; init; }
}

internal sealed class WorkshopLocalization
{
    public required string Language { get; init; }
    public required string Title { get; init; }
    public string? StatementHeading { get; init; }
    public required string Description { get; init; }
    public string? Limitations { get; init; }
    public string? Version { get; init; }
    public string? FeatureUpdate { get; init; }
    public string? CardPriorityUpdate { get; init; }
}
