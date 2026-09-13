using System.IO.Compression;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using Godot;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

// The result of scanning one repository on demand: the recognized item, the codes an author
// publishes as sc.info, and the reason when the package could not be recognized at all.
internal readonly record struct GitHubScanResult(WorkshopCatalogItem? Item, string[] Codes, string Error);

// The second skin source. The list comes from the GitHub topic search; whether a repository is
// a skin comes from the sc.info code its author published - never from a download the player did
// not ask for. Installation copies a release package into the game's own mods folder, and the
// scan action produces the very code an author has to publish, straight from the package.
internal static class GitHubSkinService
{
    // One sc.info read per repository keeps a large topic from becoming one long queue; whatever
    // is left stays pending and continues on the next refresh. Requests are tiny static files
    // from the raw host, so they cost no API budget.
    private const int MaxInfoChecksPerRefresh = 60;
    // Only an installed repository needs its release: that is what "update available" compares
    // against. A repository nobody installed costs no API request at all.
    private const int MaxReleaseChecksPerRefresh = 10;
    private static readonly GitHubSkinCatalog Catalog = new();
    private static readonly GitHubInstallLedger Ledger = new();
    private static readonly HashSet<ulong> Installing = [];
    private static readonly HashSet<ulong> Scanning = [];
    // A package this mod writes is only loaded by the game's own start-up scan, so until the
    // game is restarted the folder on disk is not part of the running game. The set lives in
    // this session only: a new session means the game scanned the mods folder again.
    private static readonly HashSet<string> RestartPending = new(StringComparer.OrdinalIgnoreCase);
    private static GitHubSkinClient? _client;
    private static WorkshopCatalogItem[] _items = [];
    private static IReadOnlyDictionary<ulong, GitHubSkinEntry> _entriesById = new Dictionary<ulong, GitHubSkinEntry>();
    private static IReadOnlyDictionary<ulong, GitHubInstall> _installsById = new Dictionary<ulong, GitHubInstall>();

    public static int Revision { get; private set; }
    public static bool Busy { get; private set; }
    public static bool Failed { get; private set; }
    public static bool RateLimited { get; private set; }
    public static string Progress { get; private set; } = "";
    public static WorkshopCatalogItem[] Items => _items;
    public static IReadOnlyDictionary<ulong, GitHubSkinEntry> EntriesById => _entriesById;

    public static GitHubSkinEntry? Entry(ulong id) => _entriesById.GetValueOrDefault(id);
    // A card wears the repository name in every state: recognized or not, installed or not. The
    // name a package declares for itself is only readable once it is installed, so showing it
    // would rename the card at that moment, and the repository handle would put the owner in a
    // list of skins.
    public static string Title(ulong id) => Entry(id)?.Name ?? "";
    public static string CoverUrl(ulong id) => Entry(id)?.CoverUrl ?? "";
    public static string RepoUrl(ulong id) => Entry(id)?.Repo.Url ?? "";
    public static string Description(ulong id) => Entry(id)?.Description ?? "";
    public static string Readme(ulong id) => Entry(id)?.Readme ?? "";
    public static bool IsInstalling(ulong id) => Installing.Contains(id);
    public static bool IsScanning(ulong id) => Scanning.Contains(id);
    public static bool IsRecognized(ulong id) => Entry(id) is { Recognition: GitHubRecognition.Recognized };
    // The folder is on disk, but the game that is running now has not loaded it: the card offers
    // the restart instead of pretending the skin is already in use.
    public static bool IsPendingRestart(ulong id) => Entry(id) is { } entry && InstallState(id) != GitHubInstallState.NotInstalled &&
        RestartPending.Contains(entry.FullName);
    public static GitHubInstallState InstallState(ulong id) => _installsById.TryGetValue(id, out var install)
        ? GitHubInstallPolicy.Classify(install.Version, Entry(id)?.ReleaseTag)
        : GitHubInstallState.NotInstalled;

    // One client for the whole session: the README of a hovered card may be fetched before
    // any refresh has run, so creation cannot be tied to RefreshAsync.
    private static GitHubSkinClient Client() => _client ??= new GitHubSkinClient(
        new HttpClientHandler { AllowAutoRedirect = false },
        $"STS2-SkinChanger/{STS2SkinChanger.Entry.InternalTestVersion}");

