# Native Tools

`prepare-obs-runtime.ps1` stages OBS/libobs files for the native replay backend.

`build-obs-bridge.ps1` builds `Eve.ObsBridge.dll`, the native shim loaded by the Avalonia app.

The staged runtime lives in `native/vendor/obs-runtime`, which is intentionally git-ignored because OBS binaries are large and should be handled as release/package assets.
