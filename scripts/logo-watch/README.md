# Logo workbench

Open `index.html` for the live comparison page. It refreshes every four seconds and shows icons at 16, 24, 32, 48, and 64 CSS pixels. SVG frames retain the full-resolution mark, avoiding enlarged low-resolution PNGs on scaled displays. Artwork is centered by its visible bounds rather than the master image's empty padding.

- `current/clypdat-mark.png`: current app master with heavy white strokes, connected black joins, and both tip gaps.
- `current/clypdat-avatar.{svg,png}`: selected Silver Outline tile (thin solid silver, not Silver Edge). Older named masters remain for historical comparisons.
- `exports/`: upload-ready files, including the GitHub avatar below 1 MB.
- `current/previews/`: current artwork in each neutral treatment, refreshed by the app's logo exporter. Experimental previews remain separate.
- `experiments/gap-and-neutral/`: the comparison set. Silver Outline is selected and applied; other treatments remain experiments.
- `references/`: supplied border reference and earlier stroke-weight reference.
- `scripts/`: export and application helpers. `apply-logo.cjs` writes to both repositories; do not run it merely to preview a candidate.
- `archive/earlier-experiments/`: previous versions, comparisons, notes, and their original scripts. Preserved as a self-contained historical set.

About, overlays, notifications, taskbar and tray use the transparent symbol. The loader retains its spinning classic hexagons. Silver Outline is for web branding and avatar exports only.

The app retains its existing theme switching, using paired dark and light bare marks. Classic-logo assets remain unchanged.

Run `node --test scripts/test-workbench-preview.cjs` to check preview sources, embedded resolution, and artwork alignment.

Run `node ../ClypDat/clypdat-app/scripts/sync-logo-watch.cjs` to sync this watch from the app assets. The app's `generate-logo.cjs` also syncs it after exporting. Reload the page once after a watch layout update; images refresh every four seconds afterward. The HTML and this guide are maintained in the app's `scripts/logo-watch/` directory.
