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

## 改成 sc.info 信息码（2026-09-13 晚，玩家第 3 轮反馈）

玩家否定了上面那条“刷新即下载”：没有 sc.info 的皮肤归入“未识别”，安装按钮换成“扫描”，扫描时才下载并生成信息码（不安装、扫完删缓存），有 sc.info 的仓库直接读信息码取信息。

- **识别只读仓库根目录的 `sc.info`**：`https://raw.githubusercontent.com/{owner}/{repo}/HEAD/sc.info`（raw 主机，静态小文件，不占接口额度，`HEAD` 自动落到默认分支；文件上限 64 KB，404 视为没有这个文件）。文件内容就是扫描产出的 `SCM3` 信息码，`GitHubInfoPolicy.Read` 用与投稿链路同一个 `WorkshopSubmissionCodec.Read` 读回，并要求码里的工坊 ID 等于该仓库的合成 ID。失败原因各有短标记：没有文件 `missing`、只有说明文字 `no-code`、贴了别的仓库的码 `id-mismatch`、校验损坏/版本未知/超限分别是 `checksum`/`version`/`too-large`。**刷新不再查询 Release、不再下载任何包。**
- **格式 3 与安装台账**：目录缓存升到格式 3（识别语义与字段全变，旧格式整份丢弃重读）。新增 `user://Gurio.SkinChanger/github_installs.json`（格式 1）记录“仓库 → 目录 / 清单 ID / 名称 / 版本”，它是“已安装”与卸载归属的唯一依据，且**不随缓存换格式丢失**——缓存丢的是包信息，归属丢了就再也删不掉自己装进去的目录。
- **筛选**：保留“全部/未安装/已安装/可更新”，新增“未识别”，默认值从“全部”改为“未安装”。未安装包含还没识别的仓库（否则一个已识别皮肤都没有时默认视图整片空掉），未识别只看有没有可读的信息码，与安装状态无关。
- **按钮**：未识别且未安装只有“扫描”；已安装=卸载、可更新=更新（右侧另有卸载）、已识别未安装=安装。作者后来删掉 sc.info 时，已安装的目录仍给卸载——这条优先于“未识别只给扫描”，否则那种皮肤再也删不掉。
- **扫描**：下载 Release zip → 解压 → 复用投稿扫描路径识别 → `WorkshopSubmissionCodec.Encode` 生成 `SCM3` 码 → 弹窗显示（每段一个只读输入框 + 复制 + 打开仓库 + 一句“把信息码存成仓库根的 sc.info”）→ 关闭即丢、不落盘；无论成功失败，`github/<合成ID>/` 缓存整份删除。
- **安装按需查询 Release**：只有本机已安装的仓库会在刷新时顺带查一次 Release（用于判断“可更新”，`ReleaseTag` 进缓存），其余仓库在点安装/更新时才查。下载→解压→选包（根清单优先，其次与仓库同名的子目录，`GitHubPackageLayout`）→整包拷进 `mods/<清单 ID 或仓库名>/`→写台账。纯素材包（没有清单）也能安装，此时台账条目就是归属证明（`UninstallTarget` 对空清单 ID 只校验目录合法且存在）。
- **长操作可见**：扫描/安装自带 0.25 秒轮询，让“扫描中…/安装中…”真的显示出来（此前只在操作结束后才刷新一次，中途文本不变）。
- **测试**：`sc.info` 读回（含说明文字与 Markdown 代码块、多段码拼回）、`missing`/`no-code`/`id-mismatch`/`checksum`/`version`/`too-large` 六种拒绝、台账往返与非法片段/陈旧格式拒绝、筛选默认值与四种匹配、`raw` 主机白名单与 404 返回 null、超限 `sc.info` 在读进内存前拒绝、选包顺序、空清单 ID 的卸载边界。

## 筛选、名字与排序复验（2026-09-14，玩家第 4 轮反馈）

