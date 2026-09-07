# 工坊热加载能力与重启提醒

> **For agentic workers:** Use superpowers:executing-plans inline. User requires master, no worktrees or subagents.

**Goal:** 扩展现有安全热注册，并在订阅前后明确需要重启的情况。
**Architecture:** 包检查返回原因，不按 DLL/依赖字段一刀切；完整资源接管仍为发布事务的必要条件。只读 DLL 分析不执行第三方初始化器。内置清单携带已审计版本的重启提示，下载后重新检查实包。失败提醒使用游戏原生弹窗并排队，关闭浏览器后仍可提醒。
**Tech Stack:** C#/.NET 9、Godot、Steamworks.NET、System.Reflection.Metadata。
**Spec:** 用户已确认本轮方案；继承 `../specs/2026-09-07-skin-workshop-design.md`。

## Global Constraints

- 默认正式版 v0.107.1 Release；测试版 v0.111.0 ReleaseBeta；AnyCPU。
- 15 种语言；沿用主题；不启动游戏、不上传工坊；内测 0.10.11.5，提交并在游戏退出后部署。
- 不在运行中自动启动未知 DLL，不清理用户订阅，不改变已有选择/优先级；不把版本不兼容说成重启即可修复。

## Tasks

- [x] 包能力检查：`WorkshopPackagePolicy.Assess(directory, gameVersion, loadedDependencies)` 返回描述符及原因；前置已满足/版本匹配可继续，无 DLL 和经只读检查可省略初始化的 DLL 进入完整资源检查。其它代码拒绝热注册并说明原因。用临时包及真实编译 DLL 验证正反例。
- [x] 接入 `SkinService.Workshop` 的原子注册；保留源文件变化、重复 ID、完整资源/脚本依赖检查，失败不修改全局目录。扩展工坊清单导出工具，只遍历现有收录 ID，记录代码审计结果；未知保持下载后检查。
- [x] 条目增加重启提示；订阅完成后按检查结果排队原生提示，一次订阅一次，原生新 Mod 提示不重复，其它 Mod 提示不吞掉。订阅前提示不能跳过下载后的实包检查。
- [x] 所有新增文本翻译 15 种语言。补充政策测试、通知队列去重/状态测试及原生弹窗调用契约。
- [x] 正式/测试版回归，内测号、部署及 SHA-256 核对；随本轮代码提交。不把离线检查当作游戏显示验证。

## 验证记录

- LogicTests、正式版/测试版 RuntimeTests 全套，以及补充后的两版本 `--test-workshop` 均通过；Release 构建 0 警告、0 错误；Test-BuildEnvironment 保持 AnyCPU。
- 原有 82 个 ID/标签保持不变；开发侧本地审计标记 57 个首次加载需重启，其他项不提前承诺免重启。
- DLL 证明范围限于可省略的空启动/日志等简单逻辑，未知回调、补丁、脚本和未完整接管资源仍交给重启加载。不是通用运行时第三方代码加载器。
- 已退出游戏后部署 0.10.11.5 到 workshop/content、Steam 工坊目录和正式版快照缓存。三处 DLL SHA-256：`428356A28F81F7E77D86636608476B24D0E3F0F2B72DE35EE9AB73ED8A2F95A7`。
- 未启动游戏、未实际订阅新物品、未上传工坊。弹窗视觉、实际 Steam 下载和新皮肤热切换需用户实机确认。
