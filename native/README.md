# ClypDat Native

This is the Avalonia/.NET desktop application for ClypDat.

The Electron app remains in the repository. The native application separates platform capture from its UI:

- `ClypDat.App`: Avalonia desktop UI.
- `ClypDat.Core`: shared settings, clip-library, and metadata logic.
- `ClypDat.Capture.Abstractions`: capture/replay-buffer interfaces used by platform backends.

Current backend:

- Windows recording: native WGC/DXGI capture, WASAPI audio, foreground-window detection, session recovery and overlay composition in `capture-native/`.
- Monitor previews, settings and library metadata remain in the Avalonia/.NET application.

Linux recording is not implemented.

## Build

From repository root, use `dotnet.ps1`. First run downloads pinned .NET SDK
into repository-local `.dotnet`; no system-wide install required.

```powershell
.\dotnet.ps1 restore native\ClypDat.Native.sln
.\dotnet.ps1 build native\ClypDat.Native.sln
.\dotnet.ps1 run --project native\src\ClypDat.App\ClypDat.App.csproj
```
