from __future__ import annotations

import json
import sys
from pathlib import Path

import bpy


def repo_root() -> Path:
    return Path(__file__).resolve().parents[1]


def main() -> None:
    output_dir = repo_root() / "build" / "smoke"
    output_dir.mkdir(parents=True, exist_ok=True)

    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    bpy.ops.mesh.primitive_cube_add(size=2.0, location=(0.0, 0.0, 0.0))
    cube = bpy.context.active_object
    cube.name = "road_environment_smoke"

    blend_path = output_dir / "road-environment-smoke.blend"
    fbx_path = output_dir / "road-environment-smoke.fbx"
    bpy.ops.wm.save_as_mainfile(filepath=str(blend_path))
    bpy.ops.export_scene.fbx(
        filepath=str(fbx_path),
        use_selection=True,
        add_leaf_bones=False,
        bake_anim=False,
    )

    result = {
        "blender_version": bpy.app.version_string,
        "python_version": sys.version,
        "blend": str(blend_path),
        "fbx": str(fbx_path),
    }
    (output_dir / "environment.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()

