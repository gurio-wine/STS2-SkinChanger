using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace STS2SkinChanger.Core;

/// <summary>Model-only Ready callbacks owned by a custom character, not a skin provider.</summary>
internal static class IntrinsicCharacterPreview
{
    private static readonly ConcurrentDictionary<MethodInfo, bool> VisualCallbacks = new();

    internal static bool Replay(NCreature owner, CharacterModel character, string? groupId)
    {
        var assembly = character.GetType().Assembly;
        if (assembly == typeof(CharacterModel).Assembly ||
            (groupId != null && SkinService.GetSelectedCreatureRuntimeProvider(groupId) != null)) return false;
        var callbacks = Harmony.GetPatchInfo(AccessTools.Method(typeof(NCreature), "_Ready"))?.Postfixes;
        if (callbacks == null) return false;
        var count = 0;
        foreach (var callback in callbacks.OrderByDescending(patch => patch.priority).ThenBy(patch => patch.index)
                     .Select(patch => patch.PatchMethod).Distinct())
        {
            if (!CanReplay(callback, assembly)) continue;
            try
            {
                callback.Invoke(null, [owner]);
                count++;
            }
            catch (Exception exception)
            {
                ModLog.Warn($"角色原生模型预览初始化失败 {character.Id.Entry}/{callback.DeclaringType?.FullName}：" +
                            exception.GetBaseException().Message);
            }
        }
        if (count > 0) ModLog.Info($"选角小模型已衔接角色原生外观初始化：{character.Id.Entry}；步骤={count}。");
        return count > 0;
    }

    internal static bool CanReplay(MethodInfo callback, Assembly characterAssembly)
    {
        if (callback.DeclaringType?.Assembly != characterAssembly || !callback.IsStatic ||
            callback.ReturnType != typeof(void) || callback.GetParameters() is not [{ ParameterType: var parameter }] ||
            parameter != typeof(NCreature)) return false;
        return VisualCallbacks.GetOrAdd(callback, Analyze);
    }

    private static bool Analyze(MethodInfo callback)
    {
        var assembly = callback.DeclaringType!.Assembly;
        var pending = new Stack<MethodBase>();
        var visited = new HashSet<MethodBase>();
        var hasBody = false;
        var hasVisuals = false;
        var instructions = 0;
        pending.Push(callback);
        try
        {
            while (pending.TryPop(out var method))
            {
                if (!visited.Add(method)) continue;
                if (visited.Count > 128 || method.GetMethodBody() == null) return false;
                foreach (var instruction in PatchProcessor.GetOriginalInstructions(method))
                {
                    if (++instructions > 16000 || instruction.opcode == OpCodes.Calli) return false;
                    if (instruction.operand is FieldInfo field &&
                        field.DeclaringType?.Assembly == typeof(CharacterModel).Assembly) return false;
                    // Follow delegated/deferred bodies too. Merely inspecting Postfix would miss
                    // both the actual model injection and delayed gameplay/UI side effects.
                    if (instruction.operand is not MethodBase called) continue;
                    var type = called.DeclaringType;
                    if (type == null) return false;
                    if (type == typeof(NCreature))
                    {
                        hasBody |= called.Name == "get_Body";
                        hasVisuals |= called.Name == "get_Visuals";
                    }
                    if (type.Assembly == assembly) pending.Push(called);
                    else if (!AllowsExternalCall(called, method)) return false;
                }
            }
            // A UI-only callback (counter, health bar, combat subscription...) isn't a model
            // initializer. Unknown call graphs are left alone, never run as full combat Ready.
            return hasBody && hasVisuals;
        }
        catch { return false; }
    }

    private static bool AllowsExternalCall(MethodBase called, MethodBase caller)
    {
        var type = called.DeclaringType!;
        var name = type.FullName ?? "";
        if (type.Assembly == typeof(CharacterModel).Assembly)
        {
            if (name.StartsWith("MegaCrit.Sts2.Core.Bindings.MegaSpine.", StringComparison.Ordinal) ||
                name.StartsWith("MegaCrit.Sts2.Core.Logging.", StringComparison.Ordinal)) return true;
            if (type == typeof(NCreature)) return called.Name is "get_Body" or "get_Visuals" or "get_Entity";
            if (type == typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature))
                return called.Name is "get_IsPlayer" or "get_ModelId";
            if (type == typeof(AbstractModel)) return called.Name == "get_Id";
            if (type == typeof(ModelDb)) return called.Name == "Character";
            if (type == typeof(ModelId)) return called.Name is "op_Equality" or "op_Inequality";
            return false;
        }
        if (type.Assembly == typeof(Node).Assembly)
            return name.StartsWith("Godot.", StringComparison.Ordinal) &&
                   type != typeof(SceneTree) && type != typeof(OS) && type != typeof(Godot.FileAccess) &&
                   type != typeof(DirAccess) && called.Name is not ("GetTree" or "GetTreeRoot" or "GetMainLoop");
        if (!name.StartsWith("System.", StringComparison.Ordinal)) return false;
        if (name.StartsWith("System.Reflection.", StringComparison.Ordinal) && called.Name == "Invoke")
        {
            // Version-adaptive Spine wrappers dispatch animation methods reflectively. This
            // exception does not cover arbitrary reflection from gameplay or node callbacks.
            return caller.GetParameters().Any(parameter => parameter.ParameterType.FullName?
                .StartsWith("MegaCrit.Sts2.Core.Bindings.MegaSpine.", StringComparison.Ordinal) == true);
        }
        return !name.StartsWith("System.IO.", StringComparison.Ordinal) &&
               !name.StartsWith("System.Net.", StringComparison.Ordinal) &&
               !name.StartsWith("System.Threading.", StringComparison.Ordinal) &&
               !name.StartsWith("System.Reflection.Emit.", StringComparison.Ordinal) &&
               !name.StartsWith("System.Diagnostics.Process", StringComparison.Ordinal) &&
               !name.StartsWith("System.Runtime.InteropServices.", StringComparison.Ordinal) &&
               type != typeof(System.Environment) && called.Name != "DynamicInvoke";
    }
}
