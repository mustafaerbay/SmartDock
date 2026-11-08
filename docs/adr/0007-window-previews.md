# ADR 0007 — Live window previews with DWM thumbnails

**Status:** accepted (v1.6.0)

## Context
When an app has several windows (five File Explorer windows, for example), the dock icon only showed a
count. The user wanted taskbar-style previews on hover.

## Decision
Use the documented DWM thumbnail API (`DwmRegisterThumbnail` / `DwmUpdateThumbnailProperties`), which is
the same mechanism the Windows taskbar uses. The compositor draws each source window live into a rectangle
of our own window.

- `WindowPreviewWindow` is a separate top-level window. DWM doesn't draw thumbnails into layered
  (transparent) windows, and the dock is one. It is set to WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, topmost,
  has rounded corners on Windows 11, and is placed fully outside the dock window so the dock never covers it.
- It opens after hovering for 450 ms and closes 300 ms after the pointer leaves both the icon and the
  preview. Moving between icons switches it at once.
- Each thumbnail is fitted to its source window's aspect ratio. Thumbnails shrink when there are many
  windows so the preview fits the monitor.
- If DWM can't provide a thumbnail, the app icon is shown instead.

## Consequences
- No screenshots are taken and nothing is stored, so the privacy impact is the same as the taskbar's.
- Minimised windows show whatever DWM keeps for them, normally their last frame.
