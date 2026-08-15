using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger.Catalog;
using System.Reflection;

namespace STS2SkinChanger.Core;

internal static class ManagedSkinModLoader
{
    private static readonly MethodInfo InvokeOnModDetectedMethod =
        AccessTools.Method(typeof(ModManager), "InvokeOnModDetected");
    private static readonly FieldInfo GameVersionField =
        AccessTools.Field(typeof(ModManager), "_gameVersion");
    private static readonly FieldInfo CircularDependenciesField =
        AccessTools.Field(typeof(ModManager), "_circularDependencies");
    private static readonly Dictionary<string, SkinProviderProbe> ProvidersByRoot =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;

    public static bool IsFirstInLoadOrder { get; private set; } = true;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var mods = ModManager.Mods.ToArray();
        var descriptors = mods
            .Where(mod => mod.state is ModLoadState.None or ModLoadState.Loaded)
            .Where(mod => mod.manifest is { id: not null })
            .Where(mod => !mod.manifest!.id!.Equals(Entry.ModId, StringComparison.OrdinalIgnoreCase))
            .Select(ToDescriptor)
            .ToArray();
        var probes = SkinCatalog.ProbeSkinProviders(descriptors);
        foreach (var probe in probes)
        {
            if (probe.RootPath == null)
            {
                continue;
            }

            ProvidersByRoot[NormalizePath(probe.RootPath)] = probe;
        }

        var selfIndex = Array.FindIndex(mods, mod =>
            mod.manifest?.id?.Equals(Entry.ModId, StringComparison.OrdinalIgnoreCase) == true);
        IsFirstInLoadOrder = selfIndex == 0;
        var alreadyLoaded = selfIndex <= 0
            ? []
            : mods.Take(selfIndex)
                .Where(mod => IsManagedProvider(mod, out _))
                .Select(mod => mod.manifest?.name ?? mod.manifest?.id ?? mod.path)
                .ToArray();
        if (alreadyLoaded.Length > 0)
        {
            ModLog.Warn(
                "托管加载模式仅能拦截排在本 Mod 后面的皮肤提供者。请把 STS2 皮肤切换器移到 Mod 顺序最前并重启。" +
                $" 本次已提前加载：{string.Join("、", alreadyLoaded)}");
        }

        ModLog.Info(
            $"托管加载模式已识别 {ProvidersByRoot.Count} 个纯皮肤提供者；" +
            "其 DLL 与 PCK 将由加载入口隔离，资源改由皮肤切换器读取。");
    }

    public static bool TryManage(Mod mod)
    {
        if (mod.state != ModLoadState.None ||
            !IsManagedProvider(mod, out var provider))
        {
            return false;
        }

        try
        {
            if (!CanBypassOriginalLoader(mod))
            {
                return false;
            }

            if (mod.manifest?.version != null &&
                SemanticVersion.TryFromString(mod.manifest.version, out var version))
            {
                mod.version = version;
            }

            mod.state = ModLoadState.Loaded;
            InvokeOnModDetectedMethod.Invoke(null, [mod]);
            ModLog.Info(
                $"已托管纯皮肤提供者 {mod.manifest?.name ?? mod.manifest?.id}：" +
                $"视觉组={provider.VisualGroupCount}, 卡图={provider.CardAssetCount}, " +
                $"独立图片={provider.RuntimeImageCount}；未加载原 DLL/PCK。");
            return true;
        }
        catch (Exception exception)
        {
            mod.state = ModLoadState.None;
            ModLog.Warn(
                $"托管 {mod.manifest?.name ?? mod.manifest?.id} 失败，将交回游戏原加载器：" +
                exception.GetBaseException().Message);
            return false;
        }
    }

    private static bool CanBypassOriginalLoader(Mod mod)
    {
        var manifest = mod.manifest;
        if (manifest?.id == null)
        {
            return false;
        }

        var mods = ModManager.Mods;
        if (mods.Any(other =>
                !ReferenceEquals(other, mod) &&
                other.manifest?.id == manifest.id &&
                other.state == ModLoadState.Loaded))
        {
            return false;
        }

        if (manifest.dependencies?.Any(dependency =>
                !DependencyIsSatisfied(mods, dependency)) == true)
        {
            return false;
        }

        var circularDependencies = CircularDependenciesField.GetValue(null) as
            IReadOnlyDictionary<string, string>;
        if (circularDependencies?.ContainsKey(manifest.id) == true)
        {
            return false;
        }

        if (manifest.minGameVersion == null)
        {
            return true;
        }

        if (!SemanticVersion.TryFromString(manifest.minGameVersion, out var minimum))
        {
            return false;
        }

        var gameVersion = GameVersionField.GetValue(null) as SemanticVersion;
        return gameVersion == null || gameVersion.CompareTo(minimum) >= 0;
    }

    private static bool DependencyIsSatisfied(
        IEnumerable<Mod> mods,
        ModDependency dependency)
    {
        var loaded = mods.FirstOrDefault(candidate =>
            candidate.manifest?.id == dependency.id &&
            candidate.state == ModLoadState.Loaded);
        if (loaded == null)
        {
            return false;
        }

        if (dependency.minVersion == null)
        {
            return true;
        }

        return SemanticVersion.TryFromString(dependency.minVersion, out var minimum) &&
               loaded.version != null &&
               loaded.version.CompareTo(minimum) >= 0;
    }

    private static bool IsManagedProvider(Mod mod, out SkinProviderProbe provider)
    {
        try
        {
            return ProvidersByRoot.TryGetValue(NormalizePath(mod.path), out provider!);
        }
        catch
        {
            provider = null!;
            return false;
        }
    }

    private static SkinModDescriptor ToDescriptor(Mod mod)
    {
        var manifest = mod.manifest!;
        return new SkinModDescriptor(
            manifest.id!,
            manifest.name ?? manifest.id!,
            manifest.hasPck
                ? Path.Combine(mod.path, manifest.id + ".pck")
                : null,
            manifest.affectsGameplay,
            mod.path,
            manifest.hasDll);
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

[HarmonyPatch]
internal static class ManagedSkinModLoadPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(ModManager), "TryLoadMod");

    private static bool Prefix(Mod mod) => !ManagedSkinModLoader.TryManage(mod);
}
