# Native Framework Functions Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline. User requires master and no subagents/worktrees.

**Goal:** 管理器功能保留，双方显式选择协作，而非替换整个管理器。

**Architecture:** 原注册表仍拥有原 SkinData、存档、配置和呈现回调；选择请求经过现有 SC 热切换事务，再向原注册表发布已完成的本机选择。查询按玩家作用域，发布防回声。

**Tech Stack:** .NET 9, Harmony, formal 0.107.1 / beta 0.111.0.

**Spec:** `docs/superpowers/specs/2026-09-05-framework-cooperation-design.md`

## Global Constraints

- 内测 0.10.3.7；不启动游戏、不上传；提交后退出检查及双版本部署。
- 原管理器启用时不加载同名后备 DLL；未知依赖不豁免。
- 原管理器不标记 [SC]，其所属皮肤仍标记。

## Task 1: Real registry transactions

Files: `Core/FrameworkRegistrySession.cs`, `tests/STS2SkinChanger.FrameworkTests/Program.cs`.

- [x] 红测：`PublishSelection(id, "skin-a")` 后原 `_activeSkins[id] == "skin-a"`、原缓存跟随，且不会再请求 SC；重复同值不保存，切回 default 撤销附属资源。仅替换测试中的 Godot IO/日志边界。
- [x] 实现 `PublishSelection(ModelId id, string? skinId)`：防回声范围内调用原 setter；保留 Load/Save/RefreshSkinCache。注册补齐默认项，finalize 仅清理重复指针并校验已卸载的旧选择。
- [x] 测试真实 setter、Load/Save 路径、重复初始化、invalid index 和作用域查询；观察先失败后通过。

## Task 2: Native UI and callback cooperation

Files: `Core/FrameworkRegistryCooperation.cs`, `Core/FrameworkCompatibilityLayer.cs`, `Ui/ContextualSkinControls.cs`, `Core/ManagedProviderDisplayPolicy.cs`, `Core/ManagedSkinModLoader.cs`.

- [x] 红测：绑定会话后原 Power/Orb 等呈现补丁仍存在；已参与顺序检查的功能管理器不显示 [SC]。
- [x] 删除整包 Unpatch 和自建 AttachControl；记录原 `_Ready` 创建的控件，保留原 Refresh/LoadPreview，pending/非本框架人物时防止相互覆盖。
- [x] 原箭头按目录中的本框架选项提交到已有异步切肤入口；完成后刷新原控件，SC 主动选择取消尚未执行的旧请求。
- [x] `SynchronizeSelections` 只发布 Config 本机选择，不把远端/临时预览保存进管理器；资源挂载完成后才刷新控件。
- [x] 晚到提供者按其实际 SkinDbSetup postfix 精确登记，不重复初始化其它皮肤。原 UI 注入器仅移除尾部硬编码日志查找，不替换创建逻辑。
- [x] 测试版暴露原管理器 5 处旧动画接口调用；通过 `NativeFrameworkAssemblyLoader` 在游戏正常加载时复用原跨版本适配器，不删原功能，不改安装文件；测试确认原路径保留和双版本绑定。

## Task 3: Verification and delivery

- [x] 运行 LogicTests、formal/beta RuntimeTests、formal/beta FrameworkTests、Test-BuildEnvironment；自审 retained callbacks / lifecycle / rollback。
- [x] 更新四处版本和 README；交付使用 master 提交后的正式 Release 重新构建产物。
- [x] 确认游戏未运行；已部署 0.10.3.7。提交后重复部署并核对最终 DLL/adapter/manifest 版本及 SHA-256，不上传。

## 验证边界

本轮未启动或控制游戏。实包测试执行注册表、原缓存与存档流程（只替换引擎 IO/日志边界）、作用域和保留补丁；不等同于界面/战斗已实机验证。游戏内原控件和双方快速切换仍由用户复测。
