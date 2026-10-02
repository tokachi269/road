from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import struct
import tempfile
from pathlib import Path

import bpy


BUNDLE_SCHEMA_VERSION = 2
NETWORK_MAIN_TEXTURE_SCALE = (1.0, 0.5)
SAFE_ID = re.compile(r"[^A-Za-z0-9_.-]+")
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


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


def _file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _png_size(path: Path) -> tuple[int, int]:
    with path.open("rb") as stream:
        header = stream.read(24)
    if len(header) != 24 or header[:8] != PNG_SIGNATURE or header[12:16] != b"IHDR":
        raise ValueError(f"Photoshop Generator output is not a valid PNG: {path}")
    return struct.unpack(">II", header[16:24])


def _atomic_copy(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{destination.name}.", suffix=".tmp", dir=str(destination.parent),
    )
    os.close(descriptor)
    temporary = Path(temporary_name)
    try:
        shutil.copyfile(source, temporary)
        os.replace(temporary, destination)
    finally:
        if temporary.exists():
            temporary.unlink()


def _texture_contract(texture_layout_path: Path) -> tuple[dict, list[dict]]:
    manifest = json.loads(texture_layout_path.read_text(encoding="utf-8"))
    generator = manifest.get("photoshop_generator")
    if not isinstance(generator, dict):
        raise ValueError("dimensions.json has no photoshop_generator contract")
    assets_directory = generator.get("assets_directory")
    maps = generator.get("maps")
    families = generator.get("families")
    runtime_packs = generator.get("runtime_packs")
    if not isinstance(assets_directory, str) or not assets_directory.strip():
        raise ValueError("photoshop_generator.assets_directory must be a relative directory")
    if Path(assets_directory).is_absolute() or ".." in Path(assets_directory).parts:
        raise ValueError("photoshop_generator.assets_directory must stay under textures")
    if (
        not isinstance(maps, dict)
        or not isinstance(families, dict)
        or not isinstance(runtime_packs, dict)
    ):
        raise ValueError(
            "photoshop_generator maps, runtime_packs and families must be objects"
        )

    packed_map_ids = set()
    for packing, pack_contract in runtime_packs.items():
        if packing not in {"APR", "XYS"} or not isinstance(pack_contract, dict):
            raise ValueError(f"Unsupported runtime texture packing: {packing}")
        property_name = pack_contract.get("shader_property")
        source_maps = pack_contract.get("source_maps")
        if not isinstance(property_name, str) or not property_name:
            raise ValueError(f"Runtime texture packing has no shader property: {packing}")
        if not isinstance(source_maps, list) or not source_maps:
            raise ValueError(f"Runtime texture packing has no source maps: {packing}")
        for map_id in source_maps:
            if map_id not in maps:
                raise ValueError(
                    f"Runtime texture packing references an unknown map: {packing}.{map_id}"
                )
            if map_id in packed_map_ids:
                raise ValueError(f"Photoshop Generator map is packed twice: {map_id}")
            packed_map_ids.add(map_id)

    expected_size = (
        int(manifest["atlas_width_px"]), int(manifest["texture_height_px"]),
    )
    assets_root = texture_layout_path.parent / assets_directory
    outputs = []
    output_names = set()
    for family, family_maps in sorted(families.items()):
        if not isinstance(family_maps, dict):
            raise ValueError(f"photoshop_generator family must be an object: {family}")
        if set(family_maps) != set(maps):
            raise ValueError(
                f"Photoshop Generator family must name every declared map: {family}"
            )
        for map_id, filename in sorted(family_maps.items()):
            map_contract = maps.get(map_id)
            if not isinstance(map_contract, dict):
                raise ValueError(f"Unknown Photoshop Generator map '{map_id}' in {family}")
            property_name = map_contract.get("shader_property")
            if property_name is not None and (
                not isinstance(property_name, str) or not property_name
            ):
                raise ValueError(f"Map '{map_id}' has an invalid shader_property")
            if property_name is None and map_id not in packed_map_ids:
                raise ValueError(f"Map '{map_id}' has no runtime consumer")
            if not isinstance(filename, str) or not filename.lower().endswith(".png"):
                raise ValueError(f"Generator output must be a PNG: {family}.{map_id}")
            relative = Path(filename)
            if relative.is_absolute() or ".." in relative.parts:
                raise ValueError(f"Generator output must stay under its assets directory: {filename}")
            normalized_name = relative.as_posix().lower()
            if normalized_name in output_names:
                raise ValueError(f"Generator output filename is reused: {filename}")
            output_names.add(normalized_name)
            source = assets_root / relative
            required = bool(map_contract.get("required", False))
            if not source.is_file():
                if required:
                    raise FileNotFoundError(
                        f"Photoshop Generator output is missing: {source}. "
                        f"Name the {family} layer group '{filename}' and enable Image Assets."
                    )
                continue
            actual_size = _png_size(source)
            if actual_size != expected_size:
                raise ValueError(
                    f"Photoshop Generator output has size {actual_size[0]}x{actual_size[1]}, "
                    f"expected {expected_size[0]}x{expected_size[1]}: {source}"
                )
            outputs.append({
                "family": family,
                "map_id": map_id,
                "property": property_name,
                "filename": relative.as_posix(),
                "source": source,
            })
    return manifest, outputs


