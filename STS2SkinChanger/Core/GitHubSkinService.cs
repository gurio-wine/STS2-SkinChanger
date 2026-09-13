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
    // against, and it is the only question a release read answers. A repository nobody installed
    // costs no API request at all.
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
        ? GitHubInstallPolicy.Classify(install.Version, install.Digest, Entry(id)?.ReleaseTag, Entry(id)?.AssetDigest)
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
                // A previous session may have left a hidden folder behind (the game had it open), and
                // an older build may have left a swap folder inside mods.
                ReconcileFolders();
                CleanLeftovers();
                RefreshInstalls();
                BackfillDigests();
                PublishItems();
            }
            var client = Client();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var token = timeout.Token;
            var repos = await FetchReposAsync(token);
            Catalog.Merge(repos.Select(GitHubSkinCatalog.FromRepo));
            // The repositories this round discovers are the ones the release check below has to walk,
            // and an install recorded in an earlier session is only visible to it once the ledger has
            // been matched against them - otherwise the first refresh after a dropped cache would
            // report every installed skin as "not installed" and check no release at all.
            RefreshInstalls();
            PublishItems();
            SaveCatalog();
            await CheckInfoAsync(token);
            await CheckReleasesAsync(token);
            BackfillDigests();
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

    // Every installed repository is re-read on every refresh, however complete the cache looks: the
    // digest of the package the release publishes now is the only thing that can say whether the
    // installed copy is still the current one, and nothing in the search result changes when an
    // author replaces that file under an existing tag.
    private static async Task CheckReleasesAsync(CancellationToken token)
    {
        var wanted = GitHubReleasePolicy.ReleaseChecks(Catalog.Entries, id => _installsById.ContainsKey(id),
            MaxReleaseChecksPerRefresh);
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

    // The live release, always: the refresh asks whether the published package is still the
    // installed one, and a scan or an install asks where that package is - neither question can be
    // answered by what an earlier read happened to see. See GitHubReleasePolicy.
    private static async Task<GitHubSkinEntry> WithReleaseAsync(GitHubSkinEntry entry, CancellationToken token)
    {
        var json = await Client().LatestReleaseAsync(entry.Repo, token);
        return GitHubReleasePolicy.WithRelease(entry, json);
    }

    private sealed record StagedPackage(string Directory, SkinModDescriptor[] Descriptors, string Digest)
    {
        public SkinModDescriptor? Select(string repoName)
        {
            var index = GitHubPackageLayout.SelectIndex(Descriptors.Select(mod => mod.RootPath).ToArray(), Directory, repoName);
            return index < 0 ? null : Descriptors[index];
        }
    }

    private static async Task<StagedPackage> StageAsync(GitHubSkinEntry entry, CancellationToken token)
    {
        // An explicit scan or install reads the live release too: this call is the player asking
        // for the package itself, not for a version number.
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
        // The published digest says what the current package is, so a cached copy is only reused
        // when it really is that file: a replacement of the same size would otherwise be installed
        // out of the stale download, and the card would then call that old package "up to date".
        var declared = GitHubSkinPolicy.NormalizeDigest(release.AssetDigest);
        var cachedSize = File.Exists(zip) ? new FileInfo(zip).Length : -1;
        var cachedDigest = cachedSize == release.AssetSize ? GitHubInstallPolicy.DigestOfFile(zip) : "";
        var digest = GitHubInstallPolicy.CanReusePackage(declared, release.AssetSize, cachedSize, cachedDigest) ? cachedDigest : "";
        if (digest.Length == 0)
        {
            await Client().DownloadAsync(new Uri(release.AssetUrl), zip, token);
            digest = GitHubInstallPolicy.DigestOfFile(zip);
        }
        if (declared.Length > 0 && !digest.Equals(declared, StringComparison.OrdinalIgnoreCase))
        {
            // Nothing is written from bytes GitHub does not recognize: the ledger would otherwise
            // record an installation that never matched the release it claims to come from.
            TryDeleteFile(zip);
            throw new InvalidDataException("下载的文件与 GitHub 公布的校验值不一致，请稍后重试。");
        }
        Extract(zip, package);
        return new(package, WorkshopSubmissionScanner.ReadDescriptors(package), digest);
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
            if (scan.Item == null)
            {
                // The panel only shows this in a tooltip, so the log has to carry it: a scan that
                // recognizes nothing is otherwise indistinguishable from one that never started.
                var error = scan.Error.Length > 0 ? scan.Error : "unrecognized";
                ModLog.Warn($"GitHub 皮肤 {entry.FullName} 扫描未识别：{error}");
                return new(null, [], error);
            }
            var codes = WorkshopSubmissionCodec.Encode(scan.Item, STS2SkinChanger.Entry.InternalTestVersion, GameVersion());
            ModLog.Info($"GitHub 皮肤 {entry.FullName} 扫描完成：识别到 {scan.Item.Targets.Length} 项，生成 {codes.Length} 段信息码。");
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
            // A skin the running game has loaded keeps its assembly and its resource pack open, and
            // Windows refuses to delete - or even rename - a folder that holds an open file. Writing
            // the new package into a folder beside it and hiding the old copy's manifest is what the
            // game's own loader allows at any time; see GitHubFolderSwap.
            var placement = GitHubFolderSwap.Place(mods, Ledger.Find(entry.FullName)?.Folder, folder, packageId,
                staged.Digest, staged.Directory, IsLoadedFolder, ModLog.Warn);
            CompleteInstall(entry, placement, packageId, version ?? "", staged.Digest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(ex.GetBaseException().Message, ex);
        }
        finally { Installing.Remove(id); }
    }

    private static void CompleteInstall(GitHubSkinEntry entry, GitHubPlacement placement, string packageId, string version, string digest)
    {
        // The digest of the package that was just written is what tells a later refresh whether the
        // release still holds the same package, whatever the author names the tag.
        Ledger.Record(entry.FullName, new(placement.Folder, packageId, version, digest));
        // This folder is the live install now: a delete that was queued for the same name earlier (the
        // player uninstalled and re-installed) would remove the package that was just written.
        Ledger.ClearDelete(entry.FullName, placement.Folder);
        // The copy that had to be hidden leaves at the next start, once nothing holds it open - the
        // same promise the Steam side makes for the files of an unsubscribed item.
        if (placement.Disabled is { Length: > 0 } disabled) Ledger.SetDelete(entry.FullName, disabled);
        // The game that is running has never seen this folder: the card now offers the restart,
        // and keeps offering it until the game is started again.
        RestartPending.Add(entry.FullName);
        SaveLedger();
        RefreshInstalls();
        PublishItems();
        ModLog.Info(placement.Disabled is { Length: > 0 }
            ? $"GitHub 皮肤 {entry.FullName} 已更新到 mods/{placement.Folder}；旧目录 mods/{placement.Disabled} 将在下次启动游戏时移除。"
            : $"GitHub 皮肤 {entry.FullName} 已安装到 mods/{placement.Folder}；下次启动游戏生效。");
    }

    // Removing the folder this mod wrote is what "uninstalled" means for this source; the
    // download cache stays, so re-installing the same release needs no network at all.
    public static async Task UninstallAsync(ulong id)
    {
        var entry = Entry(id) ?? throw new InvalidOperationException("Unknown GitHub skin.");
        if (Ledger.Find(entry.FullName) is not { } install) throw new InvalidOperationException("Skin is not installed.");
        var mods = GitHubInstallPolicy.ModsDirectory(OS.GetExecutablePath());
        var target = GitHubInstallPolicy.UninstallTarget(mods, install.Folder, install.PackageId);
        var loaded = target != null && IsLoadedFolder(target);
        if (loaded)
        {
            // The game holds this folder open, so its files can only go at the next start. Hiding the
            // manifest is what the loader allows right now, and it is what makes the skin really gone
            // from the next start on - the card may forget it right away.
            if (!GitHubFolderSwap.Disable(target!, ModLog.Warn))
                throw new InvalidOperationException($"无法停用正在使用的皮肤目录：mods/{install.Folder}");
            DropInstall(entry.FullName);
            Ledger.SetDelete(entry.FullName, install.Folder);
        }
        else
        {
            if (target != null) await Task.Run(() => Directory.Delete(target, recursive: true));
            DropInstall(entry.FullName);
        }
        SaveLedger();
        RefreshInstalls();
        PublishItems();
        ModLog.Info(loaded
            ? $"GitHub 皮肤 {entry.FullName} 正被本次游戏使用；文件将在下次启动游戏时移除：{target}"
            : $"GitHub 皮肤 {entry.FullName} 已卸载；下次启动游戏生效。");
    }

    // Forgetting the skin also throws away the package an old version staged for a helper that ran
    // after the game had exited: the player asked for the folder to be gone, not for a newer version.
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

    // A record this mod wrote before digests existed holds a version but no digest, so that one
    // repository is still judged by the tag alone. Once the catalog knows what the release
    // publishes, that digest is adopted and written straight back: from this session on the card
    // answers by package content, without asking the player to re-install anything. Nothing is
    // adopted while the version says the install is older than the release - there the update has
    // to stay visible on the card, and the digest arrives with the update itself.
    private static void BackfillDigests()
    {
        var changed = false;
        foreach (var entry in Catalog.Entries)
        {
            if (Ledger.Find(entry.FullName) is not { } install) continue;
            var digest = GitHubInstallPolicy.BackfillDigest(install.Version, install.Digest, entry.ReleaseTag, entry.AssetDigest);
            if (digest.Length == 0 || !Ledger.Record(entry.FullName, install with { Digest = digest })) continue;
            changed = true;
            ModLog.Info($"GitHub 皮肤 {entry.FullName} 的安装记录已补记发布校验值，此后按包内容判断更新。");
        }
        if (!changed) return;
        SaveLedger();
        RefreshInstalls();
        PublishItems();
    }

    private static string? ModsDirectory()
    {
        try { return GitHubInstallPolicy.ModsDirectory(OS.GetExecutablePath()); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        { ModLog.Warn("无法定位游戏 mods 目录：" + ex.Message); return null; }
    }

    // Folder work an earlier session could not finish is finished here, at the first refresh of a new
    // session: the hidden folders have nobody holding them open any more, so their files can go. A
    // game closed by force leaves exactly this state behind, and so does a platform that never had the
    // helper an older version shipped.
    private static void ReconcileFolders()
    {
        var mods = ModsDirectory();
        if (mods == null) return;
        var changed = MigrateLegacyPending(mods);
        var pending = Ledger.Deletes.SelectMany(pair => pair.Value.Select(folder => (FullName: pair.Key, Folder: folder))).ToArray();
        if (pending.Length > 0)
        {
            var live = Ledger.Installs.Values.Select(install => install.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remaining = GitHubFolderSwap.Sweep(mods, pending, live, ModLog.Info).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Anything the sweep refused or finished is dropped: a folder an install points at now, one
            // that was re-installed, or one that is simply gone must not be retried forever.
            foreach (var (fullName, folder) in pending)
                if (!remaining.Contains(folder)) changed |= Ledger.ClearDelete(fullName, folder);
        }
        if (changed) SaveLedger();
    }

    // 1.0.12 queued folder work for a helper process that ran after the game had exited. That helper is
    // gone, so both kinds of step are carried out right here, which is what the current model does
    // anyway: a staged package is written into the mods folder, and a queued removal joins the folders
    // waiting for the next start. A staged package is never dropped while it is still on disk - the
    // ledger records the version it holds, and discarding it would leave the card claiming a version
    // the mods folder does not have.
    private static bool MigrateLegacyPending(string mods)
    {
        if (Ledger.Pending.Count == 0) return false;
        var changed = false;
        foreach (var (fullName, operation) in Ledger.Pending.ToArray())
        {
            if (operation.Kind == GitHubModsOperationKind.Remove)
            {
                Ledger.ClearPending(fullName);
                Ledger.SetDelete(fullName, operation.Folder);
                changed = true;
                ModLog.Info($"GitHub 皮肤 {fullName} 的旧版退出卸载步骤已改为下次启动时删除 mods/{operation.Folder}。");
                continue;
            }
            var install = Ledger.Find(fullName);
            if (install == null || !Directory.Exists(operation.Staging))
            {
                Ledger.ClearPending(fullName);
                TryDelete(operation.Staging);
                changed = true;
                ModLog.Warn($"GitHub 皮肤 {fullName} 的旧版待安装包已不在磁盘上，目录保持原样（需要时可卸载后重新安装：{operation.Staging}）。");
                continue;
            }
            try
            {
                var placement = GitHubFolderSwap.Place(mods, install.Folder, install.Folder, install.PackageId,
                    install.Digest, operation.Staging, IsLoadedFolder, ModLog.Warn);
                Ledger.Record(fullName, install with { Folder = placement.Folder });
                Ledger.ClearDelete(fullName, placement.Folder);
                if (placement.Disabled is { Length: > 0 } disabled) Ledger.SetDelete(fullName, disabled);
                Ledger.ClearPending(fullName);
                TryDelete(operation.Staging);
                RestartPending.Add(fullName);
                changed = true;
                ModLog.Info($"GitHub 皮肤 {fullName} 的旧版待安装包已写入 mods/{placement.Folder}；下次启动游戏生效。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ModLog.Warn($"GitHub 皮肤 {fullName} 的旧版待安装包暂时写不进去，下次启动再试：{ex.GetBaseException().Message}");
            }
        }
        return changed;
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

    // The files are still on disk because the game that had this folder loaded was running, and they
    // leave at the next start. Only an uninstalled skin says this: an update hides the previous copy
    // the same way, and that card is about the new copy.
    public static bool IsPendingRemoval(ulong id) => Entry(id) is { } entry &&
        InstallState(id) == GitHubInstallState.NotInstalled && Ledger.HasDelete(entry.FullName);

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

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ModLog.Warn("临时文件清理失败：" + ex.Message); }
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
