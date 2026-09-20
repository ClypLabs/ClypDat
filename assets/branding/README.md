# Logo masters

Silver Outline is the selected identity: a charcoal tile with a thin, solid silver frame (not the thicker Silver Edge gradient). The mark retains its heavy off-white band for legibility at icon sizes.

- `clypdat-mark.png`: the 1254px transparent symbol master; preserve its contours.
- `clypdat-avatar.svg` and `clypdat-avatar.png`: the approved centered frame and its 768px render.
- `assets/clypdat-icon*.png`: unframed transparent in-app marks, including the header, About page, overlay controls, and notifications. RGB-inverted light-theme variants retain exactly the same alpha. Never use the Silver Outline tile on these surfaces.
- `assets/clypdat-icon.ico`: the same unframed transparent symbol in 16, 24, 32, 48, 64, 128, and 256px frames for the executable, taskbar, and system tray.
- `assets/clypdat-logo.svg`: Silver Outline for the browser sign-in page, preserving the source artwork inside a vector frame.
- `assets/clypdat-loader.svg` and `ClypDatLoader.axaml`: legacy animated hexagon mark for loading surfaces, retained because its outer and inner hexagons counter-rotate.

The classic logo assets remain unchanged. Their generator reads the historical dark tile from Git rather than sampling the current avatar.

Regenerate with `node scripts/generate-logo.cjs`, then run `node --test scripts/test-logo-assets.cjs`. The exporter also updates the sibling `clypdat-webapp` assets and requires that project's installed Next.js/Sharp dependencies. Tests keep framed web branding separate from every unframed app icon and verify the theme pairs. Email BIMI uses a compact, path-only approximation of the framed mark.
