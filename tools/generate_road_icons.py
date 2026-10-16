"""Offline road thumbnails. Requires Pillow; does not load Blender or CS1."""
from __future__ import annotations

import argparse
import importlib.util
import io
import json
from pathlib import Path
import re

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
SIZE, SCALE = 128, 4
STATES = ("thumb", "hovered", "pressed", "focused", "disabled")
DEFAULT_CONFIG = ROOT / "textures/toolbar/icons.json"


def variants():
    spec = importlib.util.spec_from_file_location("road_icon_domain", ROOT / "blender_addon/road_builder/domain.py")
    domain = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(domain)
    return domain.vehicle_lane_variants()


def load_settings(path=DEFAULT_CONFIG, overlay_path=None):
    path = Path(path).resolve()
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    unknown = set(data) - {"footer_height", "font_size", "font", "overlay", "import_job_names"}
    if unknown:
        raise ValueError(f"Unknown icon settings: {sorted(unknown)}")
    for key, low, high in (("footer_height", 12, 36), ("font_size", 8, 32)):
        value = data.get(key)
        if type(value) is not int or not low <= value <= high:
            raise ValueError(f"{key} must be an integer from {low} to {high}")
    font_path = path.parent / data["font"]
    # FreeType otherwise holds a Windows file handle for the font's lifetime.
    font = ImageFont.truetype(io.BytesIO(font_path.read_bytes()), data["font_size"] * SCALE)
    bbox = font.getbbox("1234+", anchor="ls")
    if bbox[3] - bbox[1] > (data["footer_height"] - 4) * SCALE:
        raise ValueError("Footer is too short for font_size (requires 4 px padding)")
    overlay = None
    source = overlay_path or (path.parent / data["overlay"] if data.get("overlay") else None)
    if source:
        source = Path(source)
        if source.stat().st_size > 1024 * 1024:
            raise ValueError("Overlay PNG exceeds 1 MiB")
        with Image.open(source) as image:
            if image.format != "PNG" or image.size != (SIZE, SIZE):
                raise ValueError("Overlay must be a 128x128 PNG")
            overlay = image.convert("RGBA")
    jobs = data.get("import_job_names", {})
    if not isinstance(jobs, dict) or set(jobs) - {v.road_id for v in variants()}:
        raise ValueError("import_job_names must map known road IDs to import job names")
    names = [jobs.get(v.road_id, v.road_id) for v in variants()]
    if any(not isinstance(n, str) or not re.fullmatch(r"[\w -]+", n) or n.endswith((" ", ".")) for n in names):
        raise ValueError("Import job names must be plain filenames without path separators")
    if len({n.casefold() for n in names}) != len(names):
        raise ValueError("Import job names must be unique (including case)")
    return data, font, overlay


def label(backward, forward):
    return f"→{backward + forward}" if not backward or not forward else f"{backward}+{forward}"


