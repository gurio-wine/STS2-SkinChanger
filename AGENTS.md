# Skin Changer 开发约定

## 当前环境

- 主环境为 Windows 原生 PowerShell。仓库位于 `C:/Users/gurio/Documents/ChatGPT/STS2-SkinChanger`。
- 使用 Windows 的 `dotnet` 构建和测试。SDK 版本由 `global.json` 约束。
- `Directory.Build.props` 集中管理游戏引用：默认正式版 `v0.107.1`；测试版 `v0.111.0` 必须显式传入 `GameAssemblyDir`。不能因为正式版文件缺失而自动改用测试版。
- 代码与配置使用 LF；不要批量改写整库换行。

## 发现与修改

- 优先使用 codebase-memory-mcp 的图查询；当前 Windows 索引名为 `STS2-SkinChanger`。命名、增量索引与变更检测的注意事项见 [docs/agents/codebase-memory.md](docs/agents/codebase-memory.md)。
- MCP 未重新连接时，可用 `codebase-memory-mcp cli` 调用同一套工具。工具不可用、索引排除或解析不完整时再用 `rg`，不要运行 CodeGraph。
- 定位、调用链、影响面先走图查询，不要习惯性先 `rg`；改完代码用 `detect_changes` 自查影响面。只有目标在索引之外（游戏 `sts2.dll`、框架、第三方 Mod 包等二进制）时才用 `rg` 或反编译。
- 直接在 master 修改；不创建隔离工作区，尽量不派子智能体。
- 排查中已确认原因的错误直接修复并验证，不再单独停下来询问是否修复；涉及新的设计取舍、额外权限或无法确认原因时再说明。
- 改完后提交，并提升四段内测版本：未上传时只加第四位，第三位只在实际上传工坊时提升，并与三段公开版本一起更新。四段内测标记需同步 csproj、Entry、SkinChanger.json 和 workshop/workshop.json。
- 不控制游戏做实机测试，由用户检查画面。构建和离线测试成功不能代表游戏表现已修复。

## 验证与发布

- 常用命令见 README 的“构建”和“开发检查工具”。发布产物必须来自默认正式版引用的 `Release` 构建，保持 `AnyCPU`。
- 测试版验证用 `ReleaseBeta` 配置，避免覆盖正式版发布产物。
- 部署前确认游戏已退出；核对构建产物、workshop/content、测试版工坊目录和正式版快照缓存的 DLL 版本及 SHA-256。
- 仅用户要求时上传；先 Steam 工坊，再推 GitHub。工坊标题/描述变化须同步全部 15 种语言。
- 游戏内工坊清单的重启标签核验规则见 [docs/agents/workshop-catalog.md](docs/agents/workshop-catalog.md)。
- 不向全局 Python 安装项目依赖；本项目核心构建不需要 Python。

## 沟通

- 回答保持简短：只给结论、当前状态和用户需要做的动作；实现细节、排查过程、改动文件清单与验证流水不展开，用户问了再讲。