- **未识别独立成栏**：未识别的仓库不再出现在“未安装/已安装/可更新”里——`GitHubSkinFilter.Matches` 的三个安装状态都先要求已识别。没有信息码的仓库没有皮肤可装，这三个视图只列真能安装的仓库；作者删掉 `sc.info` 后，已装的目录也只留在“未识别”里（上一轮那条“未安装也要包含未识别”的取舍被玩家否决并撤销）。
- **未识别视图不看类型/目标筛选**：未识别的仓库没有类型可筛，而类型筛选是跨来源共用的，留着别的来源选中的类型会让这一栏必然空掉（`GitHubSkinFilter.IgnoresKindFilter`）。只有“未识别”这一栏忽略类型/目标，其它视图不变。
- **未识别的名字**：没有信息码的仓库没有皮肤名，卡片改显示 `@作者/仓库`（`GitHubSkinPolicy.CardName` / `Handle`），不再只显示光秃秃的仓库名；名称排序的右侧指标列与扫描弹窗标题都用同一个写法（已识别的仓库在指标列里仍显示仓库名，也就是名称排序的键）。
- **默认排序的标签**：GitHub 排序下拉此前按索引选择条目，而条目按“星标/最近更新/名称”的顺序添加、枚举却从“最近更新”开始，于是列表实际按星标排序、下拉标签却显示“最近更新”。改为按条目 ID 选择（`WorkshopOptionSelectionPolicy.IndexOfId`），排序下拉、GitHub 排序下拉与方向下拉统一走这一条路径。
- **测试**：安装三栏都不收未识别（含“已装的目录随 sc.info 消失而只留未识别”）、`IgnoresKindFilter` 只对未识别为真、`CardName` 的未识别与已识别两分支、指标列跟随卡片名、按 ID 选择（含 GitHub 枚举与条目顺序不一致的真实映射、ID 不存在时返回 -1 不落到第一项）。

## 重启按钮与卡片名字复验（2026-09-14，玩家第 5 轮反馈）

- **安装后的“需要重启”按钮**：写进 `mods/` 的包只由游戏启动时扫描，安装/更新完成的那一刻，运行中的游戏并没有加载它。此前“已安装”状态下卡片直接给“卸载”，玩家要求像 Steam 那样给重启按钮：`GitHubItemActions.Primary` 在“已安装 + 本次会话还没加载过”时返回 `Restart`，按钮文案直接用 Steam 自己的 `WorkshopTextKey.Restart`（15 种语言不新增翻译、措辞完全一致），带强调色，点击走与 Steam 完全相同的 `RestartGame`（先置 `_restartPending` 并轮询刷新按钮，再 `OS.SetRestartOnExit(true)` + `NGame.Instance.Quit()`）。状态行在这一状态下留空，与 Steam 一致，不把“需要重启”说两遍。第二个按钮改由 `GitHubItemActions.ShowsUninstall` 决定：主按钮是“更新”或“需要重启”时都保留“卸载”，主按钮本身就是“卸载”时不重复。
- **重启优先于更新**：磁盘上那份连一次都没加载过时，即使作者已经发了新版本，主按钮仍是“需要重启”（重启之后自然变成“更新”）。`RestartPending` 只在本次会话内有效：安装/更新成功时加入、卸载时移除、目录被玩家删掉时由 `RefreshInstalls` 按台账清掉；游戏重启后同一目录即为“已加载”，按钮回到“卸载/更新”。
- **卡片名字只用仓库名**：包内的皮肤名只有装完才读得到，用它会让卡片在安装那一刻改名；`@作者/仓库` 又把作者名带进皮肤列表。现在 `GitHubSkinService.Title`、名称排序的指标列与扫描弹窗标题统一用仓库名，`GitHubSkinPolicy.CardName` / `Handle` 随之删除。
- **台账不再保存显示名**：`GitHubInstall` 只剩 `Folder`/`PackageId`/`Version`，格式仍为 1（旧文件里多出来的 `PackageName` 被 JSON 反序列化忽略，读取不受影响）。副作用：用 1.0.8.16 及更早的构建读这份台账时，缺名字字段的条目会被旧校验判为无效而丢弃（那台机器会显示“未安装”，目录仍在，重装写回同一个目录名）。
- **测试**：五种动作的路由表（含“重启优先于更新”“作者删掉 sc.info 后已装目录仍能卸载或重启”“什么都没装时残留的重启标记不生效”）、`ShowsUninstall` 的三种假值、台账属性表只有三个字段、名称排序的指标列在识别与未识别两种状态下都是仓库名。

