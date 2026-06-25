# Copy Pasta

Copy Pasta is a small Avalonia desktop utility for capturing selected text into an app-local history without using the system clipboard. It reads selections through native platform services and types saved entries back into the active app one character at a time.

The app is intentionally hotkey-driven and does not auto-capture.

## Features

- App-local text history capped at 50 entries
- No clipboard dependency for capture or typing
- Character-by-character output through simulated keyboard input
- Optional text styling metadata capture when the source app exposes it through UI Automation
- Persistent history at `%AppData%\CopyPasta\history.json`

## Platform Support

| Platform | UI | Global hotkeys | Selection capture | Text output |
| --- | --- | --- | --- | --- |
| Windows | Supported | Supported | Supported through UI Automation and native edit controls | Supported through `SendInput` |
| macOS | Builds | Not implemented yet | Not implemented yet | Not implemented yet |
| Linux | Builds | Not implemented yet | Not implemented yet | Not implemented yet |

macOS and Linux have different native accessibility and input APIs, so their capture/type behavior is represented behind platform service interfaces and intentionally reports unsupported until those implementations are added.

## Hotkeys

- `Ctrl+Alt+C`: capture the current selection
- `Ctrl+Alt+V`: type the selected history item
- `Ctrl+Alt+X`: stop typing

Copy Pasta does not auto-capture. Capture only runs when you press `Ctrl+Alt+C`.

## Notes

- Selection capture depends on the target app exposing selected text through Windows UI Automation. Some apps, games, terminals, remote desktops, and elevated/admin windows may not expose it.
- The clipboard is not used for capture or typing.
- App-internal rich capture is limited to text styling metadata exposed by UI Automation. Arbitrary rich content such as images, files, HTML, and RTF is not available through a general non-clipboard Windows selection API.

## Requirements

- Windows, macOS, or Linux for the Avalonia UI
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
