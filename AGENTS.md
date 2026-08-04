# Repository Guidelines

## Project Overview

WUJI (吾迹) is a local-first Windows activity-recording desktop app. This repository contains only the Rebuild v0.1 chain: React 19 UI → Tauri 2 Rust Host → Rust Agent → SQLite v0.1. The legacy WPF/C#/Bridge system was removed (ADR-003). All user-facing text is Chinese-first.

Key behavioral facts:
- All data/process isolation uses fixed names: channel via `WUJI_REBUILD_CHANNEL` env var, process `wuji-rebuild-agent-v01.exe`, named pipe, mutex, data root `%LOCALAPPDATA%\WUJI-Rebuild-V01`.
- The Agent is the **only writer** of the behavior database; the Tauri Host opens it read-only. React never connects to the pipe, queries SQLite, or recomputes domain aggregates.
- "暂停记录" only pauses capture — the Agent process stays online. "停止 Agent" commits a CaptureStop boundary then requests graceful shutdown.
- Closing/minimizing the Desktop window hides to tray; it does not quit the application.
- Desktop exit does not implicitly terminate the Agent. `capture_start` ensures the Agent is running first.
- Old WUJI/WUJI-Dev databases must never be modified or deleted. Package validation may only checksum them.

## Project Structure

- `apps/agent/`: long-running Rust capture pipeline, Activity/Work state machine, `CaptureCoordinator` (single serialization point), Barrier/WriterControl ack path, settings reconcile/store, command server (named pipe IPC server).
- `apps/desktop/`: React UI (`src/`) and Tauri Rust host (`src-tauri/`). The host contains IPC client, QueryService (read-only), SettingsService, DesktopPrefsService, ControlService, AgentController (process lifecycle), tray, and single-instance.
- `crates/wuji-core/`: platform-independent domain, Settings, DTOs, error contract (`SafeError` + frozen `SafeErrorCode`), runtime names. Must not depend on Tauri, Win32, or SQLite.
- `crates/wuji-storage/`: SQLite schema (`schema/schema.sql`, embedded at build), Writer, and read-only Reader.
- `crates/wuji-windows/`: Win32 capture, foreground/idle detection, process handles, named-pipe wrappers, session/power events.
- `scripts/`: package, soak, and validation utilities.
- `docs/dev/`: implementation baseline, ADRs, review records, and migration status.

`docs/dev/09-Tauri-Rust-Rebuild-v0.1实施基线.md` is the v0.1 implementation contract. `docs/dev/migration-status.md` records actual status and does not redefine design.

## Build and Test Commands

Run Rust commands from the repository root (`rust-toolchain.toml` pins Rust 1.97):

```powershell
cargo fmt --all -- --check
cargo check --workspace --all-targets
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
cargo test -p <crate> <test_name>   # single test, e.g. cargo test -p wuji-core bindings
```

Run frontend commands from `apps/desktop/` (Node 24.14.0, pnpm 11.9.0):

```powershell
pnpm.cmd install --frozen-lockfile
pnpm.cmd typecheck        # tsc -b
pnpm.cmd lint             # eslint, --max-warnings 0
pnpm.cmd test             # vitest run; single file: pnpm.cmd test src/features/settings/SettingsPage.test.tsx
pnpm.cmd build
```

Dev/package entry scripts at repo root (PowerShell):

- `rebuild-tauri-dev.ps1` — sets `WUJI_REBUILD_CHANNEL=rebuild-v01-dev` and runs `pnpm.cmd tauri dev`. It does not build the Agent; after Desktop starts, Desktop may use an existing debug Agent and start recording according to the local auto-start preference. Run `rebuild-agent.ps1` first when the debug Agent binary is missing.
- `rebuild-agent.ps1` — `cargo build -p wuji-rebuild-agent` (debug).
- `rebuild-package.ps1` — runs `scripts/build_dev_package.py` (NSIS dev package + acceptance validation). Any change to directories, bundling, sidecar, or release path must run this at least once.

### TypeScript Bindings (drift-gated)

Rust serde/specta types are the single source of truth. `crates/wuji-core/src/bindings.rs` generates `crates/wuji-core/bindings/wuji-core.ts` AND `apps/desktop/src/types/wuji-core.ts` — both copies must be byte-identical. Regenerate after any DTO change:

```powershell
$env:WUJI_UPDATE_BINDINGS = '1'
try {
    cargo test -p wuji-core bindings
}
finally {
    Remove-Item Env:WUJI_UPDATE_BINDINGS -ErrorAction SilentlyContinue
}
```

The drift test fails the Rust test gate if bindings are stale. `Int64String` is a branded string type — never treat arbitrary strings as i64 text. The React invoke surface is defined in `apps/desktop/src/bridge/client.ts`, mirroring the Rust allowlist.

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

### Tauri Command Allowlist

All 17 Tauri commands are declared in `invoke_handler` in `apps/desktop/src-tauri/src/lib.rs`:

| Category | Commands |
|----------|----------|
| Agent lifecycle | `agent_process_stop`, `agent_get_status` |
| Capture control | `capture_start`, `capture_pause`, `capture_resume` |
| Activity queries | `activity_get_today`, `activity_get_timeline`, `activity_get_heatmap` |
| Stats homepage | `stats_get_home`, `stats_get_status` |
| Settings | `settings_get`, `settings_update`, `settings_resync_login_startup` |
| Desktop prefs | `desktop_prefs_get`, `desktop_prefs_update`, `auto_start_status` |
| Diagnostics | `diagnostics_get_summary` |

