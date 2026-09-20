# Logo masters

Silver Edge is the selected identity: a charcoal tile with an 18px bright-silver-to-steel gradient frame. The mark combines the original heavy off-white band with visible dark separation at both rounded tips. Its black outline stays connected across both joins.

- `clypdat-mark.png`: the 1254px transparent symbol master; preserve its contours, white stroke weight, and both tip gaps. The exporter reads this file without replacing it from Git history. See `clypdat-mark-edit.md` for the edit prompt and measured checks.
- `clypdat-avatar.svg` and `clypdat-avatar.png`: the approved Silver Edge frame and its 768px render.
- `assets/clypdat-icon*.png`: unframed transparent in-app marks, including the header, About page, overlay controls, and notifications. RGB-inverted light-theme variants retain exactly the same alpha. Never use the Silver Outline tile on these surfaces.
- `assets/clypdat-icon.ico`: Silver Edge in 16, 24, 32, 48, 64, 128, and 256px frames for the executable, taskbar, system tray, and installer. Framed rather than transparent because Windows draws it over the user's taskbar colour, where a bare symbol reads as a floating shape instead of an application. Byte-identical to the web favicon, so the two cannot drift.
- `assets/clypdat-logo.svg`: Silver Edge for the browser sign-in page, preserving the source artwork inside a vector frame.
- `assets/clypdat-loader.svg` and `ClypDatLoader.axaml`: legacy animated hexagon mark for loading surfaces, retained because its outer and inner hexagons counter-rotate.

The classic logo assets remain unchanged. Their generator reads the historical dark tile from Git rather than sampling the current avatar.

Regenerate with `node scripts/generate-logo.cjs`, then run `node --test scripts/test-logo-assets.cjs`. The exporter also updates the sibling `clypdat-webapp` assets and requires that project's installed Next.js/Sharp dependencies. Tests keep the framed desktop and web icons separate from every unframed in-app mark and verify the theme pairs. Email BIMI uses a compact solid-steel approximation because Tiny PS forbids gradient URL references.

When the local `ClypDat-logo-thickness` watch exists beside this workspace, the exporter also refreshes its current masters, every neutral preview, and the GitHub avatar. Run `node scripts/sync-logo-watch.cjs` to refresh only the watch. Its page and checks are versioned in `scripts/logo-watch/`; reload the page after layout changes, then images refresh every four seconds.
