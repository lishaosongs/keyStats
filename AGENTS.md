# KeyStats - AI Agent Development Guide

**Project Type**: Windows system tray application (WPF + WinForms interop)
**Language**: C# (nullable enabled, LangVersion 10.0)
**Runtime**: .NET Framework 4.8 (`net48`)
**Architecture**: event-driven singleton services with light MVVM UI, plus an independent Cloudflare Worker for optional E2EE sync

## Quick Context

KeyStats is a privacy-focused Windows tray app that tracks keyboard/mouse statistics (counts and distances only, no content logging). Core components:

- `InputMonitorService`: global low-level keyboard/mouse hooks
- `StatsManager`: data aggregation and persistence
- `TrayIconViewModel` / `App.xaml.cs`: tray icon, context menu, popup lifecycle
- `StatsPopupWindow`: detailed statistics panel

**Privacy First**: NEVER persist keystroke content, raw mouse paths, or clipboard data — only aggregate counts and distances.

See `KeyStats.Windows/CLAUDE.md` and `KeyStats.Windows/AGENTS.md` for the full app guide, `contracts/sync/v1/README.md` for the sync protocol, and `services/sync-worker/README.md` for the sync backend.

---

## Decision Trees for Common Tasks

### 🔧 Before Making Any Code Changes

```
1. Read the relevant file(s) first
2. Check existing patterns and naming conventions
3. Consider thread safety (hook/thread-pool vs UI thread)
4. Check if changes affect privacy or persisted data compatibility
```

### 🎯 When Adding New Features

```
New feature request?
├─ UI-related?
│  ├─ Tray/popup? → Update App.xaml.cs / TrayIconViewModel / StatsPopupWindow
│  └─ New window? → Add View + ViewModel under Views/ and ViewModels/
├─ Statistics tracking?
│  ├─ New metric? → Update Models/DailyStats + StatsManager persistence
│  └─ New event type? → Update InputMonitorService event callbacks
├─ Data persistence? → Keep JSON fields backward-compatible
└─ Sync-related? → Update contracts/sync/v1 + services/sync-worker + client sync layer
```

### 🐛 When Debugging Issues

```
Issue type?
├─ No statistics updating?
│  ├─ Check: hooks installed (SetWindowsHookEx returns non-null)
│  └─ Check: InputMonitorService is raising events
├─ UI not updating?
│  ├─ Verify: updates marshalled via the WPF Dispatcher
│  └─ Check: StatsUpdateRequested handler is wired
├─ Data not persisting?
│  └─ Check: StatsManager flush ran on the debounced timer and on exit
└─ Sync failing?
   └─ Check: credentials/cache state, then services/sync-worker README
```

---

## Critical Rules

### 🔴 MUST Follow (Security & Privacy)

- ✅ Only track counts and distances, NEVER content
- ✅ Never persist keystroke content, raw mouse paths, or clipboard data
- ✅ Keep hook callbacks non-blocking; always call `CallNextHookEx`
- ✅ Marshal UI updates through the WPF Dispatcher
- ✅ Dispose hooks/timers/background resources in the exit sequence

### 🟡 SHOULD Follow (Quality)

- ✅ One primary class per file, matching namespace/folder boundaries
- ✅ Use `guard`-style early returns and descriptive names, avoid magic numbers
- ✅ User-facing copy is Chinese-first; keep JSON fields and analytics event names stable
- ✅ Support runtime light/dark theme switching via `ThemeManager` dynamic resources
- ✅ When adding a new page/window, add matching analytics at the same time: a `pageview` for the page and `click` events for key actions

### 🟣 Automatic Commit Policy

- After a feature or module is fully implemented and its relevant checks pass, create an atomic commit **without waiting for the user to ask**.
- Re-check `git status` first; stage only files belonging to the completed feature or module.
- Do not automatically commit incomplete work, failed verification, or unrelated existing changes.
- Use Conventional Commits style: `feat: ...`, `fix: ...`, `docs: ...`.
- Commit on the current branch only; never push, force-push, amend, or rewrite history automatically.

