# Steam 工坊反馈待办（更新于 2026-08-31）

采集时间：2026-08-31 09:49（北京时间）

工坊物品：[皮肤切换器 - Skin Changer（3787302680）](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680)

采集范围：当前公开可见的 75 条[工坊留言](https://steamcommunity.com/sharedfiles/filedetails/comments/3787302680)及“建议 / 问题 / 疑惑”三个[讨论帖](https://steamcommunity.com/sharedfiles/filedetails/discussions/3787302680)；“问题”帖当前有 9 条回复，另两个讨论帖没有公开回复。

本表只保留仍需处理、仍待玩家复测或仍缺复现材料的内容。纯夸奖、作者进度说明、已撤回留言和已经明确完成的功能不再列入。

状态说明：

- **未解决**：现有代码或复测结果仍不能关闭问题。
- **已处理待复测**：已有对应修复，但原留言者尚未确认。
- **信息不足**：缺少具体 Mod、版本、场景或日志，暂时无法定位。
- **已暂缓**：问题成立，但当前明确不继续处理该版本或方向。
- **建议评估**：功能建议，尚未排入实现。

## 从旧到新的问题与建议

| 时间（北京时间） | 类型 | 问题或需求 | 当前判断 | 来源 |
|---|---|---|---|---|
| 08-25 14:16 | 角色显示 | 亡灵契约师和猎手图片错位。 | **信息不足**：没有具体皮肤 Mod、出现界面、游戏版本和日志。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_580554442217370878) |
| 08-26 21:35 | CZN / Chaos Zero | Chaos Zero 卡图无法切换；后续又反馈 Pelleas 的储君、猎手角色皮肤不显示、怪物模型消失，以及 CZN 卡图仍为原版或全部丢失。 | **未解决**：这些续报出现在已有 CZN 图集和怪物资源识别改动之后，不能再按“旧版本已修”关闭。 | [Bug 讨论](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/591813130434318585/#c592939664045825393) · [角色/怪物续报](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621182261) · [卡图续报 1](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621193360) · [卡图续报 2](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621227322) |
| 08-27 11:16 | 独立头像 | 角色皮肤与独立头像 Mod（例：`SIlent Icons addon`）不能同时使用。 | **未解决**：头像仍随角色皮肤来源应用，没有独立头像来源选择。08-28 的“多个外观 Mod 同时生效”需求中，卡牌部分已由优先级和预设覆盖，头像部分仍在这里。 | [头像留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310164511665457) · [多来源留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310164511724665) |
| 08-27 13:25 | 界面建议 | 可隐藏局内“外观”入口；选角皮肤按钮可放右上角；悬浮角色皮肤名称时预览。 | **建议评估**：三项均未完整实现。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310164511671928) |
| 08-28 12:42 | 性能 | 打开卡牌奖励时卡顿。 | **未解决**：已有图鉴和皮肤选择优化没有专门覆盖卡牌奖励生成路径。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310164511770586) |
| 08-28 13:54 | 稳定性 | Save & Quit 或 Give Up 返回选角界面后白屏卡死，日志显示已释放的 `Godot.FontVariation`。 | **信息不足**：作者无法复现，仍缺完整 Mod 列表、游戏版本和最新日志。 | [Bug 讨论](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/591813130434318585/#c592939664045961806) |
| 08-29 03:49 | 卡牌外形 | 某些异画卡会被错误加上先古蜡烛、黑色说明框和先古背景。 | **未解决**：需要确认卡牌类型和外形来源为什么越过“单一来源胜出”规则。 | [Bug 讨论](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/591813130434318585/#c592939664046010390) |
| 08-29 05:58 | 加载顺序提醒 | 多次开关 Mod 后提醒不再弹出；另有玩家点击“置前并重启”时报错。 | **本地修复待发布**：日志确认报错玩家有 15 个皮肤提供者（含 `Merchant2CuteII`）排在 Skin Changer 前面。顺序检测和“不再提示”重置已重做，自动重启已改用 Godot 的跨平台重启，不再依赖 PowerShell。 | [Bug 讨论](https://steamcommunity.com/workshop/filedetails/discussion/3787302680/591813130434318585/#c592939664046019968) |
| 08-29 13:09 | Card Art Editor | 多卡面环境中部分切换没有变化，而且无法悬浮预览。 | **未解决**：需要单独检查编辑器生成资源的生命周期和覆盖顺序。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621060618) |
| 08-29 14:42 | Nekobinder | `Nekobinder/necrobinder skin mod`（`3748419805`）切换后，选角预览变成散乱的衣服碎片。 | **未解决**：骨骼、图集或附件绑定仍没有得到原留言者复测确认。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621061850) |
| 08-29 14:44 | Moe-Necrobinder | 切换离开 `Moe-Necrobinder`（`3773814239`）后，小手素材仍被它占用。 | **未解决**：皮肤专属附件的退出回滚仍需检查。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621061952) |
| 08-30 12:06 | 崩溃 | 进入下一个房间时闪退。 | **信息不足**：缺角色、皮肤 Mod、前后房间类型、游戏版本和日志。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621141150) |
| 08-30 18:01 | 同 ID 差分 | 多个差分 Mod 使用同一个 Mod ID，希望能全部加载并热切换。 | **建议评估**：需要按来源目录或包指纹区分，同时规避程序集和注册项冲突。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621161094) |
| 08-30 19:47 | 正式版多人 | 创建多人房间后进入地图时所有人黑屏；续报确认发生在正式版。 | **未解决（高影响）**：测试版后来可以正常进入；正式版还出现过第二次抽牌卡死。此前“先不测试正式版快照”只针对当时检查的特定依赖 Mod，并不表示 Skin Changer 暂缓正式版支持。正式版有大量玩家，必须继续作为完整支持目标排查。 | [首报](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621168940) · [仍存在](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621184669) · [正式版确认](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621190360) |
| 08-30 22:52 | 猎手皮肤 | 同时安装猫娘阿塔与劣人 TV 时，无法切出劣人 TV。 | **已处理待复测**：当前本机和多人测试中劣人 TV 可以加载，但原留言者没有确认其具体组合已恢复。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621182905) |
| 08-31 05:07 | 外观调整 UX | 把 500% 角色拖到屏幕外后再缩小并关闭菜单，角色无法找回；询问是否有打开菜单或恢复位置的快捷键。 | **建议评估**：需要提供不依赖目标点击的“恢复当前角色位置”入口或快捷键。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621212090) |
| 08-31 08:02 | 选角界面 | 选中角色后尝试返回选角界面，画面不切换，只继续播放音乐。 | **信息不足（高影响）**：缺单人/多人、返回操作、游戏版本、角色和日志。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621222655) |
| 08-31 09:37 | 商人入口 / 头像 | 商人只能遇到后在局内切换，不够方便；假商人会使入口认知更混乱；另有头像 Mod 被角色皮肤覆盖。 | **建议评估 + 未解决**：商人需要局外管理入口；头像覆盖归入前面的“独立头像来源”问题。 | [留言](https://steamcommunity.com/sharedfiles/filedetails/?id=3787302680#comment_587310457621227779) |

## 按紧急程度的处理顺序

| 顺序 | 级别 | 待办 | 排序理由 / 下一步 |
|---:|---|---|---|
| 1 | P1 | 发布并复测加载顺序提醒与跨平台重启 | 本地修复已完成；玩家日志证明错误加载顺序会同时造成 `Merchant2CuteII` 跑位和角色商店场景资源串用，发布后需确认自动置前能稳定生效。 |
| 2 | P1 | 正式版多人进入地图黑屏、抽第二张牌卡死 | 正式版是完整支持目标且有大量玩家；继续对照正式版运行时、多人建图和抽牌流程定位，不能用测试版正常代替正式版验证。 |
| 3 | P1 | CZN / Chaos Zero 卡图、角色皮肤和怪物模型缺失 | 同一系列跨卡牌、角色和怪物三个域，且续报晚于已有兼容改动；应从共享资源包和代码注册入口做一次完整清点。 |
| 4 | P1 | 返回选角界面无响应，只播放音乐 | 可能是界面生命周期阻断；先补问单人/多人、版本和日志。 |
| 5 | P1 | Save & Quit / Give Up 后 `FontVariation` 白屏卡死 | 有旧堆栈但无法复现；需要新版日志和完整 Mod 列表。 |
| 6 | P1 | 进入下一个房间闪退 | 高影响但信息最少；先收集版本、房间、角色、皮肤和日志。 |
| 7 | P2 | 异画卡被错误套用先古外形 | 系统性卡牌来源串用；检查单一优先级赢家是否同时控制卡图、外壳和类型。 |
| 8 | P2 | `Card Art Editor` 多卡面切换和悬浮预览失效 | 兼容性明确但不阻断对局；检查动态生成资源的注册与缓存失效。 |
| 9 | P2 | Nekobinder 预览碎片、Moe-Necrobinder 小手残留 | 放在同一轮检查角色骨骼、附件和退出回滚。 |
| 10 | P2 | 卡牌奖励界面卡顿 | 需要针对奖励界面做性能采样，不能用图鉴优化结果代替。 |
| 11 | P2 | 角色被拖出屏幕后无法恢复 | 增加不依赖目标点击的恢复入口或快捷键。 |
| 12 | P2 | 独立头像 Mod 无法与角色皮肤组合 | 需要把头像做成独立来源，而不是继续添加个别 Mod 特判。 |
| 13 | P2 | 亡灵契约师/猎手错位、劣人 TV 组合问题 | 前者缺复现信息；后者当前本机可用，优先等待原留言者复测。 |
| 14 | P3 | 商人局外入口、角色悬浮预览、按钮位置和入口开关 | 都是可用性增强，不应抢占崩溃和资源串用修复。 |
| 15 | P3 | 同 ID 差分 Mod 同时加载 | 涉及加载器身份和程序集冲突，成本高、风险大，最后评估。 |
