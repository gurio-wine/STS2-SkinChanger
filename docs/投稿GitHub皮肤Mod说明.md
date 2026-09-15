# 把你的皮肤发到 GitHub（作者说明）

其他语言：[English](publish-github-skin-mod.md) · [繁體中文](publish-github-skin-mod.zh-Hant.md) · [日本語](publish-github-skin-mod.ja.md) · [한국어](publish-github-skin-mod.ko.md) · [Русский](publish-github-skin-mod.ru.md) · [Deutsch](publish-github-skin-mod.de.md) · [Français](publish-github-skin-mod.fr.md) · [Italiano](publish-github-skin-mod.it.md) · [Polski](publish-github-skin-mod.pl.md) · [Português (Brasil)](publish-github-skin-mod.pt-BR.md) · [ไทย](publish-github-skin-mod.th.md) · [Türkçe](publish-github-skin-mod.tr.md) · [Español (España)](publish-github-skin-mod.es.md) · [Español (LatAm)](publish-github-skin-mod.es-419.md)

玩家装了「皮肤切换器」后，会在游戏里的 **皮肤创意工坊 → 来源：GitHub** 里看到你的仓库，点一下就能安装。
你要做的只有三件事：加一个标签、发一个 zip、贴一段码。

（自己先用面板扫描，需要先装好皮肤切换器，Steam 创意工坊搜 Skin Changer。）

## 一、给仓库加标签

仓库页面右侧 About 旁的齿轮 → Topics 填：

```
sts2-sc-mod
```

标签没加或拼错，游戏里就找不到你的仓库；fork 出来的仓库也不会出现。

## 二、发一个 Release，附件传 zip

- zip 里就是游戏能加载的那份皮肤包（`<id>.json` + `.pck` / `.dll`，纯卡图素材包也行）。
- 包内文件放在 zip 的**根目录**，或者放在文件夹里（套几层都行；只有包里含多个 Mod 时，才优先挑文件夹名与仓库同名的那一个）。
- 只认 `.zip`：`.rar`、`.7z`、`.tar.gz` 一律当作没有附件，仓库会停在「未识别」。
- 挂在**最新**的那个 Release 上，**不要勾 pre-release**（勾了面板读不到）；单个附件不超过 128 MB。
- 只传一个 zip 最省事。传了多个时，面板优先挑名字与仓库同名的那个，否则挑最大的。
- 压缩包可以**先发 Release、后补附件**：点「扫描」时会重新读取实时 Release，不用改 tag 或重新提交。

## 三、把信息码存成 sc.info

1. 进游戏 → 皮肤创意工坊 → 来源切到 **GitHub** → 筛选选 **未识别** → 找到你的仓库。
2. 点 **扫描**（这时才会下载你的 zip，扫完即删）。弹窗里会出现 1 段或多段**信息码**，每段右边都有「复制」。
3. 在仓库**根目录**新建文件 `sc.info`，把码粘进去，提交。
4. 回游戏点刷新：你的仓库就从「未识别」变成可以安装的皮肤（卡片名字用的是**仓库名**）。

### 多段码怎么贴

**一段码一行，从上到下依次粘**，就这样，不用想别的：

```
SCM3 6714 3f2a…（示意，实际是一整行很长的字符）1/2 eJw…Cd34
SCM3 6714 3f2a…（示意，实际是一整行很长的字符）2/2 eJw…Cd34
```

只有四条硬规则：

- **一段码不能断行**：一段码必须完整待在同一行里。编辑器把长行折起来显示没关系，手动敲回车就废了。
- **一段都不能少**：弹窗里几段就贴几段。少一段整个包读不出来，仓库还是「未识别」。
- **不要手改**：码自带校验，改一个字符（加个空格也算）就失效。
- **不要括引号**：写成 `"SCM3 …"` 会被当成引用文本忽略掉。

其余都宽松：顺序随意、中间空几行随意、夹标题和说明文字随意、包在 Markdown 代码块里随意、同一行用空格连着写两段也认。整个文件别超 64 KB。

## 玩家那边看到什么

类型 / 对象标签和「需要重启」都来自那段信息码，扫描时自动判定，你不用手填。包装好游戏不会立刻加载，所以面板会像 Steam 一样提示「需要重启」，重启后才生效。

## 几个坑

- **改仓库名、或换账号**：`作者/仓库` 变了，旧信息码立刻作废，重新扫一次再提交。
- **只发零散 dll / pck、不发 zip**：仓库会列出来，但永远是「未识别」，没有安装按钮。
- **贴了别的仓库的码**：同样算「未识别」——码是和仓库绑定的。
- **更新皮肤**：发新 Release 就行。只有**替换对象变了**，或者**加了脚本 / DLL（重启要求变了）**才需要重新扫描、更新 `sc.info`；只换图片不用。

支持的替换对象：角色、卡牌、怪物、先古、商人、伙伴、事件。
