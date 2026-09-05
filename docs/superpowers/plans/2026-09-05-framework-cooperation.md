# Framework Cooperation Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline; user requires master, no worktrees or subagents.

**Goal:** 原管理器可协作，未启用原管理器时保留内置接口后备。
**Architecture:** 原宿主优先且只加载一个 CLR 身份；注册表读写桥接 SC；原设置委托参与声明式资源过滤，保留原生初始化/设置服务并隔离重复呈现。
**Tech Stack:** .NET 9、已有 Harmony、正式版 0.107.1 / 测试版 0.111.0。
**Spec:** `docs/superpowers/specs/2026-09-05-framework-cooperation-design.md`

## 全局约束

只读第三方实包，不写原 DLL/PCK/配置；不执行游戏实测；无原前置仍可运行已验证声明式皮肤；未知依赖不豁免。内测版本 0.10.3.6。

## Task 1：装载选择与原注册表会话

Files: `Core/OptionalSkinFrameworkPolicy.cs`, `Core/FrameworkCompatibilityLayer.cs`, `Core/FrameworkRegistrySession.cs`, `Core/ManagedSkinModLoader.cs`, 新增 `tests/STS2SkinChanger.FrameworkTests`。

- [x] 先更新策略红测：原宿主可用时 `CanInstallCompatibilityAssembly(..., true)` 必须 false；缺失宿主而声明/闭包齐全时仍 true。
- [x] 独立测试进程不引用内置 DLL；通过反射加载原 DLL，安装 `FrameworkRegistrySession`，用真实 SkinData/RegisterConfig 和可控 SC 选择委托测试。
- [x] 注册表 session 构造接受 Assembly、`Func<ModelId,string?>` 读取和 `Action<ModelId,string>` 请求。真实 GetActiveSkin/IsUsingSkin 经过桥接；setter 不写旧存档，查询不依赖全局活动指针。
- [x] 装载后绑定原宿主，Bundled/Native 两个状态分开，原框架错误不退成同名双加载。

## Task 2：选择、配置与原控制入口

Files: `Core/FrameworkRegistryCooperation.cs`, `Core/SkinService.cs`, `Core/FrameworkSkinRuntimePatches.cs`, `Ui/ContextualSkinControls.cs`。

- [x] 皮肤登记按原 ID/角色匹配 SC 框架 contract；切肤使用已有选角流程，不直接修改背景/头像；无映射不写另一份选择。
- [x] 原设置读取按当前 contract 的具体 SkinId，不从全局其它角色抓配置；关闭卡框/能量/手部/充能球时过滤对应声明资源。
- [x] 原皮肤初始化器只对已确认的原框架会话恢复，确保 BaseLib 设置注册执行；仍不运行其它被隔离框架初始化器。
- [x] 原框架注册回调可以重复触发，但不能重置已登记其它皮肤；原作者循环选择不能死递归到 SC 同步。

## Task 3：双版本验证和交付

- [x] 正式/测试版 RuntimeTests、LogicTests、原 DLL 独立进程测试；缺失前置路径回归。
- [x] 自审默认皮肤、切走/切回、配置关闭、无数据/无效索引、重复加载与原管理器先加载的情况。
- [x] 更新 README、四处版本标记；提交，退出检查后部署并核对四份 DLL/manifest；不上传。

## 本轮记录

0.10.3.6：实现双路径和原注册表协作；独立实 DLL 注册表/控制补丁测试、LogicTests、正式/测试版 RuntimeTests 及构建环境检查通过。原控制场景复用，移除其硬编码调试路径与重复小模型预览；作者设置过滤同时覆盖资源映射和缓存身份。没有启动游戏、执行提供者初始化器测试或上传工坊，实际界面和局内表现待用户确认。
