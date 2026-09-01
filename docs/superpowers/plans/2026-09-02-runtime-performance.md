# Runtime Performance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restrict third-party skin behavior to the current visible scene and prepare only current resources without reducing visual fidelity.

**Architecture:** A pure scope policy decides which selected providers may execute. `SkinService` supplies current scene groups, `ManagedSkinModLoader` suspends provider-owned global nodes outside that scope, and a bounded asynchronous warmer reads only small current provider packs once per session.

**Tech Stack:** C# 13, .NET 9, Godot 4 C#, Harmony, repository console logic/runtime tests.

**Spec:** `docs/superpowers/specs/2026-09-02-runtime-performance.md`

## Global Constraints

- Preserve every third-party animation, effect, sound and visual layer.
- Publish-target compatibility remains formal v0.107.1 plus public beta v0.111.0.
- Do not change persisted skin configuration or multiplayer selection formats.
- Increment `Entry.InternalTestVersion` from `0.9.126` to `0.9.126.1`.
- Remove this temporary plan and its spec after the implementation is committed and verified.

---

### Task 1: Runtime provider scope policy

**Files:**
- Create: `STS2SkinChanger/Core/RuntimeProviderScopePolicy.cs`
- Modify: `tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj`
- Modify: `tests/STS2SkinChanger.LogicTests/Program.cs`

**Interfaces:**
- Produces: `RuntimeProviderScope`, `RuntimeProviderCandidate`, and `RuntimeProviderScopePolicy.SelectActiveProviders(...)`.
- Consumes: case-insensitive provider and group identifiers.

- [ ] **Step 1: Write failing policy tests**

Add literal fixtures covering boot, character preview, other-compendium preview, and run-wide monster behavior. Assert the exact selected provider IDs for each scope.

- [ ] **Step 2: Verify RED**

Run: `dotnet run --project tests/STS2SkinChanger.LogicTests/STS2SkinChanger.LogicTests.csproj -c Release`

Expected: compile failure because `RuntimeProviderScopePolicy` does not exist.

- [ ] **Step 3: Implement the minimal policy**

Implement immutable records and a case-insensitive overlap check. Empty visible groups activate nothing unless the scope explicitly allows a selected run-wide monster provider.

- [ ] **Step 4: Verify GREEN**

Run the same logic-test command and require exit code 0.

- [ ] **Step 5: Commit**

Commit message: `perf: scope runtime skin providers`

### Task 2: Scene-scoped provider activation and node suspension

**Files:**
- Modify: `STS2SkinChanger/Catalog/SkinCatalog.cs`
- Modify: `STS2SkinChanger/Core/SkinService.cs`
- Modify: `STS2SkinChanger/Core/ManagedSkinModLoader.cs`
- Modify: `STS2SkinChanger/Ui/ContextualSkinControls.cs`
- Modify: `STS2SkinChanger/Ui/CharacterAppearanceRuntime.cs`
- Modify: `STS2SkinChanger/Ui/AncientCompendium.cs`
- Modify: `STS2SkinChanger/Ui/MerchantRuntimeAppearance.cs`
- Modify: `tests/STS2SkinChanger.RuntimeTests/Program.cs`

**Interfaces:**
- Consumes: `RuntimeProviderScopePolicy.SelectActiveProviders(...)`.
- Produces: `SkinService.FocusRuntimeProviderBehaviorsOnGroups(IEnumerable<string>, bool, string)` and provider node suspend/resume lifecycle.

- [ ] **Step 1: Write failing runtime integration checks**

Add reflection checks that the formal/test game patch assembly exposes `NCombatRoom.Create(ICombatRoomVisuals, CombatRoomMode)` and that Skin Changer contains the generalized focus method. The test must fail before the method is added.

- [ ] **Step 2: Verify RED on formal and beta assemblies**

Run the runtime test once with the default formal directory and once with `-p:GameAssemblyDir=/mnt/d/Programs/Steam/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64`.

- [ ] **Step 3: Implement contextual activation**

Expose provider-owned visual groups from `SkinCatalog`; make startup scope empty; focus selection/bestiary/other-compendium/merchant/run contexts on their visible groups; include selected scoped-monster providers only during a run.

- [ ] **Step 4: Implement provider-node suspension**

Track valid `SceneTree` nodes whose managed type belongs to the provider assembly. Store original `ProcessMode`, disable on provider deactivation, and restore before reactivation without invoking the initializer twice.

- [ ] **Step 5: Verify GREEN**

Run both runtime-test commands and the logic tests. Require exit code 0.

- [ ] **Step 6: Commit**

Commit message: `perf: suspend offscreen skin runtimes`

### Task 3: Bounded current-resource warming and diagnostics

**Files:**
- Modify: `STS2SkinChanger/Core/SkinService.cs`
- Modify: `STS2SkinChanger/Core/ManagedSkinModLoader.cs`
- Modify: `STS2SkinChanger/Ui/CharacterAppearanceRuntime.cs`
- Modify: `STS2SkinChanger/Ui/ContextualSkinControls.cs`
- Modify: `tests/STS2SkinChanger.LogicTests/Program.cs`

**Interfaces:**
- Produces: `RuntimePackWarmPolicy.ShouldWarm(long sizeBytes, bool alreadyWarmed)` and one-session pack warming scheduled by the current behavior scope.

- [ ] **Step 1: Write failing warm-policy tests**

Assert that a fresh 32 MiB pack warms, a 64 MiB boundary pack warms, a pack larger than 64 MiB does not, and an already warmed pack does not warm again.

- [ ] **Step 2: Verify RED**

Run the logic tests and expect compilation failure because `RuntimePackWarmPolicy` does not exist.

- [ ] **Step 3: Implement bounded asynchronous warming and activation timing**

Create the pure policy, schedule existing asynchronous sequential reads only for active providers whose packs pass it, cache completed paths for the session, and log provider activation phases taking at least 50 ms.

- [ ] **Step 4: Verify GREEN**

Run logic tests plus both runtime test variants and require exit code 0.

- [ ] **Step 5: Commit**

Commit message: `perf: warm current skin resources`

### Task 4: Version, dual builds, deployment, and cleanup

**Files:**
- Modify: `STS2SkinChanger/Entry.cs`
- Delete: `docs/superpowers/specs/2026-09-02-runtime-performance.md`
- Delete: `docs/superpowers/plans/2026-09-02-runtime-performance.md`

**Interfaces:**
- Produces: local formal and beta-compatible `Gurio.SkinChanger.dll` identified as `0.9.126.1`.

- [ ] **Step 1: Increment the internal version**

Set `Entry.InternalTestVersion` to `0.9.126.1`.

- [ ] **Step 2: Run complete verification**

Run logic tests, formal runtime tests, beta runtime tests, formal Release build, and beta Release build. Require zero failures and exit code 0 for every command.

- [ ] **Step 3: Deploy both local game targets**

Copy the formal-built AnyCPU DLL and manifest to the formal-version local Mods directory and the beta-compatible build to the active beta Mods directory according to the repository's established deployment layout. Compare hashes at the actual load paths.

- [ ] **Step 4: Remove temporary documents**

Delete this plan and its spec after recording their completed implementation in git history.

- [ ] **Step 5: Commit**

Commit message: `chore: deploy performance test build 0.9.126.1`