def texture_fingerprint(texture_layout_path: Path) -> str:
    try:
        _, outputs = _texture_contract(texture_layout_path)
        state = []
        for output in outputs:
            stat = output["source"].stat()
            state.append((
                output["family"], output["map_id"], output["filename"],
                stat.st_size, stat.st_mtime_ns,
            ))
        return _hash(state)
    except (OSError, ValueError, KeyError, TypeError) as error:
        return _hash({"error": str(error)})


def stage_runtime_textures(
    output_root: Path, texture_layout_path: Path,
) -> tuple[dict[str, dict], str]:
    manifest, outputs = _texture_contract(texture_layout_path)
    bindings: dict[str, dict] = {}
    staged_outputs: dict[str, dict[str, dict]] = {}
    revision_items = []
    for output in outputs:
        source = output["source"]
        relative_output = Path("textures") / output["filename"]
        destination = output_root / relative_output
        content_hash = _file_sha256(source)
        if not destination.is_file() or _file_sha256(destination) != content_hash:
            _atomic_copy(source, destination)
        staged = dict(output)
        staged["relative_output"] = relative_output.as_posix()
        staged_outputs.setdefault(output["family"], {})[output["map_id"]] = staged
        family_bindings = bindings.setdefault(
            output["family"], {"textures": [], "packed_textures": []},
        )
        if output["property"] is not None:
            family_bindings["textures"].append({
                "name": output["property"],
                "value_json": json.dumps(relative_output.as_posix()),
            })
        revision_items.append({
            "family": output["family"],
            "map_id": output["map_id"],
            "property": output["property"],
            "path": relative_output.as_posix(),
            "sha256": content_hash,
        })
    for family, family_outputs in staged_outputs.items():
        family_bindings = bindings[family]
        for packing, pack_contract in sorted(
            manifest["photoshop_generator"]["runtime_packs"].items()
        ):
            sources = [
                {
                    "name": map_id,
                    "value_json": json.dumps(
                        family_outputs[map_id]["relative_output"]
                    ),
                }
                for map_id in pack_contract["source_maps"]
                if map_id in family_outputs
            ]
            if sources:
                family_bindings["packed_textures"].append({
                    "name": pack_contract["shader_property"],
                    "packing": packing,
                    "sources": sources,
                })
    return bindings, _hash(revision_items)


def safe_road_id(value: str) -> str:
    result = SAFE_ID.sub("-", value.strip()).strip("-.")
    return result or "road"


def _unity_vector(vector) -> list[float]:
    # Blender road space: X lateral, Y longitudinal, Z up.
    # Unity road space: X lateral, Z longitudinal, Y up.
    return [float(vector.x), float(vector.z), float(vector.y)]


