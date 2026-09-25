from __future__ import annotations

import hashlib
import json
import os
import re
import tempfile
from pathlib import Path

import bpy


BUNDLE_SCHEMA_VERSION = 1
SAFE_ID = re.compile(r"[^A-Za-z0-9_.-]+")


def _canonical_bytes(value) -> bytes:
    return json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":"),
    ).encode("utf-8")


def _hash(value) -> str:
    return hashlib.sha256(_canonical_bytes(value)).hexdigest()


def _atomic_write_json(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".tmp", dir=str(path.parent),
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, ensure_ascii=False, sort_keys=True, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def safe_road_id(value: str) -> str:
    result = SAFE_ID.sub("-", value.strip()).strip("-.")
    return result or "road"


def _unity_vector(vector) -> list[float]:
    # Blender road space: X lateral, Y longitudinal, Z up.
    # Unity road space: X lateral, Z longitudinal, Y up.
    return [float(vector.x), float(vector.z), float(vector.y)]


def serialize_mesh_object(obj: bpy.types.Object, shader: str) -> dict:
    if obj.type != "MESH":
        raise ValueError(f"{obj.name}: runtime export accepts Mesh objects only")
    mesh = obj.data
    mesh.calc_loop_triangles()
    # Object translation is only the Blender preview layout. Export the mesh in
    # road-local space while preserving an explicitly applied rotation/scale.
    basis = obj.matrix_world.to_3x3()
    normal_matrix = basis.inverted_safe().transposed()
    uv_layer = mesh.uv_layers.active
    vertices = []
    normals = []
    uvs = []
    material_count = max(1, len(mesh.materials))
    submeshes = [[] for _ in range(material_count)]

    for triangle in mesh.loop_triangles:
        material_index = min(max(triangle.material_index, 0), material_count - 1)
        for loop_index in triangle.loops:
            loop = mesh.loops[loop_index]
            vertex = mesh.vertices[loop.vertex_index]
            vertices.extend(_unity_vector(basis @ vertex.co))
            normals.extend(_unity_vector((normal_matrix @ loop.normal).normalized()))
            uv = uv_layer.data[loop_index].uv if uv_layer is not None else (0.0, 0.0)
            uvs.extend((float(uv[0]), float(uv[1])))
            submeshes[material_index].append(len(vertices) // 3 - 1)

    materials = []
    for index in range(material_count):
        material = mesh.materials[index] if index < len(mesh.materials) else None
        materials.append({
            "name": material.name if material is not None else f"material-{index}",
            "shader": shader,
            "color": list(material.diffuse_color) if material is not None else [1.0, 1.0, 1.0, 1.0],
            "textures": [],
        })
    return {
        "name": obj.name,
        "vertices": vertices,
        "normals": normals,
        "uv": uvs,
        "submeshes": submeshes,
        "materials": materials,
    }


def serialize_mesh_entries(obj: bpy.types.Object, shader: str) -> list[dict]:
    source = serialize_mesh_object(obj, shader)
    result = []
    for index, triangles in enumerate(source["submeshes"]):
        if not triangles:
            continue
        result.append({
            "name": f"{source['name']}.{index}",
            "vertices": source["vertices"],
            "normals": source["normals"],
            "uv": source["uv"],
            "triangles": triangles,
            "material": source["materials"][index],
        })
    return result


def export_runtime_bundle(
    output_root: Path,
    road_id: str,
    prefab_name: str,
    template_name: str,
    lanes: list[dict],
    modes: dict[str, list[bpy.types.Object]],
) -> dict:
    road_id = safe_road_id(road_id)
    serialized_modes = []
    for mode, objects in sorted(modes.items()):
        shader = "Custom/Net/Road" if mode == "basic" else "Custom/Net/RoadBridge"
        entries = []
        for index, obj in enumerate(objects):
            for mesh in serialize_mesh_entries(obj, shader):
                entries.append({
                    "kind": "segment" if index == 0 else "node",
                    "mesh": mesh,
                })
        serialized_modes.append({"mode": mode, "entries": entries})

    structural_signature = _hash({"template_name": template_name, "lanes": lanes})
    payload = {
        "schema_version": BUNDLE_SCHEMA_VERSION,
        "road_id": road_id,
        "prefab_name": prefab_name,
        "template_name": template_name,
        "structural_signature": structural_signature,
        "lanes": lanes,
        "modes": serialized_modes,
    }
    revision = _hash(payload)
    payload["revision"] = revision

    relative_path = f"roads/{road_id}.json"
    _atomic_write_json(output_root / relative_path, payload)
    manifest_path = output_root / "manifest.json"
    manifest = {"schema_version": BUNDLE_SCHEMA_VERSION, "roads": []}
    if manifest_path.exists():
        try:
            loaded = json.loads(manifest_path.read_text(encoding="utf-8"))
            if loaded.get("schema_version") == BUNDLE_SCHEMA_VERSION:
                manifest = loaded
        except (OSError, json.JSONDecodeError):
            pass
    entries = {
        item["road_id"]: item
        for item in manifest.get("roads", [])
        if isinstance(item, dict) and item.get("road_id")
    }
    entries[road_id] = {
        "road_id": road_id,
        "prefab_name": prefab_name,
        "bundle_path": relative_path,
        "revision": revision,
        "structural_signature": structural_signature,
    }
    manifest = {
        "schema_version": BUNDLE_SCHEMA_VERSION,
        "roads": [entries[key] for key in sorted(entries)],
    }
    manifest["revision"] = _hash(manifest)
    _atomic_write_json(manifest_path, manifest)
    return payload


def export_prop_bundle(output_root: Path, prop_id: str, obj: bpy.types.Object, shader: str) -> dict:
    prop_id = safe_road_id(prop_id)
    meshes = serialize_mesh_entries(obj, shader)
    if len(meshes) != 1:
        raise ValueError("Runtime Prop/Decal export requires exactly one used material slot")
    payload = {
        "schema_version": BUNDLE_SCHEMA_VERSION,
        "prop_id": prop_id,
        "mesh": meshes[0],
    }
    payload["revision"] = _hash(payload)
    _atomic_write_json(output_root / "props" / f"{prop_id}.json", payload)
    return payload


def geometry_fingerprint(objects: list[bpy.types.Object]) -> str:
    state = []
    for obj in objects:
        if obj.type != "MESH":
            continue
        state.append({
            "name": obj.name,
            "vertices": [[round(value, 6) for value in vertex.co] for vertex in obj.data.vertices],
            "polygons": [list(polygon.vertices) for polygon in obj.data.polygons],
            "uv": [
                [round(float(value), 6) for value in item.uv]
                for item in obj.data.uv_layers.active.data
            ] if obj.data.uv_layers.active is not None else [],
        })
    return _hash(state)
