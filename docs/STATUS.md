# Implementation status

**Current version: v1.25.3** (release pipeline verified on Windows with v1.14.4) — see [CHANGELOG.md](../CHANGELOG.md).

**Milestone 1 (phases 1–3): builds and runs** (built by the user in JetBrains Rider on Windows 11).

**Phase 4 (rich widgets): code complete, not yet compiled.** Written without a .NET SDK; syntax-checked
and independently reviewed. Expect a few compile fixes on the first Rider build.

## Phases 1–3 — implemented (built and running)
| Area | Notes |
|---|---|
| Solution, DI, Serilog, JSON config with validation + corrupt-file recovery | |
| Dock window: 3 regions, rounded/translucent, shadow, all dimensions configurable | |
| 5 themes, live switching, accent colour, opacity, radius, spacing, height, shadow | Glass = gradients (ADR 0003) |
| Tray icon with the full menu from the spec | Hung-UI recovery via watchdog |
| Companion mode (default) and replacement mode with watchdog/startup/manual recovery | ADR 0002 |
| In-app updates: ssh:// (OpenSSH scp), https://, share or folder; latest.json + SHA-256; click to install; passive MSI + relaunch (v1.13.0) | ADR 0010, docs/UPDATES.md |
| MSI installer: WiX v5 per-user, Start menu + rescue shortcut, autostart, clean shutdown on upgrade/uninstall, launch on finish (v1.12.0) | docs/PACKAGING.md |
| Multi-row dock: apps and widgets wrap onto a second row when the dock is full (v1.16.0); maximised windows re-fit when the dock gets shorter (v1.18.1) | — |
| Full-width mode and dock size scaling 60–150 % (v1.9.0) | — |
| Window previews on hover: live DWM thumbnails per window, click to switch, × to close (v1.6.0) | ADR 0007 |
| Attention blinking: app icons blink then stay highlighted on HSHELL_FLASH until the app is activated (v1.4.0) | — |
| Stick to the screen edge: reserves the dock's height so maximised windows stop at it (v1.1.0); configurable gap to screen edge and to windows (v1.2.0) | ADR 0005 |
| Explorer restart handling, fullscreen avoidance, multi-monitor selection, per-monitor DPI | |
| Running apps (shell hook), pins, activate/minimise/cycle, context menu, pin drag-reorder | |
| Widget engine: contract, registry, shared scheduler, timeouts, isolation, backoff, per-instance settings | |
| Widgets: Clock, CPU, Memory, Disk, Network, Battery, Music (GSMTC), Pomodoro | real data only |
| Widget manager (search, category filter, add/remove, drag into dock), reset layout | |
| Drag-and-drop reorder with insertion indicator, insert/remove/hover/auto-hide/toast animations | respects reduced-motion |
| Notification centre (dock-generated notifications, history, dismiss, dedupe) | |
| Start with Windows (HKCU Run), DPAPI secret store | |
| Unit tests: scheduler, backoff, event bus, notifications, settings/layout, widgets | |

