# Window capture validation

The supplied September 27 Beat Saber recording is H.264, 2496 x 1440, about
15 seconds, with `h264_amf` encoder metadata. Decoded frames at 2 and 10 seconds
include a caption and right-edge strip. At 2 seconds the caption ends around
row 40; the last six columns are green. No live screen or window was captured.

The WGC window path passed the entire captured texture to the recorder and
reported the decorated capture item's size as the initial canvas. DXGI already
cropped the client, but its coordinate queries had no explicit DPI context.
[Microsoft documents](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect)
that window rectangles can include invisible resize borders and are virtualized
for DPI, whereas DWM frame bounds are physical pixels.

Both backends now query physical client bounds under a scoped per-monitor DPI
context. WGC selects the frame origin only when both content dimensions match
the DWM frame or window rectangle. It skips unavailable or inconsistent bounds
instead of copying decorations. Each borrowed frame retains its arrival crop;
later geometry changes cannot alter that copy. Output sizing uses the client
aspect ratio.

The generated regression failed before the crop fix with
`Window capture includes title bar or green right border`. The fixed test checks:

- 100%, 125%, 150% and 200% geometry, negative coordinates, decorated,
  borderless and maximized windows, DWM fallback and stale resize dimensions.
- Hidden owned windows across monitor and caller/target DPI contexts, including
  restoration of the caller's original context.
- Every generated client pixel through immediate and deferred copies, retained
  crops, invalid crops, default monitor regions and CPU/GPU encoded video.
- Every decoded luma/chroma pixel, including canvas padding, at the client
  aspect ratio through CPU conversion and the D3D11 video processor/NVENC.

Run `ClypDat.Capture.Native.WindowCaptureTests.exe` and repeat with `--gpu`
from `native/capture-native/build/Release`. The existing aspect-fit GPU check
also passed before this change; the old recording alone cannot identify whether
its green strip came from capture pixels or encoding padding. No encoder change
was needed for the generated regression. A new user-controlled Beat Saber
recording remains the live acceptance check.
