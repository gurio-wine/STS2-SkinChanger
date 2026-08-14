# STS2 皮肤切换器

一个独立的《杀戮尖塔 2》皮肤管理 Mod。它不会修改其他 Mod，也不依赖旧版“皮肤管理器”。

## 功能

- 自动识别已加载、且 `affects_gameplay=false` 的纯外观 PCK。
- 按角色、NPC 以及怪物分别归类皮肤。
- 每一组都可切回游戏原版或当前玩法 Mod 提供的基础外观。
- 在选角界面切换当前角色的皮肤。
- 在怪物图鉴切换当前怪物的皮肤。
- NPC 皮肤目前只识别资源，不显示切换入口，等待确定合适位置。
- 选择后立即生成并挂载资源覆盖包，无需重启游戏。
- 选择保存在游戏用户目录的 `sts2_skin_switcher.json` 中，下次启动自动应用。
- 不改写任何已安装 Mod 的 PCK。

## 使用

将发布目录放到游戏的 `mods/STS2SkinChanger` 下并正常启用。角色皮肤选项会出现在选角界面，怪物皮肤选项会出现在怪物图鉴；没有对应皮肤时不会显示控件。

仅替换皮肤资源、并正确声明 `affects_gameplay=false` 的 Mod 才会作为候选项出现。混合玩法内容的 Mod 会作为当前基础资源的一部分，不会被误判为皮肤。

## 构建

项目引用本机游戏目录中的 `GodotSharp.dll`、`0Harmony.dll` 和 `sts2.dll`：

```bash
dotnet build STS2SkinChanger/STS2SkinChanger.csproj -c Release
```

产物位于 `STS2SkinChanger/bin/Release/STS2SkinChanger.dll`。

## 开发检查工具

- `tools/PckInspect`：检查 PCK 目录或复制少量文件验证写入器。
- `tools/CatalogInspect`：用实际游戏与 Mod PCK 构建皮肤目录，输出识别结果。

设置环境变量 `STS2_SKIN_SWITCHER_SMOKE_TEST=1`，或在游戏用户目录创建一次性的 `sts2_skin_switcher_smoke_test` 文件后启动游戏，会自动切换一次首个可用皮肤并恢复原选择；结果只写入游戏日志，供开发验证使用。
