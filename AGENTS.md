# Skin Changer 开发约定

## 当前环境

- 当前主环境为 Windows 原生 PowerShell。仓库位于 `C:/Users/gurio/Documents/ChatGPT/STS2-SkinChanger`。
- 历史 WSL 会话可能携带错误 cwd；每次终端调用显式指定上述真实目录，不要把 `/mnt/c/...` 拼接到 Codex 安装目录，也不要为错误目录创建占位副本。
- 使用 Windows 的 `dotnet` 构建和测试。SDK 版本由 `global.json` 约束；旧 WSL 环境保留，但不作为默认工具链。
- `Directory.Build.props` 集中管理游戏引用：默认正式版 `v0.107.1`；测试版 `v0.111.0` 必须显式传入 `GameAssemblyDir`。不能因为正式版文件缺失而自动改用测试版。
- Windows/WSL 还原缓存分别位于 `obj/windows` 与 `obj/unix`，不要相互复制。代码与配置使用 LF；不要批量改写整库换行。
- Windows Git 对本仓库启用 `core.longpaths=true`。历史 Codex 检查点引用可能超过 Windows 默认路径长度；遇到此类报错应检查仓库级长路径设置，不删除检查点或误判为对象损坏。

## 发现与修改

- 优先使用 codebase-memory-mcp 的图查询；当前 Windows 索引名为 `STS2-SkinChanger`。索引时明确传入仓库绝对路径，不索引整个用户目录。
- MCP 未重新连接时，可用 `C:/Users/gurio/.local/bin/codebase-memory-mcp.exe cli` 调用同一套工具。工具不可用、索引排除或解析不完整时再用 `rg`，不要运行 CodeGraph。
- 直接在 master 修改；不创建隔离工作区，尽量不派子智能体。
- 改完后提交，并提升四段内测版本。同步 csproj、Entry、SkinChanger.json 和 workshop/workshop.json 的内测标记；上传时再提升三段公开版本。
- 不控制游戏做实机测试，由用户检查画面。构建和离线测试成功不能代表游戏表现已修复。

## 验证与发布

- 常用命令见 README 的“构建”和“开发检查工具”。发布产物必须来自默认正式版引用的 `Release` 构建，保持 `AnyCPU`。
- 测试版验证用 `ReleaseBeta` 配置，避免覆盖正式版发布产物。
- 部署前确认游戏已退出；核对构建产物、workshop/content、测试版工坊目录和正式版快照缓存的 DLL 版本及 SHA-256。
- 仅用户要求时上传；先 Steam 工坊，再推 GitHub。工坊标题/描述变化须同步全部 15 种语言。
- 不向全局 Python 安装项目依赖；本项目核心构建不需要 Python。
