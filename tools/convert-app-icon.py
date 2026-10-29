#!/usr/bin/env python3
"""Convert the pinned upstream ICNS artwork to a multi-resolution Windows ICO."""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE = ROOT / "upstream" / "cyclop" / "Resources" / "AppIcon.icns"
DEFAULT_OUTPUT = ROOT / "src" / "Cyclop.App" / "Assets" / "Cyclop.ico"
ICON_SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--preview-png", type=Path, help="Optionally save the largest decoded source frame for visual inspection.")
    args = parser.parse_args()

    with Image.open(args.source) as source:
        if source.format != "ICNS":
            raise SystemExit(f"Expected an ICNS source, got {source.format!r}")
        artwork = source.convert("RGBA")
        if artwork.width != artwork.height:
            raise SystemExit(f"Expected square icon artwork, got {artwork.size}")
        if args.preview_png:
            args.preview_png.parent.mkdir(parents=True, exist_ok=True)
            artwork.save(args.preview_png, format="PNG", optimize=True)

        args.output.parent.mkdir(parents=True, exist_ok=True)
        artwork.save(args.output, format="ICO", sizes=ICON_SIZES)

    with Image.open(args.output) as result:
        sizes = sorted(result.ico.sizes(), key=lambda size: (size[0], size[1]))
        if sizes != ICON_SIZES:
            raise SystemExit(f"Unexpected ICO image sizes: {sizes}")
        print(f"Wrote {args.output} with sizes: {', '.join(f'{w}x{h}' for w, h in sizes)}")


if __name__ == "__main__":
    main()
