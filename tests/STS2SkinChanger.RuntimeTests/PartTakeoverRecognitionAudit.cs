using System.Reflection;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;
using STS2SkinChanger.Pck;

/// <summary>
/// Read-only check of where an installed style/UI package lands once only its overridden parts are
/// taken over: the recognised part paths, the Mod list marking, the Workshop catalog export that
/// drives active items and submission candidates, and the submission scan. Mirrors SkinService
/// startup without the engine; never runs a provider initializer.
/// </summary>
internal static class PartTakeoverRecognitionAudit
{
    internal static void Run(string gamePck, string modRoot)
    {
        if (!Directory.Exists(modRoot))
        {
            Console.WriteLine($"missing mod root: {modRoot}");
            return;
        }

        var manifestPath = Directory.EnumerateFiles(modRoot, "*.json", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (manifestPath == null)
        {
            Console.WriteLine($"no manifest under {modRoot}");
            return;
        }

        var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
        var id = manifest.GetProperty("id").GetString()!;
        var name = manifest.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? id
            : id;
        var pckPath = Path.Combine(modRoot, id + ".pck");
        var descriptor = new SkinModDescriptor(
            id,
            name,
            File.Exists(pckPath) ? pckPath : null,
            false,
            modRoot,
            manifest.TryGetProperty("has_dll", out var hasDll) && hasDll.GetBoolean());

        var probe = SkinCatalog.ProbeSkinProviders([descriptor], gamePck).FirstOrDefault();
        if (probe == null)
        {
            Console.WriteLine($"{id}: 未被识别为外观提供者。");
            return;
        }

        Console.WriteLine(
            $"{id}: 部件接管={probe.IsBaselinePartTakeover} 完整分组={probe.VisualGroupCount} " +
            $"部件分组={probe.BaselinePartGroupCount} [{string.Join(", ", probe.BaselinePartGroupIds ?? [])}] " +
            $"资源={probe.PackResourceCount} 可呈现={probe.ServedResourceCount}");

        using var catalog = SkinCatalog.Build(gamePck, [descriptor]);
        foreach (var group in catalog.Groups)
        {
            var part = group.Options.FirstOrDefault(option =>
                option.IsBaselinePartOverride &&
                option.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (part == null)
            {
                continue;
            }

            Console.WriteLine(
                $"  option {group.Id}: 默认={SkinCatalog.ResolvePartTakeoverDefault(group.Options) ?? "(无)"} " +
                $"部件路径={part.Assets.Count}");
            foreach (var path in part.Assets.Keys.Order(StringComparer.Ordinal))
            {
                var baseline = catalog.ResolveBaseline(path);
                Console.WriteLine($"    {path} 本体可还原={(baseline != null)}");
            }

            var shadow = catalog.BuildOverlay(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [group.Id] = SkinCatalog.BaseOptionId
            });
            // Every baseline file backing a part path has to win inside the overlay, otherwise the
            // author's globally mounted package would keep drawing its art when the player picks
            // the game original.
            var restored = 0;
            var mismatched = 0;
            foreach (var path in part.Assets.Keys)
            {
                if (catalog.ResolveBaseline(path) is not { } baseline)
                {
                    continue;
                }

                foreach (var baselineFile in baseline.Files)
                {
                    if (!shadow.TryGetValue(baselineFile.Path, out var shadowFile))
                    {
                        continue;
                    }

                    restored++;
                    if (!shadowFile.Read().SequenceEqual(baselineFile.Read()))
                    {
                        mismatched++;
                    }
                }
            }

            Console.WriteLine(
                $"  选择游戏原版时压回本体的资源文件：{restored}，字节不符={mismatched}");
        }

        var loader = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ManagedSkinModLoader", true)!;
        loader.GetMethod(
                "RememberProviderProbe",
                BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [probe]);
        var marking = (bool)loader.GetMethod(
                "IsManagedProviderForDisplay",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, [new Mod
            {
                path = modRoot,
                manifest = new ModManifest { id = id, name = name }
            }])!;
        Console.WriteLine($"  Mod 列表 [SC] 标记：{marking}");

        var workshopId = SkinCatalog.WorkshopSourceId(modRoot);
        foreach (var item in catalog.ExportWorkshopCatalog())
        {
            Console.WriteLine(
                $"  catalog {item.Id}: " +
                string.Join("|", item.Targets.Select(target => target.Kind + ":" + target.Target)) +
                (item.Id == workshopId ? " ← 本包" : string.Empty));
        }

        if (workshopId == 0)
        {
            Console.WriteLine("  本包不是工坊目录，跳过投稿扫描。");
            return;
        }

        var result = WorkshopSubmissionScanner.Scan(
            new(workshopId, name, modRoot),
            gamePck,
            [],
            "0.111.0",
            new Dictionary<string, string>());
        Console.WriteLine(result.Item == null
            ? $"  submission scan {workshopId}: rejected ({result.Error})"
            : $"  submission scan {workshopId}: targets=" +
              string.Join("|", result.Item.Targets.Select(target => target.Kind + ":" + target.Target)) +
              $" restart={result.Item.RestartRequired}");
    }
}
