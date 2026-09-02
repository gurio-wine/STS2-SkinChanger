using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using STS2SkinChanger;
using System.Reflection;

var managedModListPatchType = typeof(Entry).Assembly.GetType(
                                  "STS2SkinChanger.Ui.ManagedModListNamePatch") ??
                              throw new InvalidOperationException("找不到 Mod 列表 [SC] 标记补丁类型。");
var managedModListPostfix = managedModListPatchType.GetMethod(
                                "Postfix",
                                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ??
                            throw new InvalidOperationException("找不到 Mod 列表 [SC] 标记后置补丁。");
var managedModListPriority = managedModListPostfix.GetCustomAttributesData()
    .FirstOrDefault(attribute => attribute.AttributeType == typeof(HarmonyPriority))?
    .ConstructorArguments.FirstOrDefault().Value;
if (managedModListPriority is not int priority || priority != Priority.First)
{
    throw new InvalidOperationException(
        "[SC] 标记必须作为最先执行的后置补丁写入 Mod 行；否则正式版中不兼容的第三方列表补丁抛错后，所有标记都会消失。");
}

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

var combatRoomCreate = typeof(NCombatRoom).GetMethod(
    nameof(NCombatRoom.Create),
    BindingFlags.Static | BindingFlags.Public,
    binder: null,
    types: [typeof(ICombatRoomVisuals), typeof(CombatRoomMode)],
    modifiers: null);
if (combatRoomCreate == null)
{
    throw new InvalidOperationException(
        "当前游戏版本缺少 NCombatRoom.Create(ICombatRoomVisuals, CombatRoomMode)，" +
        "无法在创建战斗场景前收窄怪物皮肤运行期。");
}

var skinServiceType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.SkinService") ??
                      throw new InvalidOperationException("找不到 SkinService 类型。");
var focusRuntimeProviders = skinServiceType.GetMethod(
    "FocusRuntimeProviderBehaviorsOnGroups",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    types: [typeof(IEnumerable<string>), typeof(bool), typeof(string)],
    modifiers: null);
if (focusRuntimeProviders == null)
{
    throw new InvalidOperationException(
        "缺少按可见分组收窄运行期提供者的方法；角色专用范围仍会让商人、先古和怪物代码常驻。");
}

Console.WriteLine("Skin Changer runtime patch target tests passed.");
