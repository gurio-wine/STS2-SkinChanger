# 创意工坊新增 GitHub 来源

已由用户在会话中确认：在现有皮肤创意工坊面板增加“来源”筛选，Steam 为默认，新增 GitHub；GitHub 列表来自 `https://github.com/topics/sts2-sc-mod`；排除本 Mod 仓库 `gurio-wine/STS2-SkinChanger`。作者不需要做任何事：不上传 sc.info、不填表、不改仓库结构。原先准备的 `docs/sc.info` 模板作废并删除。

- 列表来自 GitHub 搜索接口 `GET /search/repositories?q=topic:sts2-sc-mod`，排除本仓库与 fork；身份用 `owner/repo`，显示名优先取包内清单，其次 Release 标题，最后仓库名。
- 单一身份键空间保持不变：条目的 `Id` 仍是 `ulong`。GitHub 条目使用合成 ID `SHA-256("owner/repo")` 取低 62 位后置最高位，确定性、无需分配表；真实工坊 ID 远小于该范围，不会碰撞。`Id → 来源` 由来源注册表解析，仓库名从 GitHub 缓存反查。
- 来源注册表是唯一的来源分派点：元数据、封面地址、安装状态、行内标签、主操作与打开链接都按来源分派。Steam 专用路径（订阅状态、订阅/退订、Steam 详情查询、投稿、多人与热注册）遇到合成 ID 直接跳过，不误当作 Steam 物品。
- 请求预算与缓存：目录缓存在 `user://Gurio.SkinChanger/github/catalog.json`，含每个仓库的搜索字段、Release 信息、识别结果和时间戳。刷新只发一次搜索请求；`pushed_at` 与缓存一致且已有识别结果的仓库不发后续请求；`pushed_at` 变化才查 `GET /repos/{o}/{r}/releases/latest`，tag 一致则复用已识别的包。未认证限额（core 60 次/小时、搜索 10 次/分钟）用尽或返回 403/429 时保留缓存并提示“接口受限，稍后重试”，不清空列表。
- 出网请求沿用现有 HttpClient 约定：`AllowAutoRedirect=false`、超时、响应体上限、主机白名单（`api.github.com`、`github.com`、`objects.githubusercontent.com`）、必带 `User-Agent` 与 `Accept: application/vnd.github+json`；Release 资产下载的重定向逐跳校验主机。
- 元数据来自包本身：`<id>.json` 清单（`name`/`author`/`version`/`has_pck`/`has_dll`/`affects_gameplay`），与游戏加载本地 mod 用的是同一份文件；缺失时用 Release 标题→仓库名、作者用 owner。
- 封面用 `https://opengraph.githubassets.com/1/{owner}/{repo}`，无接口限额；封面主机白名单相应扩展，取不到时沿用现有占位图。
- 识别在安装前完成：Release 资产解压到 `user://Gurio.SkinChanger/github/staging/<合成ID>/`，复用投稿扫描的离线识别路径（读取清单、卡面与卡图包扫描、`SkinCatalog.Build` + `ExportWorkshopCatalog`），结果按 Release tag 缓存。为此给 `SkinCatalog.Build` 增加“调用方指定 provider→合成 ID”的入口：当前 `_workshopSourceIds` 只从工坊目录路径解析，GitHub 包不在该布局下。
- 识别成功后按现有清单体系显示类型/目标标签与“需要重启 / 可免重启”，评估仍由 `WorkshopPackagePolicy` 完成；识别失败（无包、无资源、超出文件数上限、清单损坏）仍列出该仓库，标“未识别”，安装不可用并保留仓库链接。
- 安装：解压到 `<游戏目录>/mods/<包 ID 或仓库名>/`，与本地 mod 布局一致，游戏下次启动加载。目标已存在时按版本判定“已安装 / 可更新”，可更新时提供重新安装。写入走临时目录再改名，失败不留下半个包，不删除既有目录。安装完成后提示需要重启，不提供免重启标记。
- UI：筛选行新增“来源”（Steam / GitHub）。GitHub 模式把订阅筛选换成“全部 / 未安装 / 已安装 / 可更新”，类型、地区、目标、重启标签筛选沿用；排序在 GitHub 模式提供“最近更新 / 星标 / 名称”，Steam 专用排序（订阅数、评论数）在该模式隐藏。筛选、行、封面、翻页与悬停沿用现有实现。
- 15 种语言，兼容正式版与测试版，不主动启动游戏，离线时用缓存列表；不新增登录或 OAuth，不支持自动更新已安装 Mod（只提示可更新）。
- 列表与识别分两段：搜索接口返回后立刻列出全部仓库并标“待识别”，识别在后台按 `pushed_at` 从新到旧逐项进行，每识别完一项就刷新该项标签；首屏不等待任何下载。
- 单次刷新最多查询 30 个 Release，单个资产上限 128 MB；超出或没有 zip 资产的仓库保持“未识别”，再次刷新时按 `pushed_at` 继续。这是为了不把未认证限额（core 60 次/小时）与带宽一次用尽。
- 测试：合成 ID 的确定性与不碰撞、来源解析、搜索与 Release 响应解析、`pushed_at` 未变时不发请求、限流降级、资产解压的路径逃逸防护、安装/更新/已安装判定、未识别分支、两种来源下的筛选与排序成员。网络部分用注入的假 HTTP 客户端；另用真实资产包（SilentSkinMod v0.1.4：`KaguyaSilentRavenSkin.json/dll/pck`）离线跑识别，断言得到 silent 分组。

