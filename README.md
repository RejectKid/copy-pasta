# Copy Pasta

Copy Pasta is a small Avalonia desktop utility for capturing selected text into an app-local history without using the system clipboard. It reads selections through native platform services and types saved entries back into the active app one character at a time.

The app is intentionally hotkey-driven and does not auto-capture.

## Features

- App-local text history capped at 50 entries
- No clipboard dependency for capture or typing
- Character-by-character output through simulated keyboard input
- Optional text styling metadata capture when the source app exposes it through UI Automation
- Persistent history under the current user's application data folder

## Platform Support

| Platform | UI | Global hotkeys | Selection capture | Text output |
| --- | --- | --- | --- | --- |
| Windows | Supported | Supported | Supported through UI Automation and native edit controls | Supported through `SendInput` |
| macOS | Supported | Supported through a keyboard event tap | Supported through Accessibility selected text | Supported through Core Graphics keyboard events |
| Linux | Supported on X11 | Supported through XGrabKey | Supported through the X11 PRIMARY selection | Supported through XTest keyboard events |

Linux support currently targets X11. Wayland does not expose a general global hotkey, selected-text, or synthetic-keyboard API that this app can use without desktop-environment-specific portals or extensions.

## Hotkeys

| Platform | Capture | Type selected item | Stop typing |
| --- | --- | --- | --- |
| Windows and Linux | `Ctrl+Alt+C` | `Ctrl+Alt+V` | `Ctrl+Alt+X` |
| macOS | `Command+Option+C` | `Command+Option+V` | `Command+Option+X` |

On macOS, the older `Control+Option` variants are also accepted.

Copy Pasta does not auto-capture. Capture only runs when you press the platform capture hotkey.

## Notes

- Selection capture depends on the target app exposing selected text through the host OS accessibility or selection APIs. Some apps, games, terminals, remote desktops, elevated/admin windows, and sandboxed apps may not expose it.
- The clipboard is not used for capture or typing.
- App-internal rich capture is limited to text styling metadata exposed by the platform. Arbitrary rich content such as images, files, HTML, and RTF is not available through a general non-clipboard selection API.
- macOS requires Accessibility permission. It may also require Input Monitoring permission depending on OS version and security settings.
- Linux requires X11 plus `libX11` and `libXtst`. Text output currently supports common ASCII characters.
- App icon: [Spaghetti icon](https://www.flaticon.com/free-icon/spaghetti_4465494) by Freepik from Flaticon. Free for personal and commercial use with attribution.

## Code Layout

- `Core`: shared history models and platform service contracts
- `UI`: Avalonia app and main window
- `Platforms/Windows`: Windows UI Automation, Win32 hotkey, and `SendInput` services
- `Platforms/MacOS`: Accessibility and Core Graphics services
- `Platforms/Linux`: X11 and XTest services
- `Platforms/Unsupported`: fallback services for unsupported desktop environments

## Requirements

- Windows, macOS, or Linux
- .NET SDK 10 or later

## Build and Run

```powershell
dotnet build .\CopyPasta.slnx
dotnet run --project .\CopyPasta.csproj -f net10.0-windows
```

## Publish

```powershell
dotnet publish .\CopyPasta.csproj -c Release -f net10.0-windows -r win-x64 --self-contained false
dotnet publish .\CopyPasta.csproj -c Release -f net10.0 -r linux-x64 --self-contained false
dotnet publish .\CopyPasta.csproj -c Release -f net10.0 -r osx-arm64 --self-contained false
```

Published apps will be under `bin\Release\<target-framework>\<runtime>\publish`.
