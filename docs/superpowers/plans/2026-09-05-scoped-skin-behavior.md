# 原皮肤设置与行为归属 Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline. 用户明确要求 master 直接修改，不创建工作区、不派子智能体。

**Goal:** 保留皮肤及其管理器的设置，将可验证的角色皮肤适用性判断限定在 SC 当前选择的对象上。

**Architecture:** 原设置保存、UI、动画判断保持不变。对已接管 DLL 中同时声明角色类型、皮肤资源和 `AppliesTo(CharacterModel/Player)` 的配置类型，安装“原判断 AND 当前选择归属”的后置门控。原 Player 调用中的 CharacterModel 判断继承玩家上下文，异常也恢复上下文。未知合同和独立 UI 不扫描接管、不猜测。

**Tech Stack:** .NET 9、现有 Harmony、正式版 0.107.1 / 测试版 0.111.0。

**Spec:** 本文的范围与实包证据即本轮设计；用户已授权自行决定并实现。

## 约束与证据

- CZN 各角色 DLL 嵌入 CznCore；不是共享 UI DLL 代为执行所有角色行为。
- 实包 `CharacterSkinProfile.AppliesTo(CharacterModel)` 只有类型判断；Player 重载调用此方法。SC 去掉 Harmony 补丁不会撤销非 Harmony 订阅，保留的设置也可能再次调用这些入口。
- 不匹配 Mod ID、工坊 ID、CZN 命名空间；不以一个 `AppliesTo` 方法就判断是皮肤。
- 不覆盖原作者 false 判断，不修改设置值，不反复初始化，不添加逐帧扫描或资源重载。
- 只处理明确类型和资源声明的角色合同；任意全局写入、共享宿主无法确认实际提供者的对象不声称已自动隔离。
- 不修改卡牌稀有度、骨骼、位移、商店流程；保留 Merchant2Cute 已验证实现。
- 不启动游戏；版本 0.10.3.5，双版本离线验证，退出后本地部署；不上传。

## Task 1：合同发现与运行时门控

**Files:** 新建 `Core/ScopedSkinBehavior.cs`、`tests/STS2SkinChanger.RuntimeTests/ScopedSkinBehaviorTests.cs`；修改 RuntimeTests/Program.cs。

**Interfaces:** `SkinBehaviorContract.Find(Type): MethodInfo[]`；`ScopedSkinBehavior(string, Func<Assembly, CharacterModel, Player?, bool?>)`；`Install(Assembly): int`。null 表示未知，不改变原判断。

- [x] 测试先行：通过反射检测新实现缺失；真实 Harmony 修饰测试 profile，断言未选中 false、选中 true、原设置 false 仍 false、延迟调用重新检查选择、其它角色无串用、未知合同不修改。
- [x] 多人测试：两个相同角色的 Player 有不同选择；Player -> CharacterModel 嵌套仍按传入 Player；抛错后不泄漏作用域。
- [x] 实现结构发现（ProfileId、TargetCharacterType、BodyTexturePath/BodySkeletonDataPath）、一次安装和原返回值合取；不执行属性 getter 或初始化器进行发现。
- [x] 分别运行 RuntimeTests；使用实包审计入口检测已安装的 CZN 角色 DLL，独立 UI 仍走原加载。

## Task 2：绑定现有选择服务并验证

**Files:** 修改 `Core/ManagedSkinModLoader.cs`、`Core/SkinService.cs`、README、四个版本标记。

**Interfaces:** `SkinService.IsCharacterBehaviorSelected(Assembly, CharacterModel, Player?): bool?`，使用已存在的玩家选择作用域及角色运行时来源解析；未知组返回 null。

- [x] 在 RegisterProviderAssembly 后一次安装，不向共享 UI 宿主自动归属其它皮肤。
- [x] 测试已接管注册入口真的安装门控；不能只测试孤立策略。
- [x] 验证正式版和测试版 RuntimeTests、LogicTests、构建环境；对真实 CZN DLL 仅进行反射合同审计，不调用 Mod 初始化器。
- [x] 自审 diff，递增内测版本；发布产物使用默认正式版引用的 Release/AnyCPU。提交并按退出检查部署。

## 结果

0.10.3.5：正式/测试版 RuntimeTests、LogicTests、构建环境检查通过，Release 零警告零错误。先看到新能力缺失的红测；另临时移除真实注册接线，注册路径测试确实失败，恢复后双版本通过。测试覆盖别名来源、现有多人选择覆盖和缺失来源，不缓存选择结果，只缓存角色类型所属分组。

实包合同审计通过：ChizuruIroncladSkin、TressaSilentSkin、HeidemarieRegentSkin、SerenielDefectSkin、NarjaNecrobinderSkin、MeirinWatcherSkin。Merchant2CuteII 旧接口审计仍通过，CznStyleUI 未被商人适配器改写。

未启动游戏、未上传工坊。真实画面仍由用户验证，离线通过不代表所有原作者代码已被隔离。
