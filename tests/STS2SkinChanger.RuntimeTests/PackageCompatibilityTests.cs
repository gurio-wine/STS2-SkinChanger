using System.Reflection;
using System.Collections;
using System.Text;
using System.Text.Json.Nodes;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger;

internal static class PackageCompatibilityTests
{
    private static readonly Assembly ModAssembly = typeof(Entry).Assembly;

    internal static void Audit(string gamePck, string manifestPath)
    {
        var original = File.ReadAllBytes(manifestPath);
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var json = JsonNode.Parse(Encoding.UTF8.GetString(original).TrimStart('\uFEFF'))!;
        var id = json["id"]!.GetValue<string>();
        string Resolve(string extension) => (string)ModAssembly.GetType("STS2SkinChanger.Catalog.SkinPackagePaths")!
            .GetMethod("Resolve")!.Invoke(null, [root, id, extension])!;
        var pck = Resolve(".pck");
        var dll = Resolve(".dll");
        var descriptorType = ModAssembly.GetType("STS2SkinChanger.Catalog.SkinModDescriptor")!;
        var descriptor = Activator.CreateInstance(descriptorType, id, json["name"]?.GetValue<string>() ?? id,
            pck, false, root, json["has_dll"]?.GetValue<bool>() == true, null)!;
        var descriptors = Array.CreateInstance(descriptorType, 1);
        descriptors.SetValue(descriptor, 0);
        var result = (IEnumerable)ModAssembly.GetType("STS2SkinChanger.Catalog.SkinCatalog")!
            .GetMethod("ProbeSkinProviders")!.Invoke(null, [descriptors, gamePck])!;
        var probes = result.Cast<object>().ToArray();
        Require(probes.Length == 1, "实际皮肤包未被接管识别：" + id);
        var probe = probes[0];
        Require((string)probe.GetType().GetProperty("ResourceNamespaceId")!.GetValue(probe)! == id,
            "实包探测不能更改原始 Mod ID。");
        Console.WriteLine($"Skin package audit: id={id}, pck={pck}, dll={dll}, " +
            $"visuals={probe.GetType().GetProperty("VisualGroupCount")!.GetValue(probe)}, " +
            $"cards={probe.GetType().GetProperty("CardAssetCount")!.GetValue(probe)}");
        Require(original.SequenceEqual(File.ReadAllBytes(manifestPath)), "实包审计不能修改作者清单。");
        if (json["has_dll"]?.GetValue<bool>() == true) ProviderCompatibilityTests.Audit(dll);
    }
    internal static void Run()
    {
        Require(ModAssembly.GetType("STS2SkinChanger.Catalog.SkinPackagePaths") != null,
            "缺少保持 Mod ID 不变的资源定位兼容。");
        CheckPackagePaths();
        CheckManifestMigration();
        CheckNativeManifestReaderAndWarnings();
        Console.WriteLine("Package compatibility passed: matched manifests, paired resources, ambiguity, dependency migration and source preservation.");
    }

