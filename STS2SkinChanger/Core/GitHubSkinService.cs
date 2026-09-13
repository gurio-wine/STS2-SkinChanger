using System.IO.Compression;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using Godot;

namespace STS2SkinChanger.Core;

// The second skin source. The list comes from the GitHub topic search, recognition from the
// same offline package scan the Workshop submission flow uses, and installation from a plain
// copy into the game's own mods folder, which the game loads on its next start.
internal static class GitHubSkinService
{
    // Unauthenticated GitHub access allows 60 core requests per hour; one refresh must not
    // spend the whole budget, so the rest stays pending and continues on the next refresh.
    private const int MaxReleaseQueriesPerRefresh = 30;
    private static readonly GitHubSkinCatalog Catalog = new();
    private static readonly HashSet<ulong> Installing = [];
    private static GitHubSkinClient? _client;
    private static WorkshopCatalogItem[] _items = [];
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
    public static string CoverUrl(ulong id) => Entry(id)?.CoverUrl ?? "";
    public static string RepoUrl(ulong id) => Entry(id)?.Repo.Url ?? "";
    public static string Description(ulong id) => Entry(id)?.Description ?? "";
    public static string Readme(ulong id) => Entry(id)?.Readme ?? "";
    public static bool IsInstalling(ulong id) => Installing.Contains(id);
    public static GitHubInstallState InstallState(ulong id) => Entry(id) is { Recognition: GitHubRecognition.Recognized } entry
        ? GitHubInstallPolicy.Classify(InstalledVersion(entry), entry.PackageVersion)
        : GitHubInstallState.NotInstalled;

    private static string DisplayName(GitHubSkinEntry entry) => entry.PackageName.Length > 0 ? entry.PackageName : entry.Name;

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
                PublishItems();
            }
            var client = Client();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var token = timeout.Token;
            var repos = await FetchReposAsync(token);
            Catalog.Merge(repos.Select(GitHubSkinCatalog.FromRepo));
            PublishItems();
            SaveCatalog();
            await RecognizePendingAsync(token);
            ModLog.Info($"GitHub 皮肤列表刷新完成：{Catalog.Entries.Length} 个仓库，已识别 " +
                $"{Catalog.Entries.Count(entry => entry.Recognition == GitHubRecognition.Recognized)} 个。");
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

    private static async Task RecognizePendingAsync(CancellationToken token)
    {
        var pending = Catalog.Entries.Where(entry => entry.Recognition == GitHubRecognition.Pending)
            .OrderByDescending(entry => entry.PushedAt).Take(MaxReleaseQueriesPerRefresh).ToArray();
        if (pending.Length == 0) return;
        // Recognition reuses the live catalog's game pack and card list, so it can only run
        // once the game has built them; the entries stay listed as pending until then.
        if (SkinService.Catalog is null)
        {
            ModLog.Info($"皮肤目录尚未就绪，{pending.Length} 个 GitHub 仓库留待下次刷新识别。");
            return;
        }
        for (var index = 0; index < pending.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            Progress = $"{index + 1}/{pending.Length}";
            var entry = pending[index];
            try { Catalog.Publish(await RecognizeAsync(entry, token)); }
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

    private static async Task<GitHubSkinEntry> RecognizeAsync(GitHubSkinEntry entry, CancellationToken token)
    {
        var json = await Client().LatestReleaseAsync(entry.Repo, token);
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

    private readonly record struct StagedPackage(string Directory, string PackageId, string PackageName, string PackageVersion,
        WorkshopCatalogItem? Item, string Error);

    private static async Task<StagedPackage?> StageAsync(GitHubSkinEntry entry, CancellationToken token)
    {
        var root = Path.Combine(SkinChangerPaths.GitHubCacheDirectory, entry.Id.ToString());
        if (!PrepareStaging(root, out var package)) return null;
        var zip = Path.Combine(root, "package.zip");
        // Keep the archive between runs: re-installing an unchanged release must not download it again.
        if (!File.Exists(zip) || new FileInfo(zip).Length != entry.AssetSize)
            await Client().DownloadAsync(new Uri(entry.AssetUrl), zip, token);
        Extract(zip, package);
        var descriptors = WorkshopSubmissionScanner.ReadDescriptors(package);
        if (descriptors.Length == 0) return null;
        // The scan below runs through the same gate and snapshot the submission scanner uses,
        // so a GitHub package is recognized exactly like a Workshop one.
        var snapshot = SkinService.CaptureWorkshopScanContext();
        var gameVersion = AccessTools.Field(typeof(ModManager), "_gameVersion")?.GetValue(null)?.ToString() ?? "";
        var dependencies = ModManager.Mods.Where(mod => mod.state == ModLoadState.Loaded && mod.manifest?.id != null)
            .GroupBy(mod => mod.manifest!.id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().manifest!.version ?? "", StringComparer.OrdinalIgnoreCase);
        var result = await SkinService.RunWorkshopScan(() => WorkshopSubmissionScanner.Scan(
            new(entry.Id, entry.Name, package), snapshot.GamePack, snapshot.Cards, gameVersion, dependencies, snapshot.Baselines), token);
        // The scan reports which manifest the synthetic source id was bound to; that is the package.
        var descriptor = descriptors.FirstOrDefault(mod =>
            result.ProviderId.Length > 0 && mod.Id.Equals(result.ProviderId, StringComparison.OrdinalIgnoreCase)) ?? descriptors[0];
        return new(package, descriptor.Id, descriptor.Name,
            GitHubInstallPolicy.ReadManifestVersion(descriptor.RootPath ?? package, descriptor.Id) ?? entry.ReleaseTag,
            result.Item, result.Error);
    }

    private static bool PrepareStaging(string root, out string package)
    {
        package = Path.Combine(root, "package");
        try
        {
            if (Directory.Exists(package)) Directory.Delete(package, recursive: true);
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
            var backup = Path.Combine(mods, "." + folder + ".previous");
            if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            CopyDirectory(package.Directory, staged);
            // The previous package is only moved aside once the new one is complete on disk,
            // and is put back if the swap fails, so an update never leaves the player without one.
            if (Directory.Exists(target)) Directory.Move(target, backup);
            try
            {
                Directory.Move(staged, target);
                TryDelete(backup);
            }
            catch
            {
                if (!Directory.Exists(target) && Directory.Exists(backup)) Directory.Move(backup, target);
                throw;
            }
            finally { TryDelete(staged); }
            ModLog.Info($"GitHub 皮肤 {entry.FullName} 已安装到 {target}；下次启动游戏生效。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(ex.GetBaseException().Message, ex);
        }
        finally { Installing.Remove(id); }
    }

    // Removing the folder we installed is what "uninstalled" means for this source; the
    // download cache stays, so re-installing the same release needs no network at all.
    public static async Task UninstallAsync(ulong id)
    {
        var entry = Entry(id) ?? throw new InvalidOperationException("Unknown GitHub skin.");
        var mods = GitHubInstallPolicy.ModsDirectory(OS.GetExecutablePath());
        var folder = GitHubInstallPolicy.FolderName(entry.PackageId, entry.RepoName);
        var target = GitHubInstallPolicy.UninstallTarget(mods, folder, entry.PackageId)
            ?? throw new InvalidOperationException("Skin is not installed.");
        await Task.Run(() => Directory.Delete(target, recursive: true));
        ModLog.Info($"GitHub 皮肤 {entry.FullName} 已从 {target} 卸载；下次启动游戏生效。");
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
}
