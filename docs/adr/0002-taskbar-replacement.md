# ADR 0002 — Reversible taskbar replacement

**Status:** accepted

## Context
Replacement mode must hide the Windows taskbar without modifying system files, must always be
reversible, and must not leave the desktop unusable after a crash. Hiding `Shell_TrayWnd` alone is not
enough on Windows 11: the work area stays reserved and Explorer re-shows the taskbar in some situations
(Start menu, Explorer restart).

## Decision
`TaskbarController` (SmartDock.Windows/Taskbar):
1. Writes `%LOCALAPPDATA%\SmartDock\taskbar-recovery.json` with the original AppBar state
   (`ABM_GETSTATE`). **If that file cannot be written, it refuses to hide.**
2. Sets the taskbar to auto-hide via `ABM_SETSTATE` so Windows releases the reserved work area.
3. Hides `Shell_TrayWnd` and every `Shell_SecondaryTrayWnd` (multi-monitor).
4. Verifies the result; if the taskbar is still visible it restores and falls back to companion mode.
5. Re-applies on `TaskbarCreated` (Explorer restart) and from the dock's 1.5 s watch tick if Explorer
   shows the taskbar again. If re-hiding fails, the taskbar is restored and the app switches to companion mode.

Restore = show all taskbar windows, put back the original AppBar state, delete the recovery file.

Recovery paths (any one is enough):
- Normal exit (`App.OnExit`, restore runs first) and the fatal-exception handler.
- A **watchdog process** (`SmartDock.exe --watchdog <pid>`) restores the taskbar if the dock process
  dies, or if its UI stays hung (`IsHungAppWindow`) for ~10 s, while the recovery file names it as owner.
- At every startup, a leftover recovery file triggers a restore before anything else.
- The tray menu's "Restore Windows Taskbar" item. (The tray runs on the main UI thread because
  H.NotifyIcon's WPF control requires it; the watchdog covers the hung-UI case.)
- Manual: `SmartDock.exe --restore-taskbar`.

## Consequences
- Auto-hide is an Explorer setting, so while replacement mode is active the user's taskbar shows as
  "automatically hide" in Windows Settings; it is restored to the recorded value afterwards.
- Win+key shortcuts and the Start menu remain Explorer's (the Start button synthesises the Win key).
- **Not yet verified on hardware** — see docs/STATUS.md for the manual test checklist.
