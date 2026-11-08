# In-app updates from your own release server

SmartDock checks a **release folder** you choose for `latest.json` and offers the new version.
Installing always needs a click: tray menu › **Install update vX**, or Settings › Updates › **Update to vX**.
The update is verified (SHA-256), installed with the MSI in passive mode, and SmartDock restarts by itself.

```
release folder
├── latest.json                         ← { "version": "1.13.0", "msi": "...", "sha256": "...", "notes": "..." }
└── SmartDock-Setup-v1.13.0-x64.msi
```

## Option A — Linux machine over SSH (recommended; nothing to install on Linux)

SmartDock uses Windows' built-in OpenSSH client (`scp.exe`) and your normal SSH key, so the Linux side only
needs `sshd` and a folder.

**On the Linux machine**, once:
```bash
sudo mkdir -p /srv/smartdock && sudo chown $USER /srv/smartdock
```

**On each Windows PC that should get updates**, once:
```powershell
# 1. Key login (skip if you already ssh to that host without a password)
ssh-keygen -t ed25519                                    # accept the defaults
type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh deploy@build01.corp "cat >> ~/.ssh/authorized_keys"
# 2. Trust the host key (SmartDock never answers "yes" for you)
ssh deploy@build01.corp exit
```
Then, in SmartDock › Settings › Updates › Release location: `ssh://deploy@build01.corp/srv/smartdock`.
For a non-standard port use `ssh://deploy@build01.corp:2222/srv/smartdock`. For a key that isn't the default
one, fill in "SSH key".

> For several users it's best to create a read-only account on the Linux box (for example `smartdock-updates`)
> that can only read `/srv/smartdock`, and to give each PC's public key to that account.

## Option B — Linux machine over HTTPS / HTTP

Serve the folder with nginx:
```nginx
server {
    listen 443 ssl;
    server_name build01.corp;
    ssl_certificate     /etc/ssl/certs/build01.crt;     # issued by your company CA
    ssl_certificate_key /etc/ssl/private/build01.key;
    location /smartdock/ { alias /srv/smartdock/; autoindex off; }
}
```
Release location: `https://build01.corp/smartdock/`.

A quick test without nginx is `python3 -m http.server 8080 --directory /srv/smartdock`, with the release location
`http://build01.corp:8080/` and **"Allow unencrypted http"** ticked. Use this only on a trusted network:
over plain http, someone on the network could swap both files.

## Option C — network share or folder
`\\fileserver\tools\smartdock` or `D:\releases\smartdock`.

## Publishing a release (on your build PC)
```powershell
# 1. Bump <Version> in Directory.Build.props and add a CHANGELOG entry
# 2. Build the MSI + latest.json: Rider (Installer configuration > Build) or, with release notes from CHANGELOG:
powershell -ExecutionPolicy Bypass -File .\build\installer.ps1
# 3. Upload (MSI first, latest.json last)
powershell -ExecutionPolicy Bypass -File .\build\release.ps1 -Target deploy@build01.corp:/srv/smartdock
```
PCs pick it up on their next check: about 90 seconds after SmartDock starts, then every 60 minutes
(Settings › Updates › Every …), and shortly after the PC wakes from sleep or is unlocked. You can also use
**Check for updates** in the tray menu. If an automatic check fails, you get one notification with the reason.

## What happens on "Update now"
1. The MSI is downloaded or copied to `%LOCALAPPDATA%\SmartDock\updates`.
2. Its SHA-256 is compared with `latest.json`. If they differ, the file is deleted and nothing is installed.
3. `msiexec /i <msi> /passive /norestart RELAUNCH=1` runs, as your user without admin rights. It shows a
   progress bar only.
4. The installer asks SmartDock to exit cleanly (`--shutdown`), which restores the Windows taskbar, then
   replaces it and starts the new version.
5. The install log is saved as `%LOCALAPPDATA%\SmartDock\updates\install-<version>.log`.

## Safety rules
- Nothing is checked until you set a release location. There is no built-in update server.
- `latest.json` may only name a plain `.msi` file in the same folder. Paths, URLs and `..` are rejected.
- Only newer versions are offered, never downgrades.
- SSH runs non-interactively (`BatchMode=yes`) with strict host-key checking. An unknown or changed host key stops the update.
- Plain http is refused unless you explicitly allow it.
- Updates install only when you click.
- Follow-up idea: once releases are code-signed, also require the MSI to be signed by the same publisher as the running exe.
