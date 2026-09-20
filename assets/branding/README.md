# Logo masters

Silver Outline is the selected identity: a charcoal tile with a thin, solid silver frame (not the thicker Silver Edge gradient). The centered mark has wider white-tip spacing while its black outline stays connected.

- `clypdat-mark.png`: the 1254px transparent symbol master; preserve its contours.
- `clypdat-avatar.svg` and `clypdat-avatar.png`: the approved centered frame and its 768px render.
- `assets/clypdat-icon*.png`: framed in-app marks, with RGB-inverted light-theme variants retaining exactly the same alpha. Existing theme switching remains unchanged.
- `assets/clypdat-icon.ico`: the unframed transparent symbol in 16, 24, 32, 48, 64, 128, and 256px frames for the executable, taskbar, and system tray. These frames are generated separately from the themed PNG tiles.
- `assets/clypdat-logo.svg`: Silver Outline for the browser sign-in page, preserving the source artwork inside a vector frame.
- `assets/clypdat-loader.svg`: the selected tile within the loading ring, with clearance around its corners.

The classic logo assets remain unchanged. Their generator reads the historical dark tile from Git rather than sampling the current avatar.

Regenerate with `node scripts/generate-logo.cjs`. It also updates the sibling `clypdat-webapp` assets and requires that project's installed Next.js/Sharp dependencies. Validation checks the approved tile, theme pairs, ICO frames, and transparent desktop background. Email BIMI uses a compact, path-only approximation of the same framed mark.
