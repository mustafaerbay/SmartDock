# Changelog

SmartDock uses [Semantic Versioning](https://semver.org): `MAJOR.MINOR.PATCH`.

- **PATCH** (1.0.x) — bug fixes only.
- **MINOR** (1.x.0) — new features or widgets, backwards-compatible settings.
- **MAJOR** (x.0.0) — breaking changes to settings/layout files or behaviour.

The version lives in one place, `<Version>` in `Directory.Build.props`. It is shown on the dock (next to the
Settings button), in the tray tooltip and menu, and in Settings › Dock behaviour. Each delivered build is zipped as
`SmartDock-vX.Y.Z.zip`.

## v1.25.3 — 2026-11-06

### Fixed
- **Gap under the dock after it got shorter** (e.g. widgets 3 rows → 2) with the dock at the bottom. The dock was
  positioned with the window's previous height (the window handle isn't resized yet when WPF reports the new size),
  so it sat too high and also reserved too much screen space. It now uses the size WPF just laid out.
- **Smoother resizing.** Re-reserving the screen band makes Windows move every maximised window, and a resize
  often comes as several steps (rows re-wrapping, widgets animating in). The dock now moves immediately, while the
  band is updated once the size has been stable for 150 ms; maximised windows are re-fitted 250 ms after that.

## v1.25.2 — 2026-11-06

### Fixed
- **Widget popup stayed open behind a window it opened** (e.g. CodeHub › Search, Kubernetes › Pods & logs,
  Everything / Bookmarks search, Notes). The dock never takes focus, so the popup's close-on-outside-click didn't
  fire; SmartDock now closes open widget popups whenever it opens one of its windows.

## v1.25.1 — 2026-11-06

### Fixed
- **Merge / pull request search window showed stale data** (e.g. an MR closed or merged in the browser stayed
  listed until the widget's next scheduled refresh).
  - New **Refresh** button (and F5): reloads the selected widget, or all of them, from the server; the button shows
    "Refreshing…" until each one answers, and the list updates as they do. A widget that doesn't answer within
    45 s is named in the status line.
  - The window is now live: whenever a code widget refreshes (on its own schedule too), the list updates.
  - Coming back to the window when its data is older than a minute refreshes automatically.

## v1.25.0 — 2026-11-05

### Added
- **Kubernetes: change deployments from the explorer** (opt-in). Buttons under the Deployments list and in the
  deployment's right-click menu:
  - **Scale…** — set replicas (scale subresource); extra warning when scaling to 0.
  - **Set image…** — pick a container, edit its image/tag (strategic merge patch, like `kubectl set image`).
  - **Restart** — rolling restart (`kubectl rollout restart`: bumps `kubectl.kubernetes.io/restartedAt`).
  - **Roll back…** — rollout history from the deployment's ReplicaSets (revision, images, age, change-cause);
    restores the chosen revision's pod template, like `kubectl rollout undo --to-revision`.
  - **Edit YAML…** — editor with the deployment's YAML (status and managedFields left out). *Validate* runs a
    server-side dry run (nothing changes); *Apply* (Ctrl+S) replaces it after confirmation. Kind/name/namespace
    must stay the same; the resourceVersion makes the server refuse the change (409) if someone else edited it
    meanwhile — Reload and re-apply.
  - **Off by default**: enable *Allow changing deployments* in the widget's Configure…. Every change shows what
    will happen and the context name before it's applied, and is written to the SmartDock log. RBAC still applies.

## v1.24.1 — 2026-11-05

### Fixed
- **Kubernetes explorer: column headers now sort.** Click a header in the Pods or Deployments list to sort by it,
  click again to reverse; ▲/▼ shows the current column. Sorting uses the real values, not the text: Age by creation
  time (newest first, then oldest first), IP numerically (10.0.0.9 before 10.0.0.10), Ready and Restarts as
  numbers (Restarts starts with the most). Ties are ordered by namespace and name, and the order is kept across
  auto-refresh and filtering.

## v1.24.0 — 2026-11-05

### Added
- **Kubernetes: open a shell in a pod (kubectl exec).** In the pods & logs window: **Open shell…** under the pods
  list and in the pod's right-click menu (which also has Show logs, Show logs of the previous container, Copy exec
  command, Copy pod name).
  - Starts your own `kubectl --kubeconfig … --context … -n <ns> exec -it <pod> -c <container> -- <shell>` in a
    new terminal window (Windows Terminal when it's your default terminal). Shell: auto (bash if present,
    otherwise sh), bash or sh. Container = the one picked in the logs pane, else the pod's first container.
  - **Off by default**: enable *Allow opening a shell in pods (kubectl exec)* in the widget's Configure…. Every
    shell is confirmed first (pod, container, context shown). Each start is written to the SmartDock log.
  - Needs kubectl (PATH or a configured path) and RBAC for `pods/exec`. If the shell closes immediately (no
    permission, distroless image without a shell), the window says why.
  - **Copy exec command** works even with the option off, to paste into your own terminal.
  - Arguments are validated (Kubernetes name rules) and passed as an argument list, never through a command
    interpreter.