## 实施后按实测回填（2026-09-13）

- **资产下载主机**：GitHub 现在把 Release 资产 302 到 `release-assets.githubusercontent.com`（实测 `github.com → release-assets.githubusercontent.com → 200`，两跳）。原白名单缺这个主机，会让每一次真实下载都被自己的重定向校验拒绝；已加入白名单，并补了“github.com 302 到 release-assets 后必须成功写出文件”的用例。
- **只认 zip 资产**：`SelectAsset` 只接受 `.zip`（缺省取最大 zip）。标题为 `sts2-sc-mod` 的仓库目前只有两个：本仓库（排除）与 `qbyoffice/SilentSkinMod`（同时提供 zip 与零散 dll/json/pck）。只发零散文件、不发 zip 的仓库会列出但判为“未识别”。实测该仓库最新 Release `v0.1.4-beta` 的 zip 为 `beta.v0.1.4.zip`（16,760,501 字节，≤128 MB 上限），包内清单 `version` 为 `v0.1.4`。
- **路径校验取严**：zip 条目里的绝对路径（`/etc/passwd`）与盘符路径按“拒绝”处理，不做静默去斜杠；清单 ID 只在完全合法时用作目录名，否则回退仓库名（不做“清洗后使用”，避免目录名与包内清单 ID 不一致）。
- **缓存错误**：损坏的目录缓存统一转成 `InvalidDataException`，由调用方保留空列表重新拉取；缓存里的仓库名同样按不可信输入校验。
- **排序方向**：最近更新与星标默认降序、名称默认升序，反向开关翻转；GitHub 排序使用独立的下拉框（Steam 排序项保持不变），方向选择器两种来源共用。
- **行内状态**：GitHub 行在状态栏显示“未安装/已安装/可更新/未识别/刷新中”，重启要求由行内既有的“需要重启”标签表达，避免同一信息重复两遍；未识别行不显示主按钮，只保留“打开仓库”。
- **文案键**：不保留未使用的键（去掉 `Downloading`），15 种语言一一对应。

## 玩家复验后修订（2026-09-14）

- **来源下拉框**：原来借用通用分页下拉（空值项承载 Steam 标签，并把当前值作为“上下文项”追加），在只有两个取值的来源上表现为三项，其中两项都写 `GitHub`，而默认选中的那一项内容其实是 Steam。改为专用两项下拉（`SkinSources.Options`，Steam 在前），标签统一由 `SkinSources.Name` 给出，越界索引回落到 Steam；补了“两个来源名称在 15 种语言里必须互不相同”的用例。
- **打开仓库**：卡片点击本来就是打开仓库，行内单独的“打开仓库”按钮删除（文案键 `OpenRepo` 一并移除），空出来的第二个按钮改作“卸载”。
- **安装后的按钮**：原来已安装时仍显示“重新安装”（文案键 `Reinstall` 移除）。现在未安装=安装、可更新=更新（右侧“卸载”移除旧版本）、已是最新=卸载。卸载只删除本 Mod 安装过、且目录内仍有该清单 `version` 的目录（`GitHubInstallPolicy.UninstallTarget`），玩家手工放置的同名目录不会被删；文案键新增 `Uninstall`，15 种语言补齐。
- **默认排序**：GitHub 模式默认按星标（此前按最近更新），下拉项顺序改为星标、最近更新、名称。
- **悬停简介**：原来悬停走 Steam 简介路径，合成 ID 读不到元数据，显示“加载失败”。现在 GitHub 行取仓库 README：`GET /repos/{o}/{r}/readme` 配 `Accept: application/vnd.github.raw`（README 文件名与默认分支由 GitHub 解析；raw 主机要求精确文件名，故不用），`GitHubReadmePolicy.ToExcerpt` 去掉徽章、图片、HTML、代码块、表格符与列表编号并压成一段（上限 800 字），首次悬停读取一次后随目录缓存保存（缓存格式升到 2，旧格式整份丢弃重拉）。读取失败不缓存，回落仓库描述再到“暂无简介”。简介写入不递增 `Revision`，否则会触发列表重建、把正在悬停的卡片收起来。
- **安装过滤**：GitHub 模式的“未安装/已安装”原来问的是工坊安装状态，合成 ID 永远为 false，导致已安装的 GitHub 皮肤仍留在“未安装”里；改为按来源分派。
- **识别会下载资产（事实说明，未改）**：识别必须拿到包才能给出类型/对象标签，所以每次刷新会把待识别仓库的 Release zip 下到 `user://Gurio.SkinChanger/github/<合成ID>/`（单次刷新最多 30 个仓库、单资产 ≤128 MB），多次刷新会把主题内所有仓库逐步下完并保留在缓存里；安装复用该缓存，重复安装与更新不再下载。
