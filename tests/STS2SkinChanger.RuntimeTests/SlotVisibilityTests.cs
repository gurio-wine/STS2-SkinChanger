using System.Reflection;
using System.Text.Json;
using STS2SkinChanger;

internal static class SlotVisibilityTests
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly string[] PreviewSlots = [
        "tougu mianju 0", "tougu mianju 1", "tougu mianju 2", "mianju yanjing 0",
        "mianju yanjing 6", "mianju yanjing 2", "mianju yanjing 3", "mianju yanjing 4",
        "mianju yanjing 5", "mianju yanjing 1", "mianju yinying 0"];

    public static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var policy = assembly.GetType("STS2SkinChanger.Core.SlotVisibilityPolicy")
            ?? throw new InvalidOperationException("选角部件开关没有跨场景状态与映射，进入战斗会丢失选择。");
        var resolve = policy.GetMethod("ResolveSlots", Static)!;
        string[] Resolve(string[] source, string[] available) => (string[])resolve.Invoke(null, [source, available])!;
        Require(Resolve(["hat"], ["face", "hat"]).SequenceEqual(["hat"]), "同名部件应通用恢复，不依赖 Mod ID。");
        Require(Resolve(["hat"], ["face", "hat-like"]).Length == 0, "不能用相似名称猜测部件。");
        var combat = new[] { "mianjv", "gumian", "ATK mianju yanjing", "dujiaoshou_27", "head21", "ATK tou", "mianzhao" };
        Require(Resolve(PreviewSlots, combat).SequenceEqual(combat.Take(4)), "不同战斗姿势的四个头骨都须覆盖，脸和口罩不能删除。");
        Require(Resolve(PreviewSlots, ["mianju", "mianju  yanjing", "head", "kouzhao"]).SequenceEqual(["mianju", "mianju  yanjing"]),
            "休息骨骼仅隐藏头骨及其眼睛，保留脸部和口罩。");
        Require(Resolve(PreviewSlots, combat.Except(["head21"]).ToArray()).Length == 0,
            "不完整或陌生骨骼不能套用跨场景映射。");
        Require(Resolve(["mianju yanjing 0"], combat).Length == 0, "只有相似的单个部件不能触发整套映射。");

        var configType = assembly.GetType("STS2SkinChanger.Core.SkinConfig", true)!;
        var config = JsonSerializer.Deserialize("""
            {"SlotVisibilitySelections":[
                {"GroupId":"character:silent","ProviderId":"provider-a","ToggleId":"head","Hidden":true,"SourceSlots":["hat"]},
                {"GroupId":"character:silent","ProviderId":"provider-a","ToggleId":"cloak","Hidden":false,"SourceSlots":["cloak"]},
                {"GroupId":"character:regent","ProviderId":"provider-a","ToggleId":"head","Hidden":true,"SourceSlots":["crown"]},
                {"GroupId":"character:silent","ProviderId":"provider-b","ToggleId":"head","Hidden":true,"SourceSlots":["ribbon"]}
            ]}
            """, configType)!;
        var getHidden = policy.GetMethod("GetHiddenSourceSlots", Static)!;
        string[] Hidden(object data, string group, string provider) => (string[])getHidden.Invoke(null, [data, group, provider])!;
        Require(Hidden(config, "character:silent", "provider-a").SequenceEqual(["hat"]), "不同角色、皮肤及已取消隐藏的部件必须隔离。");
        Require(Hidden(config, "character:silent", "vanilla").Length == 0, "原皮不能继承其他皮肤的隐藏部件。");
        var directory = Path.Combine(Path.GetTempPath(), "sc-slot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            configType.GetMethod("Save")!.Invoke(config, [path]);
            var loaded = configType.GetMethod("Load", Static)!.Invoke(null, [path])!;
            Require(Hidden(loaded, "character:silent", "provider-a").SequenceEqual(["hat"]), "重启后必须恢复已保存的部件选择。");
        }
        finally { Directory.Delete(directory, true); }

        var maskType = assembly.GetType("STS2SkinChanger.Core.SlotAlphaMask", true)!;
        var mask = Activator.CreateInstance(maskType)!;
        var hide = maskType.GetMethod("Hide")!;
        var restore = maskType.GetMethod("Restore")!;
        float Hide(float alpha) => (float)hide.Invoke(mask, [alpha])!;
        Require(Hide(1f) == 0 && Hide(0f) == 0 && Hide(0.6f) == 0,
            "动画重新写入透明度后仍应维持隐藏，不能只在 Ready 隐藏一次。");
        Require((float)restore.Invoke(mask, [0f])! == 0.6f, "取消遮罩时恢复动画最近写入的透明度。");
        Require((float)restore.Invoke(mask, [0.4f])! == 0.4f, "已经释放的遮罩不得再次改动模型。");
        var discover = assembly.GetType("STS2SkinChanger.Core.SlotToggleContract", true)!.GetMethod("TryCreate", Static)!;
        Require(discover.Invoke(null, [typeof(RenamedVisibilityController)]) != null,
            "通用检测应识别改名后的显隐开关，不绑定作者类名和字段名。");
        Require(discover.Invoke(null, [typeof(PressAnimationController)]) == null,
            "不能把按住触发动画的状态保存为部件隐藏。");
        Console.WriteLine("Slot visibility passed: scene mapping, provider isolation, persistence and animation alpha.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Audit(string assemblyPath)
    {
        var provider = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var discover = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.SlotToggleContract", true)!
            .GetMethod("TryCreate", Static)!;
        var matches = provider.GetTypes().Where(type => typeof(Godot.Control).IsAssignableFrom(type))
            .Select(type => (Type: type, Contract: discover.Invoke(null, [type])))
            .Where(result => result.Contract != null).ToArray();
        Require(matches.Length == 1 && matches[0].Type.Name == "HeadBoneClickToggle",
            "实包必须只识别头骨显隐开关，不得把长按动作当成持久开关。");
        Console.WriteLine("Verified actual provider visibility contract: " + matches[0].Type.FullName);
        AuditSkeletonMappings(Path.ChangeExtension(assemblyPath, ".pck"));
    }

    // A small fixture isolates managed input/alpha semantics; it does not construct native Godot nodes.
    private sealed class RenamedVisibilityController
    {
        public string[] parts = ["hat"];
        private bool withoutHat;
        private readonly ColorSlot slot = new();
        public void _Input(Godot.InputEvent _) { withoutHat = !withoutHat; Apply(); }
        private void Apply()
        {
            var color = slot.Read("get_color");
            if (withoutHat) { color.A = 0f; slot.Write("set_color", color); }
        }
    }

    private sealed class PressAnimationController
    {
        public string[] parts = ["hat"];
        private bool pressed;
        public void _Input(Godot.InputEvent _) { pressed = true; }
        public bool IsPressed => pressed;
    }

    private sealed class ColorSlot
    {
        private Godot.Color color = new(1, 1, 1, 1);
        public Godot.Color Read(string _) => color;
        public void Write(string _, Godot.Color value) => color = value;
    }

    private static void AuditSkeletonMappings(string pckPath)
    {
        var assembly = typeof(Entry).Assembly;
        var archiveType = assembly.GetType("STS2SkinChanger.Pck.PckArchive", true)!;
        using var archive = (IDisposable)archiveType.GetMethod("Open", Static)!.Invoke(null, [pckPath, 2_000_000u])!;
        var readFile = archiveType.GetMethod("ReadFile")!;
        var resolve = assembly.GetType("STS2SkinChanger.Core.SlotVisibilityPolicy", true)!.GetMethod("ResolveSlots", Static)!;
        foreach (var (path, count) in new[] {
            ("res://silent/animations/character_select/silent/characterselect_silent.skel.import", 11),
            ("res://silent/animations/characters/Silent/silent.skel.import", 4),
            ("res://silent/animations/merchant/Silent/silent.skel.import", 4),
            ("res://silent/animations/rest_site/Silent/restsite_silent.skel.import", 2) })
        {
            var import = System.Text.Encoding.UTF8.GetString((byte[])readFile.Invoke(archive, [path])!);
            var payload = System.Text.RegularExpressions.Regex.Match(import, "path=\"([^\"]+)\"").Groups[1].Value;
            var slots = ReadSlotNames((byte[])readFile.Invoke(archive, [payload])!);
            var mapped = (string[])resolve.Invoke(null, [PreviewSlots, slots])!;
            Require(mapped.Length == count, $"实包骨骼映射不完整：{path}，实际 {mapped.Length}/{count}。");
            Console.WriteLine($"Verified skeleton slots: {path} -> {mapped.Length}");
        }
    }

    // Read only the public Spine 4.2 binary header/bone/slot table for an offline asset audit.
    // Format reference: github.com/EsotericSoftware/spine-runtimes/blob/4.2/spine-csharp/src/SkeletonBinary.cs
    // No animation evaluation, asset mutation, provider initializer, or native engine is executed.
    private static string[] ReadSlotNames(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        int VarInt()
        {
            var value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var b = reader.ReadByte(); value |= (b & 127) << shift;
                if ((b & 128) == 0) return value;
            }
            throw new InvalidDataException("Invalid variable-length integer");
        }
        string Text()
        {
            var length = VarInt();
            return length <= 1 ? "" : System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length - 1));
        }
        void Skip(int count) => stream.Position += count;
        Skip(8);
        Require(Text().StartsWith("4.2.", StringComparison.Ordinal), "实包骨骼版本改变，需重新审计映射。");
        Skip(20);
        var nonessential = reader.ReadBoolean();
        if (nonessential) { Skip(4); Text(); Text(); }
        var strings = VarInt();
        for (var i = 0; i < strings; i++) Text();
        var bones = VarInt();
        for (var i = 0; i < bones; i++)
        {
            Text(); if (i > 0) VarInt(); Skip(32); VarInt(); Skip(1);
            if (nonessential) { Skip(4); Text(); Skip(1); }
        }
        var slots = new string[VarInt()];
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = Text(); VarInt(); Skip(8); VarInt(); VarInt();
            if (nonessential) Skip(1);
        }
        return slots;
    }
}
