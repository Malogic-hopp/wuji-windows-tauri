# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

WUJI (吾迹) is a local-first Windows activity-recording desktop app. This repo contains only the Rebuild v0.1 chain: React 19 UI → Tauri 2 Rust Host → Rust Agent → SQLite v0.1 (single-writer). The legacy WPF/C#/Bridge system was removed (ADR-003). All user-facing text is Chinese-first.

Key facts:
- All data/process isolation uses fixed names: channel `WUJI_REBUILD_CHANNEL` env var, process `wuji-rebuild-agent-v01.exe`, named pipe, mutex, data root `%LOCALAPPDATA%\WUJI-Rebuild-V01`.
- The Agent is the **only writer** of the behavior database; the Tauri Host opens it read-only. React never connects to the pipe, queries SQLite, or recomputes domain aggregates.
- "暂停记录" (pause) only pauses capture, the Agent process stays online. Desktop exit does not implicitly terminate the Agent. `capture_start` ensures the Agent is running first.
- Closing/minimizing the Desktop window hides to tray; it does not quit.
- Old WUJI/WUJI-Dev databases must never be modified or deleted (package validation may only checksum them).

## Commands

Run Rust commands from the repository root (`rust-toolchain.toml` pins Rust 1.97):

```powershell
cargo fmt --all -- --check
cargo check --workspace --all-targets
cargo clippy --workspace --all-targets -- -D warnings   # clippy must pass with -D warnings
cargo test --workspace                                   # all Rust tests
cargo test -p <crate> <test_name>                        # single test, e.g. cargo test -p wuji-core bindings
```

Run frontend commands from `apps/desktop/` (Node 24.14.0, pnpm 11.9.0):

```powershell
pnpm install --frozen-lockfile
pnpm typecheck        # tsc -b
pnpm lint             # eslint, --max-warnings 0
pnpm test             # vitest run; single file: pnpm test src/features/settings/SettingsPage.test.tsx
pnpm build
```

Dev/package entry scripts at repo root (PowerShell):
- `rebuild-tauri-dev.ps1` — sets `WUJI_REBUILD_CHANNEL=rebuild-v01-dev` and runs `pnpm tauri dev`; does NOT build/start the Agent itself.
- `rebuild-agent.ps1` — `cargo build -p wuji-rebuild-agent` (debug).
- `rebuild-package.ps1` — runs `scripts/build_dev_package.py` (NSIS dev package + acceptance validation). Any change to directories, bundling, sidecar, or release path must run this at least once.

## Architecture

```
React UI
  │ fixed Tauri command allowlist (invoke)
  ▼
Tauri Rust Host ── read-only queries ──► SQLite v0.1
  │ Named Pipe IPC (command/control)
  ▼
Rust Agent ── single writer ───► SQLite v0.1
  └─ Capture → Privacy filter → Processor (Activity/Work state machine) → Writer
```

Cargo workspace members: `apps/agent` (crate `wuji-rebuild-agent`), `apps/desktop/src-tauri`, `crates/wuji-core`, `crates/wuji-storage`, `crates/wuji-windows`.

- `wuji-core`: domain, Settings, DTOs, error contract (`SafeError` + frozen `SafeErrorCode`), runtime names. Must not depend on Tauri, Win32, or SQLite.
- `wuji-storage`: SQLite schema (`schema/schema.sql`, embedded at build), Writer, read-only Reader.
- `wuji-windows`: Win32 capture, foreground/idle, process handles, named-pipe wrappers, session/power events.
- `apps/agent`: capture pipeline, Activity/Work state machine, `CaptureCoordinator` (single serialization point), Barrier/WriterControl ack path, settings reconcile/store, command server (named pipe IPC server).
- `apps/desktop/src-tauri`: IPC client, QueryService (read-only), SettingsService, DesktopPrefsService (auto-start-recording preference, 09 §9.4), ControlService (shared semantic control path), AgentController (process lifecycle), tray, single-instance. All Tauri commands are declared in `invoke_handler` in `src-tauri/src/lib.rs` (15 commands: agent_process_stop, agent_get_status, capture_start/pause/resume, activity_get_today/timeline/heatmap, settings_get/update/resync_login_startup, desktop_prefs_get/update, auto_start_status, diagnostics_get_summary).
- Startup orchestration (pref on) uses `ControlService.ensure_recording()` → internal Agent command `capture_ensure_recording` (Coordinator-atomic: Stopped→Start / Paused→Resume / Running→idempotent; not exposed to React, 09 §8.2). Startup outcome lives in host-side `AutoStartOutcome` (visible to top bar via `auto_start_status`, never just stderr).
- `apps/desktop/src`: React app (pages: Today, Timeline, Heatmap, Settings, Diagnostics). `src/bridge/client.ts` is the single typed `invoke` surface mirroring the Rust command allowlist; `src/types/wuji-core.ts` is generated (see below).

