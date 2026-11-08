# SmartDock architecture

```
SmartDock.App            WPF exe: composition root (App), DockWindow, Settings/WidgetManager windows,
  │                      WidgetCard chrome, themes, tray icon, Program (headless modes)
  ├── SmartDock.Widgets  Built-in widgets (view models only, net10.0, unit-tested with fakes)
  ├── SmartDock.Windows  Win32/WinRT: taskbar, windows, icons, monitors, metrics, GSMTC media, DPAPI
  ├── SmartDock.Infrastructure  JSON stores with atomic save + corrupt-file quarantine
  └── SmartDock.Core     Contracts and engine: IDockWidget, WidgetBase, WidgetHost (scheduler),
                         WidgetRegistry, EventBus, NotificationService, settings/layout models
```

## Runtime flow
1. `Program.Main` handles `--restore-taskbar`, `--watchdog PID`, `--wait-for PID` without loading UI.
2. `App.OnStartup`: logging → single-instance mutex → restore taskbar if a recovery record exists →
   DI container → theme → `DockWindow` → tray icon → watchdog process → taskbar mode →
   load widgets from `layout.json` → start the shared scheduler.
3. `DockWindow` registers as a shell-hook window (`RegisterShellHookWindow`) and a foreground WinEvent
   hook. Window create/destroy/activate messages refresh the app list (debounced 150 ms); there is no
   window polling apart from one 1.5 s watch tick (fullscreen check, taskbar enforcement, topmost).
4. Settings changes go through `ISettingsService.Update` → saved → `Changed` → theme swap, reposition,
   taskbar mode, startup registration. Layout changes go through `ILayoutService.Mutate` (debounced save).

## Placement and DPI
The app is Per-Monitor-V2 aware (`app.manifest`). The dock sizes itself in DIPs (`SizeToContent`) and is
positioned with `SetWindowPos` in physical pixels on the resolved monitor's work area, re-run on
`WM_DISPLAYCHANGE`, `WM_DPICHANGED`, `WM_SETTINGCHANGE` and size changes. A disconnected monitor falls
back to the primary one.

## Files (%LOCALAPPDATA%\SmartDock)
| File | Purpose |
|---|---|
| `settings.json` | Appearance and behaviour (see docs/examples) |
| `layout.json` | Pinned apps and widget instances with per-instance settings |
| `taskbar-recovery.json` | Exists only while the Windows taskbar is hidden |
| `secrets\*.bin` | DPAPI-encrypted tokens (current user) — nothing secret in JSON |
| `logs\smartdock-*.log` | Serilog, daily rolling, 7 files kept |

SQLite is not used yet: nothing in this milestone needs relational storage. It is the planned store for
notes, clipboard history and automation execution history.

## Threading rules
- Widget refreshes run on the thread pool; widgets raise scalar property changes only and expose
  immutable snapshots for lists.
- Commands' `CanExecuteChanged` is marshalled with `WidgetBase.PostToUi`.
- Collections bound to the UI (`DockViewModel`, `AppsViewModel`) are mutated only on the UI thread.
- The tray icon lives on the UI thread. `TaskbarController` is thread-safe, so the watchdog process and
  recovery paths can call the same restore logic from anywhere.
