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

Library retains the original adaptive card sizes and compact heading/search toolbar.
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

## Library and font baseline

Library tile geometry and the default Inter font match pre-overhaul master
`4639bb12`. Adaptive layout uses the original 400-pixel target and floor-based
column count. The fixed three-column option is unchanged. Card footers retain
the original padding and 15-pixel bold titles. Custom font choices remain
supported.

The Library toolbar also matches that baseline: 52 pixels high, a 15-pixel bold
heading, 20-pixel horizontal margins, and a 240-pixel search field.

Settings retains its narrower content and sidebar, sentence-case headings,
tighter General layout, and compact process priority picker. Content is capped
at 960 logical pixels; navigation uses 224 pixels and status uses 280. The status
sidebar hides below 1120 logical pixels.

The restoration passed 37 focused appearance, layout, settings, and overlay checks.

Settings navigation uses 36-pixel rows, 2-pixel row gaps, and 16-pixel group
gaps. Group headings and item labels share the same inset. Search results hide
empty groups together; long labels truncate with their full text in a tooltip.

The Status panel restores pre-overhaul master width, padding, and typography:
280 pixels wide, 16-pixel bold values, and 11-pixel bold labels. Font family
continues to follow the saved app font, with bundled Inter as the default.