## 安装台账没落盘与运行中无法替换（2026-09-14，玩家第 6 轮反馈）

玩家实测：安装后按需要重启按钮重启游戏，卡片显示“未安装”，再点安装报“安装失败”。日志与磁盘给出两个各自独立的原因。

- **根因一：配置文件名白名单漏了新增的两个文件。** `SkinChangerStorage.ConfigurationFile` 只接受一份固定的旧文件名列表（`skin_changer.json` 等五个），而 `github_skins.json` 与 `github_installs.json` 是后来加的调用方，从没进过这份列表，于是路径解析每次都抛 `ArgumentException`：`SaveCatalog`/`SaveLedger` 各自吞掉异常只记一条警告，两个文件**从未写到磁盘**。后果是目录缓存每次都重新拉取（此前被误认为正常），以及最要命的：安装归属只活在内存里，重启后台账为空 → 卡片显示“未安装”。修法是**不再用名单**，而是校验文件名本身（单层文件名、`.json` 结尾、长度与字符集安全、禁止路径分隔与上级目录），新增配置文件不可能再忘记登记。名单本身只留给旧文件迁移用，并改名为 `LegacyConfigurationNames`。
- **根因二：运行中的游戏锁住已加载的皮肤目录。** 未识别→安装的那一次写入能成功（目录本来不存在），但重启后该皮肤已被游戏加载：它的 `.dll` 被程序集加载器打开、`.pck` 被本 Mod 的资源包挂载打开，Windows 既不允许删除也不允许**重命名**这样的目录（实测：目录内任一文件被打开时 `Directory.Move` 直接 `Access to the path ... is denied`，与日志一致）。旧代码用 `mods/.<目录>.installing` 暂存后把旧目录改名让位，改名失败就抛出“安装失败”，而且那句改名在 `try/finally` 之外，20 MB 的暂存副本永远留在 `mods/` 里——那会被游戏启动扫描当成第二个同 ID 皮肤。
- **修法：把这一步交给退出助手，与 Steam 的说法一致。** Steam 取消订阅后原话是“本次已加载资源保留，文件由 Steam 在退出后移除”，GitHub 源照做：`GitHubModsPolicy` 定义两种只在进程结束后执行的步骤——`Replace`（把暂存包换进目录）与 `Remove`（删掉本 Mod 写过的目录），按仓库记账（`GitHubInstallLedger` 格式 2，新增 `Pending` 段：目录 + 暂存路径，替换步骤要求与安装条目的目录一致，否则读回时丢弃）。`GitHubSkinService.InstallAsync` 先判断目标目录是否已被本次游戏加载（`ModManager.Mods` 路径比对），已加载就直接排入退出计划；没加载则先试一次直接替换（改名让位法：改名失败不会碰掉任何一个文件，而递归删除会删一半），失败也转为退出计划。`UninstallAsync` 同理：目录被占用时立刻忘掉归属、排 `Remove`，卡片状态行显示“退出游戏后移除”（新增 `RemovePending` 文案，15 种语言），文件在退出后消失。暂存包放在 `user://Gurio.SkinChanger/github-pending/<合成ID>/`，**不在 `mods/` 里**，游戏扫描看不到第二个副本。
- **退出助手扩展**：`SkinChanger.CacheCleanup.exe` 的清单新增 `modsDirectory` 与 `modsOperations`（`replace`/`remove`），在等待父进程退出后先做皮肤目录操作、再做缓存清理；它自己重新校验：只接受名为 `mods` 的真实目录、拒绝位于配置目录或临时目录之内的路径、目录名必须是单层安全名、替换的暂存必须在自己配置目录的 `github-pending` 之下。旧版本可能已经留下 `<目录>.previous` 残留，下次启动时删除（游戏正占用时排入退出计划）。
- **自我修复**：步骤跨会话保存在台账里，下次启动先对账——目录已消失（助手做过、或玩家手删）或暂存包已丢失的步骤直接作废，其余继续排在本次退出；非 Windows 平台没有助手，步骤留在台账里每次启动重新排队。退出助手只对清单做事，不依赖下一次加载 Mod。
- **测试**：文件名白名单放宽后 `github_installs.json`/`github_skins.json` 可解析而越界名仍被拒；台账格式 2 的待执行步骤往返、非法目录名/相对暂存路径/未知步骤类型被拒、同一仓库改排卸载顶掉替换、替换步骤目录与安装条目不一致时丢弃；`GitHubModsPolicy` 的目录名与残留名识别、退出计划（同目录只执行替换、越界名与玩家目录不入计划）、对账丢弃规则、暂存根目录不在 `mods` 下；运行时用真实子进程验证——进程内打开文件占用一个皮肤目录与一个旧包，父进程退出后助手删掉了被占用的目录、用暂存包换掉了旧的皮肤目录并清掉暂存，同时拒绝了越界目录名与指向暂存目录之外的替换。

