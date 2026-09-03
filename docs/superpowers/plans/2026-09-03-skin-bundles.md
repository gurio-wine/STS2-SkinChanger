# Character Skin Bundles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-character skin bundles that atomically apply one character skin plus optional card-category and monster-region presets.

**Architecture:** Store bundles as references to existing card and monster presets. A pure policy validates and rewrites references; `SkinService` owns persistence and an atomic apply transaction; a focused character-select control owns editing and application.

**Tech Stack:** C# 13, .NET 9, Godot C#, Harmony, existing logic/runtime test executables.

**Spec:** `docs/superpowers/specs/2026-09-03-skin-bundles-design.md`

## Global Constraints

- Work directly on `master`; do not create a worktree.
- Preserve existing card and monster presets and all current player settings.
- “Unchanged” categories are omitted from a bundle and must not be mutated when applied.
- Bump the internal test version to `0.9.132.20`, commit, and deploy both supported game versions locally.
- Do not upload Steam Workshop unless the user asks.

---

### Task 1: Bundle data and reference policy

**Files:**
- Create: `STS2SkinChanger/Core/CharacterSkinBundlePolicy.cs`
- Modify: `STS2SkinChanger/Core/SkinConfig.cs`
- Modify: `tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj`
- Test: `tests/STS2SkinChanger.LogicTests/Program.cs`

**Interfaces:**
- Produces: `CharacterSkinBundle`, `CharacterSkinBundlePolicy.Normalize`, `RenamePresetReference`, and `RemovePresetReference`.
- Consumes: character group IDs, visual option IDs, and category-to-preset-name dictionaries.

- [ ] **Step 1: Write failing policy tests**

```csharp
var bundle = new CharacterSkinBundle { CharacterGroupId = "regent", CharacterOptionId = "skin:a" };
bundle.CardPresetNames["regent"] = "Cards A";
CharacterSkinBundlePolicy.RenamePresetReference(bundle, false, "regent", "Cards A", "Cards B");
Require(bundle.CardPresetNames["regent"] == "Cards B", "重命名必须更新引用");
CharacterSkinBundlePolicy.RemovePresetReference(bundle, false, "regent", "Cards B");
Require(!bundle.CardPresetNames.ContainsKey("regent"), "删除必须变为不修改");
```

- [ ] **Step 2: Run the logic build and verify failure**

Run: `dotnet build tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj -c Release --no-restore`

Expected: FAIL because `CharacterSkinBundlePolicy` does not exist.

- [ ] **Step 3: Implement normalized persistent data**

```csharp
internal sealed class CharacterSkinBundle
{
    public string Name { get; set; } = string.Empty;
    public string CharacterGroupId { get; set; } = string.Empty;
    public string CharacterOptionId { get; set; } = SkinCatalog.BaseOptionId;
    public Dictionary<string, string> CardPresetNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> MonsterPresetNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
```

Add `List<CharacterSkinBundle> CharacterSkinBundles`, per-character active bundle names, and independent selector X/Y values to `SkinConfig`; normalize case-insensitive dictionaries during load.

- [ ] **Step 4: Run logic tests**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release --no-restore`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add STS2SkinChanger/Core/CharacterSkinBundlePolicy.cs STS2SkinChanger/Core/SkinConfig.cs tests/STS2SkinChanger.LogicTests
git commit -m "feat: add character skin bundle data"
```

### Task 2: Atomic bundle service

**Files:**
- Modify: `STS2SkinChanger/Core/SkinService.cs`
- Test: `tests/STS2SkinChanger.RuntimeTests/Program.cs`

**Interfaces:**
- Produces: `GetCharacterSkinBundles`, `CreateCharacterSkinBundle`, `OverwriteCharacterSkinBundle`, `RenameCharacterSkinBundle`, `DeleteCharacterSkinBundle`, and `ApplyCharacterSkinBundle`.
- Consumes: existing card/monster preset services and character visual-selection transactions.

- [ ] **Step 1: Add failing runtime contract and rollback tests**

```csharp
foreach (var method in new[] { "GetCharacterSkinBundles", "CreateCharacterSkinBundle",
    "OverwriteCharacterSkinBundle", "RenameCharacterSkinBundle",
    "DeleteCharacterSkinBundle", "ApplyCharacterSkinBundle" })
    Require(skinServiceType.GetMethod(method,
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null,
        $"缺少 {method}");
```

- [ ] **Step 2: Run runtime tests and verify failure**

Run: `dotnet run --project tests/STS2SkinChanger.RuntimeTests -c Release --no-restore`

Expected: FAIL on the first missing bundle service method.

- [ ] **Step 3: Implement CRUD and one transaction**

