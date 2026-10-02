"""Renders the app logo SVGs into the PNG and ICO files embedded in the apps.

Usage (from the repository root):  pip install cairosvg pillow && python tools/render-logos.py
Renders assets/fieldtoolkit/*.svg into src/FieldToolkit/Resources and assets/fieldmap/*.svg into src/FieldMap/Resources.
"""
import io
import pathlib

import cairosvg
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent.parent
ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

# (source folder, output folder, name of the SVG that becomes the application icon)
APPS = [
    (ROOT / "assets" / "fieldtoolkit", ROOT / "src" / "FieldToolkit" / "Resources", "toolkit"),
    (ROOT / "assets" / "fieldmap", ROOT / "src" / "FieldMap" / "Resources", "fieldmap"),
]


def render(svg: pathlib.Path, size: int) -> Image.Image:
    png = cairosvg.svg2png(url=str(svg), output_width=size, output_height=size)
    return Image.open(io.BytesIO(png)).convert("RGBA")


def main() -> None:
    for src, out, app in APPS:
        out.mkdir(parents=True, exist_ok=True)
        for svg in sorted(src.glob("*.svg")):
            name = svg.stem
            for size in (48, 128):
                render(svg, size).save(out / f"{name}-{size}.png")
            big = render(svg, 256)
            # The app logo is the application icon; each tool's icon is used for its windows and taskbar entries.
            ico = "app.ico" if name == app else f"{name}.ico"
            big.save(out / ico, sizes=[(s, s) for s in ICON_SIZES])
            if name == app:
                render(svg, 512).save(src / f"{name}-512.png")
            print("rendered", src.name, name)


if __name__ == "__main__":
    main()
