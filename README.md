# Copy Pasta

Copy Pasta is a small Windows desktop utility for capturing selected text into an app-local history without using the Windows clipboard. It reads selections through Windows UI Automation and types saved entries back into the active app one character at a time.

The app is intentionally hotkey-driven and does not auto-capture.

## Features

- App-local text history capped at 50 entries
- No clipboard dependency for capture or typing
- Character-by-character output through simulated keyboard input
- Optional text styling metadata capture when the source app exposes it through UI Automation
- Persistent history at `%AppData%\CopyPasta\history.json`

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

- Windows
- .NET SDK 10 or later

## Build and Run

```powershell
dotnet build .\CopyPasta.slnx
dotnet run --project .\CopyPasta.csproj
```

## Publish

```powershell
dotnet publish .\CopyPasta.csproj -c Release -r win-x64 --self-contained false
```

The published app will be under `bin\Release\net10.0-windows\win-x64\publish`.
