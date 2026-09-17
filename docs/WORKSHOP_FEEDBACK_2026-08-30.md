# Steam 工坊反馈待办（更新于 2026-09-16 第三轮）

在线采集：2026-09-16 20:31–20:35（北京时间），采集时的核对基准 `DSH-GL53` / `497e31e`，内测 `1.0.17.1`，公开版本 `1.0.17`。采集时的公开页面为 102 条留言（3 页）与 4 个讨论主题、26 条回复。

采集方法更正：2026-09-07 那一轮记录的「71 条留言（2 页）」是**分页参数错误**导致的漏采——留言与讨论区的翻页参数是 `ctp`，`p` 会被服务器静默忽略并永远返回第 1 页。本轮按 `ctp` 全量重采：留言页面自报 `total_count=102`（50+50+2 吻合），讨论区 4 个主题逐页翻完（`Showing 1-4 of 4 active topics`，上一轮存在的 2 个主题已被删除或移出活跃列表，非漏采）。此前所有基于 71 条清单的对应关系以本轮 102 条为准；2026-09-07 已关闭项的判定不受影响（关闭依据是用户确认，不是留言数量）。

来源：[工坊留言](https://steamcommunity.com/sharedfiles/filedetails/comments/3787302680) · [讨论区](https://steamcommunity.com/sharedfiles/filedetails/discussions/3787302680)。已删除、隐藏和非公开内容不在采集范围内。时间统一从页面时间戳（epoch）换算为北京时间，页面渲染时区为 PDT（UTC-7），北京 = PDT + 15 小时。本轮全部只读，未登录、未发帖、未回复。F25–F27 为 2026-09-16 的日志补录（见「现有」段说明），未翻页复核 09-16 之后的工坊页面。

现有 **11 项：4 项启动级故障（2 项冲突群、2 项初始化失败）、5 项兼容或显示问题、2 项功能建议**；其中 F25、F26 已按日志把失败原因定位到本 Mod 自身代码路径，F27 挂起待证，其余未核查。「定案」只表示原因已定位，不等于已修复、更不等于已在游戏内复验。按首次反馈从旧到新排列；同一问题的补充并入原项。完成或关闭一项即删除对应行及优先级行，历史经过查 Git。

补录（2026-09-16，非重新采集）：F25、F26、F27 来自 2026-09-16 的工坊留言与当次会话日志分析（克劳恩皮丝的 `godot.log` 与 5 张截图、白翼 18:31 会话日志），证据与结论写在对应条目里，未复查工坊页面；F17、F18 只补入邮箱检查线索，未改动原判定。

## 从旧到新的问题与建议

| 编号 | 首次反馈 / 补充时间（北京时间） | 问题或需求 | 当前判断与下一步 | 来源 |
|---|---|---|---|---|
| F17 | 2026-09-14 13:11（gddfzxhr 留言，晓夫九冲突）<br>2026-09-15 09:45（EDHMIAO 留言，你画瓦猜）<br>2026-09-15 21:01（996391450 留言，basekib/ritsulib）<br>2026-09-15 23:08（川口名人留言，ReflectionTypeLoadException）<br>2026-09-16 12:35（ZiRAN_ice 留言，ModLaunchManager）<br>2026-09-16 17:53（小蝌笔肘妈妈留言，basekib/ritsulib 黑屏） | 测试版分支下与多个框架 / 管理器类 Mod 同开出现启动级故障：黑屏无响应、启动未响应、启动错误、类型加载失败（ReflectionTypeLoadException）。涉及 basekib、ritsulib（三人两次报告，含取消订阅后恢复正常的对照）、你画瓦猜、ModLaunchManager。EDHMIAO 称 09-14 之前一直正常 | 全部无日志，先按群报告挂起。三人同报 basekib/ritsulib 且取消订阅即恢复，指向真实的加载交互；EDHMIAO 的时间线（09-14 起异常）与近期公开版本的关联待日志确认，不预设结论。下一步：等邮箱日志（LoaderExceptions 列表能直接定位 ReflectionTypeLoadException 的缺失依赖），收到前不做猜测式修改。邮箱线索（2026-09-16 补）：报告人至今未发来日志邮件；开发者已接入并检查三个邮箱（Outlook、gurio、gmail），均无相关新邮件，缺少 LoaderExceptions 的情况未变 | [留言](https://steamcommunity.com/sharedfiles/filedetails/comments/3787302680) |
| F18 | 2026-09-14 11:25（Madoka，讨论区问题-Bug #1、#2） | Mod 更新后先古事件（欧洛巴斯）无法弹出遗物选项，`System.ArgumentNullException: Value cannot be null. (Parameter 'player')`，栈顶 `AncientEventModel.GenerateInitialOptionsWrapper_Patch3` → `SetInitialEventState` → `EventModel.BeginEvent` | 该方法本 Mod 不打补丁（已核对本仓库全部 Harmony 补丁：先古侧只有四个图标 getter 与界面视觉钩子），`_Patch3` 后缀说明是第三个补丁方，先按「归属未定」处理。下一步：向反馈者要日志确认补丁归属；其同时报告 `gurio.wine@gmail.com` 被 QQ 邮箱退信（作者 09-15 已改留三个邮箱）。邮箱线索（2026-09-16 补）：报告人至今未发来日志邮件，三个邮箱（Outlook、gurio、gmail）均已接入检查、无相关新邮件 | [讨论区](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/592940620292648589/) |
| F19 | 2026-09-14 13:11（gddfzxhr 留言） | 与晓夫九 Mod 同开会让角色消失 | 与 F17 同日同作者，但现象（角色消失）与启动故障不同，单列。无 Mod 链接、无日志。下一步：等样本（Mod 链接 + 日志 + 游戏版本） | [留言](https://steamcommunity.com/sharedfiles/filedetails/comments/3787302680) |
| F20 | 2026-09-14 13:45（无聊的我，讨论区建议-Suggestion #2） | 「跟随分类」里的单卡设置下拉选项排序未跟随「皮肤优先级」的手动排序：手动排序后是 4→1→3→2，单卡下拉仍是 1→2→3→4 | 可离线核查：定位单卡下拉的选项构建处是否按目录顺序而非优先级顺序排列。开发者此前已问过一次未理解到位，本轮用户补充了具体例子，语义明确 | [讨论区](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/591813130434318558/) |
| F21 | 2026-09-15 00:34（命，留言） | 阿塔的商人娘化每次进游戏都要重新设置；另一个娘化的位置总会跑到屏幕左上角 | 无日志，但两个现象都具体可离线核查：商人皮肤选择是否持久化（存档 / 配置往返）、商人位置偏移是否在重进后丢失或被重置。下一步：先查持久化链路再决定是否要样本 | [留言](https://steamcommunity.com/sharedfiles/filedetails/comments/3787302680) |
| F22 | 2026-09-14 18:21（Aya，留言，韩文） | 多人模式下每局都要重新给队友设置皮肤 | 与既有设计（多人只显示双方都装了的皮肤、会话隔离）可能是同一机制的表现，先核查多人会话里队友皮肤的保存与恢复链路，再判断是设计限制还是缺陷 | [留言](https://steamcommunity.com/sharedfiles/filedetails/comments/3787302680) |
| F23 | 2026-09-10 02:44（萬惡的資本主義走狗，讨论区建议-Suggestion #1） | 皮肤分场景混用：战斗时用皮肤 A、选择遗物（事件界面）时用皮肤 B 的手 | 功能建议。与既有「皮肤组合 / 分部位」能力有重叠，差别在按场景（战斗 / 事件 / 遗物）切换。是否做、怎么做等用户定 | [讨论区](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/591813130434318558/) |
| F24 | 2026-09-16 16:21（杀软二刺螈，留言） | 局内按血量自动切换皮肤（如高血量用 SFW、低血量切 NSFW） | 功能建议，涉及局内运行时换肤（本 Mod 支持局内即时切换，技术上可行）。是否做等用户定 | [留言](https://steamcommunity.com/sharedfiles/filedetails/comments/3787302680) |
| F25 | 2026-09-16（克劳恩皮丝，留言，附完整日志与 5 张截图） | 安卓手机版初始化失败：识别、排序正常，但选项内不显示任何皮肤；同一条另报「GitHub 不可用」 | 已定案（日志 L320 `[ERROR] 初始化失败 UnauthorizedAccessException`，堆栈 `GamePackLocator.BuildCandidates` → `Resolve` → `InitializeBeforeAssets`；成功时必有的「发现 N 个生物外观组」日志不存在）：`BuildCandidates` 对可执行文件目录做 `Directory.EnumerateFiles`，安卓上该目录解析为根目录 `/`，沙箱拒绝枚举 → 初始化失败、皮肤目录未构建。环境：包名 `com.megacrit.sts2re`、测试分支 v0.111.0、本 Mod 1.0.17.0。修复方向：安卓改用启动器 `--main-pack` 注入路径定位 PCK（安卓上 PCK 在 `payloads/.../game/`，本就不在可执行目录旁），且任何平台不得枚举根目录。「GitHub 不可用」**无法归因，不并入本结论**：全部日志 GitHub 相关 0 条（任何刷新尝试都会留日志），与 Steam 社区帖两次网络失败（Connection reset / task canceled）是不同通道。附注：5 张截图内容尚未人工核验，保留在 `%TEMP%\sc-mobile-report\` | [留言](https://steamcommunity.com/sharedfiles/filedetails/comments/3787302680) |
| F26 | 2026-09-16 18:31（白翼报告的动画卡住会话日志，分析中发现的本 Mod 缺陷） | 启动期 `NGame.Quit` 误调导致初始化失败：本会话本 Mod 未挂载任何覆盖层 | 已定案，属本 Mod 自身缺陷：启动期 27 次来源不明的 `NGame.Quit` 调用（BaseLib 仅记录、无栈，嫌疑为某 mod 启动期误调，游戏实际并未退出）→ `QuitCacheCleanup` 拦截即置退出标志并停止缓存写入 → `MountOverlay` 写覆盖层缓存被拒 → 初始化失败（`OperationCanceledException: Skin Changer is quitting`，栈 `WriteCache` → `MountArchiveOverlay` → `MountOverlay` → `InitializeBeforeAssets`）。修复方向：退出拦截不能把启动期 / 未真正退出的 Quit 调用当成真退出（需辨别真退出，或初始化完成前不生效）。调用者待查 | 日志（2026-09-16 18:31 会话） |
| F27 | 2026-09-16（白翼，留言，提及 3801530111 使用「新的皮肤制作方式」后动画后半段卡住） | 攻击动画后半段卡住 | **挂起待证**。已查明：`3801530111` = ReplaceIroncladWithCrusader v0.0.6（纯 PCK + 场景脚本、无 DLL），被本 Mod 隔离接管；该日志为单次会话、游戏过程段零动画 / Spine 报错；BaseLib 3.4.7 的 `WaitCustomAnim` 启动补丁无任何失败记录，与卡住关联强度弱；且该会话本 Mod 初始化失败未接管任何资源，不可能是本会话所致。无法确认：该日志是否就是复现会话、卡住会话中本 Mod 是否挂载成功。待向玩家索要：复现会话的 `godot.log`、卡住时本 Mod 启用状态与选中皮肤、是否装了依赖 BaseLib 动画机制的 mod（其列表中有 NecrobinderOstyAnimMod v1.4.0） | [留言](https://steamcommunity.com/sharedfiles/filedetails/comments/3787302680) |

## 按紧急程度的处理顺序

P1：影响启动、整组功能或主要局内流程；P2：特定 Mod 兼容/显示问题；P3：新增能力或易用性改进。新反馈先做代码与实包核查；缺样本或日志时不连续猜测式修改。

| 顺序 | 级别 | 编号 | 下一步 |
|---:|---|---|---|
| 1 | P1 | F17 | 等邮箱日志（含 LoaderExceptions）；确认 basekib/ritsulib 的加载交互与 EDHMIAO 报告的 09-14 时间线，未收到日志前不动代码 |
| 2 | P1 | F25 | 改 `GamePackLocator`：安卓改用启动器 `--main-pack` 注入路径定位 PCK，任何平台不得枚举根目录；「GitHub 不可用」单列为无法归因，不并入本次结论 |
| 3 | P1 | F26 | 改退出拦截：辨别真退出，或初始化完成前不生效；并查那 27 次 `NGame.Quit` 的调用者 |
| 4 | P2 | F18 | 向反馈者要日志，确认 `GenerateInitialOptionsWrapper_Patch3` 的补丁归属（本 Mod 不打该补丁） |
| 5 | P2 | F27 | 挂起待证：等玩家给复现会话 `godot.log`、卡住时本 Mod 启用状态与选中皮肤、依赖 BaseLib 动画机制的 mod 清单 |
| 6 | P2 | F20 | 离线核查单卡下拉排序来源：是否按优先级顺序而非目录顺序构建选项 |
| 7 | P2 | F21 | 离线核查商人皮肤持久化与位置偏移的保存/恢复链路 |
| 8 | P2 | F19 | 等样本：Mod 链接、日志、游戏版本 |
| 9 | P3 | F22 | 核查多人会话队友皮肤保存与恢复；区分设计限制与缺陷后答复 |
| 10 | P3 | F23 | 等用户决定是否做场景化皮肤混用 |
| 11 | P3 | F24 | 等用户决定是否做血量触发自动切肤 |

## 收集与关闭规则

- 用户要求删除的未复现项目不继续占据待办；只有新的具体复现、日志或样本才重新建项，不因旧留言仍在或作者引用旧留言而恢复任务。
- 已发布、已确认解决或已取消的事项立即从任务及优先级表删除；已有修复代码或离线测试不能代替游戏复验，也不能用“以前修过”直接驳回新的具体回归报告。
- 本轮采集未建项：夸奖 / 打赏 / 与功能无关的互动（山坡羊、双手没兜、hhgg、半岛铁盒、好女孩？坏女孩？、白宵燿夜-shirayoi_kaguya-、山坡羊“生猴子”、“老大复活了/又死了”）；作者已答复且在等对方补充的（设置界面“外观”消失——右键外观按钮可恢复，作者 09-08 已答；先古图鉴只显示佩尔——鼠标移到最右，作者 09-15 已答；手机版兼容——作者 09-14 已答并要具体不兼容点或日志）；过于模糊无法定位的（Pengo's Tarot Enchantments Pack 文本乱码，英文留言，未说明具体界面与复现步骤）；Aya 同一条里的“程序偏重”性能抱怨（无量化数据，不单独建项）。
- 「模组工坊」主题的 22 条回复全部是 SCM3 皮肤信息码投稿，属投稿内容不是反馈：投稿码在工坊面板刷新时由 Mod 自动从讨论帖读取、校验并合并进清单，无需人工收录，不在本表跟踪。
- 邮箱可达性：`gurio.wine@gmail.com` 被 QQ 邮箱判“找不到收件人”（Madoka 09-14 报告）。作者 09-15 起在留言区改留三个邮箱（gmail / outlook / me@gurio.cn），后续日志引导以三个邮箱为准。
- 补充信息至少包括：游戏版本/正式或测试分支、实际加载的 Skin Changer 版本、具体 Mod 链接、失败操作、当次日志。涉及互切或重进时，还要记录启动前的选择与切换顺序。
- 公开原帖消失不等于修复；用户要求保留的暂缓事项仍保留，并标明历史来源。本轮只读取工坊、更新本文件，没有发送回复、删帖或修改游戏代码。上一轮（2026-09-16 第一轮）按用户确认关闭的三项（蛇蜥皮肤识别、反馈者皮肤无法使用、宠物兼容复测）不因本轮全量重采而恢复，判定过程查本文件 Git 历史。
