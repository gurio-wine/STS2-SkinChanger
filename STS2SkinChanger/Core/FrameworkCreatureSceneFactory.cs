using Godot;
using MegaCrit.Sts2.Core.Nodes.Combat;
using System.Reflection;

namespace STS2SkinChanger.Core;

/// <summary>
/// Preserves the scene-conversion contract used by BaseLib-backed character skin frameworks
/// without taking a compile-time dependency on one particular installed BaseLib snapshot.
/// </summary>
internal static class FrameworkCreatureSceneFactory
{
    private const string FactoryTypeName = "BaseLib.Utils.NodeFactories.NodeFactory`1";

    public static NCreatureVisuals Create(PackedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var factoryType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(FactoryTypeName, throwOnError: false))
            .FirstOrDefault(type => type != null)?
            .MakeGenericType(typeof(NCreatureVisuals)) ??
            throw new InvalidOperationException(
                "所选框架皮肤需要 BaseLib 的 NCreatureVisuals 场景工厂，但当前未找到该工厂。");
        var createFromScene = factoryType.GetMethod(
            "CreateFromScene",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: [typeof(PackedScene)],
            modifiers: null) ??
            throw new MissingMethodException(factoryType.FullName, "CreateFromScene(PackedScene)");

        try
        {
            return createFromScene.Invoke(null, [scene]) as NCreatureVisuals ??
                   throw new InvalidOperationException(
                       "BaseLib 场景工厂没有返回 NCreatureVisuals。");
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw new InvalidOperationException(
                "BaseLib 无法把所选框架皮肤场景转换为战斗角色节点。",
                exception.InnerException);
        }
    }
}
