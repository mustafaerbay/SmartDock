# ADR 0010 — In-app updates from a user-chosen release folder

**Status:** accepted (v1.13.0)

## Context
The user wants SmartDock to fetch new versions from a Linux machine on their network. The spec forbids
arbitrary remote code execution, so any update path has to be explicit, verifiable and user-controlled.

## Decision
- A release folder holds `latest.json` (version, MSI file name, SHA-256, notes) and the MSI. Releases are made
  with `build/installer.ps1`, which generates the manifest, and `build/release.ps1`, which uploads the MSI
  first and the manifest last.
- Transports:
  - `ssh://` via Windows' built-in `scp.exe`, using the user's keys, `BatchMode`, strict host-key checking and
    a restricted character set for the path. This needs no SSH library and no secrets in SmartDock.
  - `https://` via the shared HttpClient. `http://` only with explicit opt-in.
  - UNC paths and local folders.
- Updates are verified with SHA-256 against the manifest, offered only when newer, and installed only on a
  click, with `msiexec /passive RELAUNCH=1`. The per-user MSI closes SmartDock (`--shutdown`) and relaunches it.
- There is no default feed: nothing is contacted until the user sets a location.

## Consequences
- The manifest and the MSI come from the same place, so the SHA-256 protects against corrupt or partial
  downloads rather than a compromised server. The trust anchor is the transport: SSH host key, HTTPS
  certificate, or share permissions. Requiring a code signature that matches the running exe is the planned
  next layer once releases are signed.
- Windows-side SSH setup (key login and a known host) is a one-time manual step per PC; it is documented in docs/UPDATES.md.
