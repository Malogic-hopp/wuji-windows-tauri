# Repository Guidelines

## Project Structure & Module Organization

This repository contains the WUJI Windows desktop app.

- `src/QuantifiedSelf.Windows.Core/`: shared domain models, options, paths, runtime state, and enums.
- `src/QuantifiedSelf.Windows.Infrastructure/`: SQLite data access, Win32 capture, IPC, settings, events, and runtime-state persistence.
- `src/QuantifiedSelf.Windows.Agent/`: background sampling process built on `BackgroundService`.
- `src/QuantifiedSelf.Windows.App/`: WPF UI, tray integration, ViewModels, services, resources, and LiveCharts2 charts.
- `src/QuantifiedSelf.Windows.App/Themes/`: light, dark, and High Contrast resources plus shared typography, spacing, card, button, navigation, and DataGrid tokens.
- `src/QuantifiedSelf.Windows.App/UI/`: preview-shell layout infrastructure such as `AdaptiveLayout` and its layout modes.
- `tests/QuantifiedSelf.Windows.Tests/`: xUnit coverage for state flow, data flow, IPC, UI ViewModel behavior, and regression cases.
- `docs/`: design plans, completion notes, review records, and manual validation checklists.
- `publish/scripts/publish.ps1`: standard self-contained win-x64 folder publish script.
- `scripts/`: Python analysis utilities; generated images should not be committed.

## Build, Test, and Development Commands

Use commands from the repository root:

```powershell
dotnet restore .\QuantifiedSelf.Windows.sln
dotnet build .\QuantifiedSelf.Windows.sln
dotnet test .\QuantifiedSelf.Windows.sln
dotnet run --project .\src\QuantifiedSelf.Windows.App\QuantifiedSelf.Windows.App.csproj
.\publish\scripts\publish.ps1
```

`dotnet run` starts the WPF App. Start the Agent from the UI unless `AutoStartAgentWhenAppStarts` is enabled. The publish script assembles App output under `publish/release/App/` and embeds Agent output under `publish/release/App/Agent/`.

For UI redesign work, use the isolated development channel:

```powershell
dotnet run --project .\src\QuantifiedSelf.Windows.App\QuantifiedSelf.Windows.App.csproj -- --channel dev --ui-preview
```

Add `--show-agent-console` only when an Agent debug window is needed.

## Coding Style & Naming Conventions

Use C# nullable reference types and implicit usings as configured in the projects. Prefer existing service/ViewModel patterns over new abstractions. Use PascalCase for public types and members, camelCase for locals, and `_fieldName` for private fields where the surrounding file already does so. Keep edits narrowly scoped. Read Chinese documentation with `Get-Content -Encoding UTF8`.

## Testing Guidelines

Tests use xUnit. Add focused regression tests for state-machine, file I/O, IPC fallback, ViewModel refresh, and data-query changes. Prefer `async Task` tests with `await`; avoid `.GetAwaiter().GetResult()` and long real-time sleeps. Run full tests before submitting behavior changes:

```powershell
dotnet test .\QuantifiedSelf.Windows.sln
```

Test classification and execution guidance:

- Tag fast, deterministic tests with `Category=Fast`. These should cover pure logic, ViewModel state, formatting, and hand-triggered scheduler behavior only.
- Tag SQLite, named pipe, registry, real file system, and Agent lifecycle coverage with `Category=Integration`.
- Tag Dispatcher, `Application.Current`, shared static state, and other WPF/runtime-dependent coverage with `Category=Wpf`.
- Tag intentionally slow regression coverage with `Category=Slow`.
- Prefer one trait declaration per test class when the whole class shares the same category. Use method-level traits only when a class intentionally mixes categories, and avoid duplicating the same trait on both class and method.
- Keep tests isolated when they touch the same resource. Use unique temp directories, unique database names, and unique IPC or runtime identifiers.
- Reuse shared test infrastructure from `tests/QuantifiedSelf.Windows.Tests/TestHelpers/` for common temp workspaces, schedulers, and other small helpers instead of re-defining them in each file.
- Large legacy test files such as `DataFlowTests.cs` may stay while behavior is being stabilized, but extract repeated helpers and split by domain when doing focused maintenance.
- Do not rely on `Task.Delay(...)` as a synchronization primitive in tests. Prefer `TaskCompletionSource`, events, fakes, or manually triggered schedulers.
- Do not disable parallelism globally. The test project uses xUnit collection and runner settings to keep safe parallelism enabled while leaving WPF and shared-resource tests isolated.

Recommended commands:

```powershell
dotnet test .\tests\QuantifiedSelf.Windows.Tests\QuantifiedSelf.Windows.Tests.csproj --no-build --filter "Category=Fast"
dotnet test .\tests\QuantifiedSelf.Windows.Tests\QuantifiedSelf.Windows.Tests.csproj --no-build --filter "Category=Integration"
dotnet test .\tests\QuantifiedSelf.Windows.Tests\QuantifiedSelf.Windows.Tests.csproj --no-build --filter "Category=Wpf"
dotnet test .\tests\QuantifiedSelf.Windows.Tests\QuantifiedSelf.Windows.Tests.csproj --no-build --filter "Category=Integration|Category=Wpf"
dotnet test .\QuantifiedSelf.Windows.sln
```

## Commit & Pull Request Guidelines

Recent commits use concise Chinese imperative summaries, for example `修复 PersistAsync 文件替换崩溃` or `补充洞察页面实施完成说明`. Keep one logical change per commit. PRs should include: what changed, why, verification output, related docs updated, and screenshots for visible WPF UI changes.

## Security & Configuration Tips

Do not commit local runtime data, databases, logs, publish outputs, `.codex/`, or screenshots generated by scripts. Avoid exposing usernames, absolute private paths, SIDs, raw window titles, or registry command values in docs and tests unless sanitized.

## UI Preview Architecture

Do not run experimental UI work against the production runtime. `--channel dev` isolates data, IPC, Agent mutex, and startup registration from the stable App:

- Prod data: `%LOCALAPPDATA%\WUJI\WindowsAgent`
- Dev data: `%LOCALAPPDATA%\WUJI-Dev\WindowsAgent`
- Prod Run Key value: `WUJI`
- Dev Run Key value: `WUJI Dev`

The default startup path creates `LegacyMainWindow`; `--ui-preview` creates the redesigned `MainWindow`. `StartupLaunchOptions.UsePreviewUi` is the shell-selection gate. Keep the redesigned shell, page templates, and preview-only behavior behind this flag until product approval explicitly promotes them. Do not change the default startup path as part of ordinary UI work.

During preview work, use only `--channel dev --ui-preview`. Prefer read-only views or copied data snapshots, and never validate destructive privacy or maintenance actions against production data.

## WUJI UI Implementation Rules

The frozen product and visual specification under `.agents/skills/wuji-wpf-ui-design/references/` is authoritative. Use `migration-status.md` for the current implementation-versus-target baseline; update it when a UI change closes or introduces a material gap instead of copying volatile progress into this file.

- Keep the product local-first, quiet, restrained, data-first, and Chinese-first. Ordinary UI text is Chinese; English is limited to brand names, real application names, and technical identifiers inside collapsed advanced diagnostics.
- Do not present unavailable product semantics. Stable work, context switching, interruptions, 30-day trends, and 12-week trends stay hidden or disabled until their domain models and aggregations exist. A selected filter must refresh its data, range, legend, and metric-specific color scale.
- Use theme tokens: brushes through `DynamicResource`; sizes and static styles through `StaticResource`. Do not add page-level hex colors or hardcoded `FontSize`, `CornerRadius`, or `Padding`. High Contrast uses Windows `SystemColors`, never a light-theme fallback.
- Use `AdaptiveLayout.Mode` in page XAML. Below 1280 DIPs, metric strips become 2×2 or stacked and multi-column modules become single-column; at 1280 DIPs and above, retain four-column metrics and side-by-side modules. The minimum supported preview client size is 960×640 DIPs.
- Data-driven pages use the shared `PageState` values `Loading`, `Empty`, `Ready`, and `Error`. Error UI provides a safe Chinese explanation and retry action without exposing raw exceptions.
- Heatmaps remain per-cell controls: every cell is focusable, has an automation name and tooltip, supports arrow-key movement, and uses Enter/Space to navigate to the corresponding Timeline date/hour. Keep a visible five-level low-to-high legend and a cell boundary in High Contrast.
- Keep Agent status and control in the top bar only. Diagnostics starts with plain-language health and repair actions; PID, IPC, SQLite, Tick, paths, and raw runtime state remain collapsed by default.
- Add focused page or section ViewModels rather than exposing the whole `SettingsViewModel` from feature pages. Keep Privacy independent, mask paths by default, and preserve confirmation flows for export and clear operations.

## UI Acceptance and Promotion Gate

Do not mark preview UI work complete from build and unit tests alone. Before promoting the redesigned shell, run it with `--channel dev --ui-preview` and record manual results for:

- 960×640 and 1280×800 DIPs, plus representative wider layouts;
- 100%, 125%, 150%, and 200% DPI;
- Light, Dark, and Windows High Contrast themes;
- keyboard-only navigation, heatmap Enter/Space linkage, visible focus, and screen-reader output;
- screenshots for each acceptance theme and compact/standard layout.

Build must finish with 0 errors and 0 new warnings, Fast tests and the full suite must pass, and the UI review checklist must have no unresolved P0 items. Keep the dual-shell path until all runtime checks pass and product approval is explicit.