React must use only these commands. It must not receive arbitrary SQL, filesystem paths, pipe names, shell commands, or raw window titles.

### Control Boundaries

Capture, Settings, and Lock/Sleep lifecycle changes are serialized through the single `CaptureCoordinator`:

```text
transition lock → freeze → Barrier injected ack
→ WriterControl → Writer ack → publish/watch → restore effective state
```

Fail closed — never auto-resume capture when a writer result is unknown, a pipeline task died, or lifecycle monitoring broke. Do not introduce a second settings lock, WriterControl route, or direct React-to-Agent/SQLite path.

Startup orchestration (when auto-start pref is on) uses `ControlService.ensure_recording()` → internal Agent command `capture_ensure_recording` (Coordinator-atomic: Stopped→Start / Paused→Resume / Running→idempotent). This is not exposed to React. Startup outcome lives in host-side `AutoStartOutcome` (visible to top bar via `auto_start_status`).

### Settings

Settings updates are CAS on `expectedRevision` — returns `SettingsConflict` on mismatch; the applied revision comes from the DB MAX. `settings_get` and `settings_update` explicitly report a corrupted `settings.json`; Agent startup recovery from last-known-good data is handled by the Agent settings store. `settings_resync_login_startup` only reapplies the Windows Run Key from the current Settings and reports the DB MAX applied revision. Effectivity changes must flow through the same Coordinator path as Capture transitions.

### Error Contract

Cross-boundary errors use `SafeError { code, message }` with a frozen `SafeErrorCode` set (e.g. `IpcProtocolUnsupported`, `SettingsConflict`, `AgentWriterFaulted`, `InternalSafeError`). Messages are user-safe Chinese text — never raw exceptions, paths, SQL, SIDs, or window titles. `apps/desktop/src/bridge/client.ts` maps unknown invoke errors via `toSafeError`.

- `wuji-core` must not depend on Tauri, Win32, or SQLite.
- Prefer explicit error propagation and stable protocol error codes. Do not silently discard failures on persistence, process, IPC, or lifecycle paths.

## Rust and TypeScript Style

- Use `cargo fmt`; Clippy must pass with `-D warnings`.
- Keep React business semantics in Rust DTO/query results. React may format and present data but must not recompute activity durations or merge domain records.
- Preserve Chinese-first user-facing text and the existing design tokens; do not expose raw exceptions or private paths in ordinary UI.
- Do not introduce `any` types in TypeScript bridge code; use the generated `wuji-core.ts` types.

## Testing and Validation

- Tests must be deterministic: prefer channels, events, paused Tokio time, or bounded deadlines over arbitrary sleeps.
- Add focused regression tests for changes to state machines, Barrier ordering, revision effectivity, SQLite transactions, IPC idempotency, process lifecycle, and UI states.
- Agent integration tests (`apps/agent/tests/`) spawn real agent processes with **unique test channels** and the `TestAgentGuard` process-identity guard (`apps/agent/tests/common/`). They must confirm child exit and leave zero `wuji-rebuild-agent-v01` processes running.
- The guard is regression-tested in `agent_guard.rs`. Do not terminate a process by bare PID without proving its identity — reuse the repo's process-handle and test-guard abstractions.
- Windows E2E tests write isolated data under `%LOCALAPPDATA%`; run them with normal user permissions when a sandbox blocks that location.
- Frontend tests: Vitest + Testing Library (`*.test.tsx` colocated). Host integration tests: `apps/desktop/src-tauri/tests/host_integration.rs`.
- A directory, bundling, sidecar, or release-path change must run `rebuild-package.ps1` at least once. Eight-hour soak and manual UI/Lock/Sleep matrices remain release gates, not ordinary edit gates.

## Data and Process Safety

- Never modify or delete old WUJI/WUJI-Dev databases. Package validation may only checksum them.
- Do not commit runtime databases, logs, `target/`, `dist/`, `node_modules/`, local screenshots, or private absolute paths.
- Do not terminate a process by bare PID without first proving its identity. Reuse the repository's process-handle and test-guard abstractions.
- Preserve unrelated user changes in a dirty worktree. Do not use `git reset --hard`, `git checkout --`, `git restore`, or `git clean` to discard uncommitted work.

## Documentation Hierarchy

- `docs/dev/09-Tauri-Rust-Rebuild-v0.1实施基线.md` — the v0.1 implementation contract (authoritative for scope, runtime/algorithm/protocol contracts, acceptance order).
- `docs/dev/migration-status.md` — records actual implementation/validation status; it does **not** define or override design.
- `docs/dev/ADR-002` — long-term React-Tauri-Rust target architecture.
- `docs/dev/ADR-003` — rebuild-only repo decision and legacy system retirement.
- Architecture exceptions go in ADRs, never in status documents.

When implementation or validation state materially changes, update `docs/dev/migration-status.md`. Read and write Chinese Markdown as UTF-8. Keep one logical change per commit. Stage with explicit pathspecs so unrelated dirty-worktree changes are not included. Use `git add -A` only after proving the entire working tree belongs to the same logical change; always inspect `git diff --cached --summary --find-renames` before committing.
