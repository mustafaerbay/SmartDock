# ADR 0005 — Reserve screen space with the AppBar API

**Status:** accepted (v1.1.0)

## Context
The dock is a topmost window placed inside the monitor work area. Maximised windows fill the whole work
area, so they sit under the dock and their bottom edge (status bars, scroll bars, buttons) is hidden.

## Decision
Offer "Stick to the screen edge" (`Behavior.ReserveSpace`, default on). When enabled and auto-hide is off,
the dock window registers as an application desktop toolbar (`SHAppBarMessage` `ABM_NEW` → `ABM_QUERYPOS`
→ `ABM_SETPOS`) for a band equal to the gap to the screen edge (`EdgeMargin`) + the visible dock height + the gap to windows
(`WindowGap`, v1.2.0). The shell
shrinks the work area by that band, so maximised windows stop above the dock. The shell may move the band
next to other app bars (the Windows taskbar in companion mode); the dock is placed inside the granted band.

- `ABN_POSCHANGED` → recompute the band. `TaskbarCreated` (Explorer restart) → register again.
- An identical band is not re-sent, to avoid a work-area-change → reposition loop.
- Released with `ABM_REMOVE` when the setting is turned off, auto-hide is turned on, the window closes,
  the app exits, or an unhandled exception is caught.

## Consequences
- Nothing is persisted. If SmartDock is killed without cleanup, Windows drops the reservation once the
  window no longer exists or when Explorer restarts. No registry or Explorer settings are changed.
- Fullscreen apps ignore the work area, so fullscreen behaviour is unchanged.
- The alternative of lowering the dock below other windows was rejected: the dock would disappear behind
  every app.
