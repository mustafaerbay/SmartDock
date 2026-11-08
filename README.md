# SmartDock

Current version: **v1.25.3** · [Changelog](CHANGELOG.md)

A native Windows 11 smart dock: a floating, themeable bar with your apps on the left, live widgets in the
middle and system status on the right. It runs alongside the Windows taskbar (companion mode, default)
or temporarily replaces it (replacement mode), always reversibly.

Built with C# / .NET 10 / WPF (MVVM), no embedded browser.

> **Status:** milestone 1 is code-complete but has **not yet been compiled or run** — see
> [docs/STATUS.md](docs/STATUS.md) for exactly what is done, what is next, and a manual test checklist.
> Screenshots will be added after the first Windows run; none of the images here are mock-ups.

## Features (milestone 1)

**Dock**
- Three regions: Start + pinned/running apps · smart widgets · system widgets, notifications, settings
- Rounded translucent container, soft shadow, accent indicators; height, padding, spacing, radius,
  opacity and shadow are all configurable and apply live
- Themes: Midnight, Dark, Light, Frosted Glass, Colorful Glass
- Bottom or top edge, monitor selection, auto-hide with edge reveal, hides over fullscreen apps
- Per-monitor DPI aware (100–200 % and mixed-DPI setups)

**Apps**
- Running apps detected through shell hook messages (no polling), grouped by executable
- Click: launch, activate, minimise, or cycle through an app's windows
- Right-click: window list, open new window, pin/unpin, close
- Drag pinned apps to reorder

**Widgets** — real system data only; unavailable sources show an explicit state, never fake numbers
| Widget | Data source |
|---|---|
| Clock | 12/24 h, seconds, date, local or UTC |
| CPU | `GetSystemTimes`, sparkline, load-coloured |
| Memory | `GlobalMemoryStatusEx`, used/free, meter |
| Disk | Any fixed drive, used/free |
| Network | Interface byte counters, live ↓/↑ rate, offline state |
| Battery | `GetSystemPowerStatus`; shows AC on desktops |
| Music | Windows media sessions (GSMTC) — Spotify, browsers, any app that publishes a session |
| Pomodoro | Focus/break timer with notifications |

Widgets can be added from the **widget manager** (search, categories, drag into the dock), reordered by
drag-and-drop with an insertion indicator, configured from their context menu, and removed. Layout and
settings persist in `%LOCALAPPDATA%\SmartDock`.

**Robustness**
- One shared scheduler refreshes widgets off the UI thread; a failing widget shows an error badge and
  retries with exponential backoff — it cannot take the dock down
- Corrupt config files are moved aside and replaced by defaults
- Replacement mode is undone on exit, after a crash or UI hang (watchdog process), at next start, from
  the tray, or with `SmartDock.exe --restore-taskbar`

## Build

Requirements: Windows 10 19041+ / Windows 11, .NET 10 SDK.

```powershell
dotnet restore SmartDock.sln
dotnet build SmartDock.sln -c Release
dotnet test SmartDock.sln -c Release
dotnet run --project src/SmartDock.App -c Release
```

Or use the scripts:

```powershell
./build/build.ps1                      # restore, build, test
./build/publish.ps1                    # ONE self-contained SmartDock.exe + zip in ./artifacts (no .NET install needed)
./build/installer.ps1                  # MSI installer (per user, no admin) + latest.json in ./artifacts/installer
./build/release.ps1 -Target user@host:/srv/smartdock   # publish for in-app updates (docs/UPDATES.md)
```

See [docs/PACKAGING.md](docs/PACKAGING.md) for publishing from Rider (publish profiles), the
small framework-dependent variant, signing and updating.

Package versions use floating minor/patch ranges (e.g. `9.0.*`) because they were chosen without
NuGet access; pin them (or enable a lock file) after the first successful restore.

## Using it

- **Tray icon** → Show Dock, Widget Manager, Settings, Companion/Replacement mode, Restart Dock,
  Restore Windows Taskbar, Exit
- **Right-click** the dock background for the widget manager and settings; right-click a widget for its
  own options (e.g. 24-hour clock, refresh rate, drive)
- **Rescue:** if the taskbar ever stays hidden, run `SmartDock.exe --restore-taskbar`

## Project layout

```
src/SmartDock.App              WPF app, views, themes, tray, composition root
src/SmartDock.Core             Widget contracts, scheduler, registry, event bus, models
src/SmartDock.Widgets          Built-in widgets (platform-neutral view models)
src/SmartDock.Windows          Win32/WinRT integrations
src/SmartDock.Infrastructure   Configuration persistence
tests/                         xUnit tests
docs/                          Architecture, ADRs, status, example config
build/                         Build and publish scripts
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/adr](docs/adr) for design decisions.

SmartDock is an independent implementation inspired by the idea of a widget dock; it contains no code or
assets from other products.