## v1.23.0 — 2026-11-05

### Added
- **Kubernetes: pods, deployments & live logs window.** "Pods & logs" in the widget popup (or right-click ›
  Pods, deployments & logs…) opens a window for that cluster:
  - **Deployments** (ready, up-to-date, available, age, images) and **Pods** (ready, status as kubectl shows it —
    CrashLoopBackOff, ImagePullBackOff, Init:…, Terminating —, restarts, age, node, IP); unhealthy rows tinted.
    Namespace picker (your configured namespaces, or all the account can list), name filter, auto-refresh every 10 s.
  - Double-click a deployment to list only its pods (by its label selector).
  - **Live logs** (`kubectl logs -f`): select a pod and its log streams on the right. Container picker or all
    containers, last 100/500/1000/5000 lines, follow, previous container (after a crash), timestamps, wrap.
  - **Logs of all pods of a deployment** at once (up to 10 pods), each line prefixed with the pod.
  - Errors and warnings coloured; "Warnings & errors only" and a Find box filter the lines; scrolling up pauses
    auto-scroll (new lines are counted), Resume / Ctrl+Space continues; copy shown or selected lines (Ctrl+C).
  - Problem rows in the popup got a **View live logs** button that opens the window on that pod (previous
    container for crash loops).
  - Read-only: nothing in the cluster is changed (no delete, restart, scale or exec). Uses its own API client
    from the same kubeconfig/context, and your RBAC — missing `pods/log` permission is shown as a clear message.

## v1.22.0 — 2026-11-05

### Added
- **GitHub widget: pull requests with the same features as CodeHub.** Review requests, your own PRs and PRs
  assigned to you (plus, optionally, every open PR in watched repositories) now appear in the shared review list:
  grouping by repository or category, collapsible groups, filter chips (To review · Mine · Conflicts · Open 7d+ ·
  Hide drafts), open time, hover quick view (labels, milestone, reactions, comments, description) and the search
  window (`#123` works like `!123`). The watched repository's latest Actions run shows as the group's pipeline dot.
  New review requests notify (once per PR). Assigned issues stay listed below the PRs.
  - New settings: *Repositories for pull requests*, *Show*, *Group pull requests by*, *Name on the dock*, *Notify*.
  - GitHub's search API doesn't return branches, reviewers or merge status for PRs; those lines are hidden in the
    quick view instead of being guessed.
- The search window now covers every code widget (CodeHub / GitLab and GitHub) together.

### Changed
- The CodeHub and GitHub popups share one review-list component (`MergeRequestBoard`), so future improvements
  apply to both. The popup toolbar (search, expand/collapse, grouping) moved to its own row under the title.

## v1.21.0 — 2026-11-05

### Added
- **CodeHub / GitLab: filtering in the popup.** Chips under the title: All · To review · Mine · Conflicts ·
  Open 7d+ (with counts) and Hide drafts. Click a chip to filter the groups, click it again (or "Clear filter") to
  reset. Empty groups are hidden while filtering; the choice is remembered.
- **CodeHub / GitLab: search window.** Search button in the popup (or right-click › Search merge requests…) opens
  a normal window — the dock can't take keyboard input — that searches the MRs of **all** CodeHub widgets:
  - Words match title, project, !number, author (username or name), branches, labels, reviewers and assignees;
    every word must match.
  - Qualifiers: `author:`, `label:`, `branch:`, `target:`, `project:`, `reviewer:`, `title:`, `is:draft|conflict|stale|review|mine`,
    `!123`; `-` excludes (`-is:draft`), quotes match a phrase.
  - Same quick filters with live counts, Hide drafts, a server picker when there's more than one widget.
  - Detail pane with reviewers, status, labels, dates, description; Enter / double-click opens, Ctrl+Shift+C copies
    the link, Esc closes.
  - Uses what the widgets already loaded (including MRs hidden by "Only MRs that involve me"), so typing never
    calls the server; the window picks up newer data whenever it's brought back to the front.

