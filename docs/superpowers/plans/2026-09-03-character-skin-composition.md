# Character Skin Composition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the independent character-avatar selector with saved, ordered character-skin compositions that behave as ordinary skins everywhere, including multiplayer.

**Architecture:** Keep raw `SkinOption` entries immutable, store composition recipes in `SkinConfig`, and let `SkinCatalog` materialize each recipe as a virtual `SkinOption`. A pure policy owns validation, naming, hiding, ordered first-wins merging, and remote partial-match behavior; every preview/runtime/icon path then consumes the same selected virtual option through the existing overlay pipeline.

**Tech Stack:** C# 13, .NET 9, Godot C# UI, Harmony, STS2 resource PCK overlays, existing console-based logic/runtime test projects.

**Spec:** `docs/superpowers/specs/2026-09-03-character-skin-composition-design.md`

## Global Constraints

- Work directly on `master`; do not create a worktree or dispatch subagents.
- A composition may contain one or more raw skins; nested compositions are forbidden.
- Earlier sources win each canonical resource target; later sources only fill missing targets; the game/base Mod remains the final fallback.
- Only the earliest dynamic provider runs; later sources contribute static resources only.
- Pure avatar packs are ordinary skins; there is no independent avatar selector.
- Multiplayer transmits source identities only, never downloads resources, and resolves missing sources independently per remote player.
- All new player-facing text must cover the existing 15 languages.
- Every implementation commit increments the four-part internal test version; Workshop upload remains explicit.

---

### Task 1: Composition policy and persisted recipes

**Files:**
- Create: `STS2SkinChanger/Core/CharacterSkinCompositionPolicy.cs`
- Modify: `STS2SkinChanger/Core/SkinConfig.cs`
- Modify: `tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj`
- Modify: `tests/STS2SkinChanger.LogicTests/Program.cs`
- Modify: `STS2SkinChanger/STS2SkinChanger.csproj`

**Interfaces:**
- Produces: `CharacterSkinComposition` with `Id`, `GroupId`, `Name`, `SourceOptionIds`, and `HideSources`.
- Produces: `CharacterSkinCompositionPolicy.Normalize`, `UniqueName`, `VisibleRawOptionIds`, `ResolveAvailableSourceIds`, and generic `ResolveAssets<T>`.
- Produces: `SkinConfig.CharacterSkinCompositions` and preserves `CharacterIconSelections` only as a legacy migration input.

- [ ] **Step 1: Write failing policy tests**

Add literal assertions covering single-source aliases, duplicate source removal, stable recipe IDs, duplicate-name suffixes, union hiding across multiple recipes, missing-source skipping and restoration, canonical first-wins assets, and first dynamic source selection.

```csharp
var resolved = CharacterSkinCompositionPolicy.ResolveAssets(
    ["primary", "fallback"],
    new Dictionary<string, CharacterSkinCompositionSource<string>>
    {
        ["primary"] = new("primary", true, new Dictionary<string, string> { ["res://a"] = "A1" }),
        ["fallback"] = new("fallback", true, new Dictionary<string, string>
        {
            ["RES://A"] = "A2",
            ["res://b"] = "B2"
        })
    },
    path => path.ToLowerInvariant());
Require(resolved.Assets["res://a"] == "A1" && resolved.Assets["res://b"] == "B2",
    "合并皮肤必须前项覆盖、后项补缺。");
Require(resolved.DynamicSourceId == "primary", "只能运行最高优先级动态来源。");
```

