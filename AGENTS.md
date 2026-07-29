# Repository Guidelines

## Project Structure

This repository contains only the WUJI Tauri + Rust Rebuild implementation.

- `apps/agent/`: long-running Rust capture/processing/writer/IPC process.
- `apps/desktop/`: React UI and Tauri Rust host.
- `crates/wuji-core/`: platform-independent domain, settings, DTO, and error contracts.
- `crates/wuji-storage/`: SQLite schema, single-writer storage, and read-only queries.
- `crates/wuji-windows/`: Win32 capture, process, session/power, and named-pipe wrappers.
- `scripts/`: package, soak, and validation utilities.
- `docs/dev/`: implementation baseline, ADRs, review records, and migration status.

`docs/dev/09-Tauri-Rust-Rebuild-v0.1实施基线.md` is the v0.1 implementation contract. `docs/dev/migration-status.md` records actual status and does not redefine design.

## Build and Test Commands

Run Rust commands from the repository root:

```powershell
cargo fmt --all -- --check
cargo check --workspace --all-targets
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
```

Run frontend commands from `apps/desktop/`:

```powershell
pnpm install --frozen-lockfile
pnpm typecheck
pnpm lint
pnpm test
pnpm build
```

Use `rebuild-tauri-dev.ps1` for the Tauri development UI, `rebuild-agent.ps1` for the debug Agent, and `rebuild-package.ps1` for the NSIS dev package and package validation.

## Architecture Boundaries

- `wuji-core` must not depend on Tauri, Win32, or SQLite.
- The Agent is the only writer of the behavior database; the Tauri Host opens it read-only.
- React must use the fixed Tauri command allowlist. It must not receive arbitrary SQL, filesystem paths, pipe names, shell commands, or raw window titles.
- Settings effectivity and Capture/Lifecycle transitions must continue through the single Coordinator and Barrier/Writer acknowledgement path.
- Do not introduce a second settings lock, WriterControl route, or direct React-to-Agent/SQLite path.

## Rust and TypeScript Style

- Use `cargo fmt`; Clippy must pass with `-D warnings`.
- Prefer explicit error propagation and stable protocol error codes. Do not silently discard failures on persistence, process, IPC, or lifecycle paths.
- Keep tests deterministic. Prefer channels, events, paused Tokio time, or bounded deadlines over arbitrary sleeps.
- Keep React business semantics in Rust DTO/query results. React may format and present data but must not recompute activity durations or merge domain records.
- Preserve Chinese-first user-facing text and the existing design tokens; do not expose raw exceptions or private paths in ordinary UI.

## Testing and Validation

- Add focused regression tests for changes to state machines, Barrier ordering, revision effectivity, SQLite transactions, IPC idempotency, process lifecycle, and UI states.
- Tests that create Agent processes must use unique test channels and the shared process guard, confirm child exit, and leave zero `wuji-rebuild-agent-v01` processes.
- Windows E2E tests write isolated data under `%LOCALAPPDATA%`; run them with normal user permissions when a sandbox blocks that location.
- A directory, bundling, sidecar, or release-path change must run `rebuild-package.ps1` at least once. Eight-hour soak and manual UI/Lock/Sleep matrices remain release gates, not ordinary edit gates.

## Data and Process Safety

- Never modify or delete old WUJI/WUJI-Dev databases. Package validation may only checksum them.
- Do not commit runtime databases, logs, `target/`, `dist/`, `node_modules/`, local screenshots, or private absolute paths.
- Do not terminate a process by bare PID without first proving its identity. Reuse the repository's process-handle and test-guard abstractions.
- Preserve unrelated user changes in a dirty worktree. Do not use `git reset --hard`, `git checkout --`, `git restore`, or `git clean` to discard uncommitted work.

## Documentation and Commits

- Update `docs/dev/migration-status.md` when implementation or validation state materially changes.
- Record architecture exceptions as ADRs; status documents cannot override the implementation baseline.
- Read and write Chinese Markdown as UTF-8.
- Keep one logical change per commit. For large moves, stage with `git add -A` and inspect `git diff --cached --summary --find-renames` before committing.
