namespace STS2SkinChanger.Core;

/// <summary>
/// Godot's ScriptManagerBridge keys scripts by resource path alone and inserts them with
/// <c>Dictionary.Add</c>, so *any* repeat of a path aborts the whole <c>LookupScriptsInAssembly</c>
/// call - even the identical class out of the identical file. Complete skin packs register their
/// scenes twice by design: Skin Changer registers them before a private <c>PackedScene</c> can be
/// instantiated, and the pack's own initializer then scans its assembly again. These rules decide
/// which repeats are harmless; everything else keeps Godot's own judgement.
/// </summary>
internal static class GodotScriptPathRegistrationPolicy
{
    private static readonly string[] DuplicateMarkers =
    [
        "same key",
        "already been added",
        "相同的键",
        "已添加"
    ];

    /// <summary>
    /// True when the incoming declaration repeats a path Godot already mapped to an equivalent
    /// declaration: the identical type, or the same compiled module reached through another copy
    /// (a second install, another load context, or an in-memory rewrite). The map stores one type
    /// per path and equivalent copies answer that path identically, so the first one stays.
    /// </summary>
    public static bool IsIdempotentRepeat(Type? existing, Type? incoming)
    {
        if (existing is null || incoming is null)
        {
            return false;
        }

        if (existing == incoming)
        {
            return true;
        }

        try
        {
            return existing.Module.ModuleVersionId == incoming.Module.ModuleVersionId;
        }
        catch (Exception)
        {
            // Modules without a readable version id (dynamic or resource-only) cannot be proven equal.
            return false;
        }
    }

    /// <summary>
    /// Godot reports a path it already holds as a duplicate-key <see cref="ArgumentException"/>.
    /// When that is raised while a skin pack registers its own scenes it must not be allowed to end
    /// the pack's visual session, which the player otherwise sees as a skin without a model.
    /// </summary>
    public static bool IsHarmlessDuplicate(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is not ArgumentException)
            {
                continue;
            }

            foreach (var marker in DuplicateMarkers)
            {
                if (current.Message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The offending <c>res://</c> path out of Godot's message, so a report can name the exact
    /// declaration instead of only quoting a dictionary error.
    /// </summary>
    public static string? DuplicatePath(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is not ArgumentException)
            {
                continue;
            }

            var message = current.Message;
            var start = message.IndexOf("res://", StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var end = start;
            while (end < message.Length && IsPathCharacter(message[end]))
            {
                end++;
            }

            if (end > start)
            {
                return message[start..end];
            }
        }

        return null;
    }

    private static bool IsPathCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '/' or '_' or '.' or '-' or ':';
}
