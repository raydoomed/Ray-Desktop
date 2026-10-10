<p align="center">
  <img src="assets/RayDesktop-preview.png" width="520" alt="Ray Desktop">
</p>

<h1 align="center">Ray Desktop · A Separate Windows Desktop</h1>

<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">中文</a>
</p>

<p align="center">
  <b>Use a separate window to operate another desktop session within your current signed-in Windows, without touching the main desktop.</b>
</p>

<p align="center">
  <a href="#quick-start">Quick Start</a> ·
  <a href="#how-it-works">How It Works</a> ·
  <a href="#build">Build</a> ·
  <a href="#known-limitations">Known Limitations</a> ·
  <a href="#license">License</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2011-blue" alt="Platform">
  <img src="https://img.shields.io/badge/License-MIT-yellow.svg" alt="License">
  <img src="https://img.shields.io/badge/.NET-Framework%204.8-informational" alt=".NET">
</p>

---

**Ray Desktop** is a Windows desktop application: the host program displays the current Windows user's **Child Session** desktop inside a normal window. While you continue using your main desktop normally, it gives you a separate window to operate another desktop session.

It is not a virtual machine, nor is it Windows' `Win + Tab` virtual desktops. The Child Session and the current session share the **same Windows, kernel, user profile, files, and installed programs**; the desktop, windows, and interaction state inside the session are independent. Image and input are connected to the local Child Session through the Microsoft Remote Desktop ActiveX control.