    public static void StartRefresh()
    {
        // RefreshAsync sets Busy before its first await, so a second call cannot stack up.
        if (Busy) return;
        _ = RefreshAsync(restore: true);
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
                try { Ledger.Restore(SkinChangerPaths.GitHubInstallLedgerPath); }
                catch (Exception ex) { ModLog.Warn("GitHub 安装记录不可用，将重新建立：" + ex.Message); }
                RefreshInstalls();
                // A previous session may have ended before its queued folder work happened, and an
                // older build may have left a swap folder inside mods.
                ReconcilePending();
                CleanLeftovers();
                RefreshInstalls();
                PublishItems();
            }
            var client = Client();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var token = timeout.Token;
            var repos = await FetchReposAsync(token);
            Catalog.Merge(repos.Select(GitHubSkinCatalog.FromRepo));
            PublishItems();
            SaveCatalog();
            await CheckInfoAsync(token);
            await CheckReleasesAsync(token);
            ModLog.Info($"GitHub 皮肤列表刷新完成：{Catalog.Entries.Length} 个仓库，已识别 " +
                $"{Catalog.Entries.Count(entry => entry.Recognition == GitHubRecognition.Recognized)} 个，本地已安装 {Ledger.Count} 个。");
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
            var json = await Client().SearchPageAsync(page, token);
            var pageItems = GitHubSkinPolicy.ParseSearch(json);
            result.AddRange(pageItems);
            if (pageItems.Length < GitHubSkinPolicy.SearchPageSize) break;
        }
        return result.DistinctBy(info => info.Repo.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Recognition is one small file per repository. Nothing is downloaded and nothing is
    // installed: a repository without the file stays listed as "not recognized".
    private static async Task CheckInfoAsync(CancellationToken token)
    {
        var pending = Catalog.Entries.Where(entry => entry.Recognition is GitHubRecognition.Pending or GitHubRecognition.Failed)
            .OrderByDescending(entry => entry.PushedAt).Take(MaxInfoChecksPerRefresh).ToArray();
        if (pending.Length == 0) return;
        for (var index = 0; index < pending.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            Progress = $"{index + 1}/{pending.Length}";
            var entry = pending[index];
            try { Catalog.Publish(Recognize(entry, await Client().InfoFileAsync(entry.Repo, token))); }
            catch (GitHubRateLimitedException) { RateLimited = true; throw; }
            catch (Exception ex)
            {
                var message = ex.GetBaseException().Message;
                Catalog.Publish(entry with { Recognition = GitHubRecognition.Failed, Error = message[..Math.Min(512, message.Length)] });
            }
            PublishItems();
            SaveCatalog();
        }
    }

    private static GitHubSkinEntry Recognize(GitHubSkinEntry entry, string? info)
    {
        var result = GitHubInfoPolicy.Read(info, entry.Id);
        return result.Item is { } item
            ? entry with { Recognition = GitHubRecognition.Recognized, Error = "", Targets = item.Targets, RestartRequired = item.RestartRequired }
            : entry with { Recognition = GitHubRecognition.Unrecognized, Error = result.Error, Targets = [], RestartRequired = true };
    }

    // A release is only fetched for a repository that is installed here, because that is the
    // only case where the version in the release changes what the panel shows.
    private static async Task CheckReleasesAsync(CancellationToken token)
    {
        var wanted = Catalog.Entries.Where(entry => _installsById.ContainsKey(entry.Id) && entry.ReleaseTag.Length == 0)
            .OrderByDescending(entry => entry.PushedAt).Take(MaxReleaseChecksPerRefresh).ToArray();
        for (var index = 0; index < wanted.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            Progress = $"{index + 1}/{wanted.Length}";
            var entry = wanted[index];
            try { Catalog.Publish(await WithReleaseAsync(entry, token)); }
            catch (GitHubRateLimitedException) { RateLimited = true; throw; }
            catch (Exception ex) { ModLog.Info($"GitHub 版本检查跳过（{entry.FullName}）：{ex.GetBaseException().Message}"); }
            PublishItems();
            SaveCatalog();
        }
    }

    private static async Task<GitHubSkinEntry> WithReleaseAsync(GitHubSkinEntry entry, CancellationToken token)
    {
        if (entry.ReleaseTag.Length > 0) return entry;
        var json = await Client().LatestReleaseAsync(entry.Repo, token);
        var release = json == null ? null : GitHubSkinPolicy.ParseRelease(json);
        if (release == null) return entry;
        var asset = GitHubSkinPolicy.SelectAsset(release, entry.RepoName);
        return entry with { ReleaseTag = release.Tag, AssetUrl = asset?.Url ?? "", AssetSize = asset?.Size ?? 0 };
    }

    private sealed record StagedPackage(string Directory, SkinModDescriptor[] Descriptors)
    {
        public SkinModDescriptor? Select(string repoName)
        {
            var index = GitHubPackageLayout.SelectIndex(Descriptors.Select(mod => mod.RootPath).ToArray(), Directory, repoName);
            return index < 0 ? null : Descriptors[index];
        }
    }

    private static async Task<StagedPackage> StageAsync(GitHubSkinEntry entry, CancellationToken token)
    {
        var release = await WithReleaseAsync(entry, token);
        // Reading the release is what makes the version column and the update check work, so the
        // freshly read tag is published even when the download below fails.
        Catalog.Publish(release);
        PublishItems();
        SaveCatalog();
        if (release.AssetUrl.Length == 0)
            throw new InvalidOperationException("This release has no downloadable zip package.");
        var root = Path.Combine(SkinChangerPaths.GitHubCacheDirectory, entry.Id.ToString());
        var package = Path.Combine(root, "package");
        try
        {
            if (Directory.Exists(package)) Directory.Delete(package, recursive: true);
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("The download folder is unavailable: " + ex.Message, ex); }
        var zip = Path.Combine(root, "package.zip");
        if (!File.Exists(zip) || new FileInfo(zip).Length != release.AssetSize)
            await Client().DownloadAsync(new Uri(release.AssetUrl), zip, token);
        Extract(zip, package);
        return new(package, WorkshopSubmissionScanner.ReadDescriptors(package));
    }

    private static void Extract(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(zipPath);
        long total = 0;
        var index = 0;
        var root = Path.GetFullPath(destination);
        foreach (var item in archive.Entries)
        {
            var verdict = GitHubZipPolicy.Classify(item.FullName, item.Length, index++, total, out var relative);
            if (verdict == GitHubZipEntry.Reject) throw new InvalidDataException("Unsafe archive entry: " + item.FullName);
            if (verdict == GitHubZipEntry.Skip) continue;
            total += item.Length;
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Archive entry escapes the staging folder.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            item.ExtractToFile(target, overwrite: true);
        }
    }

    // Fetched on the first hover and kept in the catalog, so a repository is read once.
    // A failure is not cached: the next hover may well succeed.
    public static async Task<string> ReadmeAsync(ulong id, CancellationToken token)
    {
        if (Entry(id) is not { } entry || entry.Readme.Length > 0) return Readme(id);
        try
        {
            var text = await Client().ReadmeAsync(entry.Repo, token);
            var excerpt = GitHubReadmePolicy.ToExcerpt(text, GitHubReadmePolicy.MaxExcerptChars);
            if (excerpt.Length == 0) return "";
            Catalog.Publish(entry with { Readme = excerpt });
            // No revision bump: a README changes neither the filters nor the order, and a
            // rebuild here would cancel the very hover that asked for it.
            RefreshEntries();
            SaveCatalog();
            return excerpt;
        }
        catch (OperationCanceledException) { return ""; }
        catch (Exception ex)
        {
            ModLog.Info($"GitHub 仓库简介不可用（{entry.FullName}）：{ex.GetBaseException().Message}");
            return "";
        }
    }

    // The scan is what produces the sc.info code, and it is the only action here that downloads
    // a package the player did not ask to install. Whatever it downloads is removed again before
    // the result is handed back, so nothing stays cached behind the player's back.
    public static async Task<GitHubScanResult> ScanAsync(ulong id, CancellationToken token)
    {
        var entry = Entry(id) ?? throw new InvalidOperationException("Unknown GitHub repository.");
        if (!Scanning.Add(id)) return new(null, [], "busy");
        var root = Path.Combine(SkinChangerPaths.GitHubCacheDirectory, entry.Id.ToString());
        try
        {
            var staged = await StageAsync(entry, token);
            var descriptor = staged.Select(entry.RepoName);
            // A bundle-only card art pack ships no cosmetic manifest, but the scanner can still
            // recognize it from its artwork alone, so the missing descriptor is not an error here.
            var scan = await SkinService.RunWorkshopScan(() => Scan(entry, staged.Directory, descriptor), token);
            if (scan.Item == null) return new(null, [], scan.Error.Length > 0 ? scan.Error : "unrecognized");
            var codes = WorkshopSubmissionCodec.Encode(scan.Item, STS2SkinChanger.Entry.InternalTestVersion, GameVersion());
            return new(scan.Item, codes, "");
        }
        finally
        {
            TryDelete(root);
            Scanning.Remove(id);
        }
    }

    // The scan reuses the live catalog's game pack and card list, exactly like the Workshop
    // submission scan, so a code produced here is a code the Workshop browser would accept.
    private static WorkshopScanResult Scan(GitHubSkinEntry entry, string package, SkinModDescriptor? descriptor)
    {
        if (SkinService.Catalog is null) return new(null, entry.Name, "The game catalog is not ready yet.");
        var snapshot = SkinService.CaptureWorkshopScanContext();
        return WorkshopSubmissionScanner.Scan(new(entry.Id, descriptor?.Name ?? entry.Name, package),
            snapshot.GamePack, snapshot.Cards, GameVersion(), Dependencies(), snapshot.Baselines);
    }

    private static string GameVersion() => AccessTools.Field(typeof(ModManager), "_gameVersion")?.GetValue(null)?.ToString() ?? "";

    private static Dictionary<string, string> Dependencies() => ModManager.Mods
        .Where(mod => mod.state == ModLoadState.Loaded && mod.manifest?.id != null)
        .GroupBy(mod => mod.manifest!.id!, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().manifest!.version ?? "", StringComparer.OrdinalIgnoreCase);

    public static async Task InstallAsync(ulong id, CancellationToken token)
    {
        if (!Installing.Add(id)) return;
        try
        {
            var entry = Entry(id) ?? throw new InvalidOperationException("Unknown GitHub skin.");
            var staged = await StageAsync(entry, token);
            var descriptor = staged.Select(entry.RepoName);
            var packageId = descriptor?.Id ?? "";
            var folder = GitHubInstallPolicy.FolderName(packageId, entry.RepoName);
            var version = descriptor is null ? entry.ReleaseTag
                : GitHubInstallPolicy.ReadManifestVersion(descriptor.RootPath ?? staged.Directory, descriptor.Id) ?? entry.ReleaseTag;
            var mods = GitHubInstallPolicy.ModsDirectory(OS.GetExecutablePath());
            var target = Path.Combine(mods, folder);
            // A skin the running game has loaded is open in this process, and Windows refuses to
            // delete or rename a folder that holds an open file. Writing the package aside and
            // letting the quit helper swap it in after the game is gone is the same promise the
            // Steam side makes for its own subscriptions - and the only ending here that is not
            // a lie to the player: the new files do arrive, on the next start.
            if (!IsLoadedFolder(target))
            {
                try
                {
                    ReplaceFolder(mods, target, staged.Directory);
                    CompleteInstall(entry, folder, packageId, version ?? "", "");
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ModLog.Info($"GitHub 皮肤 {entry.FullName} 的目录暂时无法替换，改为退出游戏后写入：{ex.Message}");
                }
            }
            CompleteInstall(entry, folder, packageId, version ?? "", StageForQuit(id, staged.Directory));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(ex.GetBaseException().Message, ex);
        }
        finally { Installing.Remove(id); }
    }

    // The old copy is moved aside first: renaming a folder that is in use fails without touching
    // anything, while a recursive delete stops halfway and leaves a package the game can no
    // longer load. Whatever is left of that moved copy is cleaned up here or by the quit plan.
    private static void ReplaceFolder(string mods, string target, string source)
    {
        Directory.CreateDirectory(mods);
        if (Directory.Exists(target))
        {
            var previous = Path.Combine(mods, "." + Path.GetFileName(target) + ".previous");
            TryDelete(previous);
            Directory.Move(target, previous);
            TryDelete(previous);
        }
        CopyDirectory(source, target);
    }

    // The staged package has to outlive this session, and it may not live inside mods: the game's
    // own start-up scan would take it for a second copy of the same skin.
    private static string StageForQuit(ulong id, string source)
    {
        var staging = GitHubModsPolicy.StagingDirectory(SkinChangerPaths.Storage.RootDirectory, id);
        TryDelete(staging);
        CopyDirectory(source, staging);
        return staging;
    }

    private static void CompleteInstall(GitHubSkinEntry entry, string folder, string packageId, string version, string staging)
    {
        // A replace that was queued earlier is superseded either way, so its package goes too.
        if (Ledger.ClearPending(entry.FullName) is { Staging.Length: > 0 } superseded) TryDelete(superseded.Staging);
        Ledger.Record(entry.FullName, new(folder, packageId, version));
        if (staging.Length > 0) Ledger.SetPending(entry.FullName, new(GitHubModsOperationKind.Replace, folder, staging));
        // The game that is running has never seen this folder: the card now offers the restart,
        // and keeps offering it until the game is started again.
        RestartPending.Add(entry.FullName);
        SaveLedger();
        RefreshInstalls();
        PublishItems();
        ModLog.Info(staging.Length > 0
            ? $"GitHub 皮肤 {entry.FullName} 已排入退出后安装：mods/{folder}；下次启动游戏生效。"
            : $"GitHub 皮肤 {entry.FullName} 已安装到 mods/{folder}；下次启动游戏生效。");
    }

    // Removing the folder this mod wrote is what "uninstalled" means for this source; the
    // download cache stays, so re-installing the same release needs no network at all.
    public static async Task UninstallAsync(ulong id)
    {
        var entry = Entry(id) ?? throw new InvalidOperationException("Unknown GitHub skin.");
        if (Ledger.Find(entry.FullName) is not { } install) throw new InvalidOperationException("Skin is not installed.");
        var mods = GitHubInstallPolicy.ModsDirectory(OS.GetExecutablePath());
        var target = GitHubInstallPolicy.UninstallTarget(mods, install.Folder, install.PackageId);
        if (target != null && IsLoadedFolder(target))
        {
            // The game holds this folder open, so its files can only go once the game is gone -
            // the very promise the Steam side makes. The card may forget the skin right away.
            DropInstall(entry.FullName);
            Ledger.SetPending(entry.FullName, new(GitHubModsOperationKind.Remove, install.Folder, ""));
            SaveLedger();
            RefreshInstalls();
            PublishItems();
            ModLog.Info($"GitHub 皮肤 {entry.FullName} 正被本次游戏使用；文件将在退出游戏后移除：{target}");
            return;
        }
        if (target != null) await Task.Run(() => Directory.Delete(target, recursive: true));
        DropInstall(entry.FullName);
        SaveLedger();
        RefreshInstalls();
        PublishItems();
        ModLog.Info($"GitHub 皮肤 {entry.FullName} 已卸载；下次启动游戏生效。");
    }

    // Forgetting the skin also throws away the package a queued replace was waiting with: the
    // player asked for the folder to be gone, not for a newer version of it.
    private static void DropInstall(string fullName)
    {
        Ledger.Remove(fullName);
        if (Ledger.ClearPending(fullName) is { Staging.Length: > 0 } dropped) TryDelete(dropped.Staging);
        RestartPending.Remove(fullName);
    }

    // The ledger is the only record of what this mod wrote into the game folder, so it is read
    // back against the disk: a folder the player deleted is forgotten, a version that changed
    // underneath (a manual edit) is picked up again.
    private static void RefreshInstalls()
    {
        var mods = ModsDirectory();
        if (mods == null) return;
        var changed = false;
        foreach (var (fullName, install) in Ledger.Installs.ToArray())
        {
            var root = Path.Combine(mods, install.Folder);
            if (!Directory.Exists(root)) { Ledger.Remove(fullName); changed = true; continue; }
            var version = GitHubInstallPolicy.ReadManifestVersion(root, install.PackageId);
            if (version != null && version != install.Version) { Ledger.Record(fullName, install with { Version = version }); changed = true; }
        }
        if (changed) SaveLedger();
        // A folder the player removed by hand is no longer waiting for a restart either.
        RestartPending.RemoveWhere(name => Ledger.Find(name) == null);
        var installs = new Dictionary<ulong, GitHubInstall>();
        foreach (var entry in Catalog.Entries)
            if (Ledger.Find(entry.FullName) is { } install) installs[entry.Id] = install;
        _installsById = installs;
    }

    private static string? ModsDirectory()
    {
        try { return GitHubInstallPolicy.ModsDirectory(OS.GetExecutablePath()); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        { ModLog.Warn("无法定位游戏 mods 目录：" + ex.Message); return null; }
    }

    // Steps the quit helper did not get to run - a game closed by force, or a platform without the
    // helper - are picked up here: whatever the disk already shows as done or pointless is dropped,
    // and the rest stays queued, so the next exit finishes it.
    private static void ReconcilePending()
    {
        if (Ledger.Pending.Count == 0) return;
        var mods = ModsDirectory();
        var changed = false;
        foreach (var (fullName, operation) in Ledger.Pending.ToArray())
        {
            var folderExists = mods != null && Directory.Exists(Path.Combine(mods, operation.Folder));
            var stagingExists = operation.Staging.Length > 0 && Directory.Exists(operation.Staging);
            if (GitHubModsPolicy.KeepQueued(operation.Kind, folderExists, stagingExists)) continue;
            Ledger.ClearPending(fullName);
            if (operation.Staging.Length > 0) TryDelete(operation.Staging);
            changed = true;
        }
        if (changed) SaveLedger();
    }

    // The swap folders an interrupted install of an earlier version left inside mods. Nothing
    // writes them any more, but the game's own start-up scan may have taken one for a second copy
    // of the same skin, so they go as soon as nothing holds them.
    private static void CleanLeftovers()
    {
        var mods = ModsDirectory();
        if (mods == null || !Directory.Exists(mods)) return;
        foreach (var directory in Directory.EnumerateDirectories(mods))
        {
            var name = Path.GetFileName(directory);
            if (!GitHubModsPolicy.IsLeftoverFolderName(name) || IsLoadedFolder(directory)) continue;
            ModLog.Info($"清理 GitHub 皮肤安装残留：{name}");
            TryDelete(directory);
        }
    }

    // What the quit helper has to do once this process is gone. Null when there is nothing: the
    // helper is only started for real work.
    public static GitHubModsPlan? QuitPlan()
    {
        var mods = ModsDirectory();
        if (mods == null) return null;
        var queued = Ledger.Pending.Values.Select(operation => new GitHubModsOperation(operation.Kind, operation.Folder, operation.Staging));
        var leftovers = Directory.Exists(mods) ? Directory.EnumerateDirectories(mods).Select(Path.GetFileName).OfType<string>() : [];
        var operations = GitHubModsPolicy.QuitPlan(queued, leftovers);
        return operations.Length == 0 ? null : new(mods, operations);
    }

    // The folder is still on disk but already forgotten, and its files leave when the game does.
    public static bool IsPendingRemoval(ulong id) => Entry(id) is { } entry &&
        Ledger.PendingFor(entry.FullName) is { Kind: GitHubModsOperationKind.Remove };

    // The game keeps every skin it loaded open for the whole session: the assembly it loaded and
    // the resource pack this mod mounted cannot be handed back. Such a folder can be neither
    // deleted nor renamed, so every write to it has to wait for the process to end.
    private static bool IsLoadedFolder(string path)
    {
        try
        {
            var wanted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            foreach (var mod in ModManager.Mods)
            {
                if (mod.path is not { Length: > 0 } loaded) continue;
                if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(loaded)), wanted, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex) { ModLog.Warn("无法确认皮肤目录是否已被游戏加载：" + ex.Message); }
        return false;
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ModLog.Warn("临时目录清理失败：" + ex.Message); }
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

    private static void PublishItems()
    {
        RefreshEntries();
        Revision++;
    }

    private static void RefreshEntries()
    {
        _entriesById = Catalog.Entries.ToDictionary(entry => entry.Id);
        _items = Catalog.Entries.Select(entry => new WorkshopCatalogItem(entry.Id,
            entry.Recognition == GitHubRecognition.Recognized ? entry.Targets : [], true)).ToArray();
    }

    private static void SaveCatalog()
    {
        try { Catalog.Save(SkinChangerPaths.GitHubCatalogPath, null); }
        catch (Exception ex) { ModLog.Warn("GitHub 皮肤缓存写入失败：" + ex.Message); }
    }

    private static void SaveLedger()
    {
        try { Ledger.Save(SkinChangerPaths.GitHubInstallLedgerPath, null); }
        catch (Exception ex) { ModLog.Warn("GitHub 安装记录写入失败：" + ex.Message); }
    }
}
