# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test

```bash
# Build
dotnet build QuantifiedSelf.Windows.sln

# Run all tests (xUnit, 468 tests)
dotnet test

# Run a single test class or method
dotnet test --filter "FullyQualifiedName~DataFlowTests"
dotnet test --filter "FullyQualifiedName~DataFlowTests.AgentControlFileStore_RoundsTripStringEnums_AndFlagsMalformedFiles"

# Publish (self-contained win-x64, includes Agent→App embedding and verification)
.\publish\scripts\publish.ps1
```

## Architecture

**Two-process model**: WPF App (UI + tray) and Agent (background sampling) run independently. They communicate via Named Pipe IPC with file-system fallback. See [ARCHITECTURE.md](./ARCHITECTURE.md) for full details.

### Process roles

- **Agent** (`QuantifiedSelf.Windows.Agent`): .NET Worker SDK, `BackgroundService` with 1-second `PeriodicTimer`. Hosts `AgentStateMachine` (sampling, privacy filtering, session aggregation, state persistence) and `AgentCommandServerHostedService` (Named Pipe IPC server). Uses `Microsoft.Extensions.Hosting` DI container (`Program.cs`).
- **App** (`QuantifiedSelf.Windows.App`): WPF desktop app with manual DI in `App.xaml.cs` OnStartup (no container — all services created with `new`). `MainWindowViewModel` orchestrates pages. `RefreshService` polls Agent status every 2s; data pages refresh at `AppSettings.RefreshIntervalSeconds` (default 15s).

### Project dependency graph

```
Core (net8.0, zero deps)
  ↑
Infrastructure (net8.0-windows, SQLite, Win32, IPC)
  ↑
Agent (net8.0-windows, Worker SDK)  ←  App (net8.0-windows10.0.19041, WPF+WinForms)
                                        ↑ ReferenceOutputAssembly=false to Agent
```

App references Agent with `ReferenceOutputAssembly=false` — for build-order and IDE navigation only; Agent is never compiled into App. At runtime, App launches Agent as a separate process.

### IPC and fallback

- **Primary**: Named Pipe (protocol v1), pipe name based on current user SID for single-user isolation. App uses `NamedPipeAgentControlClient`, Agent uses `NamedPipeAgentCommandServer`.
- **Fallback**: File system. Agent writes `runtime_state.json` + `health_state.json` (status); App writes `agent_control.json` (commands). Agent deletes `agent_control.json` in a `finally` block after processing.
- **GetStatus fallback**: App reads status files directly; does not write an `agent_control.json` entry. The `GetStatus` channel in IPC tables is `IPC / 状态文件读取`, not `IPC / 文件`.

### Data flow

1. Agent's `Worker` ticks every 1s → `AgentStateMachine.TickAsync()`
2. Read `agent_control.json` for pending commands (delete after processing)
3. If `Running` state and `sampleDue` (≥ `SamplingIntervalSeconds`, default 3s): capture via Win32 (`GetForegroundWindow` → `SendMessageTimeout(WM_GETTEXT, SMTO_ABORTIFHUNG, 500ms)`)
4. Privacy filter (`ExcludedProcesses` / `ExcludedTitlePatterns`)
5. Insert into `foreground_samples` table (SQLite, WAL mode)
6. Aggregate into `app_sessions` via `SessionAggregator`
7. `PersistAsync`: write `runtime_state.json` + `health_state.json`

### Database (SQLite, WAL mode)

Three tables: `foreground_samples`, `app_sessions`, `agent_events`. Schema details in ARCHITECTURE.md.

### State files

| File | Writer | Purpose |
|------|--------|---------|
| `runtime_state.json` | Agent | PID, state, heartbeat, version |
| `health_state.json` | Agent | Health metrics, Tick diagnostics, error codes |
| `agent_control.json` | App | Control commands (Agent deletes after processing) |
| `windows-agent.json` | App (Settings) | Agent runtime configuration |
| `app-settings.json` | App (Settings) | App settings (refresh interval, auto-start, etc.) |

