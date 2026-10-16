English | [简体中文](./README_ZH.md)

# KeyStats - Windows Keyboard & Mouse Statistics Tray App

KeyStats is a lightweight native Windows tray application that tracks daily keyboard keystrokes, mouse clicks, mouse movement distance, and scroll distance, with optional end-to-end encrypted multi-device sync.

## Installation & Usage

### Option 1: Install via Scoop

```bash
scoop bucket add keystats https://github.com/debugtheworldbot/scoop-keystats
scoop install keystats
```

### Option 2: [Download from GitHub Releases](https://github.com/debugtheworldbot/keyStats/releases)

> **No dependencies required**: The Windows version uses .NET Framework 4.8, which is pre-installed on Windows 10 (1903+) and Windows 11 - ready to use out of the box. If your Windows 10 version is older (before 1903), you can upgrade your system or [manually install .NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48).

### Linux (community port)

[Download the Linux version](https://github.com/0x5c0f/keyStats/releases)

## Features

- **Keyboard Keystroke Statistics**: Real-time tracking of daily key presses
- **Mouse Click Statistics**: Separate tracking of left and right clicks
- **Mouse Movement Distance**: Track total distance of mouse movement
- **Scroll Distance Statistics**: Record cumulative page scroll distance
- **System Tray Integration**: Runs in the system tray with quick access to statistics
- **Detailed Panel**: Click the tray icon to view complete statistics
- **Key Breakdown & History Charts**: Most-used keys plus week/month line and bar charts
- **Daily Auto-Reset**: Statistics automatically reset at midnight
- **Data Persistence**: Data persists after application restart
- **Multi-Device Sync**: End-to-end encrypted synchronization of supported daily keyboard and mouse-click statistics across paired devices

## System Requirements

- **Windows 10 (1903+) or Windows 11**
- **No dependencies required**: Uses .NET Framework 4.8 (pre-installed on Windows 10/11, ready to use out of the box)
- **App size**: ~5-10 MB (lightweight, no additional runtime needed)

> **Note**: If your Windows 10 version is older (before 1903), you can:
> 1. Upgrade to Windows 10 1903 or higher (recommended)
> 2. Or manually install .NET Framework 4.8: [Download link](https://dotnet.microsoft.com/download/dotnet-framework/net48)

## First Run

The Windows version **requires no additional permission setup**. The app will automatically start tracking once launched.

> **Note**: On first launch, Windows may show a security warning. Click "Run anyway" to proceed.

## Privacy Statement

KeyStats stores aggregate statistics and **does NOT record**:
- Typed text or the order of keystrokes
- Specific cursor or click locations

Data stays on the local device by default. When you explicitly enable multi-device sync, supported daily key-press totals, per-key/key-combination counts, mouse-button click counts, and encrypted device information are encrypted locally before upload. Mouse movement distance, scroll distance, and per-app statistics are not currently synced, and the service cannot read the encrypted statistics.

## Development

### Build & test

```bash
cd KeyStats.Windows

# Debug / Release build
dotnet build KeyStats/KeyStats.csproj -c Release

# Automated tests (sync protocol, crypto fixtures, scheduling, persistence)
dotnet test KeyStats.Sync.Tests/KeyStats.Sync.Tests.csproj

# Package for distribution (produces dist/KeyStats-Windows-<version>.zip)
powershell -ExecutionPolicy Bypass -File ./build.ps1 -Configuration Release
```

See [`KeyStats.Windows/CLAUDE.md`](./KeyStats.Windows/CLAUDE.md) for architecture and [`services/sync-worker/README.md`](./services/sync-worker/README.md) for the sync backend.

### Repository layout

```
KeyStats.Windows/       Windows tray app (WPF, .NET Framework 4.8) + sync tests
contracts/sync/v1/      E2EE sync wire protocol (JSON Schema, OpenAPI, fixtures)
services/sync-worker/   Cloudflare Worker backend for E2EE sync
scripts/                Build and release scripts
docs/                   Design docs and implementation plans
```

## License

MIT License
