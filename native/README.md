# EVE Native

This is the Avalonia/.NET migration target for EVE.

The Electron app remains in the repository while the native app catches up. The native app is split so platform-specific capture work can be implemented without coupling it to the UI:

- `Eve.App`: Avalonia desktop UI.
- `Eve.Core`: shared settings, clip-library, and metadata logic.
- `Eve.Capture.Abstractions`: capture/replay-buffer interfaces used by platform backends.

Planned backend shape:

- Windows: Windows Graphics Capture, WASAPI audio capture, Win32 foreground process detection.
- Linux: PipeWire/xdg-desktop-portal capture and desktop-environment-specific foreground app detection.

## Build

Install the .NET SDK first. This machine currently has the .NET runtime only.

```powershell
dotnet restore native\EVE.Native.sln
dotnet build native\EVE.Native.sln
dotnet run --project native\src\Eve.App\Eve.App.csproj
```
