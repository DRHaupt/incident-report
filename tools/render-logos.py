"""Renders the Field Toolkit logo SVGs into the PNG and ICO files embedded in the app.

Usage (from the repository root):  pip install cairosvg pillow && python tools/render-logos.py
"""
import io
import pathlib

import cairosvg
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "assets" / "fieldtoolkit"
OUT = ROOT / "src" / "FieldToolkit" / "Resources"
ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def render(svg: pathlib.Path, size: int) -> Image.Image:
    png = cairosvg.svg2png(url=str(svg), output_width=size, output_height=size)
    return Image.open(io.BytesIO(png)).convert("RGBA")


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for svg in sorted(SRC.glob("*.svg")):
        name = svg.stem
        for size in (48, 128):
            render(svg, size).save(OUT / f"{name}-{size}.png")
        big = render(svg, 256)
        # The toolkit logo is the application icon; each tool's icon is used for its windows and taskbar entries.
        ico = "app.ico" if name == "toolkit" else f"{name}.ico"
        big.save(OUT / ico, sizes=[(s, s) for s in ICON_SIZES])
        if name == "toolkit":
            render(svg, 512).save(SRC / "toolkit-512.png")
        print("rendered", name)


if __name__ == "__main__":
    main()
