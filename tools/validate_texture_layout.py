from __future__ import annotations

import argparse
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


def validate_manifest(
    path: Path = DEFAULT_MANIFEST,
    *,
    require_generator_outputs: bool = False,
) -> dict[str, dict]:
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

    generator = manifest.get("photoshop_generator")
    if not isinstance(generator, dict):
        raise ValueError("photoshop_generator contract is missing")
    assets_directory = Path(generator.get("assets_directory", ""))
    if not str(assets_directory) or assets_directory.is_absolute() or ".." in assets_directory.parts:
        raise ValueError("photoshop_generator assets_directory is unsafe")
    generator_maps = generator.get("maps")
    generator_families = generator.get("families")
    runtime_packs = generator.get("runtime_packs")
    if (
        not isinstance(generator_maps, dict)
        or not isinstance(generator_families, dict)
        or not isinstance(runtime_packs, dict)
    ):
        raise ValueError(
            "photoshop_generator maps, runtime_packs and families must be objects"
        )
    if set(generator_families) != set(manifest["atlas_layout"]):
        raise ValueError("Photoshop Generator families must match atlas_layout families")
    packed_map_ids: set[str] = set()
    for packing, pack_contract in runtime_packs.items():
        if packing not in {"APR", "XYS"} or not isinstance(pack_contract, dict):
            raise ValueError(f"Unsupported runtime texture packing: {packing}")
        if not pack_contract.get("shader_property"):
            raise ValueError(f"Runtime texture packing has no shader property: {packing}")
        source_maps = pack_contract.get("source_maps")
        if not isinstance(source_maps, list) or not source_maps:
            raise ValueError(f"Runtime texture packing has no source maps: {packing}")
        for map_id in source_maps:
            if map_id not in generator_maps:
                raise ValueError(
                    f"Runtime texture packing references an unknown map: {packing}.{map_id}"
                )
            if map_id in packed_map_ids:
                raise ValueError(f"Photoshop Generator map is packed twice: {map_id}")
            packed_map_ids.add(map_id)
    for map_id, map_contract in generator_maps.items():
        if not isinstance(map_contract, dict):
            raise ValueError(f"Photoshop Generator map is not an object: {map_id}")
        if not map_contract.get("shader_property") and map_id not in packed_map_ids:
            raise ValueError(f"Photoshop Generator map has no runtime consumer: {map_id}")
    generated_names: set[str] = set()
    for family, family_maps in generator_families.items():
        if not isinstance(family_maps, dict):
            raise ValueError(f"Photoshop Generator family is not an object: {family}")
        if set(family_maps) != set(generator_maps):
            raise ValueError(
                f"Photoshop Generator family must name every declared map: {family}"
            )
        for map_id, filename in family_maps.items():
            if map_id not in generator_maps:
                raise ValueError(f"Photoshop Generator map is not declared: {map_id}")
            map_contract = generator_maps[map_id]
            if filename in generated_names:
                raise ValueError(f"Photoshop Generator output is reused: {filename}")
            generated_names.add(filename)
            generated_path = path.parent / assets_directory / filename
            if (
                require_generator_outputs
                and map_contract.get("required")
                and not generated_path.is_file()
            ):
                raise ValueError(f"Photoshop Generator output is missing: {generated_path}")
            if generated_path.is_file():
                with Image.open(generated_path) as image:
                    if image.size != (atlas_width, atlas_height):
                        raise ValueError(
                            f"Photoshop Generator output has wrong size: {filename} {image.size}"
                        )

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
                if width % authoring_scale:
                    raise ValueError(
                        f"{atlas_name}/{region['id']}: region width cannot downscale exactly"
                    )
                previous_end = x + width

                filename = region["file"]
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

    profiles = manifest.get("uv_profiles")
    if not isinstance(profiles, dict) or not profiles:
        raise ValueError("uv_profiles contract is missing")
    region_ids = set(regions_by_id(manifest))
    for profile_id, profile in profiles.items():
        if profile_id in region_ids:
            raise ValueError(f"UV profile duplicates a texture region id: {profile_id}")
        if not isinstance(profile, dict):
            raise ValueError(f"UV profile must be an object: {profile_id}")
        atlas_name = profile.get("atlas")
        if atlas_name not in manifest["atlas_layout"]:
            raise ValueError(f"UV profile has unknown atlas: {profile_id}")
        x = int(profile.get("x_px", -1))
        width = int(profile.get("width_px", -1))
        if x < 0 or width <= 0 or x + width > atlas_width:
            raise ValueError(f"UV profile exceeds atlas: {profile_id}")
        if x % authoring_scale or width % authoring_scale:
            raise ValueError(f"UV profile cannot downscale exactly: {profile_id}")
        containing_slots = [
            slot for slot in manifest["atlas_layout"][atlas_name]["slots"]
            if x >= int(slot["slot_x_px"])
            and x + width <= int(slot["slot_x_px"]) + int(slot["slot_width_px"])
        ]
        if len(containing_slots) != 1:
            raise ValueError(
                f"UV profile must stay inside exactly one slot: {profile_id}"
            )

    missing = set(dimensions) - referenced_files
    if missing:
        raise ValueError(f"dimension entries are not placed: {sorted(missing)}")
    return regions_by_id(manifest)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Validate the texture layout contract.")
    parser.add_argument(
        "--require-generator-outputs",
        action="store_true",
        help="also require every mandatory Photoshop Generator PNG to exist",
    )
    args = parser.parse_args()
    result = validate_manifest(
        require_generator_outputs=args.require_generator_outputs,
    )
    print(f"texture-layout: OK ({len(result)} regions)")