Default data root: `%LOCALAPPDATA%\WUJI\WindowsAgent` (subdirs: `config\`, `data\`, `runtime\`, `logs\`).

## Critical invariants

### State file writes
**Always use `File.Delete` + `File.Move` with retry (3×50ms), never `File.Move(overwrite:true)`.** The read side opens files with `FileShare.ReadWrite | FileShare.Delete`, which can block `MoveFileEx(MOVEFILE_REPLACE_EXISTING)`. Delete+Move handles this correctly because `DeleteFileW` removes the directory entry while the open read handle keeps the old file data alive. See `RuntimeStateStore.MoveWithRetryAsync`.

### Agent exception handling
- `PersistAsync` catches all write exceptions (logged, not re-thrown).
- `Worker.ExecuteAsync` catches all non-`OperationCanceledException` from `TickAsync` — Agent never crashes on a single bad tick.
- Agent `Program.cs` uses a per-user `Mutex` to prevent duplicate instances.

### Win32 window title capture
Use `SendMessageTimeout` with `SMTO_ABORTIFHUNG | SMTO_BLOCK` (500ms timeout), never `GetWindowText`. A hung target window would otherwise block the Agent's main loop indefinitely.

### Agent lifecycle states
`AgentActualState` enum: `NotRunning → Starting → Running ↔ Paused → Stopping → Stopped`. Additional states: `Stale` (process alive but heartbeat expired, or process dead but state file lingering), `Error`, `Maintenance` (during PruneData/ClearHistory).

### Stale detection (App side, file fallback)
- `processRunning && !heartbeatFresh` → Stale
- `!processRunning && runtimeState exists` → Stale
- `!processRunning && runtimeState is null` → NotRunning

## Code conventions

### Language & logging
- All C# projects use `Nullable: enable`, `ImplicitUsings: enable`.
- Agent log messages are in **Chinese** (e.g., `"采样成功：状态={State}，前台={DisplayName}"`). App UI strings are also Chinese.
- Log levels: `LogInformation` for normal operations, `LogWarning` for recoverable errors, `LogError` for exceptions caught at the top-level handler.

### DI patterns
- **Agent**: Standard `Host.CreateApplicationBuilder` + `builder.Services.AddSingleton/AddHostedService`. All services are singletons. `SqliteDatabaseInitializer` and repository classes receive database path from `WindowsAgentPaths`.
- **App**: Manual DI in `App.xaml.cs` — services and ViewModels created with `new`. No DI container. Rationale: WPF startup sequence requires explicit control over initialization order (IPC setup → tray creation → window lifecycle).

### Testing
- xUnit with `TempWorkspace` pattern: disposable workspace class that creates a temp directory and cleans it up. Used for any test that touches the file system.
- Tests reference all four projects directly.
- Test project targets `net8.0-windows10.0.19041` (requires Windows, uses WPF types).
- Key test files: `DataFlowTests.cs` (state machine, control files, persistence), `InsightsTests.cs` (insight suggestions, metrics), `AgentExeLocatorTests.cs`, `VersionTests.cs`.

### Namespace conventions
- `QuantifiedSelf.Windows.Core.*` — domain types, enums, options, paths
- `QuantifiedSelf.Windows.Infrastructure.*` — data access, Win32 interop, IPC protocol, file stores
- `QuantifiedSelf.Windows.Agent.*` — Agent process logic (State, Services, Events)
- `QuantifiedSelf.Windows.App.*` — WPF UI (ViewModels, Views, Services, Converters, Models)

### Configuration file naming
- Agent options: `windows-agent.json` (not `agent_options.json`)
- App settings: `app-settings.json`
- Both use `System.Text.Json` with `PropertyNameCaseInsensitive = true` and `CamelCase` naming policy.

## Key files to update together

When modifying a feature, these areas often need coordinated changes:

| Feature area | Touch these |
|--------------|-------------|
| IPC commands | `Core/Control/AgentCommandType.cs`, `Agent/Services/AgentCommandServerHostedService.cs`, `Infrastructure/Ipc/NamedPipeAgentControlClient.cs`, `App/Services/AgentControlService.cs` |
| Agent options | `Core/Options/WindowsAgentOptions.cs`, `Infrastructure/Settings/WindowsAgentOptionsStore.cs`, `App/ViewModels/SettingsViewModel.cs` |
| Database schema | `Infrastructure/Database/SqliteDatabaseInitializer.cs`, `Infrastructure/Database/ForegroundSampleRepository.cs`, `Infrastructure/Database/AppSessionRepository.cs` |
| State files | `Infrastructure/RuntimeState/RuntimeStateStore.cs`, `Infrastructure/RuntimeState/AgentHealthStateStore.cs`, `Core/Paths/WindowsAgentPaths.cs` |
| Diagnostics/Events | `Agent/Events/AgentEventWriter.cs`, `Infrastructure/Events/AgentEventJournal.cs`, `Infrastructure/Database/AgentEventRepository.cs`, `Core/Events/AgentEvent.cs` |
| Tick diagnostics | `Agent/State/AgentStateMachine.cs` (health snapshot fields), `Core/Runtime/AgentHealthState.cs` |
