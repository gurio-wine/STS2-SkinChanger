# Publish your skin mod on GitHub (author guide)

Translations: [简体中文](投稿GitHub皮肤Mod说明.md) · [繁體中文](publish-github-skin-mod.zh-Hant.md) · [日本語](publish-github-skin-mod.ja.md) · [한국어](publish-github-skin-mod.ko.md) · [Русский](publish-github-skin-mod.ru.md) · [Deutsch](publish-github-skin-mod.de.md) · [Français](publish-github-skin-mod.fr.md) · [Italiano](publish-github-skin-mod.it.md) · [Polski](publish-github-skin-mod.pl.md) · [Português (Brasil)](publish-github-skin-mod.pt-BR.md) · [ไทย](publish-github-skin-mod.th.md) · [Türkçe](publish-github-skin-mod.tr.md) · [Español (España)](publish-github-skin-mod.es.md) · [Español (LatAm)](publish-github-skin-mod.es-419.md)

With Skin Changer installed, players see your repository in game under **Skin Workshop → Source: GitHub** and can install it with one click.
There are only three things to do: add one topic, publish one zip, paste one code.

(To scan your own package you need Skin Changer first — search "Skin Changer" on the Steam Workshop.)

## 1. Add the topic

On your repository page: gear icon next to About → Topics:

```
sts2-sc-mod
```

Misspell it or leave it out and the game never finds your repository; forks are skipped too.

## 2. Publish a Release with the zip

- The zip is the skin package the game loads (`<id>.json` + `.pck` / `.dll`; a card-art-only pack works too).
- Put its files either at the **root** of the zip, or inside folders (any depth works; a folder named after the repository is only preferred when the zip holds several Mods).
- Only `.zip` is read: `.rar`, `.7z` and `.tar.gz` count as no attachment at all, and the repository stays "Not recognized".
- Attach it to your **latest** Release and **do not** mark it pre-release (the panel cannot read those). One attachment must stay under 128 MB.
- One zip is the easiest. With several, the panel prefers the one named after the repository, otherwise the largest.
- The zip may be **added after the Release was published**: Scan re-reads the live Release, so no new tag or re-submission is needed.

## 3. Save the code as sc.info

1. In game → Skin Workshop → Source **GitHub** → filter **Not recognized** → find your repository.
2. Click **Scan** (this is the only moment your zip is downloaded, and it is deleted right after). The window lists one or more **codes**, each with a Copy button.
3. In the **repository root**, create a file named `sc.info`, paste the codes in, and commit.
4. Back in game, click Refresh: your repository turns from "Not recognized" into an installable skin (the card is named after the **repository**).

### Pasting several codes

**One code per line, top to bottom.** That is all there is to it:

```
SCM3 6714 3f2a… (illustration; the real code is one long line) 1/2 eJw…Cd34
SCM3 6714 3f2a… (illustration; the real code is one long line) 2/2 eJw…Cd34
```

Only four hard rules:

- **Never break a code across lines.** A code must stay complete on one line. Your editor wrapping a long line is fine; pressing Enter inside a code is not.
- **Paste every code.** As many as the window showed. Miss one and the whole package fails to read — the repository stays "Not recognized".
- **Do not edit a code.** Each one carries a checksum; changing a single character (even adding a space) breaks it.
- **Do not put a code in double quotes.** `"SCM3 …"` is treated as quoted text and ignored.

Everything else is loose: order does not matter, blank lines do not matter, headings and prose around the codes are fine, a Markdown code fence around them is fine, and two codes separated by a space on one line also work. Keep the file under 64 KB.

## What players see

The type/target labels and the "restart required" hint come from the code — they are detected while scanning, so you never fill them in. A freshly installed package is not loaded by the running game, so the panel asks for a restart, exactly like Steam does; it takes effect after the restart.

## Common mistakes

- **Renaming the repository or switching accounts**: the `owner/repository` pair changes, old codes stop working — scan again and commit the new ones.
- **Publishing only loose dll / pck files, no zip**: your repository is listed but stays "Not recognized", with no install button.
- **Pasting a code from another repository**: also "Not recognized" — a code is bound to its own repository.
- **Updating the skin**: just publish a new Release. You only need to rescan and update `sc.info` when the **replaced targets changed** or you **added a script / DLL (which changes the restart requirement)**; swapping images does not need it.

Supported targets: character, cards, monster, Ancient, merchant, companion, event.