def serialize_mesh_object(
    obj: bpy.types.Object, shader: str,
    material_families: dict[str, str] | None = None,
    texture_bindings: dict[str, dict] | None = None,
) -> dict:
    if obj.type != "MESH":
        raise ValueError(f"{obj.name}: runtime export accepts Mesh objects only")
    mesh = obj.data
    mesh.calc_loop_triangles()
    # Object translation is only the Blender preview layout. Export the mesh in
    # road-local space while preserving an explicitly applied rotation/scale.
    basis = obj.matrix_world.to_3x3()
    normal_matrix = basis.inverted_safe().transposed()
    # _unity_vector swaps Blender Y/Z. That axis permutation has determinant
    # -1, so it reverses triangle winding unless the object basis already has
    # a negative determinant. Keep Unity's geometric face normal aligned with
    # the explicitly exported loop normal.
    reverse_winding = basis.determinant() > 0.0
    uv_layer = mesh.uv_layers.active
    vertices = []
    normals = []
    uvs = []
    material_count = max(1, len(mesh.materials))
    submeshes = [[] for _ in range(material_count)]

    for triangle in mesh.loop_triangles:
        material_index = min(max(triangle.material_index, 0), material_count - 1)
        triangle_loops = reversed(triangle.loops) if reverse_winding else triangle.loops
        for loop_index in triangle_loops:
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
        material_name = material.name if material is not None else f"material-{index}"
        family = (material_families or {}).get(material_name)
        family_bindings = (texture_bindings or {}).get(family, {})
        materials.append({
            "name": material_name,
            "shader": shader,
            "color": list(material.diffuse_color) if material is not None else [1.0, 1.0, 1.0, 1.0],
            "textures": list(family_bindings.get("textures", [])),
            "packed_textures": list(
                family_bindings.get("packed_textures", [])
            ),
            "main_texture_scale": list(NETWORK_MAIN_TEXTURE_SCALE),
        })
    return {
        "name": obj.name,
        "vertices": vertices,
        "normals": normals,
        "uv": uvs,
        "submeshes": submeshes,
        "materials": materials,
    }


def serialize_mesh_entries(
    obj: bpy.types.Object, shader: str,
    material_families: dict[str, str] | None = None,
    texture_bindings: dict[str, dict] | None = None,
) -> list[dict]:
    source = serialize_mesh_object(
        obj, shader, material_families, texture_bindings,
    )
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
    half_width: float,
    pavement_width: float,
    node_min_corner_offset: float,
    imt_marking_style: dict,
    lanes: list[dict],
    modes: dict[str, list[bpy.types.Object]],
    texture_layout_path: Path,
    material_families: dict[str, str],
) -> dict:
    road_id = safe_road_id(road_id)
    texture_bindings, texture_revision = stage_runtime_textures(
        output_root, texture_layout_path,
    )
    serialized_modes = []
    for mode, objects in sorted(modes.items()):
        shader = "Custom/Net/Road" if mode == "basic" else "Custom/Net/RoadBridge"
        entries = []
        for index, obj in enumerate(objects):
            for mesh in serialize_mesh_entries(
                obj, shader, material_families, texture_bindings,
            ):
                entries.append({
                    "kind": "segment" if index == 0 else "node",
                    "mesh": mesh,
                })
        serialized_modes.append({"mode": mode, "entries": entries})

    structural_signature = _hash({
        "template_name": template_name,
        "half_width": half_width,
        "pavement_width": pavement_width,
        "node_min_corner_offset": node_min_corner_offset,
        "lanes": lanes,
    })
    payload = {
        "schema_version": BUNDLE_SCHEMA_VERSION,
        "road_id": road_id,
        "prefab_name": prefab_name,
        "template_name": template_name,
        "half_width": half_width,
        "pavement_width": pavement_width,
        "node_min_corner_offset": node_min_corner_offset,
        "imt_marking_style": imt_marking_style,
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
        "texture_revision": texture_revision,
        "roads": [entries[key] for key in sorted(entries)],
    }
    manifest["revision"] = _hash(manifest)
    _atomic_write_json(manifest_path, manifest)
    return payload


def finalize_runtime_manifest(output_root: Path, road_ids: list[str]) -> dict:
    """Restrict one completed batch export to the roads in the Blender Scene."""
    manifest_path = output_root / "manifest.json"
    if not manifest_path.exists():
        raise ValueError("Runtime manifest was not created")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("schema_version") != BUNDLE_SCHEMA_VERSION:
        raise ValueError("Runtime manifest schema does not match the exporter")
    requested = {safe_road_id(value) for value in road_ids}
    entries = {
        item.get("road_id"): item
        for item in manifest.get("roads", [])
        if isinstance(item, dict) and item.get("road_id") in requested
    }
    missing = sorted(requested - set(entries))
    if missing:
        raise ValueError(
            "Runtime manifest is missing exported roads: " + ", ".join(missing)
        )
    finalized = {
        "schema_version": BUNDLE_SCHEMA_VERSION,
        "texture_revision": manifest.get("texture_revision", ""),
        "roads": [entries[key] for key in sorted(entries)],
    }
    finalized["revision"] = _hash(finalized)
    _atomic_write_json(manifest_path, finalized)
    return finalized


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
