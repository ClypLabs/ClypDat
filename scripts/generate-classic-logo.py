"""Builds the classic (hexagon) logo set next to the current one.

The About logo is a toggle between the current mark and the original hexagon
mark, and the switch has to reach the window, taskbar and tray icons too, so
the classic mark needs the same two forms the current one has:

  * bare in-app marks - clypdat-classic-{256,32}{,-light}.png - which are the
    original mark exactly as it shipped before e2795df2, pulled from git history
    rather than redrawn;
  * a tile icon - clypdat-classic.ico - the original mark on the same dark tile
    with its top glow that clypdat-icon.ico uses, so switching logos does not
    also switch icon styles.

The tile's shape (alpha) is taken from clypdat-icon.ico frame by frame, and its
background is reconstructed from the 256px frame with the current mark removed,
so both icon sets sit on an identical tile. Run from the repository root:

    python scripts/generate-classic-logo.py
"""

import io
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "assets"
# The last commit that still carried the original hexagon mark.
CLASSIC_COMMIT = "e2795df2^"
SIZES = [16, 24, 32, 48, 64, 128, 256]
# How much of the tile the mark fills. The original mark is a full-bleed
# hexagon; this leaves the same visual margin the current mark has on its tile.
MARK_FILL = 0.74


def from_history(name: str) -> Image.Image:
    data = subprocess.run(["git", "show", f"{CLASSIC_COMMIT}:assets/{name}"],
                          cwd=ROOT, check=True, capture_output=True).stdout
    return Image.open(io.BytesIO(data)).convert("RGBA")


def tile_frames() -> dict[int, Image.Image]:
    ico = Image.open(ASSETS / "clypdat-icon.ico")
    frames = {}
    for size in SIZES:
        ico.size = (size, size)
        frames[size] = ico.convert("RGBA").copy()
    return frames


def tile_background(tile: Image.Image) -> np.ndarray:
    """The 256px tile with the current mark removed.

    The background is a smooth vertical gradient plus a glow at the top centre,
    so it is fitted with a low-order polynomial over every pixel the mark does
    not touch, then evaluated everywhere - including under the mark.
    """
    rgba = np.asarray(tile).astype(np.float64)
    value = rgba[..., 0]
    inside = rgba[..., 3] >= 255
    # The mark is white and the background never exceeds ~60, so anything
    # brighter is mark; grow it to swallow the anti-aliased edge.
    mark = Image.fromarray(((value > 64) * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(9))
    known = inside & (np.asarray(mark) == 0)

    size = value.shape[0]
    ys, xs = np.mgrid[0:size, 0:size]
    u = (xs - (size - 1) / 2) / size
    v = ys / size

    def features(uu, vv):
        terms = []
        for i in range(6):
            for j in range(0, 6 - i, 2):  # even powers of u: the glow is symmetric
                terms.append(vv ** i * np.abs(uu) ** j)
        return np.stack(terms, axis=-1)

    design = features(u[known], v[known])
    coefficients, *_ = np.linalg.lstsq(design, value[known], rcond=None)
    fitted = features(u, v) @ coefficients
    residual = np.abs(fitted[known] - value[known])
    print(f"background fit: mean error {residual.mean():.2f}, worst {residual.max():.2f} (of 255)")
    return np.clip(fitted, 0, 255)


def classic_tile(size: int, frame: Image.Image, background: np.ndarray, mark: Image.Image) -> Image.Image:
    back = Image.fromarray(background.astype(np.uint8)).resize((size, size), Image.LANCZOS)
    tile = Image.merge("RGBA", (back, back, back, frame.getchannel("A")))
    extent = max(1, round(size * MARK_FILL))
    placed = mark.resize((extent, extent), Image.LANCZOS)
    offset = ((size - extent) // 2, (size - extent) // 2)
    tile.alpha_composite(placed, offset)
    # The mark cannot spill past the tile's rounded corners.
    tile.putalpha(frame.getchannel("A"))
    return tile


def source_mark(size: int, marks: dict[int, Image.Image]) -> Image.Image:
    """The original set was tuned by hand at small sizes; start from the
    smallest tuned frame at least as big as the target rather than squeezing
    the 256px drawing, whose 6px lines vanish when scaled down that far."""
    target = size * MARK_FILL
    for candidate in sorted(marks):
        if candidate >= target:
            return marks[candidate]
    return marks[max(marks)]


def main() -> None:
    marks = {size: from_history(f"clypdat-icon-{size}.png") for size in (16, 24, 32, 48, 64, 128, 256)}
    light = from_history("clypdat-icon-256-light.png")

    # Bare in-app marks, as the app loads them: 256 for large surfaces, 32 for
    # small ones, each with a light-theme variant. There never was a 32px light
    # frame, so it is derived from the 256px one.
    marks[256].save(ASSETS / "clypdat-classic-256.png")
    marks[32].save(ASSETS / "clypdat-classic-32.png")
    light.save(ASSETS / "clypdat-classic-256-light.png")
    light.resize((32, 32), Image.LANCZOS).save(ASSETS / "clypdat-classic-32-light.png")

    frames = tile_frames()
    background = tile_background(frames[256])
    tiles = [classic_tile(size, frames[size], background, source_mark(size, marks)) for size in SIZES]
    tiles[-1].save(ASSETS / "clypdat-classic.ico", sizes=[(s, s) for s in SIZES], append_images=tiles[:-1])
    print("wrote", ", ".join(p.name for p in sorted(ASSETS.glob("clypdat-classic*"))))


if __name__ == "__main__":
    main()