## Phase 4 — implemented (unverified)
| Widget | Data source / notes |
|---|---|
| Bookmarks | Own links + Chrome/Edge/Brave profile bookmarks (local file, read-only), folder browsing, search window, open in default or source browser (v1.14.0) |
| CodeHub / GitLab | GitLab v4-compatible API: review MRs, own MRs, pipelines per project/branch; review & pipeline notifications; PRIVATE-TOKEN/Bearer; tolerant of missing /user and /pipelines (v1.11.0); MRs grouped by project or category, collapsible, remembered (v1.19.0); MR open time, stale after 7 days (v1.19.1); hover quick view with reviewers, merge status, labels, description (v1.20.0); filter chips + search window across all CodeHub widgets with author:/label:/is: qualifiers (v1.21.0) |
| Script runner | Opt-in; local .ps1/.bat/.cmd/.py/.exe only; manual or every N min; status/exit code/duration/output tail; timeout + kill tree; failure notifications (v1.10.0, ADR 0009) |
| Notes beautifier & checker | Live JSON/XML/YAML validation with line/column (whole note or ``` blocks), Beautify (Ctrl+Shift+F, selection-aware, undoable): JSON/XML pretty-print, Markdown tidy (headings, bullets, numbering, spacing, tabs), code font toggle (v1.15.0) |
| Quick folders | Saved folders/files: open in Explorer, terminal here, copy path, remove; drag from Explorer to add; missing/offline dimmed (v1.8.0) |
| Kubernetes cluster | One widget per kubeconfig context; nodes/pods/workloads/PVC/jobs/events analysis; copy kubectl commands; critical alerts; partial-RBAC tolerant (v1.7.0, ADR 0008); explorer window: deployments, pods, live logs (follow, previous, all pods of a deployment) (v1.23.0); opt-in shell in pod via kubectl exec, confirmed per use (v1.24.0); opt-in deployment changes: scale, set image, restart, rollback, edit YAML with dry run (v1.25.0) |
| Web page status | Up to 20 URLs, GET with timeout, status/latency/uptime, slow threshold, down/recovery notifications after 2 consecutive failures (v1.5.0) |
| Everything search | voidtools Everything via its WM_COPYDATA IPC; type-to-search window, open / open folder / copy path; honest Not running / Not installed states (v1.3.0, ADR 0006) |
| Calendar | Local `.ics` files or iCal URLs (Outlook "publish", Google "secret address"); RRULE subset (DAILY/WEEKLY BYDAY/MONTHLY incl. ordinal BYDAY like 2TU/YEARLY, COUNT/UNTIL), EXDATE, overrides; BYSETPOS series skipped rather than misdated; countdown, agenda popup, Join button (Teams/Zoom/Meet/Webex), reminder notifications; publishes `CalendarEventsUpdated` for automation |
| Weather | Open-Meteo (no key) behind `IWeatherProvider`; city or coordinates; °C/°F; 3-day forecast popup; cached data with "updated x ago" when offline |
| GitHub | REST API with a PAT (DPAPI); assigned issues, review requests, notifications (classic tokens), repo stars + latest Actions run; build-finished notification; publishes `MetricObserved` for star milestones; pull requests on the shared review board — grouping, filters, quick view, open time, search (v1.22.0) |
| Claude usage | Anthropic Admin API cost_report + usage_report (organisation Admin key); optional monthly budget meter. claude.ai plan limits: no public API → explicit unavailable state. Anthropic labels these endpoints "legacy" (still served); migration to `/v1/organizations/analytics/usage` is a follow-up |
| ChatGPT / OpenAI usage | OpenAI organization costs + usage/completions (Admin key). ChatGPT subscription usage: no public API → explicit unavailable state |
| Notes | notes.json; notes window with list/editor, autosave, delete |
| Quick actions | Apps, https URLs, folders, approved built-ins (lock, show desktop, screenshot, settings, task manager, downloads); scripts refused, no arguments |
| Volume | Core Audio COM; wheel to adjust, popup slider + mute, device name |
| Clipboard | Opt-in (off by default), AddClipboardFormatListener, memory only, skips formats apps mark private |

Shared infrastructure added: click-to-open widget popups, mouse-wheel widgets, a generic **Configure…** dialog
generated from each widget's fields (secrets go to DPAPI, never to layout.json), shared `HttpClient`.

## Not implemented yet (next phases)
| Item | Phase | Notes |
|---|---|---|
| Outlook / Google Calendar API connectors (OAuth) | 4+ | ICS URLs from both services work today |
| Automation engine (rules, triggers, meeting → pause music, milestones) | 5 | Event bus is already in place |
| True acrylic backdrop | 6 | ADR 0003 |
| Animated FLIP reordering of neighbours | 6 | Moves currently fade the moved card in |
| Integration tests, performance measurements | 7 | No performance numbers are claimed |
| Screenshots in README | — | Need a real Windows run |

## Known limitations

- Recurring calendar events are expanded in the PC's local time zone; a series defined in another zone can shift by an hour across DST changes that zone doesn't share. WKST is ignored.
- Widget popups rely on WPF `StaysOpen=false`. Because the dock never takes focus, clicking another app may not close a popup on every system — click the widget again to close it. (To verify on Windows.)
- Packaged (UWP/MSIX) apps are grouped per window and may show a monogram instead of an icon.
- `.lnk` pins are launched correctly but are not matched to their running windows (target not resolved).
- Glyphs come from Segoe Fluent Icons (Windows 11) with Segoe MDL2 Assets as fallback.

## Manual verification checklist (Windows 11)

- [ ] v1.1.0: maximise a window with "Stick to the screen edge" on → its bottom edge stops above the dock; turn it off → the window extends under the dock again; exit SmartDock → maximised windows use the full height.
- [ ] v1.1.0: with a maximised app focused, click the dock's Settings button → the Settings window appears in front.
1. `./build/build.ps1` — builds and all tests pass.
2. Run `SmartDock.exe`: dock appears centred above the taskbar; CPU/RAM/Network/Clock update.
3. Play music in Spotify/Edge: title, artwork and controls work.
4. Drag widgets to reorder; restart; order persists.
5. Settings: switch every theme, move the dock to the top, change monitor, enable auto-hide.
6. Tray → Replacement Mode: taskbar hides, maximised windows use the full height, Start/Win shortcuts work.
7. While in replacement mode: restart Explorer (Task Manager) → taskbar stays hidden, dock stays.
8. Kill SmartDock in Task Manager → taskbar comes back within ~2 s (watchdog).
9. Tray → Restore Windows Taskbar → taskbar back, mode switches to companion.
10. Start a fullscreen video/game → dock hides; leaves fullscreen → dock returns.