## 更新判断改用发布校验值（2026-09-14，玩家第 7 轮反馈）

玩家指出：“比大小只适用于固定格式的 tag，应该记录下载时那个文件的哈希值”，并说明 GitHub 在 Release 文件那里就会给出这个哈希。核对属实：`GET /repos/{o}/{r}/releases/latest` 的每个资产都带 `digest`（`sha256:<64 位十六进制>`），实测把它与真实下载文件的 sha256 比对完全一致（`qbyoffice/SilentSkinMod` 的 `beta.v0.1.4.zip`，16,760,501 字节）。

- **旧判法的问题**：tag 由作者自己起名（`latest`、日期、`-beta` 后缀都合法），把它当版本号比较时，读不出数字的 tag 只能落回“已安装”；tag 不变而包被换掉时，永远发现不了。
- **现在的判法**：安装时把实际写入的那个包（`github/<合成ID>/package.zip`）的 sha256 记进台账（`GitHubInstall.Digest`；旧台账缺这个字段照样读回，只是退回按版本号比较），刷新时把 GitHub 公布的 digest 随 Release 缓存进目录（`GitHubSkinEntry.AssetDigest`，缓存格式 3 → 4：格式 3 的 Release 没有这个值，而已有 tag 的条目不会再查，必须整份丢弃重读）。两边都有值时**只按校验值判定**——相同＝已安装，不同＝可更新，换 tag 但包没变仍算最新；两边都拿不到值（旧资产没有该字段）才退回按 tag 比版本号。
- **缓存与写入**：缓存里的包只有与公布的 digest 一致时才复用（`GitHubInstallPolicy.CanReusePackage`）——大小相同但内容被换掉的包必须重新下载，否则装上去的还是旧文件、卡片却说它已是最新；下载完再核对一次，不一致就删掉下载并报错重试，不把对不上的包写进目录、更不记成已安装。
- **校验值不是归属证明**：读不懂的校验值只丢掉这个值（台账与目录缓存都只做归一化，`NormalizeDigest`），不因为这一项拒绝整条安装记录或整份缓存——安装归属与识别结果不受影响。
- **测试**：`NormalizeDigest` 的前缀/大小写/长度/非十六进制；资产解析（非 sha256、长度不对、缺字段都算没有，裸 64 位十六进制可用）；四种状态组合（同值、异值、单边缺失、双边缺失）、无版本号只有校验值、只有校验值都空才算未安装；缓存复用规则（同尺寸异内容必须重新下载、发布方没给校验值时按大小）；台账往返、旧台账缺字段读回、文件里不认识的校验值被归一化；目录缓存的校验值往返、`pushed_at` 变化时校验值随 Release 一起作废、格式 3 被拒、越界长度被拒。

## 旧安装记录补记发布校验值（2026-09-14，玩家第 8 轮反馈）

玩家要求：“现有的也按照哈希判断更新，检测到没有记录哈希，立刻记录一次。”加校验值之前装下的记录只有版本号，仍退回按 tag 比较，玩家不该为此重装一次。

