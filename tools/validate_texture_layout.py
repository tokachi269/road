from __future__ import annotations

import json
from pathlib import Path

from PIL import Image


REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_MANIFEST = REPO_ROOT / "textures" / "dimensions.json"
BASE_LAYER_ROOT = REPO_ROOT / "textures" / "base_layers"


def load_manifest(path: Path = DEFAULT_MANIFEST) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def regions_by_id(manifest: dict) -> dict[str, dict]:
    regions: dict[str, dict] = {}
    atlas_width = int(manifest["atlas_width_px"])
    for atlas_name, atlas in manifest["atlas_layout"].items():
        for slot in atlas["slots"]:
            for region in slot["regions"]:
                item = dict(region)
                item["atlas"] = atlas_name
                item["slot_id"] = slot["id"]
                item["u_min"] = region["x_px"] / atlas_width
                item["u_max"] = (
                    region["x_px"] + region["width_px"]
                ) / atlas_width
                if item["id"] in regions:
                    raise ValueError(f"duplicate texture region id: {item['id']}")
                regions[item["id"]] = item
    return regions


def validate_manifest(path: Path = DEFAULT_MANIFEST) -> dict[str, dict]:
    manifest = load_manifest(path)
    root = BASE_LAYER_ROOT
    atlas_width = int(manifest["atlas_width_px"])
    atlas_height = int(manifest["texture_height_px"])
    downscale = int(manifest["downscale_size_px"])
    authoring_scale = int(manifest["authoring_scale"])
    grid = int(manifest["placement_grid_px"])

    if atlas_width != atlas_height:
        raise ValueError("authoring atlas must be square")
    if atlas_width != downscale * authoring_scale:
        raise ValueError("authoring atlas must downscale by authoring_scale exactly")
    if atlas_width % 4 or atlas_height % 4:
        raise ValueError("BC/DXT atlas dimensions must be multiples of four")

    dimensions = {item["file"]: item for item in manifest["layers"]}
    referenced_files: set[str] = set()
    for atlas_name, atlas in manifest["atlas_layout"].items():
        occupied: list[tuple[int, int, str]] = []
        for slot in atlas["slots"]:
            slot_start = int(slot["slot_x_px"])
            slot_width = int(slot["slot_width_px"])
            slot_end = slot_start + slot_width
            if slot_start % grid or slot_width % grid:
                raise ValueError(f"{atlas_name}/{slot['id']}: slot is off the {grid}px grid")
            if slot_start < 0 or slot_end > atlas_width:
                raise ValueError(f"{atlas_name}/{slot['id']}: slot exceeds atlas")
            for other_start, other_end, other_id in occupied:
                if slot_start < other_end and slot_end > other_start:
                    raise ValueError(
                        f"{atlas_name}: slots {other_id} and {slot['id']} overlap"
                    )
            occupied.append((slot_start, slot_end, slot["id"]))

            regions = slot["regions"]
            if not regions:
                raise ValueError(f"{atlas_name}/{slot['id']}: empty slot")
            region_start = int(regions[0]["x_px"])
            region_end = int(regions[-1]["x_px"]) + int(regions[-1]["width_px"])
            expected_start = slot_start + int(slot["padding_left_px"])
            expected_end = slot_end - int(slot["padding_right_px"])
            if region_start != expected_start or region_end != expected_end:
                raise ValueError(f"{atlas_name}/{slot['id']}: padding does not bound content")

            previous_end = region_start
            for region in regions:
                x = int(region["x_px"])
                width = int(region["width_px"])
                if x != previous_end:
                    raise ValueError(
                        f"{atlas_name}/{slot['id']}: connected regions have a gap"
                    )
                if x % authoring_scale or width % authoring_scale:
                    raise ValueError(
                        f"{atlas_name}/{region['id']}: region cannot downscale exactly"
                    )
                previous_end = x + width

                filename = region["file"]
                if filename in referenced_files:
                    raise ValueError(f"texture file is placed more than once: {filename}")
                referenced_files.add(filename)
                if filename not in dimensions:
                    raise ValueError(f"layout file has no dimension entry: {filename}")
                item = dimensions[filename]
                if int(item["width_px"]) != width or int(item["height_px"]) != atlas_height:
                    raise ValueError(f"dimension entry disagrees with layout: {filename}")
                with Image.open(root / filename) as image:
                    if image.size != (width, atlas_height):
                        raise ValueError(
                            f"PNG size disagrees with layout: {filename} {image.size}"
                        )

    missing = set(dimensions) - referenced_files
    if missing:
        raise ValueError(f"dimension entries are not placed: {sorted(missing)}")
    return regions_by_id(manifest)


if __name__ == "__main__":
    result = validate_manifest()
    print(f"texture-layout: OK ({len(result)} regions)")