def render(backward, forward, settings, font, overlay=None):
    if not (0 <= backward <= 4 and 0 <= forward <= 4 and backward + forward):
        raise ValueError("Icon supports 1 to 4 lanes in each direction")
    image = Image.new("RGBA", (SIZE * SCALE, SIZE * SCALE))
    draw = ImageDraw.Draw(image)

    def rect(x0, y0, x1, y1, color):
        draw.rectangle((round(x0*SCALE), round(y0*SCALE), round(x1*SCALE)-1, round(y1*SCALE)-1), fill=color)

    footer_top = 126 - settings["footer_height"]
    diagram_bottom = footer_top - 4
    rect(2, 2, 126, 126, (37, 40, 42, 255))
    rect(2, footer_top, 126, 126, (26, 29, 31, 255))
    total = backward + forward
    width = min(112, 24 + 12 * total)
    left, right = (SIZE - width) / 2, (SIZE + width) / 2
    paint = (236, 237, 236, 255)
    rect(left, 8, right, diagram_bottom, (107, 109, 109, 255))
    rect(left+3, 8, right-3, diagram_bottom, (60, 62, 63, 255))
    rect(left+3.9, 8, left+5.1, diagram_bottom, paint)
    rect(right-5.1, 8, right-3.9, diagram_bottom, paint)
    pitch = (width-8) / total
    for i in range(1, total):
        x = round(left + 4 + i * pitch)
        if backward and forward and i == backward:
            rect(x-.3, 8, x+1.3, diagram_bottom, paint)
        else:
            for y in range(8, diagram_bottom, 16):
                rect(x-.1, y, x+1.1, min(y+6, diagram_bottom), paint)
    for i in range(total):
        x, half = left+4+(i+.5)*pitch, min(4, pitch*.22)
        shaft = half / 3
        points = [(x,31),(x+half,55),(x+shaft,55),(x+shaft,79),
                  (x-shaft,79),(x-shaft,55),(x-half,55)]
        if backward and forward and i >= backward:
            points = [(px, 110-py) for px, py in points]
        points = [(px*SCALE, (8+(py-8)*(diagram_bottom-8)/94)*SCALE) for px, py in points]
        draw.polygon(points, fill=(244,245,244,255))

    # Noto Sans has no U+2192. Draw that symbol as a scalable stroke offline,
    # alongside TrueType text, rather than displaying a missing glyph.
    text = label(backward, forward)
    one_way = text.startswith("→")
    digits = text[1:] if one_way else text
    arrow_advance = settings["font_size"] * .875 * SCALE if one_way else 0
    x = (SIZE*SCALE - font.getlength(digits) - arrow_advance) / 2
    box = font.getbbox("1234+", anchor="ls")
    baseline = (footer_top + settings["footer_height"]/2)*SCALE - (box[1]+box[3])/2
    ink = (246,246,246,255)
    if one_way:
        cy, unit = (footer_top+settings["footer_height"]/2)*SCALE, settings["font_size"]/16*SCALE
        draw.line([(x,cy),(x+11*unit,cy)], fill=ink, width=max(1,round(1.5*unit)))
        draw.line([(x+6*unit,cy-5*unit),(x+11*unit,cy),(x+6*unit,cy+5*unit)], fill=ink, width=max(1,round(1.5*unit)))
    draw.text((x+arrow_advance,baseline), digits, font=font, anchor="ls", fill=ink)
    image = image.resize((SIZE,SIZE), Image.Resampling.LANCZOS)
    return Image.alpha_composite(image, overlay) if overlay is not None else image


def generate(output, config=DEFAULT_CONFIG, preview_path=None, overlay_path=None):
    settings, font, overlay = load_settings(config, overlay_path)
    roads = variants()
    # Validate and render everything before replacing any existing output.
    encoded = []
    for road in roads:
        image = render(road.backward_lanes, road.forward_lanes, settings, font, overlay)
        stream = io.BytesIO()
        image.save(stream, format="PNG")
        encoded.append((road, image, stream.getvalue()))
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    imports = output / "import"
    imports.mkdir(exist_ok=True)
    preview_icons = Path(preview_path) / "icons" if preview_path is not None else None
    if preview_icons:
        preview_icons.mkdir(parents=True, exist_ok=True)
    for road, image, png in encoded:
        name = f"{road.backward_lanes}-{road.forward_lanes}.png"
        (output / name).write_bytes(png)
        if preview_icons:
            (preview_icons / name).write_bytes(png)
        job = settings.get("import_job_names", {}).get(road.road_id, road.road_id)
        for state in STATES:
            (imports / f"{job}_{state}.png").write_bytes(png)
    sheet = Image.new("RGBA", (432,432), (13,18,24,255))
    ordered = sorted(encoded, key=lambda item: (0 if item[0].backward_lanes == 0 else
                     1 if item[0].backward_lanes == item[0].forward_lanes else 2,
                     item[0].backward_lanes, item[0].forward_lanes))
    for index, (_, image, _) in enumerate(ordered):
        sheet.alpha_composite(image.resize((96,96), Image.Resampling.LANCZOS), (6+108*(index%4),6+108*(index//4)))
    sheet.save(output / "contact-sheet.png")
    template = output / "overlay-template.png"
    if not template.exists():
        Image.new("RGBA", (SIZE,SIZE)).save(template)
    print(f"ROAD_ICONS_EXPORTED count={len(roads)} import_states=5 output={output}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "build/road-icons")
    parser.add_argument("--config", type=Path, default=DEFAULT_CONFIG)
    parser.add_argument("--preview-path", type=Path)
    parser.add_argument("--overlay", type=Path)
    args = parser.parse_args()
    generate(args.output, args.config, args.preview_path, args.overlay)


if __name__ == "__main__":
    main()
