# ADR 0006 — File search through voidtools Everything IPC

**Status:** accepted (v1.3.0)

## Context
The user wanted the Everything file-search app (voidtools) available from the dock. Everything keeps its own
NTFS index and can be queried by other programs in three ways:
1. The SDK DLL (`Everything64.dll`). It is a separate download and would have to be shipped or located.
2. `es.exe`, the command-line client. It is also a separate download and starts a process for every query.
3. The window-message IPC (`WM_COPYDATA` to the `EVERYTHING_TASKBAR_NOTIFICATION` window), documented in the
   SDK's `everything_ipc.h`. The SDK DLL is itself a wrapper around this.

## Decision
Use the IPC directly (`EverythingSearchService` in SmartDock.Windows). Queries use `EVERYTHING_IPC_COPYDATAQUERYW`
with a packed 20-byte header, and replies arrive at a message-only window as an `EVERYTHING_IPC_LISTW`. Every
offset in a reply is bounds-checked before it is read. Each query times out after 3 s.

- The dock can't take keyboard input, so typing happens in a normal window (`FileSearchWindow`).
- Results open via the shell, exactly as a double-click in Explorer would. Only the item the user picks is opened.
- "Open in Everything" starts `Everything.exe -search <text>`, passing the text as a single argument (no shell).
- The widget shows the real state: Ready, Not running (with a Start action) or Not installed. It never shows
  placeholder results.

## Consequences
- Nothing extra is shipped or installed. Everything 1.4 works, and 1.5 alpha is detected by its window class.
- The protocol layout is taken from the SDK header and hasn't yet been verified against a live instance. If
  results come back empty or garbled, the 32-bit `reply_hwnd` field is the first thing to check.
