# Packaging SmartDock

## 1. One self-contained `SmartDock.exe` (recommended)

This produces a single file that runs on any Windows 10 (19041+) or Windows 11 x64 PC, without
installing .NET. Expect roughly 70–90 MB. You can copy it anywhere, for example `%LOCALAPPDATA%\Programs\SmartDock\`.

### From Rider
1. In the **Solution** view, right-click **SmartDock.App** › **Publish…**. Rider lists the profiles from
   `src/SmartDock.App/Properties/PublishProfiles/`.
2. Choose **SingleFile-win-x64** and run it.
   - You can also create a reusable run configuration: **Run › Edit Configurations › + › Publish to folder** ›
     Project: SmartDock.App › Publish profile: SingleFile-win-x64. It then appears in the run-configuration
     dropdown next to "SmartDock.App".
3. The output is `artifacts/publish/single-file-win-x64/SmartDock.exe`.

### From a terminal
```powershell
powershell -ExecutionPolicy Bypass -File .\build\publish.ps1            # exe + zip + SHA-256
powershell -ExecutionPolicy Bypass -File .\build\publish.ps1 -Runtime win-arm64
powershell -ExecutionPolicy Bypass -File .\build\publish.ps1 -Small     # needs .NET 10 Desktop Runtime on the PC
# or directly:
dotnet publish src/SmartDock.App -p:PublishProfile=SingleFile-win-x64
```
The script reads the version from `Directory.Build.props` and names the zip `SmartDock-v<version>-win-x64.zip`.

### Profiles
| Profile | Result | Needs on the target PC |
|---|---|---|
| `SingleFile-win-x64` | One self-contained exe (ReadyToRun, native WPF libraries bundled) | Nothing |
| `SingleFile-win-arm64` | Same, for ARM64 PCs | Nothing |
| `Small-FrameworkDependent-win-x64` | One small exe (a few MB) | .NET 10 Desktop Runtime |

Trimming is deliberately off, because WPF doesn't support it.

## 2. Things to know
- **Settings and logs** aren't stored next to the exe. They live in `%LOCALAPPDATA%\SmartDock`, so you can
  replace the exe with a newer version and keep everything.
- **Updating:** right-click the tray icon › Exit, replace `SmartDock.exe`, and start it again. If
  "Start with Windows" is on, the registered path must stay the same, so keep the exe in a fixed folder.
- **SmartScreen:** an unsigned exe downloaded from elsewhere may show "Windows protected your PC" (More info ›
  Run anyway). To avoid this for other users, sign the exe with a code-signing certificate, for example the
  company's: `signtool sign /fd SHA256 /tr <timestamp-url> /td SHA256 /a SmartDock.exe`.
- **Antivirus:** single-file .NET apps unpack native libraries to a temporary folder on first start. Some
  corporate antivirus tools inspect that, so the first start can take a little longer.

## 3. MSI installer (recommended for handing out)

`installer/SmartDock.Installer.wixproj` builds **SmartDock-Setup-v<version>-x64.msi** with WiX Toolset v5.
The WiX SDK is restored from NuGet, so nothing extra needs to be installed.

### Build it
- **Rider:** switch the solution configuration dropdown (top toolbar, next to the run configuration) from
  *Debug* to **Installer**, then **Build › Build Solution**. Alternatively, right-click **SmartDock.Installer**
  in the Solution view › **Build**.
- **Terminal:** `powershell -ExecutionPolicy Bypass -File .\build\installer.ps1`,
  or `dotnet build installer/SmartDock.Installer.wixproj -c Release`.

The installer project first publishes SmartDock.App with the `SingleFile-win-x64` profile, in a separate `dotnet publish` process, so the MSI always
contains a fresh exe with the same version. The output is in `artifacts/installer/`. The Debug and Release
configurations don't build the installer, so everyday builds stay fast.

### What the installer does
| | |
|---|---|
| Install location | `%LOCALAPPDATA%\Programs\SmartDock` (per user, **no administrator rights or UAC prompt**) |
| Wizard | Welcome and licence note (`installer/License.rtf`), Install, then Finish with **"Launch SmartDock"** |
| Start menu | "SmartDock" and "SmartDock - Restore Windows taskbar" (rescue shortcut that runs `--restore-taskbar`) |
| Start with Windows | On by default (same registry value as the in-app setting). `msiexec /i <msi> AUTOSTART=0` turns it off |
| Upgrades | Run a newer MSI: it closes the running SmartDock cleanly (`--shutdown`, which restores the taskbar), replaces it and offers to launch it. Installing an older version over a newer one is refused |
| Uninstall | Settings › Apps › SmartDock. SmartDock is closed first, and the files, shortcuts and autostart entry are removed. **Settings, layout, notes and tokens in `%LOCALAPPDATA%\SmartDock` are kept** |
| Silent install | `msiexec /i SmartDock-Setup-v1.12.0-x64.msi /qn` (add `AUTOSTART=0` if wanted) |

### Notes
- Keep the `UpgradeCode` in `Package.wxs` unchanged forever. That's how Windows recognises an upgrade.
- The MSI version comes from `<Version>` in `Directory.Build.props`. Bump it for every release, or Windows
  treats the new MSI as the same version.
- Signing: sign the exe (see above) and the MSI with `signtool` so SmartScreen and corporate policies trust them.
- WiX v5 is used on purpose: WiX v6 and later add a maintenance-fee EULA for commercial use.
