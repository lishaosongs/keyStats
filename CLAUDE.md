# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

KeyStats is a Windows system tray app that tracks keyboard and mouse usage — counts and distances only, never input content. It targets .NET Framework 4.8 (`net48`) with WPF + WinForms interop, C# `LangVersion` 10.0 and nullable enabled. Optional multi-device sync is end-to-end encrypted and served by an independently deployable Cloudflare Worker.

Repo layout:

- `KeyStats.Windows/` — Windows app, solution, packaging scripts, and `KeyStats.Sync.Tests`.
- `contracts/sync/v1/` — wire-level source of truth for E2EE sync (JSON Schema, OpenAPI, fixtures).
- `services/sync-worker/` — Cloudflare Worker backend (TypeScript, D1 + R2).
- `scripts/` — release and Scoop publishing scripts.

Detailed guidance:

- Sync protocol rules: `contracts/sync/v1/README.md`.
- Sync service operations and deployment: `services/sync-worker/README.md`.

## Build & Test

Prerequisites: Windows with a .NET SDK (CI uses 8.0.x to build `net48`) and, for the worker, Node.js 22.

```bash
# From the repository root (use a separate shell from the worker commands)
cd KeyStats.Windows

# Debug / Release build
dotnet build KeyStats/KeyStats.csproj -c Debug
dotnet build KeyStats/KeyStats.csproj -c Release

# Run the Debug build (output path from the csproj/build script)
./KeyStats/bin/Debug/net48/KeyStats.exe

# Automated tests (MSTest; sync protocol, crypto fixtures, scheduling, persistence)
dotnet test KeyStats.Sync.Tests/KeyStats.Sync.Tests.csproj

# Single test (class or fully qualified method)
dotnet test KeyStats.Sync.Tests/KeyStats.Sync.Tests.csproj --filter "FullyQualifiedName~SyncBatchPlannerTests.SyncProgress_AdvancesByDayAndStopsAtTotal"

# Package (produces dist/KeyStats-Windows-<version>.zip)
powershell -ExecutionPolicy Bypass -File ./build.ps1 -Configuration Release
```

`build.ps1` accepts `-SyncServiceBaseUrl <https://...workers.dev>`; a direct `dotnet build` takes `/p:SyncServiceBaseUrl=<url>` instead. Debug defaults to the staging worker, and an empty URL leaves sync networking disabled (see `KeyStats.Windows/KeyStats/Services/SyncServiceConfiguration.cs`).

```bash
# From the repository root in a separate shell
cd services/sync-worker

npm ci
npm run check                 # existing verification entry point; no separate lint script
npm run contracts:check       # schemas, OpenAPI, golden crypto vectors
npm run types:check && npm run typecheck
npm test                      # deploy-config test + Vitest in Miniflare (D1/R2)
npx wrangler d1 migrations apply DB --local
npm run dev                   # local Worker via wrangler dev

# Single worker test
npx vitest run test/worker.test.ts -t "binds client-generated vault tokens"
```

Validate according to the change, not with a full regression every time. For sync or persistence changes, run the related test class/filter. For UI changes, verify the affected window in both light and dark themes. Do not default to `dotnet build` after Windows edits unless the change warrants it or the user asks; a successful build never substitutes for UI verification.

## Architecture (data flow)