> ⚠️ This project is still a **prototype**. Desktop connection and window scaling have real run records; compatibility with games, anti-cheat, GPU acceleration, etc. is not broadly verified. Please read [Known Limitations](#known-limitations) first.

## Features

- 🖥️ **Separate-window desktop**: open another window to operate the Child Session while the main desktop keeps working
- ⚡ **Password-free, instant start**: reuses the current sign-in identity and auto-logs in via Windows-native Child Session
- 🧩 **Everything shared**: same kernel and file system, software is ready to use, no separate install
- 📋 **Clipboard interop**: text is redirected automatically; files/folders are copied both ways via a path bridge
- 🪟 **Fixed-resolution aspect scaling**: session resolution is fixed; the window scales as an aspect-preserving Letterbox, so the picture never deforms and the mouse stays aligned
- ⌨️ **Shortcut forwarding**: while the Child Session has focus, `Win` key combinations are handled by the Child Session
- 🔌 **Clean exit**: closing the window automatically logs off the Child Session

## UI Preview

<p align="center">
  <img src="assets/screenshot.jpg" width="800" alt="Ray Desktop running UI">
</p>

## Quick Start

```powershell
# 1. Make sure Remote Desktop is enabled on the system (Child Sessions depend on it)
#    Settings → System → Remote Desktop → On

# 2. Launch (first run pops a UAC prompt to enable Child Sessions)
& '.\Raydesktop\Raydesktop.exe'
```

**First-run flow:**

1. Start `Raydesktop.exe`; the program checks Child Session state via `WTSIsChildSessionsEnabled`.
2. If not yet enabled, the program launches the adjacent `EnableChildSessions.exe` with `runas` and Windows shows a UAC prompt.
3. After approval, the helper calls `WTSEnableChildSessions(true)` and verifies it, then shows the result.
4. If the feature was just enabled **during this logon session**: save your work → sign out of Windows → sign back in → start the main program again (the helper will not sign you out/in).
5. If the feature was already enabled, the main program connects directly to the localhost Child Session.

> 🔑 **If a credential prompt appears after the first enable, do not enter an empty password.** Cancel the prompt, save your work, sign out and sign back in, then start again. Microsoft documents that Child Sessions normally log in automatically, but this does not apply when the parent session uses smart-card login or was signed in before enabling. This is not about setting a password for this program.

## How It Works

```text
WinForms host starts
    │
    ├─ WTSIsChildSessionsEnabled checks state
    │     ├─ not enabled → UAC-launch EnableChildSessions.exe
    │     └─ enabled
    │
    ├─ Create Microsoft RDP ActiveX control (CLSID 8b918b82-7985-4c24-89df-c33ad2bbfbcd)
    ├─ Server = localhost
    ├─ ConnectToChildSession = true        # key: connect to the local Child Session, not a remote machine
    ├─ Enable CredSSP, SmartSizing, clipboard/drive redirection and Windows shortcut forwarding
    ├─ Call Connect()
    ├─ Monitor Connected state and subscribe to RDP events
    └─ After connection, sync host control size to the Child Session display size
```

The core mechanism is the **Windows 11 native Child Session feature**: the same Windows kernel forks a parallel interactive session that shares the user profile, files, and installed software, so it can start password-free, instantly, without displacing the main desktop. The program itself does no sandboxing or virtualization.

## Directory Layout

```text
.
├─ assets/                                 # Resource files
│  ├─ RayDesktop.ico                       # Program icon
│  ├─ RayDesktop-preview.png               # Brand lockup at the top of the README
│  └─ screenshot.jpg                       # UI preview image
├─ build.ps1                               # Windows local build script
├─ LICENSE                                 # MIT license
├─ README.md                               # This documentation
├─ Raydesktop/                             # Build output (sibling of src, gitignored, distributed via Releases)
│  ├─ Raydesktop.exe                       # Main program
│  ├─ EnableChildSessions.exe              # UAC helper to enable the feature
│  ├─ MSTSCLib.dll                         # RDP COM interop assembly
│  └─ AxInterop.MSTSCLib.dll               # RDP ActiveX WinForms wrapper assembly
└─ src/
   ├─ ChildSessionDesktop/
   │  ├─ Program.cs                        # Desktop host form, RDP ActiveX, connect & scaling logic
   │  ├─ ClipboardFileRelay.cs             # Two-way file clipboard bridging over the shared file system
   │  └─ app.manifest                      # Main program manifest: Per-Monitor V2 DPI, asInvoker
   └─ ChildSessionSetup/
      ├─ Program.cs                        # Source of the admin helper that enables Child Sessions
      └─ app.manifest                      # Requires administrator (requireAdministrator)
```

`src\ChildSessionDesktop` and `src\ChildSessionSetup` are source directories and must be kept for building and future development. **End users do not need the source directories at runtime**: deliver the whole `Raydesktop` folder to users (see [Release](#release)), not just the main EXE. The program writes `child-session.log` next to the EXE.

## Environment Requirements

### Build machine

- Windows with the built-in .NET Framework C# compiler `csc.exe`.
- `AxImp.exe` from the Windows SDK .NET Framework tools. The build script looks here:
  `C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\AxImp.exe`
- The Windows-built-in `mstscax.dll`; the script generates the RDP ActiveX wrapper assembly from `%WINDIR%\System32\mstscax.dll`.
- No NuGet packages or a virtual machine needed.

### Run machine

- Child Sessions API requires a minimum client of Windows 8 and server of Windows Server 2012; but not every Windows version/edition/policy/account state can connect, so verify on each target machine. See [Microsoft Child Sessions documentation](https://learn.microsoft.com/windows/win32/termserv/child-sessions).
- The program must be allowed to connect to the local Child Session; first enabling requires an admin UAC approval.
- Runtime dependency files must sit in the same folder as the main program.

## Build

Open PowerShell in the project root:

```powershell
.\build.ps1
```

On success it generates/updates:

```text
Raydesktop\Raydesktop.exe
Raydesktop\EnableChildSessions.exe
Raydesktop\MSTSCLib.dll
Raydesktop\AxInterop.MSTSCLib.dll
```

The script generates the ActiveX interop assemblies in the system temp directory, writes a temporary EXE in the host output directory, then publishes it as the final file; it cleans up temp files on success or failure. To run:

```powershell
& '.\Raydesktop\Raydesktop.exe'
```

To recompile a copy, keep the whole project directory in a Windows dev environment with the build tools above.

## Release

Use GitHub **Releases** for distribution rather than committing build output into the repo (`.gitignore` excludes `Raydesktop`). Per release:

1. Run `build.ps1` on the dev machine to get the four deliverable files.
2. Zip the whole `Raydesktop` directory and upload it as a Release asset.
3. Note the target Windows version and the verified scope in the Release notes.

## Runtime Workflow

### Connection status

The program reads `IMsRdpClient9.Connected`:

- `0`: not connected / disconnected.
- `1`: connected.
- `2`: connecting.

The status is checked every 750 ms; if a connection stays in "connecting" for more than 45 seconds, the program disconnects and shows a timeout in the status bar. The RDP control's disconnect, fatal-error, and logon-error events are logged with error codes. Clicking the connect button re-checks; it never calls `Connect()` twice if already connected or connecting.

### Resolution & window scaling

The session desktop resolution is **fixed** at startup to the host's full resolution (e.g. host 3440×1440 → session 3440×1440). By default the window takes 90% of the host width at the host aspect; when you drag-resize, the window aspect is locked to the host aspect and the picture scales as an aspect-preserving Letterbox; the resolution itself does not change.

Because the resolution is fixed and the window only scales proportionally, **running a game windowed means no deformation and aligned mouse** — no need to size the window before starting a game or avoid resizing it mid-play. This is the default mode; if you want the resolution to change with the window in real time, launch with `--dynamic` (not suitable for borderless-windowed games, the picture gets stretched).

The minimum window size keeps the host aspect (width from `640`, height proportional); the session display size is limited to 200–8192 pixels, and width is rounded to an even number.

### Clipboard & file copy

Text and normal clipboard formats keep using the RDP ActiveX automatic clipboard redirection. Files/folders use a **local path bridge** — the two sessions share the user profile and file system, but the clipboards are independent. You keep right-click copy/paste or `Ctrl+C`/`Ctrl+V` in Explorer, with no extra button.

Implementation flow:

1. The host form uses its own window handle; the agent inside the Child Session uses an invisible message window; both receive clipboard change notifications via `AddClipboardFormatListener`. The agent is started from the same EXE with `--clipboard-agent --parent-session <session id>`.
2. The agent only handles the standard Windows `CF_HDROP` file path list. New clipboard content is encoded as a message id plus the path list, written to `%LOCALAPPDATA%\ChildSessionDesktop\ClipboardBridge` as `host-to-child.bin` / `child-to-host.bin`. A unique temp file is written first, then atomically replaced, so the other side never reads a half message.
3. The other session polls the mailbox every 250 ms and sets the standard file clipboard format via `Clipboard.SetFileDropList`. Message ids, path fingerprints, and clipboard sequence numbers are used to deduplicate and suppress echo loops; if the clipboard is occupied by another program, a timer keeps retrying.
4. File content itself never crosses the bridge — both sides use the same local path and Explorer accesses the same file directly; this is path-list synchronization, not byte transfer.

The program registers a hidden agent startup argument under the current user's `HKCU\...\Run` and needs no admin rights; at logon the agent starts only in a non-primary console session different from the host session it was registered in. When the host window closes, the program requests a Child Session logoff and clears both mailbox messages.

File/folders that Explorer can represent as local paths are supported; virtual file objects without a local path (e.g. mail attachments) are not. The Run entry persists after closing the program, so the agent can start on the next Child Session logon; the agent exits immediately in the primary console session.

### Windows shortcuts

Before connecting, `KeyboardHookMode = 1` sends Windows key combos to the Child Session, and `EnableWindowsKey` plus `AcceleratorPassthrough` are enabled. While the Child Session picture has focus, `Win`, `Win + R`, etc. are handled by the Child Session; when focus is not on the RDP picture, shortcuts are still handled by the main desktop. `KeyboardHookMode` cannot be changed after connect, so it is set before `Connect()`.

### Closing the program

When the host window closes, the program first calls `Disconnect()`, then resolves the Child Session id via `WTSGetChildSessionId` and logs it off via `WTSLogoffSession`. Programs inside the Child Session end with the logoff, so save unsaved work first. The logoff request is submitted asynchronously and the log records whether it was accepted; the Windows session manager does the actual cleanup.

## Key Implementation Locations

### `src\ChildSessionDesktop\Program.cs`

- `Program.Log`: appends runtime time and diagnostics to `child-session.log` next to the EXE.
- `DesktopForm.LogRedirectionDiagnostics`: after connecting, logs RDP clipboard and disk redirection state, and that the local file path bridge has started.
- `Program.WTSIsChildSessionsEnabled`: imports the state-check API from `wtsapi32.dll`.
- `Program.WTSGetChildSessionId` / `WTSLogoffSession`: locate and log off the Child Session when the host window closes.
- `DesktopForm.ConnectChildSession`: checks enabled state, configures the RDP COM object, enables clipboard redirection, and starts connecting.
- `DesktopForm.RequestEnableChildSessions`: UAC-launches the admin helper.
- `DesktopForm.UpdateConnectionStatus`: reads RDP connection state and handles connection timeout.
- `DesktopForm.HandleDisconnected` / `HandleFatalError` / `HandleLogonError`: handle RDP events and update the status bar / log.
- `DesktopForm.QueueDisplayResize` / `ApplyDisplaySize`: debounce, send display size, and retry on failure.
- `ChildSessionControl`: hosts the remote session with RDP ActiveX CLSID `8b918b82-7985-4c24-89df-c33ad2bbfbcd`.

### `src\ChildSessionDesktop\ClipboardFileRelay.cs`

- `ClipboardFileRelay`: listens for `CF_HDROP` clipboard changes, reads/writes the two-way mailbox, applies the file path list, and suppresses echo loops; does not copy file data.
- `ClipboardAgentContext` / `ClipboardAgentWindow`: run an invisible agent window and 250 ms mailbox polling inside the Child Session.
- `RegisterChildAgent` / `ShouldRunChildAgent`: register the current user startup entry and avoid the agent lingering in the primary console or parent session.

### `src\ChildSessionSetup\Program.cs`

Calls `WTSEnableChildSessions(true)` to enable the feature and verifies it with `WTSIsChildSessionsEnabled`; `app.manifest` declares `requireAdministrator`, and Windows shows UAC at runtime.

## Logs & Troubleshooting

Log location: `Raydesktop.exe 同目录\child-session.log` (UTF-8 BOM, so Notepad shows Chinese well). The log may contain connection times, state changes, system error codes, and exception text; redact personal info such as user names and computer names before sending to a developer.

On first use the host program tries to write a Run entry for the current user; at Child Session logon Windows then tries to start the hidden agent. Validate file/folder copy-paste on both the host and Child Session sides. If the log shows “Registered the automatic child-session file clipboard bridge” but not “Child-session file clipboard agent started”, that only means there is no agent-start record in the log; also check the Run entry command, the agent process session, and system policy/security software — a missing log line alone does not prove the startup entry was blocked.

| Symptom / log | Meaning & handling |
|---|---|
| `Child sessions are disabled on this computer` | Feature not enabled. Check whether UAC appeared; make sure the helper sits with the main program. If you still cannot log in after enabling, sign out and sign back into Windows. |
| Account/password prompt, empty password rejected | If Child Sessions was just enabled during this logon session, cancel the prompt, sign out and sign back in, then start again. Do not set or save a password to bypass the prompt. |
| `0x00000C07` | Microsoft defines it as `SSL_ERR_ACCOUNT_RESTRICTION` (account restricted). Confirm the enable and sign-out/sign-in flow completed, and check the account and policy; this code alone does not prove an empty password. See [OnDisconnected](https://learn.microsoft.com/windows/win32/termserv/imstscaxevents-ondisconnected). |
| `0x00000001` | Microsoft defines it as a local disconnect, not an error code. If it appears before the connection is established, you cannot conclude from it whether the Child Session briefly came up; investigate with the surrounding log and `Connected` state. |
| Connection state stuck at `2` | Timeout after 45 s. Check Windows version, Child Sessions state, logon timing, and system policy. |
| `UpdateSessionDisplaySettings` returns `0x8000FFFF` | Dynamic resolution update failed in `--dynamic` mode only. The program delays and retries; the default fixed-resolution mode never makes this call. |
| Windows is continuing the sign-in | A normal notification during Child Session logon, not a failure; the program treats it as an informational event. |
| “缺少管理员启用程序” (missing admin enable program) | Incomplete delivery. Re-copy the whole `Raydesktop` folder. |
| COM/ActiveX creation failed | Confirm Windows' `mstscax.dll` is available, the program and DLLs are in the same folder, and rebuild/verify on the target Windows version. |

For disconnect codes, Microsoft recommends using the RDP control's `GetErrorDescription` with `ExtendedDisconnectReason` to get a concrete description; the current version does not implement this extended diagnostics yet.

## Security & Privacy

- The Child Session and main session belong to the **same Windows user environment and are not a security boundary**. Both share user data, software installs, the kernel, and accessible resources.
- The admin helper needs UAC approval to enable Child Sessions.
- The program never asks for or stores a Windows password; logs are written only in the program directory.
- If deployed on a multi-user or managed machine, first confirm admin policy allows enabling the feature and let the device owner handle authorization.

## Known Limitations

- This is a Child Session shown through local RDP ActiveX, not a `Win + Tab` virtual desktop or a Hyper-V VM.
- Anything that depends on the current interactive desktop, console session, or a specific GPU path is not guaranteed to work.
- Games, anti-cheat, GPU acceleration, exclusive fullscreen, Raw Input, DRM video, camera, and audio redirection are not fully verified.
- The same Windows user, OS version, group policy, and when Child Sessions were enabled all affect password-free connection; we cannot promise it works the moment the program is copied to an arbitrary machine.
- There is currently no installer/auto-update, settings page, detailed RDP error descriptions, resolution config, or session-management UI.
- If you need a reliable isolation environment, independent GPU/game compatibility, or cross-device consistency, evaluate Hyper-V VM, Windows Sandbox, or a separate Windows account.

### Game compatibility

Run the game in **windowed or borderless-windowed** mode and the picture stays undeformed with aligned mouse. With the session resolution fixed, any window resize is just aspect-preserving Letterbox; the game's internal rendering does not change, so you no longer need to size the window first or avoid resizing mid-play.

**Common pitfall that makes a game fail to start**: if the game's saved display config is "fullscreen + a full-screen resolution the current monitor does not support", it fails to start because it cannot find a matching fullscreen mode. For example Civ 6's `%LOCALAPPDATA%\Firaxis Games\Sid Meier's Civilization VI\AppOptions.txt` with `FullScreen 1` and a `RenderWidth/Height` that does not match the current monitor mode makes the game report `Unable to find correct DXGI mode` and refuse to enter. Fix: change that file's `FullScreen` to `0` (windowed), or set a full-screen resolution the current monitor supports and restart the game.

## Development & Verification Suggestions

1. When modifying the host, edit `src\ChildSessionDesktop\Program.cs`; when modifying the enable logic, edit `src\ChildSessionSetup\Program.cs` or its manifest.
2. Run `build.ps1` on a Windows dev machine and confirm all four deliverables land in `Raydesktop`.
3. Verify on a separate test PC with the target Windows version: first UAC enable, connect after sign-out, disconnect-reconnect, window scaling, closing the host, and log diagnostics.
4. After changing RDP COM/ActiveX settings, validate connection state and error events in a test environment first; don't rely only on the `Connect()` return to conclude the session succeeded — finally observe `Connected=1` and that the remote desktop is interactive.
5. Deliver the whole `Raydesktop` directory and verify compatibility separately on each target Windows version.

## License

[MIT](LICENSE) © [raydoomed](https://github.com/raydoomed)

## Author

Maintainer: [raydoomed](https://github.com/raydoomed)