- [ ] **Step 2: Run logic tests and verify RED**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj -c Release`

Expected: compilation fails because the composition policy and config model do not exist.

- [ ] **Step 3: Implement the minimal pure policy and config normalization**

Use a `composition:` plus lowercase GUID ID, trim and cap names to 40 characters, preserve missing source IDs in recipes, remove only blank/duplicate IDs, and rebuild all dictionaries/lists with case-insensitive comparers during `SkinConfig.Deserialize`.
An empty name uses the localized base name `合并皮肤`/`Combined Skin` plus the first available positive integer; an explicit duplicate name receives the same numeric suffix rule without changing any existing recipe ID.

```csharp
internal sealed class CharacterSkinComposition
{
    public string Id { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> SourceOptionIds { get; set; } = [];
    public bool HideSources { get; set; }
}
```

- [ ] **Step 4: Run logic tests and verify GREEN**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj -c Release`

Expected: `Skin Changer logic policy tests passed.`

- [ ] **Step 5: Bump internal version and commit**

Set all four-part assembly versions to `0.9.132.5`, then commit:

```bash
git add STS2SkinChanger/Core/CharacterSkinCompositionPolicy.cs STS2SkinChanger/Core/SkinConfig.cs STS2SkinChanger/STS2SkinChanger.csproj tests/STS2SkinChanger.LogicTests
git commit -m "feat: persist character skin compositions"
```

### Task 2: Catalog materialization and unified resource resolution

**Files:**
- Modify: `STS2SkinChanger/Catalog/SkinCatalog.cs`
- Modify: `STS2SkinChanger/Core/SkinService.cs`
- Modify: `tests/STS2SkinChanger.LogicTests/Program.cs`
- Modify: `tests/STS2SkinChanger.RuntimeTests/Program.cs`
- Modify: `STS2SkinChanger/STS2SkinChanger.csproj`

**Interfaces:**
- Consumes: normalized `CharacterSkinComposition` and `CharacterSkinCompositionPolicy.ResolveAssets<T>`.
- Produces: `SkinCatalog.SynchronizeCharacterSkinCompositions`, `GetRawCharacterOptions`, `GetCompositionSourceOptionIds`, and `TryCreateSessionCharacterComposition`.
- Produces: `SkinService.GetCharacterSkinOptions`, CRUD methods for saved compositions, and a session-composition overload used by multiplayer.

- [ ] **Step 1: Write failing catalog/service boundary tests**

Add runtime reflection checks proving `SkinOption` carries composition sources, the catalog exposes the four composition methods, `SkinService.ApplySelection` no longer rejects icon-only raw options, and the old automatic icon injection call is absent from `BuildGroups` behavior through an extracted policy seam.

Add logic tests for the selection transaction helper: a composition resolves its dynamic provider for linked runtime groups, while a static-only composition affects only the current group.

- [ ] **Step 2: Run both test projects and verify RED**

Run:

```bash
dotnet run --project tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj -c Release
dotnet run --project tests/STS2SkinChanger.RuntimeTests/STS2SkinChanger.RuntimeTests.csproj -c Release
```

Expected: missing composition-aware catalog/service members.

- [ ] **Step 3: Materialize virtual options and remove automatic avatar injection**

Stop calling `MergeCharacterSelectIconPacks`. Keep pure icon `SkinOption` entries in the group, mark virtual entries with ordered `CompositionSourceOptionIds`, choose the first source whose option has runtime behavior as `ProviderId`, and merge `ResourceAsset` ownership by normalized takeover path before adding the virtual option.

```csharp
internal sealed record SkinOption(...)
{
    public IReadOnlyList<string> CompositionSourceOptionIds { get; init; } = [];
    public bool IsComposition => CompositionSourceOptionIds.Count > 0;
}
```

Ensure full-runtime checks use `EffectiveProviderId` through `ResolveVisualProviderId`; selected dependencies still come from each winning `ResourceAsset.Files` closure.
Expose the ordered providers behind a virtual option to localization filtering. Return selected cosmetic localization tables from lowest to highest composition priority so the existing last-table-wins merge leaves the first recipe source authoritative while still allowing later sources to fill absent fields.

- [ ] **Step 4: Add transactional composition CRUD and legacy avatar migration**

At catalog initialization, normalize recipes, migrate each explicit legacy avatar selection to `[avatar, current skin]` (or `[avatar]` over base), synchronize virtual options, resolve stored selections, then clear the legacy field. Saving/editing/deleting a recipe must snapshot config, synchronize options, apply the selection and overlay, save atomically, and restore the snapshot/catalog/overlay on failure.

Expose visible options as base raw options minus the union of hidden sources plus every valid virtual composition. Use this in both the character-select selector and in-run appearance selector.
Deleting the active composition switches that character to base before removing the virtual option. Editing the active recipe remounts it immediately; editing an inactive recipe only rebuilds list state. Cache materialized options by recipe revision and catalog fingerprint, and invalidate only the affected character when recipes or installed sources change.

- [ ] **Step 5: Run both test projects and verify GREEN**

Expected: both console suites print their pass messages.

- [ ] **Step 6: Bump internal version and commit**

Set version to `0.9.132.6` and commit:

```bash
git add STS2SkinChanger/Catalog/SkinCatalog.cs STS2SkinChanger/Core/SkinService.cs STS2SkinChanger/STS2SkinChanger.csproj tests
git commit -m "feat: resolve composed character skins"
```

### Task 3: Remove the independent avatar UI and route every icon through the selected skin

**Files:**
- Delete: `STS2SkinChanger/Core/CharacterIconSelectionPolicy.cs`
- Modify: `STS2SkinChanger/Ui/ContextualSkinControls.cs`
- Modify: `STS2SkinChanger/Ui/CharacterAppearanceScreen.cs`
- Modify: `tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj`
- Modify: `tests/STS2SkinChanger.LogicTests/Program.cs`
- Modify: `tests/STS2SkinChanger.RuntimeTests/Program.cs`
- Modify: `STS2SkinChanger/STS2SkinChanger.csproj`

**Interfaces:**
- Consumes: `SkinService.GetCharacterSkinOptions` and the ordinary `GetVisualSelection` path.
- Removes: `GetCharacterIconOptions`, `GetCharacterIconSelection`, `ApplyCharacterIconSelection`, avatar dropdown metadata/callbacks, and resource-specific selection policy.
- Preserves: the icon texture/scene Harmony patches, now loading the ordinary selected option so local, preview, map, top-panel, and remote-player scopes stay unified.

- [ ] **Step 1: Write failing tests for the unified icon contract**

Update logic tests to assert an icon-only source is a valid one-source skin and remove old independent-resolution assertions. Add runtime checks that `BuildSelector` has no icon-control parameter and `SkinService` no longer exposes the independent write API.

- [ ] **Step 2: Run both test projects and verify RED**

Expected: the old selector/API shape is still present.

- [ ] **Step 3: Remove the separate controls and selection branch**

Build one character skin dropdown, include pure icon options and virtual compositions, and make character icon loading use `GetVisualSelection(groupId)` directly. Keep all existing icon refresh hooks so a normal skin switch still rebuilds already-created character buttons and multiplayer avatars.

- [ ] **Step 4: Run both test projects and verify GREEN**

Expected: both suites pass and no source references `CharacterIconSelectionPolicy`.

- [ ] **Step 5: Bump internal version and commit**

Set version to `0.9.132.7` and commit:

```bash
git add -A STS2SkinChanger tests
git commit -m "refactor: unify character skin and avatar selection"
```

### Task 4: Character-select composition editor and 15-language text

**Files:**
- Create: `STS2SkinChanger/Ui/CharacterSkinCompositionControls.cs`
- Modify: `STS2SkinChanger/Ui/ContextualSkinControls.cs`
- Modify: `STS2SkinChanger/Core/ModLocalization.cs`
- Modify: `tests/STS2SkinChanger.RuntimeTests/Program.cs`
- Modify: `STS2SkinChanger/STS2SkinChanger.csproj`

**Interfaces:**
- Consumes: composition CRUD and raw/visible option queries from `SkinService`.
- Produces: `CharacterSkinCompositionControls.Show(NCharacterSelectScreen, SkinGroup?, Action refresh)` and `Hide`.

- [ ] **Step 1: Write failing runtime structure tests**

Require the composition-control type and `Show` method, require the `ModText` keys for merge/create/name/hide/save/delete/unavailable, and verify every supported language returns non-empty text for each key.

- [ ] **Step 2: Run runtime tests and verify RED**

Expected: missing UI type and localization keys.

- [ ] **Step 3: Build the top-left entry and modal editor**

Attach one themed `皮肤合并` button to the character-select screen. Show it only when the current character has a non-base raw option. Open a full-screen non-penetrating mask with a centered original-style panel; list raw options only, put enabled sources first in draft order, provide enable/up/down controls, name field, hide checkbox, save/delete/close, and mark missing retained sources unavailable.

Saving calls the transactional service, immediately selects the result, repopulates the normal dropdown and invokes the existing full character-display refresh. One selected source remains valid.
Deleting the active entry refreshes to the game original; disabling the hide checkbox immediately returns its raw sources to the ordinary list after save.

- [ ] **Step 4: Add all 15 translations**

Add complete strings for `eng`, `zhs`, `zht`, `deu`, `esp`, `fra`, `ita`, `jpn`, `kor`, `pol`, `ptb`, `rus`, `spa`, `tha`, and `tur`; custom names and numeric order are never translated.

- [ ] **Step 5: Run runtime and logic tests and verify GREEN**

Run both console suites and require both pass messages.

- [ ] **Step 6: Bump internal version and commit**

Set version to `0.9.132.8` and commit:

```bash
git add STS2SkinChanger/Ui/CharacterSkinCompositionControls.cs STS2SkinChanger/Ui/ContextualSkinControls.cs STS2SkinChanger/Core/ModLocalization.cs STS2SkinChanger/STS2SkinChanger.csproj tests
git commit -m "feat: add character skin composition editor"
```

### Task 5: Multiplayer composition protocol and per-player partial resolution

**Files:**
- Modify: `STS2SkinChanger/Core/MultiplayerSkinSync.cs`
- Modify: `STS2SkinChanger/Core/SkinService.cs`
- Modify: `tests/STS2SkinChanger.RuntimeTests/Program.cs`
- Modify: `STS2SkinChanger/STS2SkinChanger.csproj`

**Interfaces:**
- Consumes: `SkinService.GetCharacterSelectionSourceIds` and session-composition resolution.
- Produces: protocol 9 `SourceOptionManifest`, a validated JSON array capped at 64 source IDs/32 KiB, and deterministic per-player transient option IDs.

- [ ] **Step 1: Write failing packet and resolution tests**

Assert protocol 9 round-trips ordered source IDs, rejects blank/duplicate/oversized manifests, preserves regular skins as one source, resolves `[installed-a, missing-b, installed-c]` to `[installed-a, installed-c]`, uses base when none remain, and never mutates `Config.Selections` for a remote player.

- [ ] **Step 2: Run runtime tests and verify RED**

Expected: packet lacks `SourceOptionManifest` and protocol remains 8.

- [ ] **Step 3: Extend advertisement and receive resolution**

Serialize the ordered source list after `OptionId`; advertise a regular skin as one source and base as an empty list. On receive, map only locally recognized raw option IDs, build or reuse a deterministic transient composition for two or more matches, use the raw option directly for one match, and explicit base for none. Store the resulting selection only in that player's `SessionCharacterSelection`.

Increment capability magic to `GSCAP09!`; incompatible peers retain the existing safe base fallback and never block the lobby.

- [ ] **Step 4: Run runtime and logic tests and verify GREEN**

Expected: both suites pass, including packet bit length and per-player isolation assertions.

- [ ] **Step 5: Bump internal version and commit**

Set version to `0.9.132.9` and commit:

```bash
git add STS2SkinChanger/Core/MultiplayerSkinSync.cs STS2SkinChanger/Core/SkinService.cs STS2SkinChanger/STS2SkinChanger.csproj tests
git commit -m "feat: sync composed character skins"
```

### Task 6: Cross-version verification and local deployment

**Files:**
- Modify only if verification exposes a defect: files owned by Tasks 1-5
- Verify: `STS2SkinChanger/SkinChanger.json` remains on the public three-part version until Workshop upload.

**Interfaces:**
- Consumes: the completed feature.
- Produces: formal and public-beta builds, deployed local test DLLs, hashes, and a clean committed tree.

- [ ] **Step 1: Run repository checks**

```bash
git diff --check
dotnet run --project tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj -c Release
dotnet run --project tests/STS2SkinChanger.RuntimeTests/STS2SkinChanger.RuntimeTests.csproj -c Release
dotnet build STS2SkinChanger/STS2SkinChanger.csproj -c Release
```

- [ ] **Step 2: Build against the public-beta assembly directory**

Build against the verified Steam public-beta assembly directory:

```bash
dotnet build STS2SkinChanger/STS2SkinChanger.csproj -c Release -p:GameAssemblyDir="/mnt/d/Programs/Steam/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64"
```

Expected: both formal and beta builds succeed with zero errors.

- [ ] **Step 3: Deploy the formal-compatible build to the shared local Workshop item**

Build the release-compatible DLL into a temporary directory, copy `Gurio.SkinChanger.dll`, `SkinChanger.json`, and `thunninoiSkinManager.dll` into `/mnt/d/Programs/Steam/steamapps/workshop/content/2868840/3787302680`, then compare SHA-256 hashes between the temporary build and deployed files. Both the formal launcher and current public-beta install consume this shared Workshop item. Do not call Steam Workshop upload tools.

- [ ] **Step 4: Final regression and repository audit**

Re-run both test projects after the final build, run `git status --short --branch`, and inspect the composition change blast radius. If a verification fix is required, write a failing regression test first, increment to the next internal suffix, commit it, and repeat all checks.