## v1.20.0 — 2026-11-05

### Added
- **CodeHub / GitLab: MR quick view on hover.** Hover a merge request in the popup to see full title, project and
  number, author (display name + username), source → target branch, reviewers, assignees, merge status in words
  (Ready to merge, Waiting for approval, Waiting for the pipeline, Has conflicts, Unresolved discussions, Needs a
  rebase…), labels, milestone, votes and comment count, opened/updated dates and the start of the description
  (template comments and code fences stripped, up to ~12 lines). Click still opens the MR in the browser.
  - Everything comes from the same `merge_requests` list response — no extra requests per hover. Fields a server
    doesn't send are simply left out.

## v1.19.1 — 2026-11-05

### Added
- **CodeHub / GitLab: MR open time.** Each merge request shows how long it has been open ("25 min", "5 h", "3 d",
  "2 w") on the right of the row; hover for the exact opening date. MRs open for 7 days or more show the age in the
  warning colour. Uses the MR's `created_at`; servers that don't send it simply show no age.

## v1.19.0 — 2026-11-05

### Added
- **CodeHub / GitLab: grouped merge requests.** The popup no longer shows one long list.
  - **By project (default):** one collapsible group per watched project. The header shows the counts ("2 to review ·
    1 yours · 9 open") and the project's latest pipeline (coloured dot, click to open). Inside a group, MRs waiting
    for your review come first, then yours, then the rest; small "Review" / "Yours" tags mark them.
  - **By category:** Needs your review / Your merge requests / Other open merge requests, each collapsible.
  - Switch with the "By project / By category" button in the popup or in Configure…. Expand all / Collapse all buttons.
  - Groups open by default when they contain something for you or are short; long groups show 6 MRs and
    "Show N more". Your expand/collapse choices are remembered. Regrouping doesn't call the server again.

## v1.18.1 — 2026-11-05

### Fixed
- Maximised windows now grow back when the dock gets shorter (e.g. three widget rows → two, or wrapping turned
  off). Windows shrinks maximised windows when the reserved band grows but doesn't always enlarge them again; the
  dock now tells the shell its position changed (`ABM_WINDOWPOSCHANGED`) and, shortly after a height change, re-fits
  windows still maximised to the old work area on the same monitor. Windows of elevated (admin) apps can't be moved
  by a normal-rights app and are left alone.

## v1.18.0 — 2026-11-05

### Added
- **Bookmarks: Open all.** Open every link in a folder, including its sub-folders, as browser tabs.
  - **Where:** the tabs button on each folder row, or the "Open all" button at the top while you're inside a folder.
  - **One browser window:** if links open in a specific browser (Configure… › "The browser the bookmark
    comes from"), all links are passed to that one browser process, so they open in order as tabs in one window.
  - **Default browser:** otherwise they're opened one after another in your default browser.
  - **Limits:** more than 15 tabs needs a second click to confirm, and at most 100 tabs open per click.
    Only http(s) links are opened.

## v1.17.0 — 2026-11-05

### Added
- **Top 10 processes.** Click the **CPU** widget for the busiest programs, or the **Memory** widget for the
  programs using the most RAM.
  - Processes with the same name are grouped, e.g. "chrome ×23".
  - Each row shows CPU % and memory, with a small bar relative to the top entry.
  - The list updates every 2 s while the popup is open, and sampling stops when it closes.
  - A **Task Manager** button opens Windows Task Manager.
  - The list is read-only: no process can be ended from SmartDock.
- Widgets can react to their popup closing (`IPopupWidget.OnPopupClosed`).

## v1.16.0 — 2026-11-05

### Added
- **Second row when the dock is full** (Settings › Dock behaviour, on by default). When apps or widgets don't
  fit any more, they continue on a second (third…) row and the dock grows taller instead of cutting them off.
  - The centre widgets use all the remaining width before wrapping.
  - The app list and the right-hand widgets each wrap at up to about 38 % of the dock's width.
  - It works in both floating and full-width modes.
  - "Stick to the screen edge" reserves the taller height automatically, so maximised windows still stop
    above the dock.

## v1.15.1 — 2026-11-05

### Changed
- **Update checks are more timely.**
  - **More often:** SmartDock now checks every **60 minutes** by default instead of every 6 hours. You can
    change this in Settings › Updates › Every … (15 min to 12 h).
  - **After sleep or unlock:** it also checks shortly after the PC wakes from sleep or is unlocked, at
    most once per 15 minutes.
  - **New or corrected location:** checking starts within about 20 seconds of entering or changing the
    release location, instead of waiting for the next interval.
- **A failed automatic check is now announced once** with a notification that gives the reason (for
  example an SSH host-key or permission problem). Before, it was only visible in Settings.

## v1.15.0 — 2026-11-05

### Added
- **Notes: live checker and Beautify.**
  - **Checking as you type:** a line under the editor validates the note when it is JSON, XML or YAML, and
    also validates ```json / ```xml / ```yaml code blocks inside a text note. It shows "✓ Valid JSON", or
    "⚠ Line 12, col 5: …"; click it to jump to the problem.
  - **Beautify** (button or **Ctrl+Shift+F**; works on the selection if there is one; **Ctrl+Z** undoes it):
    - pretty-prints JSON (comments and trailing commas tolerated, non-ASCII kept readable) and XML;
    - formats ```json / ```xml code blocks and leaves every other code block byte-for-byte;
    - tidies Markdown: `#Title` → `# Title`, `*`/`+`/`•` bullets → `-`, renumbers ordered lists, trims trailing
      spaces, converts leading tabs, collapses repeated empty lines, and puts a blank line before headings.
  - **Never destructive:** YAML is only validated, never rewritten (that would lose comments), and invalid
    JSON or XML is reported, never "repaired".
  - **Code font toggle:** on automatically for JSON, XML or YAML notes and notes with code blocks.

## v1.14.4 — 2026-11-04

### Fixed
- Building the installer from Rider now also writes `artifacts/installer/latest.json`. Before, only
  `build/installer.ps1` wrote it, so `release.ps1` stopped with "latest.json not found".
- `build/release.ps1`:
  - creates the remote folder over SSH;
  - checks that `ssh` and `scp` are available;
  - supports `-Port`;
  - prints each step, a clearer error and the release location to enter in SmartDock.
- Build scripts are pure ASCII, so Windows PowerShell 5.1 parses them the same on any code page.

## v1.14.3 — 2026-11-04

### Fixed
- Installer: `WIX0311` (characters outside code page 1252). The licence text contained a stray CJK character
  where a "›" was intended. Installer texts and shortcut names now use plain ASCII.

## v1.14.2 — 2026-11-04

### Fixed
- Installer: `WIX0091 Duplicate Property 'ARPNOMODIFY'`. WixUI_Minimal already defines it, so it was
  removed from `Package.wxs`.

## v1.14.1 — 2026-11-04

### Fixed
- Installer build: `Could not resolve SDK "Microsoft.NET.Sdk"` / `MSB4236`. The installer's `dotnet publish`
  step cleared MSBuild environment variables, which stopped the .NET SDK from being found. The child process
  now inherits the environment unchanged and runs from the repository root, so `global.json` applies.

## v1.14.0 — 2026-11-04

### Added
- **Bookmarks widget.**
  - **Sources:** your own links (`Name | URL`) plus the bookmarks of a Chrome, Edge or Brave profile. These
    are read from the browser's local Bookmarks file, read-only, and are picked up within 30 seconds of a
    change in the browser.
  - **Popup:** shows your links and the bookmarks bar first; click a folder to open it and use Back to return.
  - **Search window:** type to filter across all bookmarks by name, URL and folder. Enter opens the
    selection and Esc closes the window.
  - **Opening links:** links open in your default browser, or in the browser they came from. Only
    http(s) links are opened, so bookmarklets and `chrome://` pages are skipped.
  - You can add the widget several times, for example once per browser profile.

## v1.13.1 — 2026-11-04

### Fixed
- Building the **Installer** configuration failed with `MC3066: cannot find a public type named 'ClockWidget'`.
  The solution built the app projects while the installer was publishing the same projects in parallel, and
  that publish inherited the installer's `Platform=x64`. Now only the installer project builds in the Installer
  configuration, and it publishes the app in a separate, isolated `dotnet publish` process.
- Installer: `WIX0230`. A component that contains a file and has a registry key path can't use an
  auto-generated GUID, so fixed component GUIDs are now set in `Package.wxs`.

## v1.13.0 — 2026-11-04

### Added
- **In-app updates.**
  - **Sources:** SmartDock checks a release folder you choose for `latest.json`. That can be an `ssh://`
    location on a Linux machine (using Windows' OpenSSH and your SSH key), an `https://` server, a network
    share or a local folder.
  - **When it checks:** about 90 seconds after start-up and every 6 hours, or on demand with tray › Check for updates.
  - **Installing:** you're notified when a newer version exists. Install it from the tray menu or from
    Settings › Updates. The installer is checked against its SHA-256, installed in passive mode, and
    SmartDock restarts by itself.
  - **Publishing:** `build/installer.ps1` now also writes `latest.json`, and the new `build/release.ps1`
    uploads a release. See `docs/UPDATES.md` and ADR 0010.
- MSI: a `RELAUNCH=1` property starts SmartDock again after a passive (in-app) upgrade.

## v1.12.0 — 2026-11-04

### Added
- **MSI installer** (WiX v5, `installer/`), built from Rider using the new **Installer** solution configuration,
  or with `build/installer.ps1`.
  - **Install:** per user, without admin rights, to `%LOCALAPPDATA%\Programs\SmartDock`.
  - **Shortcuts:** Start menu entries for SmartDock and for restoring the Windows taskbar.
  - **Autostart:** optional, on by default.
  - **Wizard:** the finish page offers "Launch SmartDock".
  - **Upgrades and uninstall:** both close the running SmartDock cleanly first. Uninstalling keeps your
    settings. See `docs/PACKAGING.md`.
- `SmartDock.exe --shutdown`: asks a running SmartDock to exit cleanly (restoring the taskbar) and waits for it.
  The installer uses this.

## v1.11.2 — 2026-11-04

### Added
- **Release packaging.** Publish profiles produce one self-contained `SmartDock.exe` (`SingleFile-win-x64`,
  `SingleFile-win-arm64`) or a small exe that needs the .NET 10 Desktop Runtime
  (`Small-FrameworkDependent-win-x64`). They can be used from Rider's Publish dialog or with `dotnet publish`.
- `build/publish.ps1` now reads the version from `Directory.Build.props`, builds the single-file exe, and
  writes a versioned zip with a SHA-256 checksum. See `docs/PACKAGING.md`.

## v1.11.1 — 2026-11-04

### Fixed
- Script runner: entries that couldn't be used were silently dropped, so saved scripts seemed to disappear.
  This covered commands typed directly, relative paths, URLs and unsupported file types such as `.sh`.
  Saving now lists each problem line with the reason and asks whether to save anyway. A general
  `ISettingsValidator` capability lets any widget do this.
- Configure… windows now show an error message if saving fails, instead of doing nothing.

## v1.11.0 — 2026-11-04

### Added
- **CodeHub / GitLab widget.** Works with any GitLab v4-compatible server (`/api/v4/projects/{id}/merge_requests`),
  including the internal CodeHub, self-hosted GitLab and gitlab.com.
  - **Popup:** MRs waiting for your review, your own MRs (draft and conflict markers included), optionally
    all open MRs, and the latest pipeline for each project or branch.
  - **Dock:** the card shows a review-count badge, and its icon turns red when a pipeline is failing.
  - **Notifications:** for new review requests and for pipelines that fail or recover.
  - **Configure…:** the server address, the token (stored encrypted, sent as `PRIVATE-TOKEN` or Bearer), and
    the projects to watch, as an ID, a path or a pasted project URL, with an optional branch.
  - It keeps working when the server has no `/user` or `/pipelines` API: set "My username" for the first,
    and the pipeline section is simply hidden for the second.

## v1.10.2 — 2026-11-04

### Fixed
- The version label at the end of the dock could be cut off ("v1.1…") when the dock was full. It now sits in
  small text under the Settings gear: it never shrinks or trims, and it takes up no extra width. The Settings
  tooltip also shows the version.

## v1.10.1 — 2026-11-04

### Fixed
- "Span the full width of the screen" had no effect. The check for whether the width changed compared
  against NaN ("size to content"), and any comparison involving NaN is false, so the fixed width was never applied.

## v1.10.0 — 2026-11-04

### Added
- **Script runner widget** (opt-in). Runs your own `.ps1`, `.bat`, `.cmd`, `.py` or `.exe` files in the
  background, either on demand or every N minutes.
  - **Dock:** the card shows "1 running", "1 failed" or "3 OK".
  - **Popup:** each script's status, exit code, duration and last output lines, with Run, Stop and Copy
    output buttons.
  - **Safeguards:** scripts have a timeout, a notification is sent on failures, and nothing runs until you
    turn it on in Configure…. See ADR 0009.

## v1.9.0 — 2026-11-04

### Added
- **Full width** (Settings › Dock behaviour): the dock spans the whole screen like the Windows taskbar.
  Apps stay on the left, system widgets, notifications and settings on the right, and centre widgets fill
  the space between. Combine it with "Gap to screen edge" = 0 and "Corner radius" = 0 for a classic
  taskbar look.
- **Dock size** (Settings › Appearance, 60–150 %): scales the whole dock, including height, icons, text and
  widgets, so you can make it smaller or bigger. Popups, previews and the reserved screen space follow the size.

## v1.8.2 — 2026-11-04

### Fixed
- The Kubernetes cluster widget could only be added once, although it is designed as one widget per cluster.
  Kubernetes, Web page status and Quick folders can now each be added several times.

## v1.8.1 — 2026-11-04

### Fixed
- Build error CS0136 in `WidgetCard.OnDrop`: the variable name for the file-drop target clashed with an
  existing `target` variable.

## v1.8.0 — 2026-11-04

### Added
- **Quick folders widget.** Keep up to 40 saved folders and files, and open them with one click: folders in
  File Explorer, files in their default app.
  - Each entry has buttons to open a terminal there (Windows Terminal, falling back to PowerShell), copy the
    path, and remove it from the list.
  - Add entries by dragging folders or files from Explorer onto the widget, or type `Name | Path` lines in
    Configure…. Network shares, environment variables (`%USERPROFILE%\Downloads`) and `shell:` paths work.
  - Missing or offline entries are dimmed. Network paths are checked in the background with a short
    timeout, so the dock never freezes.
- A general capability for widgets to accept files dropped onto their card from Explorer (`IFileDropWidget`).

## v1.7.0 — 2026-11-04

### Added
- **Kubernetes cluster widget**, one per kubeconfig context, read-only.
  - **Dock:** the card shows the cluster name, for example "6/6 nodes · OK" or "2 issues", with a green,
    orange or red dot.
  - **Popup:** problems such as NotReady nodes, CrashLoopBackOff, ImagePullBackOff, OOMKilled, pods stuck
    in Pending, degraded or unavailable workloads, stuck rollouts, pending PVCs and failed Jobs. It also
    lists nodes with their roles, conditions and kubelet version, and Warning events from the last 30 minutes.
  - **Copy commands:** click a problem to copy a `kubectl describe` command, or use 📄 to copy a
    `kubectl logs` command.
  - **Alerts:** a notification for new critical problems, after they've been seen on two checks in a row.
  - **Connection:** it uses your kubeconfig as-is (certificates, tokens, exec/OIDC plugins), with a context
    picker in Configure…, optional namespace limits, and tolerance for partial RBAC.
  - See `docs/kubernetes-widget.md`, which includes a read-only RBAC example, and ADR 0008.

## v1.6.0 — 2026-11-04

### Added
- **Window previews on hover.** Hover a running app's icon and, after a short delay, you get a live thumbnail
  of each of its windows, with titles, like the Windows taskbar. This is handy when, say, five File Explorer
  windows are open.
  - Click a thumbnail to switch to that window, or click its × to close it.
  - Moving between icons switches the preview straight away.
  - The thumbnails are drawn by Windows' own compositor (DWM): nothing is captured or saved.
  - You can turn previews off in Settings › Dock behaviour. See ADR 0007.

## v1.5.0 — 2026-11-04

### Added
- **Web page status widget.** Watches up to 20 pages or services.
  - **Dock:** the card shows "3/3 up", "1 slow" or "1 down", and its icon turns green, orange or red to match.
  - **Popup:** one row per page with the HTTP status and response time, the uptime since SmartDock started,
    and when it was last checked. Click a row to open the page, or use "Check now" to check everything immediately.
  - **Alerts:** you get a notification when a page goes down (after two failed checks in a row, so one blip
    doesn't trigger it) and when it comes back up.
  - **Configure…:** the pages to watch (`Name | URL`), how often to check (30 s to 1 h), the timeout, when a
    page counts as slow, whether a 401/403 login page counts as up, and notifications on or off.
  - **What it sends:** a plain GET to the URLs you entered. It reads only the response headers, sends no
    credentials, and uses the Windows proxy settings.

## v1.4.1 — 2026-11-04

### Fixed
- Build error CS0117 in `EverythingWidget.cs`: the dock has only Center and Right widget regions, so the
  Everything widget now starts in the Center region.

## v1.4.0 — 2026-11-04

### Added
- **Attention blinking.** When an app asks for attention the way it would on the Windows taskbar (WeLink,
  Teams or Outlook with a new message, a finished download, and so on), its dock icon blinks orange a few
  times and then stays highlighted until you open the app. It uses the same shell notification the taskbar
  receives (`HSHELL_FLASH`). If the dock is auto-hidden, it slides in briefly. You can turn this off in
  Settings › Dock behaviour.

## v1.3.0 — 2026-11-04

### Added
- **Everything search widget** (voidtools Everything, installed separately). Click it to open a search window that
  shows results as you type, using Everything's documented IPC interface. In the window:
  - Enter opens the selected result.
  - Ctrl+Enter opens its folder.
  - Right-click gives Open, Open containing folder and Copy full path.
  - Esc closes the window.
  - "Open in Everything" hands the current search over to Everything's own window.

  If Everything isn't running or isn't installed, the widget says so, and offers to start Everything when it's installed.

  Configure… options:
  - What a click opens: SmartDock's window or Everything's.
  - How many results to show.
  - Whether to match the full path.
  - Where Everything.exe is, if it isn't found automatically.

  See ADR 0006.

## v1.2.3 — 2026-11-04

### Fixed
- Build errors from v1.2.1–v1.2.2 in `WidgetCard.cs` (CS0128/CS1061: a new `dock` variable clashed with the
  existing one) and a nullable warning in `App.xaml.cs`.

## v1.2.2 — 2026-11-04

### Fixed
- The Cancel button in a widget's Configure window did nothing. It now closes the window and discards the edits.

## v1.2.1 — 2026-11-04

### Fixed
- Right-click menus (dock, app icons, widgets) could open partly underneath the dock. Menus and widget popups
  are now anchored to the dock and open fully above it (below it for a top dock), at the mouse position for
  menus and over the widget for popups.

## v1.2.0 — 2026-11-04

### Added
- Settings › Dock behaviour has two new sliders (0–32 px, default 8):
  - **Gap to screen edge**: space between the dock and the screen edge, or the Windows taskbar in companion mode.
  - **Gap to windows**: space between the dock and maximised windows, used when "Stick to the screen edge" is on.

## v1.1.0 — 2026-11-04

### Added
- **Stick to the screen edge** (Settings › Dock behaviour, on by default): the dock reserves its height in
  the Windows work area, like the taskbar does, so maximised windows stop at the dock instead of going under
  it. Uses the documented AppBar API; released on exit and ignored while auto-hide is on. See ADR 0005.

### Fixed
- The dock's Settings button could appear to do nothing: because the dock never takes focus, Windows could
  open the Settings window behind the active (e.g. maximised) app. Settings, Widgets, widget Configure and
  Notes windows are now brought to the front. If opening a window fails, the error is shown as a dock
  notification instead of being silently logged.

## v1.0.0 — 2026-11-04

First versioned release (milestone 1 + rich widgets).

### Core and Windows integration (phases 1–3)
- Dock window with pinned/running apps, widget regions (left/center/right), notification center and toasts.
- Companion mode and reversible taskbar replacement mode with crash watchdog and `--restore-taskbar` recovery.
- Widget framework with isolated refresh scheduler, backoff, per-widget settings and DPAPI-protected secrets.
- Widgets: Clock, CPU, Memory, Disk, Battery, Network, Media (GSMTC), Pomodoro.
- Themes, auto-hide, multi-monitor placement, start with Windows.

### Rich widgets (phase 4)
- Calendar (ICS files/URLs, recurrence, join links, reminders), Weather (Open-Meteo), GitHub, Claude usage,
  OpenAI usage, Notes, Quick Actions, Volume, Clipboard history (opt-in, memory only).
- Per-widget Configure dialog, widget popups, click and mouse-wheel actions.

### Fixed during review
- Notes list highlighted the wrong note after switching.
- Refresh requests made while a widget was refreshing were dropped.
- Old recurring series stopped producing current occurrences; monthly "2nd Tuesday" rules misdated.
- Volume followed the multimedia instead of the console endpoint.
- Clipboard privacy check skipped normal copies.