- **补记**：目录缓存读回、Release 就位之后，对每条安装记录跑一次 `GitHubInstallPolicy.BackfillDigest`，能补的就补齐并立刻写回台账（`GitHubSkinService.BackfillDigests`，刷新开头与 `CheckReleasesAsync` 之后各一次；补记后 `RefreshInstalls` 重算状态并刷新界面）。判据是“版本号仍认为这条安装不比 Release 旧”（`CompareVersions(installedVersion, availableVersion) >= 0`）——这是旧记录仅剩的证据；若版本号说安装更旧，补记会把卡片上现成的“更新”抹掉，这类记录保持原样，等更新装完自然带上校验值。没有安装记录、已经记过校验值、发布方没有校验值（旧资产）时都不补。
- **真实数据核对**：线上唯一那条记录是 `qbyoffice/SilentSkinMod` 的 `{"Folder":"KaguyaSilentRavenSkin","PackageId":"KaguyaSilentRavenSkin","Version":"v0.1.4"}`（没有 sha256），Release tag 是 `v0.1.4-beta`——按版本号比较相等，会被补记；三条已安装文件（dll 33,792、json 301、pck 20,968,192 字节）的 sha256 与 GitHub 公布的三个资产 digest 逐一相同，说明补记的确实就是这份内容。玩家本地目录缓存是格式 3，会整份丢弃重读，所以首次刷新就能拿到 digest 并完成补记。
- **测试**：`BackfillDigest` 该补与不该补的分支（含真实那条 `v0.1.4` 对 `v0.1.4-beta`、带 `sha256:` 前缀、发布方无校验值、无安装记录、已记过校验值、版本号旧于发布），以及补记之后 `Classify` 必须在 tag 一个字都不改时把重发的包判为可更新。

## 扫描永远报“没有 zip 包”（2026-09-14，玩家第 9 轮反馈）

玩家复验：“zip 的还是扫描失败了，是不是只扫描了压缩包第一层？”日志给出的事实相反：四次尝试（同一合成 ID `16827245345540603577`）全部是 `This release has no downloadable zip package.`，包**一次也没有下载过**，压缩包内部结构根本没被读到（顺带核实：内部结构不是问题，见下）。

- **根因：缓存过的 Release 读取被当成终局。** `WithReleaseAsync` 只要 `ReleaseTag` 非空就直接返回缓存，而 Release 不是不可变的：GitHub 给出的时间线是——Release 于 00:30 建立、00:35 发布（此时没有附件），作者在 **00:57:37** 才把 `default.zip` 传上去；模组在附件出现前就读过一次（缓存里 `ReleaseTag="无"`、`AssetUrl` 为空），此后每次扫描都复用这条没有包的记录。缓存条目同时说明了另一件事：刷新路径本来只读 `ReleaseTag` 为空的条目，所以它永远不会自我纠正。
- **修法：只有刷新可以复用缓存，扫描/安装一律重读实时 Release。**（**本节这条规则已在第 10 轮被推翻：刷新同样必须重读，见文末“检查更新永远不会重读 Release”。**）刷新问的是“有没有更新的包”，tag 就够；扫描/安装问的是“包现在在哪”，只有实时读取能答。`GitHubReleasePolicy.ReuseCachedRead(cachedTag, packageRequested)` 把这条规则独立成纯逻辑（显式操作每次多花一次 core 请求，刷新路径一次都没多加），`WithReleaseAsync` 据此决定是否发请求，`StageAsync` 传 `packageRequested: true`。顺带修掉同源的另一处陷阱：作者在同一个 tag 下换掉附件时，旧代码会拿**缓存的** digest 去校验新下载的文件，必然报“下载的文件与 GitHub 公布的校验值不一致”且重试无解。
- **日志补齐**：扫描失败此前只进弹窗 tooltip，日志里看不到原因（这次靠的是下载前抛出的那句）；现在 `ScanAsync` 把失败原因与成功结果（识别 N 项、生成 M 段码）都写进日志，`ShowActionError` 对 GitHub 行记仓库全名而不是合成 ID。
- **压缩包层级：不是原因，已实测排除。** 用真实附件 `default.zip`（7,909,196 字节，sha256 与 GitHub 公布值相同）做三种摆放——原样的 `观者娘化/` 单层、再多套一层、以及拍平到根——`--audit-github-recognition` 三次都得到 `targets=character:watcher provider=WatcherShell`；`ReadDescriptors` 本来就是全目录递归，选包 `GitHubPackageLayout` 只在“包里含多个清单”时才看文件夹名。首次审计三条全失败是因为该审计工具没有基线：只替换别的 Mod 美术的包必须对着那个 Mod 的资源包才能识别，游戏里扫描时把已加载 Mod 作为基线传入（`CaptureWorkshopScanContext`），审计工具现已支持附加基线目录（README 已更新）。
- **测试**：`ReuseCachedRead` 的三种组合（刷新复用、扫描/安装不复用、没缓存过都去读）、只发 dll/pck 的 Release 不得凭空产生可下载包、同一 tag 下后补附件后重读必须给出 URL 与校验值、读不到或响应损坏时保持原样；`GitHubZipPolicy` 增补中文目录名条目（真实附件里的 `观者娘化/WatcherShell.json`）。