### Control boundaries

Capture, Settings, and Lock/Sleep lifecycle changes are serialized through the single `CaptureCoordinator` and the Barrier/Writer acknowledgement path: `transition lock → freeze → Barrier injected ack → WriterControl → Writer ack → publish/watch → restore`. Fail closed — never auto-resume capture when a writer result is unknown, a pipeline task died, or lifecycle monitoring broke. Do not introduce a second settings lock, WriterControl route, or direct React→Agent/SQLite path.

### TypeScript bindings (drift-gated)

Rust serde/specta types are the single source of truth. `crates/wuji-core/src/bindings.rs` generates `crates/wuji-core/bindings/wuji-core.ts` AND `apps/desktop/src/types/wuji-core.ts` — both copies must be byte-identical. Regenerate after any DTO change:

```powershell
WUJI_UPDATE_BINDINGS=1 cargo test -p wuji-core bindings
```

The drift test fails CI if bindings are stale. `Int64String` is a branded string type — never treat arbitrary strings as i64 text.

### Settings

Settings updates are CAS on `expectedRevision` (returns `SettingsConflict` on mismatch); the applied revision comes from the DB MAX. Corrupted settings files are reported and reconciled (`settings_resync_login_startup`). Effectivity changes must flow through the same Coordinator path as Capture transitions.

### Error contract

Cross-boundary errors are `SafeError { code, message }` with a frozen `SafeErrorCode` set (e.g. `IpcProtocolUnsupported`, `SettingsConflict`, `AgentWriterFaulted`, `InternalSafeError`). Messages are user-safe Chinese text — never raw exceptions, paths, SQL, SIDs, or window titles. `src/bridge/client.ts` maps unknown invoke errors via `toSafeError`.

## Testing Conventions

- Tests must be deterministic: prefer channels, events, paused Tokio time, or bounded deadlines over arbitrary sleeps.
- Agent integration tests (`apps/agent/tests/`) spawn real agent processes with **unique test channels** and the `TestAgentGuard` process-identity guard (`apps/agent/tests/common/`, regression-tested in `agent_guard.rs`); they must confirm child exit and leave zero `wuji-rebuild-agent-v01` processes running.
- Windows E2E tests write isolated data under `%LOCALAPPDATA%`; run with normal user permissions when a sandbox blocks that location.
- Add focused regression tests for state machines, Barrier ordering, revision effectivity, SQLite transactions, IPC idempotency, process lifecycle, and UI states.
- Do not terminate a process by bare PID without proving its identity — reuse the repo's process-handle and test-guard abstractions.
- Frontend: Vitest + Testing Library (`*.test.tsx` colocated); `apps/desktop/src-tauri/tests/host_integration.rs` covers the host.

## Documentation Hierarchy

- `docs/dev/09-Tauri-Rust-Rebuild-v0.1实施基线.md` — the v0.1 implementation contract (authority for scope, runtime/algorithm/protocol contracts, acceptance order).
- `docs/dev/migration-status.md` — records actual implementation/validation status; it does not define or override design.
- `docs/dev/ADR-002` (long-term architecture), `ADR-003` (rebuild-only repo decision).
- Architecture exceptions go in ADRs, never in status documents. Read/write Chinese Markdown as UTF-8.

When implementation or validation state materially changes, update `docs/dev/migration-status.md`. Keep one logical change per commit; for large moves, inspect `git diff --cached --summary --find-renames` before committing.

## Safety Rules

- Never modify or delete old WUJI/WUJI-Dev databases.
- Do not commit runtime databases, logs, `target/`, `dist/`, `node_modules/`, local screenshots, or private absolute paths.
- Preserve unrelated user changes in a dirty worktree: do not use `git reset --hard`, `git checkout --`, `git restore`, or `git clean` to discard uncommitted work.
