from __future__ import annotations

import argparse
import importlib
import json
import sys
from pathlib import Path

import bpy


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "blender_addon"))
road_builder = importlib.import_module("road_builder")


def arguments() -> argparse.Namespace:
    values = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--output",
        type=Path,
        default=ROOT / "build" / "vehicle-variants-preview",
    )
    return parser.parse_args(values)


def main() -> None:
    args = arguments()
    output = args.output.resolve()
    road_builder.register()
    road_builder._initialize_scene_lanes()
    scene = bpy.context.scene
    scene.cs1_roads.clear()
    scene.cs1_road_profiles.clear()
    scene.cs1_active_road_index = 0
    scene.cs1_active_profile_index = 0
    road_builder._ensure_default_profile(scene)
    result = bpy.ops.cs1_road.add_vehicle_variants()
    if result != {"FINISHED"}:
        raise RuntimeError(f"Variant creation failed: {result}")

    expected_ids = {item.road_id for item in road_builder.vehicle_lane_variants()}
    actual_ids = {road.runtime_road_id for road in scene.cs1_roads}
    if actual_ids != expected_ids:
        raise RuntimeError(
            f"Variant IDs differ: missing={sorted(expected_ids - actual_ids)}, "
            f"extra={sorted(actual_ids - expected_ids)}"
        )

    scene.cs1_runtime_output_dir = str(output)
    result = bpy.ops.cs1_road.export_all_runtime()
    if result != {"FINISHED"}:
        raise RuntimeError("Runtime export failed")

    authoring = output / "authoring"
    result = bpy.ops.cs1_road.export_all_specs(directory=str(authoring))
    if result != {"FINISHED"}:
        raise RuntimeError("Authoring spec export failed")

    manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
    manifest_ids = {item["road_id"] for item in manifest["roads"]}
    if manifest_ids != expected_ids:
        raise RuntimeError(
            f"Manifest IDs differ: missing={sorted(expected_ids - manifest_ids)}, "
            f"extra={sorted(manifest_ids - expected_ids)}"
        )
    print(f"VEHICLE_VARIANTS_EXPORTED roads={len(expected_ids)} output={output}")


if __name__ == "__main__":
    main()
