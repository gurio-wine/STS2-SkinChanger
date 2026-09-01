using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger;
using System.Reflection;

var patchType = typeof(Entry).Assembly.GetType(
                    "STS2SkinChanger.Core.FrameworkRelicPackedIconPatch") ??
                throw new InvalidOperationException("找不到框架遗物路径补丁类型。");
var relicGetters = patchType.GetMethod(
                        "RelicGetters",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ??
                    throw new InvalidOperationException("找不到框架遗物 getter 目标枚举。");
var targets = ((IEnumerable<MethodBase>?)relicGetters.Invoke(
                   null,
                   ["PackedIconPath"]))?
              .ToArray() ?? [];
var baseGetter = AccessTools.PropertyGetter(typeof(RelicModel), "PackedIconPath") ??
                 throw new InvalidOperationException("游戏缺少 RelicModel.PackedIconPath getter。");

if (!targets.Contains(baseGetter))
{
    throw new InvalidOperationException(
        "框架遗物路径补丁漏掉了 RelicModel 基类 getter；继承原版路径的遗物会被通用后置补丁恢复成原图。");
}

Console.WriteLine("Skin Changer runtime patch target tests passed.");