Resolve all references first. Copy selections, card priorities, monster priorities/follow state, active presets, and provider priority before mutation. Apply every valid item in memory, save once, then mount and refresh affected groups. On exception restore the snapshot and overlays. Return skipped reference names without failing valid items.

- [ ] **Step 4: Update preset rename/delete paths**

After card or monster preset rename, call `CharacterSkinBundlePolicy.RenamePresetReference`; after delete, call `RemovePresetReference`. Persist these changes in the existing save operation.

- [ ] **Step 5: Run logic and runtime tests**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release --no-restore && dotnet run --project tests/STS2SkinChanger.RuntimeTests -c Release --no-restore`

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add STS2SkinChanger/Core tests/STS2SkinChanger.RuntimeTests
git commit -m "feat: apply character skin bundles atomically"
```

### Task 3: Character-select bundle editor

**Files:**
- Create: `STS2SkinChanger/Ui/CharacterSkinBundleControls.cs`
- Modify: `STS2SkinChanger/Ui/ContextualSkinControls.cs`
- Modify: `STS2SkinChanger/Core/ModLocalization.cs`
- Test: `tests/STS2SkinChanger.RuntimeTests/AppearanceControlContractTests.cs`

**Interfaces:**
- Produces: a draggable `皮肤包` button and themed editor scoped to the current character.
- Consumes: bundle CRUD/apply methods and existing draggable-control placement policy.

- [ ] **Step 1: Add failing UI contract tests**

```csharp
var bundleControls = assembly.GetType("STS2SkinChanger.Ui.CharacterSkinBundleControls") ??
    throw new InvalidOperationException("缺少选角界面皮肤包控件");
Require(bundleControls.GetMethod("ShowForCharacter",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null,
    "缺少当前角色刷新入口");
Require(configType.GetProperty("CharacterSkinBundleX") != null &&
        configType.GetProperty("CharacterSkinBundleY") != null, "皮肤包按钮位置必须独立保存");
```

- [ ] **Step 2: Run runtime tests and verify failure**

Run: `dotnet run --project tests/STS2SkinChanger.RuntimeTests -c Release --no-restore`

Expected: FAIL because `CharacterSkinBundleControls` is absent.

- [ ] **Step 3: Implement the draggable button and editor**

Use the current skin-merge editor's draggable handle, modal mask, theme, action buttons, and second-click red delete confirmation. Populate character options from `GetCharacterSkinOptions`; populate card and monster rows from existing presets grouped by category, with the first option representing “不修改”.

- [ ] **Step 4: Refresh after apply**

After successful apply, call the existing character-select rebuild path once so the large preview, icon, name, description, and provider controls update together. Display skipped references in one non-blocking result message.

- [ ] **Step 5: Run tests**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests -c Release --no-restore && dotnet run --project tests/STS2SkinChanger.RuntimeTests -c Release --no-restore`

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add STS2SkinChanger/Ui STS2SkinChanger/Core/ModLocalization.cs tests/STS2SkinChanger.RuntimeTests
git commit -m "feat: add character skin bundle editor"
```

### Task 4: Version, documentation, and dual-version deployment

**Files:**
- Modify: `README.md`
- Modify: `STS2SkinChanger/STS2SkinChanger.csproj`
- Modify: `STS2SkinChanger/SkinChanger.json`

**Interfaces:**
- Produces: internal version `0.9.132.20` and locally deployed architecture-neutral DLL.

- [ ] **Step 1: Update documentation and version**

Document the per-character bundle button and optional preset behavior. Set all assembly version fields and `internal_test_version` to `0.9.132.20`; retain public version `0.9.132`.

- [ ] **Step 2: Run complete formal and beta verification**

```bash
dotnet run --project tests/STS2SkinChanger.LogicTests -c Release --no-restore
dotnet run --project tests/STS2SkinChanger.RuntimeTests -c Release --no-restore
dotnet build STS2SkinChanger/STS2SkinChanger.csproj -c Release --no-restore -o /tmp/skin-changer-formal
dotnet build STS2SkinChanger/STS2SkinChanger.csproj -c Release --no-restore -p:GameAssemblyDir=/mnt/d/Programs/Steam/steamapps/common/'Slay the Spire 2'/data_sts2_windows_x86_64 -o /tmp/skin-changer-beta
```

Expected: every command exits 0 with no build warnings or errors.

- [ ] **Step 3: Commit and deploy**

```bash
git add README.md STS2SkinChanger/STS2SkinChanger.csproj STS2SkinChanger/SkinChanger.json
git commit -m "chore: prepare skin bundle internal build"
```

After confirming the game is not running, copy the formal AnyCPU DLL, compatibility DLL, and manifest to Workshop item `3787302680`; compare source and deployed SHA-256 hashes.
