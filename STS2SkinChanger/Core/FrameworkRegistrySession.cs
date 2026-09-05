using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace STS2SkinChanger.Core;

/// <summary>
/// Keeps native SkinData/config delegates, but gives selection ownership to SC. No reference to
/// the bundled assembly: an original and an adapter must never claim the same CLR identity.
/// </summary>
internal sealed class FrameworkRegistrySession
{
    private static readonly Dictionary<Type, FrameworkRegistrySession> Sessions = [];
    private readonly Type _registry;
    private readonly Type _data;
    private readonly FieldInfo _skins;
    private readonly PropertyInfo _skinId;
    private readonly MethodInfo _config;
    private readonly Func<ModelId, string?> _read;
    private readonly Action<ModelId, string> _request;
    public Action PrepareCharacters { get; set; } = () => { };
    public Assembly Assembly => _registry.Assembly;

    public FrameworkRegistrySession(Assembly assembly, Func<ModelId, string?> read,
        Action<ModelId, string> request)
    {
        const string prefix = "thunninoiSkinManager.thunninoiSkinManagerCode.";
        _registry = assembly.GetType(prefix + "SkinRegistry", true)!;
        _data = assembly.GetType(prefix + "SkinData", true)!;
        _skins = _registry.GetField("_skins", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(_registry.FullName, "_skins");
        _skinId = _data.GetProperty("SkinId")!;
        _config = _data.GetMethod("IsConfigEnabled", [typeof(string), typeof(bool)])!;
        if (_skins.FieldType != typeof(Dictionary<,>).MakeGenericType(typeof(ModelId),
                typeof(List<>).MakeGenericType(_data)) || _skinId?.PropertyType != typeof(string) ||
            _config?.ReturnType != typeof(bool) ||
            _data.GetConstructor([typeof(ModelId), typeof(string), typeof(string)]) == null)
            throw new NotSupportedException("原管理器注册表结构已变化，不能安全启用协作。");
        _read = read;
        _request = request;
    }

    private IDictionary Skins => (IDictionary)_skins.GetValue(null)!;
    private IList? GetSkins(ModelId id) => Skins[id] as IList;
    private string Id(object skin) => (string)_skinId.GetValue(skin)!;
    private object? Find(ModelId id, string skinId) => GetSkins(id)?.Cast<object>()
        .FirstOrDefault(skin => Id(skin).Equals(skinId, StringComparison.Ordinal));
    private object? Active(ModelId id) => Find(id, _read(id) ?? "default") ?? Find(id, "default");

    public void EnsureCharacter(ModelId id)
    {
        // Original SkinDbSetup clears every list. Registration retries must only add missing
        // defaults; provider postfixes can then register their own data without losing siblings.
        if (GetSkins(id) == null)
            Skins[id] = Activator.CreateInstance(typeof(List<>).MakeGenericType(_data))!;
        if (Find(id, "default") != null) return;
        var skin = Activator.CreateInstance(_data, [id, "default", "Default"])!;
        _data.GetMethod("AsDefault")!.Invoke(skin, null);
        GetSkins(id)!.Insert(0, skin);
    }

    public bool IsConfigEnabled(string groupId, string skinId, string key)
    {
        foreach (ModelId id in Skins.Keys)
        {
            if (!Normalize(id.Entry).Equals(Normalize(groupId), StringComparison.Ordinal)) continue;
            var skin = Find(id, skinId);
            if (skin != null) return (bool)_config.Invoke(skin, [key, true])!;
        }
        return true;
    }

    private void Request(ModelId id, string skinId)
    {
        if (Find(id, skinId) != null) _request(id, skinId);
    }

    public void Install()
    {
        if (Sessions.ContainsKey(_registry)) return;
        // Resolve the complete contract before changing any method. An incompatible host must
        // not be left half patched with two independent selection stores.
        var patches = new List<(MethodInfo Target, MethodInfo Prefix)>();
        void Add(string name, Type[] args, string prefix) => patches.Add((
            AccessTools.Method(_registry, name, args) ?? throw new MissingMethodException(_registry.FullName, name),
            AccessTools.Method(typeof(FrameworkRegistrySession), prefix)));
        Add("GetActiveSkin", [typeof(ModelId)], nameof(ReadActive));
        Add("IsUsingSkin", [typeof(ModelId), typeof(string)], nameof(ReadUsing));
        Add("GetAllActiveSkins", [], nameof(ReadAll));
        Add("SetActiveSkin", [typeof(ModelId), typeof(string)], nameof(WriteString));
        Add("SetActiveSkin", [typeof(ModelId), typeof(int)], nameof(WriteIndex));
        Add("CycleNext", [typeof(ModelId)], nameof(Cycle));
        Add("CyclePrevious", [typeof(ModelId)], nameof(Cycle));
        Add("SkinDbSetup", [], nameof(Setup));
        foreach (var name in new[] { "Load", "Save", "finializeSetup", "RefreshSkinCache" })
            Add(name, [], nameof(Skip));
        foreach (var name in new[] { "ResolvePower", "ResolvePotion", "ResolveRelic", "ResolveOrb" })
            Add(name, [typeof(ModelId)], nameof(ResolveShared));
        var harmony = new Harmony(Entry.ModId + ".native-framework-registry");
        Sessions.Add(_registry, this);
        try
        {
            foreach (var patch in patches)
                harmony.Patch(patch.Target, prefix: new HarmonyMethod(patch.Prefix) { priority = Priority.First });
        }
        catch
        {
            foreach (var patch in patches) harmony.Unpatch(patch.Target, patch.Prefix);
            Sessions.Remove(_registry);
            throw;
        }
    }

    private static FrameworkRegistrySession Session(MethodBase method) => Sessions[method.DeclaringType!];
    private static bool ReadActive(MethodBase __originalMethod, ModelId __0, ref object? __result)
    { __result = Session(__originalMethod).Active(__0); return false; }
    private static bool ReadUsing(MethodBase __originalMethod, ModelId __0, string __1, ref bool __result)
    {
        var session = Session(__originalMethod);
        var active = session.Active(__0);
        __result = active != null && session.Id(active).Equals(__1, StringComparison.Ordinal);
        return false;
    }
    private static bool ReadAll(MethodBase __originalMethod, ref Dictionary<ModelId, string> __result)
    {
        var session = Session(__originalMethod);
        __result = session.Skins.Keys.Cast<ModelId>().ToDictionary(id => id,
            id => session.Active(id) is { } active ? session.Id(active) : "default");
        return false;
    }
    private static bool WriteString(MethodBase __originalMethod, ModelId __0, string __1)
    { Session(__originalMethod).Request(__0, __1); return false; }
    private static bool WriteIndex(MethodBase __originalMethod, ModelId __0, int __1)
    {
        var session = Session(__originalMethod);
        var list = session.GetSkins(__0);
        if (list != null && __1 >= 0 && __1 < list.Count) session.Request(__0, session.Id(list[__1]!));
        return false;
    }
    private static bool Cycle(MethodBase __originalMethod, ModelId __0)
    {
        var session = Session(__originalMethod);
        var list = session.GetSkins(__0);
        if (list == null || list.Count == 0) return false;
        var index = list.IndexOf(session.Active(__0));
        var offset = __originalMethod.Name == "CycleNext" ? 1 : -1;
        session.Request(__0, session.Id(list[(Math.Max(index, 0) + list.Count + offset) % list.Count]!));
        return false;
    }
    private static bool Setup(MethodBase __originalMethod)
    { Session(__originalMethod).PrepareCharacters(); return false; }
    private static bool Skip() => false;
    private static bool ResolveShared(MethodBase __originalMethod, ModelId __0, ref object? __result)
    {
        var session = Session(__originalMethod);
        var collection = __originalMethod.Name["Resolve".Length..] + "SkinDict";
        __result = null;
        foreach (ModelId character in session.Skins.Keys)
        {
            var active = session.Active(character);
            if (active == null) continue;
            if (collection == "OrbSkinDict" && !(bool)session._config.Invoke(active, ["UseDefectOrbs", true])!) continue;
            if (session._data.GetProperty(collection, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(active) is IDictionary map && map.Contains(__0))
            { __result = map[__0]; break; }
        }
        return false;
    }
    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
