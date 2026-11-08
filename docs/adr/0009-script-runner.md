# ADR 0009 — Script runner widget

**Status:** accepted (v1.10.0)

## Context
The user asked for a widget that runs their own scripts in the background and shows their status. The
original spec says "do not provide unrestricted shell execution by default", and Quick Actions refuses
script files for that reason.

## Decision
Provide script execution as an explicit, opt-in, user-scoped feature rather than general shell access:

- **Off by default.** Nothing runs until the user turns on "Allow this widget to run my scripts" in
  Configure…. The setting's help text says scripts run with the user's account. Turning it off stops
  anything the widget started.
- **Only scripts the user lists.** Each entry must be an absolute local path to a `.ps1`, `.bat`, `.cmd`,
  `.py` or `.exe` file. URLs, relative paths and other file types are rejected, and there is no free-text
  command field.
- **No shell parsing of user text.** Each script type maps to a fixed interpreter
  (`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File`, `cmd.exe /d /c call`,
  `python.exe -u`, or the .exe itself). Arguments are split and passed through `ArgumentList`.
  `-ExecutionPolicy Bypass` applies to that one process only; Windows' policy is not changed.
- **Contained runs.** Scripts run as the current user, never elevated, with no window and with stdin closed,
  so a prompt fails instead of hanging. There is a timeout (default 10 min) after which the whole process
  tree is killed. A script can't run twice at the same time, and running scripts are killed when the widget
  is removed or SmartDock exits.
- **Visible.** Each script shows its state, exit code, duration, schedule and its last output lines. The
  full output (last 400 lines) can be copied. Notifications are sent for failures by default.

## Consequences
- This is real code execution, by design. The safeguards are the user's explicit consent and an explicit
  list of files, not sandboxing.
- Scheduled scripts run for the first time shortly after SmartDock starts, then every N minutes. The schedule
  is held in memory and is not a Windows scheduled task.
