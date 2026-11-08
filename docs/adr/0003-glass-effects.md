# ADR 0003 — Glass themes without per-frame blur

**Status:** accepted (revisit)

## Context
The dock window uses `AllowsTransparency=True` (layered window) for its floating, rounded, shadowed look.
Windows 11 system backdrops (`DWMWA_SYSTEMBACKDROP_TYPE`, Mica/Acrylic) do not apply to layered windows,
and a screen-capture blur would cost CPU/GPU continuously.

## Decision
"Frosted Glass" and "Colorful Glass" use translucent gradient brushes plus light borders. No blur.
Theme switching swaps a single ResourceDictionary, so every `DynamicResource` updates without restart.

## Alternatives for later
A non-layered window with `WindowChrome`, `DwmExtendFrameIntoClientArea` and
`DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW` would give true acrylic on Windows 11 22H2+,
at the cost of rectangular hit-testing and more complex shadow/rounding handling.
