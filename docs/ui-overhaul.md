# Desktop studio appearance

`ui-overhaul` introduces graphite surfaces, explicit light surfaces, shared
typography and spacing, and common dialog composition. Preset names, Windows
accent selection, custom theme files, saved settings, commands, and recording
overlay formats remain compatible.

`Styles/Tokens.axaml` contains the appearance values. `AppThemeService` updates
brush instances in place; light presets use explicit page, panel, and control
colours. The existing colour ramp remains available for specialized graphics.
`AppStyles.axaml` styles existing controls; `StudioStyles.axaml` contains shared
dialog, typography, focus, error, and compact Editor rules. `DialogComposition`
owns utility-window chrome while callers retain modal results and cancellation.

Library uses compact adaptive cards with a larger heading and search toolbar.
Settings have a capped content width, sentence-case card headings, quieter
labels, and a status sidebar that hides below 1120 logical pixels. Editor
headers take less vertical space. Timeline labels use the selected font.
Menus, sharing, updater windows, onboarding, notifications, splash, and browser
sign-in completion pages use the same surface and typography conventions.

## Validation

- Solution build passed with `./dotnet.ps1 build native/ClypDat.Native.sln -p:Platform=x64`.
  The default AnyCPU configuration is rejected by ScreenRecorderLib. Seven
  existing deprecation warnings remain.
- Full managed App suite: 890 passed, 2 skipped, 1 failed. The failure is
  `AutoClipCatalogTests.ProtocolVersionsAndDetectorTransportLimitsArePinned`:
  it expects capture protocol 11 while the implementation declares 12. Both
  values were verified on the starting `master` commit, `4639bb12`.
- After final presentation changes, 59 targeted tests passed, including
  `StudioAppearanceTests`, onboarding and notification rendering, sharing,
  release notes, update presentation, keyboard rendering, and recording/export
  overlay layout.
- Appearance fixtures cover all 12 presets, four custom themes, Inter and
  Courier New, mutable brush identity, text contrast, long dialog labels,
  populated/loading Library states, all Settings sections, Editor dimensions,
  modal actions, and export cancellation. Dialog content renders offscreen at
  100%, 150%, and 200%; main layouts exercise minimum and larger window sizes.
- Test setup selects an isolated product folder before app initialization,
  uses fixture library paths, and disables recording.

## Visual review

No screen or window screenshots were taken. Final appearance needs user review,
particularly native video playback/fullscreen composition and popup placement
across monitors. Offscreen assertions verify geometry and rendering contracts;
they do not establish the visual quality of a live desktop session. Updater
downloads and installation were not triggered by the UI fixtures.

## Density and font refinement

Adaptive library slots target 320 logical pixels and preserve 16:9 thumbnails.
The fixed three-column preference is unchanged. Card footers use less padding.
Settings content is capped at 960 logical pixels; navigation and status columns
use 224 pixels. General settings have tighter spacing and a compact process
priority picker.

New defaults use Segoe UI Variable on Windows, with bundled Inter as fallback.
The resolver loads `SegUIVar.ttf` to avoid differences in variable-family names
between Windows and Skia. Existing saved custom fonts remain honored; Inter
remains selectable. Font checks resolve actual glyph families, and dialog
fixtures exercise Segoe UI Variable, Inter, and a custom monospaced font.

The refinement passed 43 relevant font, appearance, layout, settings, and
overlay checks. Earlier full-suite results above precede this refinement.