    private static void CheckNativeManifestReaderAndWarnings()
    {
        var directory = Directory.CreateTempSubdirectory("sc-manifest-test-");
        var harmony = new Harmony("Gurio.SkinChanger.Tests.ManifestMigration");
        const string migration = "MOD_ERROR.MIGRATION_REQUIRED";
        const string branch = "MOD_ERROR.STEAM_BRANCH_UNSUPPORTED";
        var compatibility = ModAssembly.GetType("STS2SkinChanger.Core.ProviderManifestCompatibility")!;
        try
        {
            // The logging sink calls Godot OS during static initialization. Replace only
            // that engine I/O boundary; manifest parsing and warning decisions stay real.
            foreach (var name in new[] { "Info", "Warn" })
                harmony.Patch(ModAssembly.GetType("STS2SkinChanger.Core.ModLog")!.GetMethod(name)!,
                    prefix: new HarmonyMethod(typeof(PackageCompatibilityTests), nameof(LogWithoutEngine)));
            harmony.CreateClassProcessor(ModAssembly.GetType("STS2SkinChanger.Core.LegacyManifestReadCompatibilityPatch")!).Patch();
            const string original = """{"id":"Voice","affects_gameplay":false,"has_dll":false,"has_pck":false,"dependencies":["BaseLib"]}""";
            var path = Path.Combine(directory.FullName, "mod_manifest.json");
            File.WriteAllText(path, original);
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(original));
            var manifest = ModManifest.ReadFromStream(input, out var readErrors);
            Require(manifest?.dependencies?.Single().id == "BaseLib" && readErrors?.Count is null or 0,
                "真正的游戏清单入口必须收到现代依赖对象，不能继续产生迁移错误。");
            var dependencyError = new LocString("main_menu_ui", "DEPENDENCY_MISSING_TEST");
            var unrelated = new LocString("other_table", migration);
            var mod = new Mod { path = directory.FullName, manifest = manifest, errors =
                [new("main_menu_ui", migration), dependencyError, unrelated] };
            compatibility.GetMethod("AcknowledgeParsedManifests")!.Invoke(null, [new[] { mod }]);
            Require(mod.errors.SequenceEqual(new[] { dependencyError, unrelated }) && File.ReadAllText(path) == original,
                "只处理已等价迁移的历史错误，不清除真实依赖错误或更改作者文件。");
            var migrationError = new LocString("main_menu_ui", migration);
            mod.errors.Add(migrationError);
            manifest!.dependencies![0].minVersion = "9.0.0";
            compatibility.GetMethod("AcknowledgeParsedManifests")!.Invoke(null, [new[] { mod }]);
            Require(mod.errors.Contains(migrationError), "依赖约束不等价时必须保留提示。");

            var branchError = new LocString("main_menu_ui", branch);
            mod.errors = [branchError, dependencyError];
            manifest.hasPck = true;
            mod.state = ModLoadState.None;
            var acknowledgeBranch = compatibility.GetMethod("AcknowledgeManagedBranch")!;
            acknowledgeBranch.Invoke(null, [mod, true]);
            Require(mod.errors.Contains(branchError), "尚未成功接管的包不能消除版本警告。");
            mod.state = ModLoadState.Loaded;
            acknowledgeBranch.Invoke(null, [mod, true]);
            Require(mod.errors.Contains(branchError), "资源文件缺失不能消除版本警告。");
            File.WriteAllBytes(Path.Combine(directory.FullName, "Voice.pck"), [0]);
            acknowledgeBranch.Invoke(null, [mod, false]);
            Require(mod.errors.Contains(branchError), "没有被识别为实际皮肤资源的包不能消除版本警告。");
            acknowledgeBranch.Invoke(null, [mod, true]);
            Require(mod.errors.SequenceEqual(new[] { dependencyError }), "已成功接管的无代码资源包应处理声明提示，保留其它错误。");
            manifest.hasDll = true;
            mod.errors.Add(branchError);
            File.WriteAllBytes(Path.Combine(directory.FullName, "Voice.dll"), [0, 1, 2]);
            acknowledgeBranch.Invoke(null, [mod, true]);
            Require(mod.errors.Contains(branchError), "损坏或未通过接口检查的 DLL 不能消除版本警告。");
        }
        finally { harmony.UnpatchAll(harmony.Id); directory.Delete(recursive: true); }
    }

    private static bool LogWithoutEngine(string message) { Console.WriteLine("Manifest compatibility: " + message); return false; }

    private static void CheckPackagePaths()
    {
        var directory = Directory.CreateTempSubdirectory("sc-package-test-");
        try
        {
            string Resolve(string ext, string id = "ceshi") => (string)ModAssembly.GetType("STS2SkinChanger.Catalog.SkinPackagePaths")!
                .GetMethod("Resolve")!.Invoke(null, [directory.FullName, id, ext])!;
            var manifest = """{"id":"ceshi","affects_gameplay":false,"has_pck":true,"has_dll":true,"dependencies":[]}""";
            var path = Path.Combine(directory.FullName, "Necrobinder_1.0.json");
            File.WriteAllText(path, manifest);
            File.WriteAllBytes(Path.ChangeExtension(path, ".pck"), [1]);
            File.WriteAllBytes(Path.ChangeExtension(path, ".dll"), [2]);
            Require(Resolve(".pck") == Path.ChangeExtension(path, ".pck") && Resolve(".dll") == Path.ChangeExtension(path, ".dll"),
                "必须以同一清单对应的完整 DLL/PCK 配对定位，不能改动 ceshi 的逻辑 ID。");
            Require(File.ReadAllText(path) == manifest, "不能写回作者清单。");
            Require(Resolve(".pck", "Sibling") == Path.Combine(directory.FullName, "Sibling.pck"), "不能抢用同目录另一 Mod 的文件。");
            File.WriteAllBytes(Path.Combine(directory.FullName, "ceshi.dll"), [3]);
            Require(Resolve(".pck") == Path.Combine(directory.FullName, "ceshi.pck"), "不能将两套包的 DLL 和 PCK 混合。");
            File.Delete(Path.Combine(directory.FullName, "ceshi.dll"));
            File.WriteAllText(Path.Combine(directory.FullName, "Other.json"), manifest);
            File.WriteAllBytes(Path.Combine(directory.FullName, "Other.dll"), [4]);
            File.WriteAllBytes(Path.Combine(directory.FullName, "Other.pck"), [5]);
            Require(Resolve(".dll") == Path.Combine(directory.FullName, "ceshi.dll"), "同 ID 有多个候选时不能猜选。");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static void CheckManifestMigration()
    {
        var compatibility = ModAssembly.GetType("STS2SkinChanger.Core.ProviderManifestCompatibility");
        Require(compatibility != null, "缺少旧依赖清单的等价迁移。");
        var prepare = compatibility!.GetMethod("PrepareLegacyStream")!;
        const string source = """{"id":"VoiceBridge","affects_gameplay":false,"dependencies":["BaseLib","OtherLib"]}""";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
        using var normalized = (MemoryStream?)prepare.Invoke(null, [input]);
        Require(normalized != null, "纯字符串依赖列表应生成内存兼容清单。");
        var json = JsonNode.Parse(normalized!);
        Require(json!["id"]!.GetValue<string>() == "VoiceBridge" && json["dependencies"]!.AsArray().Count == 2 &&
                json["dependencies"]![0]!["id"]!.GetValue<string>() == "BaseLib" &&
                json["dependencies"]![1]!["id"]!.GetValue<string>() == "OtherLib" &&
                json["dependencies"]![0]!["min_version"] == null,
            "依赖名称、次序及无最低版本限制的语义必须保留。");
        Require(Encoding.UTF8.GetString(input.ToArray()) == source, "不能修改原始清单字节。");
        normalized!.Position = 0;
        Require(prepare.Invoke(null, [normalized]) == null && normalized.Position == 0, "现代清单不得重复改写或提前消费流。");
        foreach (var invalid in new[] { """{"id":"A","dependencies":["BaseLib",{}]}""", "{" })
        {
            using var other = new MemoryStream(Encoding.UTF8.GetBytes(invalid));
            Require(prepare.Invoke(null, [other]) == null && other.Position == 0, "未知或损坏的清单应留给原加载器，不得吞错。");
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
