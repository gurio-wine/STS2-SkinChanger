# 原皮肤设置协作实施计划

> 本轮按 executing-plans 在 master 内联实施；不建工作区、不派子智能体、不启动游戏。

**目标：** 恢复已接管 Merchant2CuteII 的原控制台/设置配置路径，所有实时写入仅作用于当前选择且由它建立的商人节点；核对 CZN 设置系统的边界。

**依据：** 用户已批准本轮对话中的分工：SC 管选择、作用对象及退出恢复，原作者管皮肤内部设置。原配置为唯一配置源，不能将提供者程序集重新放回游戏的全局类型扫描。

**架构：** 独立命令注册及弱引用目标集合提供共用基础。旧商人接口使用经实包验证的能力适配器，在 DLL 加载进内存前重写两个全场景入口，避免补丁安装引发静态构造抢先写场景；只刷新已绑定且仍属于该提供者的手/库存。保留已验证设置页回调，原有模型创建、资源隔离、变换和交易逻辑不改。未知命令不擅自放行。

## 约束

- 正式版 0.107.1 Release/AnyCPU 发布；测试版 0.111.0 ReleaseBeta 独立验证。
- 只订阅配置变化并合并延迟刷新，不新增逐帧扫描；关闭/重建节点后旧回调不得生效。
- 控制台同名命令不覆盖游戏或其它 Mod；遵守 DebugOnly 开关。
- 本轮内测号 0.10.3.4；验证后提交及部署，工坊上传另待用户要求。

## 工作项

- [x] 在 RuntimeTests 新建 ProviderSettingsTests：原生控制台晚注册、重复/同名/调试过滤、目标按当前组和来源隔离、切走/重绑定/失效节点；旧版首先因缺少注册路径失败。
- [x] 新建 Core/ProviderSettingsControls.cs：TryRegisterCommand、ProviderSettingsTargets、DevConsole 构造后接入及已打开控制台增量注册。
- [x] 新建 Core/MerchantSettingsContract.cs 与 ManagedMerchantSettingsBridge.cs：读取已验证旧接口、观察原 Save、绑定手/库存后定向刷新。MerchantSettingsAssemblyCompatibility 在内存中改写两个全场景入口；实包审计不执行初始化器或保存用户配置。
- [x] ManagedSkinModLoader 激活前安装设置桥、分离经验证设置页补丁；MerchantRuntimeAppearance 的现有 Ready 完成点绑定节点。CZN 的通用设置、表演控制服务及现有选角面板保留，本轮未重写其管理系统，未宣称所有第三方管理器已全面兼容。
- [x] 双版本 RuntimeTests + Merchant2CuteII 实包合同审计 + LogicTests + 构建环境检查；自查退出清理、延迟执行、补丁安装失败与重复激活。静态初始化测试使用独立 SettingsFixture 程序集，避免复制整个测试程序集干扰 Harmony 方法索引。
- [x] 四处版本号、README 与反馈记录同步；游戏已退出，构建/工坊待上传目录/测试版工坊目录/正式版快照目录 DLL 与清单均为 0.10.3.4，SHA-256 为 AC2B291F21011CFE49D6CD05EB1F84123BA574AC0D4E7CC4B3B69342D154ACDA。本轮随代码提交；未上传工坊，实机验证交用户。