### ☁️ Sync Worker Staging Deployment

- ✅ After changing the staging sync service (`services/sync-worker/**`, `contracts/sync/v1/**`, or its staging workflow) and passing the relevant checks, deploy the staging Worker directly without waiting for additional confirmation
- ✅ Apply pending staging D1 migrations before deploying, and always target the explicit `staging` Wrangler environment
- ✅ Never deploy the production Worker without explicit user authorization in the current conversation

---

## Build & Development Commands

```bash
cd KeyStats.Windows

# Build
dotnet build KeyStats/KeyStats.csproj -c Release

# Automated tests (sync protocol, crypto fixtures, scheduling, persistence)
dotnet test KeyStats.Sync.Tests/KeyStats.Sync.Tests.csproj

# Package (produces dist/KeyStats-Windows-<version>.zip)
powershell -ExecutionPolicy Bypass -File ./build.ps1 -Configuration Release
```

Visual Studio: open `KeyStats.Windows/KeyStats.sln`, build with `Ctrl+Shift+B`, run with `F5`.

---

## Threading & Performance

- **Hook callbacks**: run on the hook thread → dispatch work via the thread pool; no IO/UI work inline
- **UI updates**: always marshal via `Application.Current.Dispatcher`
- **Timers**: fire on thread-pool threads → synchronize before touching shared state
- **Mouse sampling**: 30 FPS, jumps > 500 px filtered
- **Debounced saves**: 2 s write delay, 300 ms UI update debounce

---

## File Structure & Responsibilities

```
KeyStats.Windows/
├── KeyStats.sln
├── KeyStats/
│   ├── App.xaml(.cs)                     # Entry point, tray icon, context menu, lifecycle
│   ├── Services/
│   │   ├── InputMonitorService.cs        # Keyboard/mouse hooks
│   │   ├── StatsManager.cs               # Statistics, persistence, history, sync integration
│   │   ├── NotificationService.cs        # Toast notifications
│   │   └── StartupManager.cs             # Windows startup
│   ├── ViewModels/                       # Tray, popup, settings, app-stats, key-history, ...
│   ├── Views/                            # Windows + Controls/
│   ├── Models/                           # DailyStats, AppStats, AppSettings
│   └── Helpers/                          # NativeInterop, KeyNameMapper, ThemeManager, Converters
└── KeyStats.Sync.Tests/                  # MSTest suite for the sync protocol and persistence
```

---

## Testing & Validation Checklist

### Before Committing Changes

- [ ] Relevant targeted checks pass; do not build by default (see `KeyStats.Windows/AGENTS.md`)
- [ ] `dotnet test KeyStats.Sync.Tests/KeyStats.Sync.Tests.csproj` passes when sync/persistence code changed
- [ ] App runs without crashes
- [ ] Statistics update in real-time and persist across restarts
- [ ] Daily reset works at midnight
- [ ] No hook callbacks blocked by IO/UI work
- [ ] UI updates on the Dispatcher thread
- [ ] No content (keys, mouse paths, clipboard) persisted or logged

### Manual Testing Steps

1. Launch and confirm the tray icon appears
2. Type and click to verify counters increment
3. Open the popup to verify detailed stats, key breakdown, and charts
4. Test reset, settings, and startup toggle
5. Quit and relaunch to verify persistence
6. Wait past midnight to verify auto-reset

---

## Documentation References

### Project Documentation

- `README.md` / `README_ZH.md` — install, features, privacy, build
- `QUICKSTART.md` — quick start guide
- `KeyStats.Windows/README.md` — Windows app details
- `KeyStats.Windows/CLAUDE.md` / `KeyStats.Windows/AGENTS.md` — app development guide
- `contracts/sync/v1/README.md` — sync protocol
- `services/sync-worker/README.md` — sync backend operations
