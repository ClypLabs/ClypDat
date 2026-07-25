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

Install the .NET SDK first. This machine currently has the .NET runtime only.

```powershell
dotnet restore native\ClypDat.Native.sln
dotnet build native\ClypDat.Native.sln
dotnet run --project native\src\ClypDat.App\ClypDat.App.csproj
```
