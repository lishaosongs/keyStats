# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

KeyStats is a Windows system tray app (WPF + WinForms interop on .NET Framework 4.8, `net48`) that tracks keyboard and mouse input statistics — counts and distances only, never content. Optional multi-device sync is provided by an independently deployable Cloudflare Worker.

Repo layout:

- `KeyStats.Windows/` — the Windows app, its solution, packaging scripts, and `KeyStats.Sync.Tests`.
- `contracts/sync/v1/` — wire-level source of truth for E2EE sync (JSON Schema, OpenAPI, fixtures).
- `services/sync-worker/` — the Cloudflare Worker backend (TypeScript, D1 + R2).
- `scripts/` — build and release scripts.
- `docs/` — design docs and implementation plans.

Detailed guidance:

- App architecture, conventions, and constraints: `KeyStats.Windows/CLAUDE.md` and `KeyStats.Windows/AGENTS.md`.
- Sync protocol rules: `contracts/sync/v1/README.md`.
- Sync service operations and deployment: `services/sync-worker/README.md`.

## Build & Test

```bash
cd KeyStats.Windows

# Build
dotnet build KeyStats/KeyStats.csproj -c Release

# Automated tests (sync protocol, crypto fixtures, scheduling, persistence)
dotnet test KeyStats.Sync.Tests/KeyStats.Sync.Tests.csproj

# Package (produces dist/KeyStats-Windows-<version>.zip)
powershell -ExecutionPolicy Bypass -File ./build.ps1 -Configuration Release
```

```bash
# Sync worker
cd services/sync-worker
npm ci
npm run check
```

## Architecture (Windows app)

**Entry point**: `KeyStats/App.xaml.cs` — single-instance mutex, theme init, service init, tray icon, context menu, windows, import/export. Exit sequence: analytics flush → monitor stop → stats flush → cleanup.

**Core singleton services** (`KeyStats/Services/`):
- `InputMonitorService` — global `WH_KEYBOARD_LL` / `WH_MOUSE_LL` hooks via `SetWindowsHookEx`.
- `StatsManager` — aggregation, persistence, formatting, history queries, midnight rollover, import/export.
- `NotificationService` — Windows toast notifications.
- `StartupManager` — `HKCU\...\Run` registry entry.

**UI**: light MVVM (View + ViewModel, no framework) in `KeyStats/Views/` and `KeyStats/ViewModels/`; custom controls in `KeyStats/Views/Controls/`. **Helpers** in `KeyStats/Helpers/` (`NativeInterop`, `KeyNameMapper`, `ThemeManager`, `ActiveWindowManager`, `Converters`). **Models** in `KeyStats/Models/` (`DailyStats`, `AppStats`, `AppSettings`).

## Critical Rules

### Privacy
- Only store aggregate counts/distances. NEVER persist keystroke content, raw mouse paths, or clipboard data.
- Secrets, IDs, key names, and counts are never analytics properties or log fields.

### Thread Safety
- Low-level hook callbacks run on the hook thread and must be non-blocking: always call `CallNextHookEx`, no file IO, serialization, or UI work — dispatch via the thread pool.
- `StatsManager` shared state is lock-protected. UI updates must go through the WPF Dispatcher. Never mutate WPF-bound collections from background threads.
- Timers fire on thread-pool threads; synchronize before touching shared state.

### Persistence
- JSON files in `%LOCALAPPDATA%\KeyStats\` — `daily_stats.json`, `history.json`, `settings.json`. Keep backward-compatible when adding fields (old settings files must still load).
- Preserve the exit sequence in `App.OnExit`; new background/timer resources must be disposed on exit.

### UI Style
- Soft glass-card surfaces, thin separators, subtle shadows, 10–12pt card radius. Support runtime light/dark theme switching via `ThemeManager` dynamic resources.
- User-facing copy is Chinese-first. When adding a new page/window, emit matching `pageview` and `click` analytics events.

## Sync Service

- Wire protocol: `contracts/sync/v1/README.md`. Backend operations and deployment: `services/sync-worker/README.md`.
- Do not change the protocol schema, fixtures, or Windows sync behavior without a corresponding plan.

## Automatic Commits

- After a feature or module is fully implemented and its relevant checks pass, create an atomic commit **without waiting for the user to ask**.
- Re-check `git status` first; stage only files belonging to the completed feature or module.
- Do not automatically commit incomplete work, failed verification, or unrelated existing changes.
- Use Conventional Commits style: `feat: ...`, `fix: ...`, `docs: ...`.
- Commit on the current branch only; never push, force-push, amend, or rewrite history automatically.
