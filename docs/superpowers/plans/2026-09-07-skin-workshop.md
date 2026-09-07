# Skin Workshop Implementation Plan

> For agentic workers: execute inline using executing-plans; user explicitly requires master and no subagents/worktrees.

**Goal:** Curated, contextual Workshop subscription browser with truthful download/activation states.
**Architecture:** Embedded validated ID/tag catalog; independent Steam subscription service; themed paginated panel; explicit selector command handling; conservative staged resource-only catalog registration.
**Tech Stack:** C#/.NET 9 AnyCPU, Godot, Steamworks.NET, existing SkinCatalog and theme/localization infrastructure.
**Spec:** `docs/superpowers/specs/2026-09-07-skin-workshop-design.md`

## Global Constraints

No global Workshop search, no automatic subscription, no old multiplayer cache lifecycle, no gameplay changes or unsafe runtime DLL initialization. Keep old selections/resources on failure. Formal Release is deployment source; ReleaseBeta validates beta. User performs real-game checks.

## Tasks

- [x] Add failing behavioral tests for catalog validation/filtering, command exclusion, download completion and hot-load eligibility; run red.
- [x] Implement pure catalog/state policies and seed/export tool using real recognized local Workshop providers; verify green.
- [x] Add Steam exact-ID metadata, bounded async covers/cache, permanent subscription/download service and conservative complete hot-registration.
- [x] Add themed Workshop panel, contextual last-option entries and refresh callbacks; no saving/previewing the command.
- [x] Add all 15 UI translations and integration/regression tests including both game references.
- [x] Bump 0.10.11.3, build/test, review and deploy after game exit; all three target hashes match the formal Release artifact. Included in this change's master commit; no Workshop/GitHub publication.

## Review / verification notes

- Final export: 82 recognized Workshop IDs from 120 local installed manifests, including providers without a PCK. IDs without recognized cosmetic targets, local-only sources and SC itself are excluded. Developer-only exporter references the actual production catalog; no runtime subscription scan populates the list.
- Conservative hot gate rejects DLLs, scripts, native extensions, gameplay/dependency/version-constrained manifests, unclassified extra files, binary scenes without provable dependencies, and missing resource references. Existing provider IDs/changed installation snapshots require restart. No downloaded executable is initialized.
- Candidate catalog/resource inspection runs on a worker; publication retains current choices and appends new priority entries disabled. Existing resource holders retain their archive lifetimes; no global list of retired catalogs. A failed config save restores the previous config and never publishes the staged catalog.
- Tests exercised real synthetic PCKs (complete resources / extra unmanaged files / missing dependencies), ID dedup/filtering, command exclusion from random, predecode image bounds, all 15 text packs, embedded catalog and scoped native restart notification handling. Steam network and Godot visual behavior still require user in-game testing, per project rules.
- SDK reference: https://partner.steamgames.com/doc/api/ISteamUGC ; use explicit item IDs, query release in finally, item download result plus install-state validation, Steam-owned permanent subscriptions.
- Final checks: LogicTests, full RuntimeTests formal and beta (including Workshop tests), Release build with zero warnings/errors, Test-BuildEnvironment and git diff --check passed. Beta offline runner reports the expected missing Sentry GDExtension outside Godot. No game was launched and no Steam subscription was made during development.