## 检查更新永远不会重读 Release（2026-09-14，玩家第 10 轮反馈）

玩家复验：“你的检查更新是不是还是有问题，我明明都让文件哈希值变了，但是刷新后依旧没显示有更新。”核对结论：**“检查更新”这条链从第 9 轮那次修复起就是断的**——玩家看到的不是“比较错了东西”，而是刷新根本没去读。

- **根因（三处叠加）**：`CheckReleasesAsync` 只挑“本机已安装 **且** `ReleaseTag` 为空”的条目（`GitHubSkinService.cs`），`GitHubSkinCatalog.Merge` 又只在 `pushed_at` 变了（仓库有新提交）时才把 tag 清空，而 `GitHubReleasePolicy.ReuseCachedRead` 让刷新一律复用缓存。合起来：**只要某个仓库的 Release 被读过一次、之后没有新提交，刷新再也不会读它的 Release**。作者“在同一个 tag 下换掉附件”“不推 commit 直接发一个新 Release”这两件事因此永远不可能被发现。
- **证据**：本次会话日志里 10 条 `GitHub 皮肤列表刷新完成`，却没有任何一条 Release 检查记录（成功或 `版本检查跳过` 都没有）——待查集合是空集，代码里连日志都不会写。
- **修法**：刷新对**每个已安装仓库**都读一次实时 `/releases/latest`（`GitHubReleasePolicy.ReleaseChecks`：上限 10 个/轮，最近有提交的先查，未安装的仓库照旧 0 请求），删掉 `ReuseCachedRead` 这条“有 tag 就复用”的规则，`WithReleaseAsync` 不再有“可以吃缓存”的调用方式。顺带在 `Catalog.Merge` 之后补一次 `RefreshInstalls()`：它决定哪些仓库算“已安装”，缓存被丢弃重读的那一轮若没有它，所有已安装皮肤都会被当成“未安装”，Release 检查会一个都不做。
- **成本**：每轮刷新多花 N 次 core 请求（N = 已安装仓库数，上限 10）。这是“检查更新”的固有成本；要省只能在客户端存 ETag 走条件请求（304 不计额度），那属于缓存格式 5 的话题，这轮不做。
- **当时的线上事实（也是这轮不能只看代码就下结论的原因）**：把两个仓库的 Release zip 都下载实测——`gurio-wine/STS2-TestSkin` 的 `default.zip`（7,909,196 字节）sha256 = `020179f7…`，`qbyoffice/SilentSkinMod` 的 `beta.v0.1.4.zip`（16,760,501 字节）sha256 = `206ffa80…`，与 GitHub 公布的 digest 以及本机台账记录的安装校验值三方一致。即：以当时的数据，一个修好的“检查更新”也只会（正确地）显示“已安装”。玩家说的“文件哈希值变了”因此不是这两个 Release zip 的字节——改仓库源码/推 commit、改散文件（dll/pck）、改 `mods/` 里已安装目录的字节，都不会也不该被“检查更新”发现，它只认 Release 里那个 zip。
- **测试**：`ReleaseChecks` 必须选中“已安装且缓存里已有 tag”的仓库（这条在旧规则下确实是红的）、不选未安装的仓库、上限与排序；`WithRelease` 读到同一 tag 下的新 digest 必须换值，且 `Classify` 据此判为可更新。
