# Steam 工坊反馈待办快照（2026-08-30）

采集时间：2026-08-30 19:38（北京时间）  
工坊物品：[Skin Changer（3787302680）](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680)  
采集范围：当前公开可见的 73 条[工坊留言](https://steamcommunity.com/sharedfiles/filedetails/comments/3787302680)及“建议 / 问题 / 疑惑”三个[讨论帖](https://steamcommunity.com/sharedfiles/filedetails/discussions/3787302680)。纯夸奖、作者进度说明和已经明确关闭的兼容冲突未列入。

状态说明：

- **未解决**：目前仍有明确复现描述，尚无对应修复结果。
- **已处理待复测**：代码已有对应修改，但留言者还没有用最新版确认。
- **部分解决**：需求的一部分已经实现，剩余部分仍需处理。
- **信息不足**：无法仅凭现有留言定位，需要版本、Mod 列表或日志。
- **建议评估**：新功能建议，尚未决定实现方式。

## 从旧到新的问题与建议

| 时间（北京时间） | 类型 | 问题或需求 | 当前判断 | 来源 |
|---|---|---|---|---|
| 08-25 12:45 | 建议 | 多人模式中，同一角色的不同玩家使用不同皮肤。 | **已处理待复测**：0.9.102 已具备按玩家同步、临时下载和应用角色皮肤的流程，仍需双客户端验证完整生命周期。 | [建议讨论 #1](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/591813130434318558/#c591813437999539667) |
| 08-25 14:16 | 角色 | 亡灵契约师和猎手的图片错位，但没有给出具体皮肤 Mod。 | **信息不足**：需要具体 Mod 名、游戏版本、出现位置和日志。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_580554442217370878) |
| 08-26 12:11 | 性能 | 选角界面切换角色时有短暂卡顿。 | **已处理待复测**：已做预加载、资源缓存和延迟加载优化，但仍需玩家确认当前版本体感。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310164511575565) |
| 08-26 21:35 | 卡牌 | Chaos Zero 系列卡图无法切换，例子为工坊物品 `3747644438`。 | **已处理待复测**：后续已补充 Chaos/CZN 图集与共享资源识别，原留言者未确认。 | [Bug 讨论 #1](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/591813130434318585/#c592939664045825393) |
| 08-27 11:16 | 头像 | 独立角色头像 Mod（例：`SIlent Icons addon`）无法和角色皮肤同时使用。 | **未解决**：目前头像仍随选中的角色皮肤来源应用，没有独立的头像来源选择机制。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310164511665457) |
| 08-27 11:42 | 默认皮肤 | 有些皮肤 Mod 会顶掉游戏原皮，没有给出具体 Mod。 | **信息不足**：需要具体 Mod 名；现有默认皮肤隔离逻辑已多次修正，但无法据此确认是否仍存在。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310164511666787) |
| 08-27 11:48 | macOS | 请求支持 macOS；随后确认 Apple Silicon 已不再报架构错误，但功能曾无法使用。 | **已处理待复测**：现已使用 AnyCPU 并补充 macOS 游戏资源路径；无法在本机验证实际功能。 | [留言 1](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310164511667015) · [留言 2](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310164511674242) |
| 08-27 13:25 | 界面建议 | 隐藏局内“外观”入口；选角皮肤按钮位置可选右上角；角色皮肤名称悬浮预览。 | **未解决**：三项均没有完整实现；卡牌悬浮预览不能视为角色悬浮预览。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310164511671928) |
| 08-28 01:41 | 多来源外观 | 多个卡面/头像 Mod 同时生效；部分 Mod 只修改小图。 | **部分解决**：卡牌已有分类优先级、单卡来源和预设；独立头像来源组合仍未实现。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310164511724665) |
| 08-28 12:42 | 性能 | 打开卡牌奖励时卡顿。 | **未解决**：现有优化主要覆盖卡牌图鉴和皮肤选择，奖励界面没有得到针对性复测。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310164511770586) |
| 08-28 13:54 | 稳定性 | Save & Quit 或 Give Up 返回选角界面后白屏卡死，日志为已释放的 `Godot.FontVariation`。 | **信息不足**：作者未能复现；仍缺游戏版本和完整 Mod 列表。 | [Bug 讨论 #3](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/591813130434318585/#c592939664045961806) |
| 08-28 17:00 | 非皮肤 Mod 兼容 | `NSFW原版事件替换` / “自制拓展涩涩事件-Beta”等事件 Mod 的文本被恢复为原版，CG 仍可能正常；最新留言称修改或新增事件的 Mod 普遍可能受影响。 | **未解决（高优先级）**：这表明接管范围可能误包含事件文本或本地化资源，且 08-30 仍有重复报告。 | [Bug 讨论 #4](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/591813130434318585/#c592939664045970377) · [留言 1](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621055575) · [留言 2](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621104432) · [留言 3](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621153158) |
| 08-29 03:49 | 卡牌外形 | 某些异画卡被错误套上先古蜡烛、黑色说明框和先古背景。 | **未解决**：需要检查卡牌类型/外形来源是否被错误继承。 | [Bug 讨论 #5](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/591813130434318585/#c592939664046010390) |
| 08-29 05:58 | 加载顺序提醒 | 多次开关 Mod 后，加载顺序提醒不再弹出，重新订阅也无效。 | **已处理待复测**：提醒逻辑已改为只检查排在 Skin Changer 前面的皮肤提供者，并重做状态变化判断。 | [Bug 讨论 #6](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/591813130434318585/#c592939664046019968) |
| 08-29 07:33 | 商店 | `Merchant2CuteII` 与本 Mod 同时使用时，商人或玩家模型错位、缩到右下角或飞到左上角；劣人 TV 还可能变成忍者阿塔。08-30 仍有多条重复反馈。 | **未解决（高优先级）**：近期虽重写过商店热切换，但最新反馈表明正式版和测试版仍可能出错。 | [Bug 讨论 #7/#10](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/591813130434318585/#c592939664046025432) · [留言 1](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621096892) · [留言 2](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621097680) · [留言 3](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621147290) · [留言 4](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621148445) |
| 08-29 13:09 | 卡牌 | 安装 `Card Art Editor` 后，多卡面环境中部分切换不变化，且无法悬浮预览。 | **未解决**：需要明确两者的资源覆盖顺序和编辑器生成资源的生命周期。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621060618) |
| 08-29 14:42 | 角色预览 | `Nekobinder/necrobinder skin mod`（工坊物品 `3748419805`）切换后，选角界面变成散乱的衣服碎片。 | **未解决**：属于骨骼、附件或图集绑定没有完整恢复/重放。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621061850) |
| 08-29 14:44 | 残留素材 | 切换离开 `Moe-Necrobinder`（工坊物品 `3773814239`）后，小手素材仍被该 Mod 占用。 | **未解决**：需要把皮肤专属附件纳入退出时的完整回滚。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621061952) |
| 08-29 16:03 | BaseLib / 战斗 | 开启本 Mod 后，`into the spireverse`、`the sorceress` 等角色进入关卡时人物和怪物消失；另有“加入图书馆”导致敌人动画卡死的疑似报告。 | **已处理待复测**：已补充 BaseLib 自定义角色模型发现与隔离，但只确认测试版“封兽鵺”不再黑屏，其他例子未复测。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621065669) |
| 08-29 22:17 | 崩溃 | `Aeonglass Feminization` 与本 Mod 同时使用会直接闪退，玩家怀疑与沙漏动画有关。 | **未解决（高优先级）**：当前反馈是直接崩溃，不只是静态/动态皮肤显示错误。 | [留言 1](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621088977) · [留言 2](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621089108) |
| 08-30 12:06 | 崩溃 | 进入下一个房间时闪退。 | **信息不足**：缺角色、皮肤 Mod、前后房间类型、游戏版本和日志。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621141150) |
| 08-30 18:01 | 新建议 | 多个差分 Mod 使用相同 Mod ID，希望能同时加载并切换，而不是开关 Mod 后重启。 | **建议评估**：当前加载器通常把相同 ID 视为同一个 Mod；若实现，需要额外区分来源目录/包指纹并避免程序集与注册项冲突。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621161094) |

## 建议处理顺序

1. **事件 Mod 文本被还原**：它属于非皮肤功能被误接管，影响范围可能比单个皮肤大。
2. **商店错位/左上角/串角色**：最新仍有多人重复反馈，且覆盖正式版与测试版。
3. **Aeonglass 直接闪退**：先从日志确认是动画资源、运行时代码还是版本分支。
4. **BaseLib 角色通用回归测试**：用留言列出的角色补测，确认“封兽鵺”修复是否真为通解。
5. **卡牌外形与 Card Art Editor**：集中检查卡图、外壳、类型、先古特效是否坚持“单一来源胜出”。
6. **Nekobinder / Moe-Necrobinder**：补齐预览骨骼和附件的进入/退出生命周期。
7. 其余性能、界面与同 ID 多差分功能按风险和复现材料逐项推进。

## 当前未纳入的内容

- 纯夸奖、催更和作者自己的进度说明。
- 已明确说明为“皮肤修复 Mod 与 Skin Changer 接管机制互相冲突、建议不要同时使用”的个案。
- 目前页面上已经不可见的旧留言；若需要保留历史删除项，应另建“历史反馈归档”，不要混入当前公开待办。
