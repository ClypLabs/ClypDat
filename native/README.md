# ClypDat Native

This is the Avalonia/.NET migration target for ClypDat.

The Electron app remains in the repository while the native app catches up. The native app is split so platform-specific capture work can be implemented without coupling it to the UI:

- `ClypDat.App`: Avalonia desktop UI.
- `ClypDat.Core`: shared settings, clip-library, and metadata logic.
- `ClypDat.Capture.Abstractions`: capture/replay-buffer interfaces used by platform backends.

Planned backend shape:

- Windows: Windows Graphics Capture, WASAPI audio capture, Win32 foreground process detection.
- Linux: PipeWire/xdg-desktop-portal capture and desktop-environment-specific foreground app detection.

## Build

From repository root, use `dotnet.ps1`. First run downloads pinned .NET SDK
into repository-local `.dotnet`; no system-wide install required.

```powershell
.\dotnet.ps1 restore native\ClypDat.Native.sln
.\dotnet.ps1 build native\ClypDat.Native.sln
.\dotnet.ps1 run --project native\src\ClypDat.App\ClypDat.App.csproj
```