1. `InputMonitorService` installs global `WH_KEYBOARD_LL` / `WH_MOUSE_LL` hooks via `SetWindowsHookEx` and emits key/click/move/scroll events. Hook callbacks must stay non-blocking: always call `CallNextHookEx` and dispatch work through the thread pool. Mouse movement is sampled at 30 FPS; segments over 250 px are discarded as abnormal jumps.
2. `StatsManager` (singleton) subscribes to those events, updates today's totals, per-key counts, and per-app stats under a lock, and rolls over at local midnight. `ActiveWindowManager` supplies app attribution.
3. Persistence is debounced: saves are coalesced (2 s) and UI notifications are debounced (300 ms). JSON lives in `%LOCALAPPDATA%\KeyStats\` (`daily_stats.json`, `history.json`, `settings.json`). `StatsChanged` subscribers marshal to the WPF Dispatcher before updating ViewModels/Views. The UI is light MVVM (Views + ViewModels, no MVVM framework); charts and heatmaps use native WPF rendering (Canvas / DrawingContext, no charting library).
4. Optional sync: `SyncCoordinator` reads stats from `StatsManager`, canonicalizes and encrypts records (`SyncKeyCanonicalizer`, `SyncCrypto`), plans batches (`SyncBatchPlanner`), and sends them through `SyncTransport`. Device credentials are protected with DPAPI (`SyncCredentialStore`); local sync state/cache live in `sync_state.json` and `sync_cache.json`.
5. `App.xaml.cs` is the composition root: single-instance mutex, theme, services, tray icon, windows, import/export. Its exit sequence flushes analytics, disposes the tray, stops the input monitor, disposes the sync coordinator, flushes/disposes `StatsManager`, disposes `ThemeManager`, then releases the mutex.

## Windows implementation map

- ViewModels: `TrayIconViewModel` (tray tooltip and commands), `StatsPopupViewModel` (today's totals, key breakdown, chart), plus `FloatingStatsViewModel`, `KeyHistoryViewModel`, `AppStatsViewModel`. Settings have no ViewModel — `SettingsWindow` code-behind binds `StatsManager.Instance.Settings` and calls `SaveSettings()`.
- Controls (`Views/Controls/`): `StatItemControl`, `KeyBreakdownControl`, `StatsChartControl`, `KeyDistributionPieChartControl`, `KeyboardHeatmapControl`.
- Input and interop: `InputMonitorService` exposes key, click, mouse-move, and scroll events; `Helpers/KeyNameMapper.cs` formats names such as `Ctrl+Shift+A`; `Helpers/NativeInterop.cs` holds the P/Invoke surface.
- `StatsManager` raises both `StatsChanged(StatsUpdateKind)` and `StatsUpdateRequested`; saves are coalesced at 2 s, UI notifications debounced at 300 ms, settings saves at 500 ms.
- Notifications, startup, and converters: `NotificationService` sends threshold toasts via `Microsoft.Toolkit.Uwp.Notifications`; `StartupManager` writes `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`; `Helpers/Converters.cs` provides `IntToBool`, `BoolToVisibility`, `InverseBool`, `BarWidth`.

## Critical Rules

### Privacy
- Only store aggregate counts, distances, and app attribution. NEVER persist keystroke content, raw mouse paths, or clipboard data.
- Secrets, device/vault IDs, key names, and counts are never analytics properties or log fields. Analytics is optional and gated by `AppSettings.AnalyticsEnabled`.

### Thread Safety
- Low-level hook callbacks run on the hook thread: no file IO, serialization, or UI work — dispatch via the thread pool.
- `StatsManager` shared state is lock-protected. UI updates must go through the WPF Dispatcher. Never mutate WPF-bound collections from background threads.
- Timers fire on thread-pool threads; synchronize before touching shared state.

### Persistence
- Keep JSON backward-compatible when adding fields; old settings and history files must still load. Prefer additive model changes, and include new fields in clone/normalize/merge/import/export paths.
- Preserve the exit sequence in `App.OnExit`; new background/timer resources must be disposed on exit.

### Platform & UI
- This project disables `ImplicitUsings`; add explicit `using` directives.
- Use the shared `WindowBackdropHelper` and theme resources (`App.xaml` / `ThemeManager`) for window materials: keep soft glass-card surfaces, thin separators, subtle shadows, 10–12pt radii, and runtime light/dark switching via dynamic resources. Prefer `AllowsTransparency="False"` with the DWM backdrop, and change shared resources rather than hardcoding per-window colors.
- User-facing copy is Chinese-first. When adding a new page/window, emit matching `pageview` and `click` analytics events.
- Default to replying in Simplified Chinese, per `.github/instructions/instructions.md`.

## Sync Service

- Wire protocol: `contracts/sync/v1/README.md`. Backend operations and deployment: `services/sync-worker/README.md`.
- Do not change the protocol schema, fixtures, or Windows sync behavior without a corresponding plan.
- Sync currently covers supported key/key-combination counts and mouse-button click counts only. Mouse movement distance, scroll distance, and per-app statistics are not synced.
- D1 stores the current encrypted record and metadata; R2 stores immutable encrypted history revisions.
- The worker must never receive plaintext statistics, key names, local dates, device profiles, or recovery seeds; it stores only HMACs and ciphertext. Pushes to `main` deploy staging; production deployment is a manual workflow requiring explicit confirmation.

## Automatic Commits

- After a feature or module is fully implemented and its relevant checks pass, create an atomic commit **without waiting for the user to ask**.
- Re-check `git status` first; stage only files belonging to the completed feature or module.
- Do not automatically commit incomplete work, failed verification, or unrelated existing changes.
- Use Conventional Commits style: `feat: ...`, `fix: ...`, `docs: ...`.
- Commit on the current branch only; never push, force-push, amend, or rewrite history automatically.


For multi-step tasks, act as the orchestrator: decompose the task,
delegate execution to subagents running on claude-opus-5, review
every subagent report, and decide the next step yourself. Do not
write code directly in the main session. Brief each subagent with
full context — the goal, relevant files, and what "done" looks
like — and run independent subtasks in parallel.
