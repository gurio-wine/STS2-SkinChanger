using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;

namespace STS2SkinChanger.Core;

/// <summary>
/// A style/UI package that overrides parts of a presentation draws the rest of that presentation
/// itself: CznStyleUI replaces the merchant room, the merchant and the shop background from its own
/// images when its own merchant module runs. Isolating the package would drop everything it ships,
/// and leaving its module untouched would keep its merchant room on screen after the player chose
/// another merchant skin or the game original.
///
/// The package's own patches for that group are therefore kept installed but gated on the group's
/// selection: they run while the part is selected and stay out of the way otherwise, exactly like
/// the part resources themselves. Patches that do not target the group's own presentation (menus,
/// cards, combat, audio commands) are never touched.
/// </summary>
internal static class PartTakeoverScope
{
    private static readonly Harmony Gate = new(Entry.ModId + ".part-takeover");
    private static readonly ConcurrentDictionary<MethodBase, PartTakeoverOwner> GatedPatchMethods = new();
    private static readonly HashSet<string> RegisteredGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoggedGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();

    /// <summary>Test seam: production reads the group's displayed selection from the skin service.</summary>
    internal static Func<string, string?, bool> IsOptionSelected { get; set; } = DefaultIsOptionSelected;

    internal static void ResetSelectionProbe() => IsOptionSelected = DefaultIsOptionSelected;

    private static bool DefaultIsOptionSelected(string groupId, string? optionId) =>
        optionId != null &&
        SkinService.GetDisplayedSelection(groupId).Equals(optionId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Owners of every group, read from the live catalog.</summary>
    public static IReadOnlyList<PartTakeoverOwner> ResolveCatalogOwners()
    {
        var catalog = SkinService.Catalog;
        if (catalog == null)
        {
            return [];
        }

        var owners = PartTakeoverPolicy.ResolveOwners(catalog.Groups.SelectMany(group => group.Options
            .Where(option => option.IsBaselinePartOverride)
            .Select(option => (group.Id, option.Id, option.ProviderRootPath, true))));
        return [.. owners.Select(owner => owner with
        {
            ResourceNamespaces = PartTakeoverPolicy.ResolveResourceNamespaces(
                owner.OptionId,
                ResolveSiblingAssemblyNames(owner))
        })];
    }

    private static IReadOnlyList<string> ResolveSiblingAssemblyNames(PartTakeoverOwner owner)
    {
        if (owner.RootPath == null)
        {
            return [];
        }

        try
        {
            return [.. Directory.EnumerateFiles(owner.RootPath, "*.dll", SearchOption.TopDirectoryOnly)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ModLog.Warn($"读取部件接管包 {owner.OptionId} 的程序集名失败，仅使用清单 ID：" + exception.Message);
            return [];
        }
    }

    /// <summary>
    /// Installs the gate for every part owner whose package is already loaded. Safe to call
    /// repeatedly: patches of packages that load later are picked up on the next call.
    /// </summary>
    public static void EnsureInstalled()
    {
        try
        {
            var owners = ResolveCatalogOwners();
            if (owners.Count == 0)
            {
                return;
            }

            lock (Sync)
            {
                foreach (var owner in owners)
                {
                    var assemblies = ResolveLoadedAssemblies(owner);
                    if (assemblies.Count == 0)
                    {
                        continue;
                    }

                    var installed = InstallGates(owner, assemblies);
                    if (installed > 0 && LoggedGates.Add(owner.GroupId + "|" + owner.OptionId))
                    {
                        ModLog.Info(
                            $"已按 {owner.GroupId} 分组的选择接管部件 {owner.OptionId} 的自身呈现补丁：" +
                            $"选中该部件时按作者原意运行，选择其它皮肤或游戏原版时停用，" +
                            $"当前共 {installed} 个入口。");
                    }
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("接上部件接管范围失败，保留作者原有行为：" + exception.GetBaseException().Message);
        }
    }

    private static IReadOnlyList<Assembly> ResolveLoadedAssemblies(PartTakeoverOwner owner)
    {
        if (owner.RootPath == null)
        {
            return [];
        }

        var assemblies = new List<Assembly>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string location;
            try
            {
                location = assembly.Location;
            }
            catch (NotSupportedException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(location) ||
                !IsUnder(location, owner.RootPath) ||
                !owner.ResourceNamespaces.Contains(Path.GetFileNameWithoutExtension(location)))
            {
                continue;
            }

            assemblies.Add(assembly);
        }

        return assemblies;
    }

    private static bool IsUnder(string path, string root)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gates every already-installed callback of this owner's package that targets the group's own
    /// presentation. Returns how many entries became selection-bound.
    /// </summary>
    internal static int InstallGates(PartTakeoverOwner owner, IReadOnlyList<Assembly> assemblies)
    {
        var installed = 0;
        foreach (var target in HarmonyLib.Harmony.GetAllPatchedMethods().ToArray())
        {
            if (!PartTakeoverPolicy.TargetsGroupPresentation(owner, target.DeclaringType))
            {
                continue;
            }

            var patches = HarmonyLib.Harmony.GetPatchInfo(target);
            if (patches == null)
            {
                continue;
            }

            foreach (var callback in EnumerateCallbacks(patches)
                         .Where(callback => assemblies.Contains(callback.Module.Assembly))
                         .Distinct())
            {
                if (!GatedPatchMethods.TryAdd(callback, owner))
                {
                    continue;
                }

                try
                {
                    Gate.Patch(
                        callback,
                        prefix: new HarmonyMethod(typeof(PartTakeoverScope), nameof(GatePrefix))
                        {
                            priority = Priority.First
                        });
                    installed++;
                }
                catch (Exception exception)
                {
                    GatedPatchMethods.TryRemove(callback, out _);
                    ModLog.Warn(
                        $"无法接管部件 {owner.OptionId} 的补丁 " +
                        $"{callback.DeclaringType?.Name}.{callback.Name}：" +
                        exception.GetBaseException().Message);
                }
            }
        }

        return installed;
    }

    private static IEnumerable<MethodInfo> EnumerateCallbacks(Patches patches) =>
        patches.Prefixes.Select(patch => patch.PatchMethod)
            .Concat(patches.Postfixes.Select(patch => patch.PatchMethod))
            .Concat(patches.Transpilers.Select(patch => patch.PatchMethod))
            .Concat(patches.Finalizers.Select(patch => patch.PatchMethod));

    private static bool GatePrefix(MethodBase __originalMethod)
    {
        if (!GatedPatchMethods.TryGetValue(__originalMethod, out var owner))
        {
            return true;
        }

        try
        {
            // Returning false skips this author callback only; the patched game method and every
            // unrelated patch still run. A part that is selected keeps the author's behavior.
            return IsOptionSelected(owner.GroupId, owner.OptionId);
        }
        catch (Exception exception)
        {
            if (RegisteredGates.Add("failed|" + owner.OptionId))
            {
                ModLog.Warn("判断部件接管是否选中失败，保留作者行为：" + exception.GetBaseException().Message);
            }

            return true;
        }
    }
}
