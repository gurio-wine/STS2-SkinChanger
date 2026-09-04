using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using STS2SkinChanger;

internal static class ProviderAnimationCompatibilityTests
{
    internal static void Run()
    {
        // Execute the emitted adapter against a managed animation boundary. This checks the
        // emitted IL itself: argument order, nonzero track routing and the returned object.
        // No Godot object or native animation is replaced/mocked in the real-package audit.
        var cecil = typeof(Harmony).Assembly;
        var definitionType = cecil.GetType("Mono.Cecil.AssemblyDefinition", true)!;
        var definition = definitionType.GetMethod("CreateAssembly", [
            cecil.GetType("Mono.Cecil.AssemblyNameDefinition")!, typeof(string),
            cecil.GetType("Mono.Cecil.ModuleKind")!])!.Invoke(null, [
            Activator.CreateInstance(cecil.GetType("Mono.Cecil.AssemblyNameDefinition")!,
                "AnimationAdapterFixture", new Version(1, 0)),
            "AnimationAdapterFixture", Enum.Parse(cecil.GetType("Mono.Cecil.ModuleKind")!, "Dll")])!;
        var context = new AssemblyLoadContext("animation-adapter-fixture", isCollectible: true);
        try
        {
            var module = Property(definition, "MainModule");
            var import = module.GetType().GetMethod("ImportReference", [typeof(MethodBase)])!;
            var importedSet = import.Invoke(module, [typeof(AnimationStateProbe).GetMethod("SetAnimation")])!;
            var importedGet = import.Invoke(module, [typeof(AnimationStateProbe).GetMethod("GetCurrent")])!;
            var owner = ((System.Collections.IEnumerable)Property(module, "Types")).Cast<object>().First();
            var compatibility = typeof(Entry).Assembly.GetType(
                "STS2SkinChanger.Core.ProviderAssemblyCompatibility", true)!;
            var factory = compatibility.GetMethod("CreateTrackedSetAnimationAdapter",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            var adapter = factory.Invoke(null,
                [cecil, owner, importedSet, importedGet, cecil.GetType("Mono.Cecil.Cil.OpCodes")])!;
            using var bytes = new MemoryStream();
            definitionType.GetMethod("Write", [typeof(Stream)])!.Invoke(definition, [bytes]);
            bytes.Position = 0;
            var generated = context.LoadFromStream(bytes);
            var invoke = generated.ManifestModule.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method => method.Name == (string)Property(adapter, "Name"));
            var state = new AnimationStateProbe();
            var track = (AnimationTrackProbe)invoke.Invoke(null, [state, "idle_loop", false, 3])!;
            if (state.LastAnimation != "idle_loop" || state.LastLoop != false ||
                state.LastTrack != 3 || state.ReadTrack != 3 || state.PlayCount != 1 ||
                !ReferenceEquals(track, state.Track))
                throw new InvalidOperationException("Tracked animation bridge changed arguments or lost the track.");
            track.Time = 0.25f;
            if (state.Track.Time != 0.25f)
                throw new InvalidOperationException("Provider settings no longer reach the played animation.");
            state.ReturnNull = true;
            if (invoke.Invoke(null, [state, "missing", true, 7]) != null ||
                state.PlayCount != 2 || state.ReadTrack != 7)
                throw new InvalidOperationException("Missing animations must preserve a null track.");
            Console.WriteLine("Tracked animation adapter behavior passed (arguments, track identity, null result).");
        }
        finally
        {
            ((IDisposable)definition).Dispose();
            context.Unload();
        }
    }

    private static object Property(object value, string name) => value.GetType()
        .GetProperties().First(property => property.Name == name).GetValue(value)!;

    // Audit the real provider, including JIT resolution of the methods that use an animation
    // return value. The previous tests stopped at selection routing and missed this boundary.
    internal static void Audit(string assemblyPath)
    {
        var compatibility = typeof(Entry).Assembly.GetType(
            "STS2SkinChanger.Core.ProviderAssemblyCompatibility", true)!;
        object?[] arguments = [assemblyPath, null, 0, null];
        var rewritten = (bool)compatibility.GetMethod("TryRewriteForCurrentGame")!
            .Invoke(null, arguments)!;
        if (arguments[3] is string failure)
            throw new InvalidOperationException(failure);

        using var stream = (MemoryStream?)arguments[1];
        var context = new AssemblyLoadContext("provider-animation-audit", isCollectible: true);
        try
        {
            var provider = rewritten
                ? context.LoadFromStream(stream!)
                : context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            // Preparing (not running) all ordinary managed methods catches missing member
            // signatures and invalid IL without executing provider initialization or Godot.
            var prepared = 0;
            foreach (var type in provider.GetTypes())
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.ContainsGenericParameters || method.GetMethodBody() == null)
                    continue;
                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    prepared++;
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Provider method could not be JIT-compiled: {type.FullName}.{method.Name}", exception);
                }
            }
            Console.WriteLine($"Provider animation audit passed: {Path.GetFileName(assemblyPath)}; " +
                              $"rewritten={arguments[2]}, JIT methods={prepared}, " +
                              $"SetAnimation returns {typeof(MegaAnimationState).GetMethod("SetAnimation")!.ReturnType.Name}.");
        }
        finally
        {
            context.Unload();
        }
    }
}

public sealed class AnimationTrackProbe
{
    public float Time { get; set; }
}

public sealed class AnimationStateProbe
{
    public string? LastAnimation { get; private set; }
    public bool LastLoop { get; private set; }
    public int LastTrack { get; private set; }
    public int ReadTrack { get; private set; }
    public int PlayCount { get; private set; }
    public bool ReturnNull { get; set; }
    public AnimationTrackProbe Track { get; } = new();
    public void SetAnimation(string name, bool loop, int track)
    {
        LastAnimation = name;
        LastLoop = loop;
        LastTrack = track;
        PlayCount++;
    }
    public AnimationTrackProbe? GetCurrent(int track)
    {
        ReadTrack = track;
        return ReturnNull ? null : Track;
    }
}
