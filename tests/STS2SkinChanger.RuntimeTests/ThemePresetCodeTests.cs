using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;

internal static class ThemePresetCodeTests
{
    internal static void Run(Assembly assembly, Func<string, object> parse)
    {
        var codec = assembly.GetType("STS2SkinChanger.Core.ThemePresetCode");
        Require(codec != null, "主题预设需要可验证、无损的预设码，不能复制整个本机配置。");
        string Encode(object theme) => (string)AccessTools.Method(codec!, "Encode").Invoke(null, [theme])!;
        object Decode(string code) => AccessTools.Method(codec!, "Decode").Invoke(null, [code])!;
        bool Detect(string text) => (bool)AccessTools.Method(codec!, "LooksLikeCode").Invoke(null, [text])!;
        var baseline = parse("{}");
        var code = Encode(baseline);
        // Frozen V1 fixture independently packed with BinaryWriter, not this encoder.
        const string fixture = "SCT1.________________39_f____39_f____39_f09PT__bi_-qp09PTAAAAzcxMPgAAQEAAAAA_AAAAQAAAAD8AAABAPQpXPgAAAEBcj0I-AABAQAAAAD-amZk-AAAAPwAAgD8AAAA_AAoACgUCAgMBfslgUA";
        Require(Decode(fixture).Equals(baseline) && Encode(baseline) == fixture,
            "以后实现变化不能悄悄改变 V1 码的字段顺序或字节序，旧码须继续还原同一主题。");
        Require(code.Length == 159 && code.StartsWith("SCT1.") && Decode(code).Equals(baseline),
            "全部 38 个主题参数连同版本和校验应为 159 字符，并能完整恢复。");
        Require(Detect(code) && Detect("  SCT2.invalid  ") && !Detect("我的主题") && !Detect("SCTheme") &&
                Decode(" \r\n" + code + "\t ").Equals(baseline),
            "仅码标识触发导入；未知版本仍进入校验，粘贴首尾空白可忽略，普通名字不能误判。");

        var random = new Random(159);
        for (var sample = 0; sample < 100; sample++)
        {
            var values = new Dictionary<string, object>();
            foreach (var property in baseline.GetType().GetProperties())
            {
                values[property.Name] = property.PropertyType == typeof(string) ? $"#{random.Next(0x1000000):X6}" :
                    property.PropertyType == typeof(bool) ? sample % 2 == 0 :
                    property.PropertyType == typeof(float) ? random.NextSingle() * 4.731f : random.Next(-12, 25);
            }
            var theme = parse(JsonSerializer.Serialize(values));
            Require(Decode(Encode(theme)).Equals(theme), "随机合法主题往返不能漏字段、交换字段或损失小数精度。");
        }
        foreach (var invalid in new[] { "", "名称", "SCT2." + code[5..], code[..^1], code + "A",
                     code[..20] + (code[20] == 'A' ? 'B' : 'A') + code[21..], "SCT1." + new string('A', 10000) })
        {
            var rejected = false;
            try { Decode(invalid); }
            catch (TargetInvocationException e) when (e.InnerException is FormatException) { rejected = true; }
            Require(rejected, "损坏、截断、超长或未知版本预设码必须拒绝。");
        }
        foreach (var mutate in new Action<byte[]>[] {
                     data => System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(42), float.NaN),
                     data => data[102] = 255, data => data[110] = 2 })
        {
            var data = Convert.FromBase64String(code[5..].Replace('-', '+').Replace('_', '/') + "==");
            mutate(data);
            SHA256.HashData(data.AsSpan(0, 111)).AsSpan(0, 4).CopyTo(data.AsSpan(111));
            var forged = "SCT1." + Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var rejected = false;
            try { Decode(forged); }
            catch (TargetInvocationException e) when (e.InnerException is FormatException) { rejected = true; }
            Require(rejected, "即使校验码正确，NaN、越界数字和非布尔值也必须拒绝，不能进入渲染设置。");
        }

        var directory = Directory.CreateTempSubdirectory("sc-theme-code-");
        try
        {
            var libraryType = assembly.GetType("STS2SkinChanger.Core.ModThemePresets", true)!;
            var path = Path.Combine(directory.FullName, "presets.json");
            var library = Activator.CreateInstance(libraryType, path)!;
            object Import(string value) => AccessTools.Method(libraryType, "ImportCode").Invoke(library, [value, "导入预设"])!;
            var imported = Import(code);
            Require((string)Property(imported, "Name") == "导入预设" && Property(imported, "Settings").Equals(baseline),
                "导入必须新建指定名称的预设并携带完整参数。");
            Require((string)AccessTools.Method(libraryType, "ExportCode").Invoke(library, [Property(imported, "Id")])! == code,
                "导出须取指定预设的存储参数，不能取其它预设或当前未保存草稿。");
            var second = Import(code);
            Require((string)Property(second, "Name") == "导入预设 (2)" &&
                    (string)Property(Import(code), "Name") == "导入预设 (3)", "重名导入不能覆盖已有预设，必须自动递增编号。");
            var before = File.ReadAllText(path);
            try { Import(code[..^1]); }
            catch (TargetInvocationException e) when (e.InnerException is FormatException) { }
            Require(before == File.ReadAllText(path), "导入校验失败不得写入预设文件。");
            var restored = Activator.CreateInstance(libraryType, path)!;
            Require(((IEnumerable)Property(restored, "Presets")).Cast<object>().Count() == 4,
                "新导入的三项应可重开恢复，且默认项不受影响。");
        }
        finally { directory.Delete(true); }
        var editor = assembly.GetType("STS2SkinChanger.Ui.ModThemeEditor", true)!;
        var importedInstructions = PatchProcessor.GetOriginalInstructions(AccessTools.Method(editor, "ImportThemePreset"));
        var importAt = importedInstructions.FindIndex(i => i.operand is MethodInfo m && m.Name == "ImportCode");
        var applyAt = importedInstructions.FindIndex(i => i.operand is MethodInfo m && m.Name == "Preview");
        Require(importAt >= 0 && applyAt > importAt, "界面必须在导入写盘成功后应用，失败时保留当前主题。");
        var copyInstructions = PatchProcessor.GetOriginalInstructions(AccessTools.Method(editor, "CopyThemePreset"));
        Require(copyInstructions.Any(i => i.operand is FieldInfo f && f.Name == "_selectedPresetId") &&
                copyInstructions.Any(i => i.operand is MethodInfo m && m.Name == "ExportCode") &&
                copyInstructions.Any(i => i.operand is MethodInfo m && m.DeclaringType == typeof(Godot.DisplayServer) && m.Name == "ClipboardSet"),
            "复制入口必须将所选预设码交给游戏的跨平台剪贴板接口。");
        Console.WriteLine("Theme preset codes passed: 159 characters, all-field round trips, validation and numbered imports.");
    }

    private static object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
