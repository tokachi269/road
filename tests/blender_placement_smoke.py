"""Inspect actual Blender placement meshes against physical golden cases."""
import importlib
import json
import gzip
import shutil
import sys
from pathlib import Path

import bpy

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "blender_addon"))
builder = importlib.import_module("road_builder")
fixture = ROOT / "build/smoke/placement-textures"
(fixture / "road-assets").mkdir(parents=True, exist_ok=True)
for family in ("surface", "structure", "tunnel"):
    shutil.copyfile(ROOT / f"textures/atlas_bases/{family}_base_2048.png",
                    fixture / f"road-assets/{family}_d.png")
manifest = fixture / "dimensions.json"
shutil.copyfile(ROOT / "textures/dimensions.json", manifest)
builder.DEFAULT_TEXTURE_LAYOUT = manifest
builder._TEXTURE_LAYOUT_CACHE = None
builder.register()
builder._initialize_scene_lanes()
scene = bpy.context.scene
scene.cs1_roads.clear()
assert bpy.ops.cs1_road.add_vehicle_variants() == {"FINISHED"}


def activate(road_id):
    scene.cs1_active_road_index = next(i for i, r in enumerate(scene.cs1_roads)
                                      if r.runtime_road_id == road_id)
    return builder.active_road(scene)


def coords(obj):
    return {tuple(round(float(v), 6) for v in vertex.co) for vertex in obj.data.vertices}


def inspect_case(road_id, options, half, curbs, median, road_z):
    props = activate(road_id)
    before = builder._authoring_fingerprint(props)
    for mode in ("basic", "elevated", "bridge", "slope", "tunnel"):
        section = builder.resolve_placement_section(props.lanes, options)
        objects = builder.build_placement_mode(scene, mode, section)
        assert builder._authoring_fingerprint(props) == before
        for obj, part, slices in zip(objects, ("segment", "node"),
                                     (props.segment_slices, props.node_slices)):
            points = coords(obj)
            assert min(p[0] for p in points) == -half, (road_id, mode, part)
            assert max(p[0] for p in points) == half
            ys = (-32.0, 0.0, 32.0)
            for x in curbs:
                for y in ys:
                    assert (x, y, 0.0) in points, (road_id, mode, part, x, y)
                    assert (x, y, road_z) in points
            if median is not None:
                left, right = median
                mesh = obj.data
                for face in mesh.polygons:
                    face_points = [mesh.vertices[i].co for i in face.vertices]
                    # No asphalt under the median; height has independent expected value.
                    if all(abs(p.z - road_z) < 1e-6 for p in face_points):
                        assert not (min(p.x for p in face_points) < right - 1e-6
                                    and max(p.x for p in face_points) > left + 1e-6), (mode, part)
            assert obj["cs1_longitudinal_slices"] == slices
            assert obj["cs1_placement_identity"] == section.variant_identity(road_id)
            assert all(face.area > 1e-8 for face in obj.data.polygons), (road_id, mode, part)
            assert all(not all(abs(obj.data.vertices[i].co.y - end) < 1e-6 for i in face.vertices)
                       for face in obj.data.polygons for end in (-32.0, 32.0)), "unexpected end cap"
        # Node road surface must remain D0 when selected; old flush preview descends to D30.
        if road_z == 0.0 and median is None and mode == "basic":
            assert {round(v.co.z, 6) for v in objects[1].data.vertices} == {0.0}


# Hold a regular preview vertex edit and ensure placement construction cannot erase it.
props = activate("jp-1x2l")
legacy = builder.build_mode(scene, "basic")
legacy[0].data.vertices[0].co.z += 0.123
legacy_snapshot = coords(legacy[0])
cases = (
    ("jp-1x2l", builder.RoadPlacementOptions(roadside="NONE"), 16.0, (-3.0, 6.0), None, -0.3),
    ("jp-2x2l", builder.RoadPlacementOptions(divider="OFFSET_RIGHT", median="STANDARD"),
     16.0, (-5.5, 8.5), (1.0, 2.0), -0.3),
    ("jp-1x1l", builder.RoadPlacementOptions(level="FLUSH"), 8.0, (-3.5, 3.5), None, 0.0),
    ("jp-3l-oneway", builder.RoadPlacementOptions(width="WIDE", roadside="WIDE"),
     24.0, (-6.5, 6.5), None, -0.3),
    ("jp-4x4l", builder.RoadPlacementOptions(divider="OFFSET_LEFT", median="WIDE", roadside="WIDE"),
     24.0, (-16.5, 13.5), (-2.5, -0.5), -0.3),
)
for case in cases:
    inspect_case(*case)
# Height transition uses explicit endpoint nodes, not a permanently sloped
# replacement for either ordinary road. Expectations are literal metres.
for road_id, options, curbs, median in (
    ("jp-1x2l", dict(roadside="NONE"), (-3.0, 6.0), None),
    ("jp-2x2l", dict(divider="OFFSET_RIGHT", median="STANDARD"), (-5.5, 8.5), (1.0, 2.0)),
    ("jp-3l-oneway", dict(width="WIDE", roadside="WIDE"), (-6.5, 6.5), None),
):
    props = activate(road_id)
    lower = builder.resolve_placement_section(props.lanes, builder.RoadPlacementOptions(**options, level="DEPRESSED"))
    flush = builder.resolve_placement_section(props.lanes, builder.RoadPlacementOptions(**options, level="FLUSH"))
    before = builder._authoring_fingerprint(props)
    for start, end in ((lower, flush), (flush, lower)):
        transition = builder.resolve_height_transition(start, end)
        for mode in ("basic", "elevated", "bridge", "slope", "tunnel"):
            built = builder.build_height_transition_mode(scene, mode, transition)
            assert set(built) == {"segment", "start_node", "end_node"}
            segment = built["segment"]
            points = coords(segment)
            z0, z1 = (-0.3, 0.0) if start is lower else (0.0, -0.3)
            for y, expected in ((-32.0, z0), (0.0, -0.15), (32.0, z1)):
                for x in curbs:
                    assert (x, y, round(expected, 6)) in points, (road_id, mode, x, y, expected)
                    assert (x, y, 0.0) in points
            assert all(face.area > 1e-8 for face in segment.data.polygons)
            assert all(len(set(face.vertices)) == len(face.vertices) for face in segment.data.polygons)
            assert built["start_node"] is builder.build_placement_mode(scene, mode, start)[1]
            assert built["end_node"] is builder.build_placement_mode(scene, mode, end)[1]
            assert built["start_node"]["cs1_placement_identity"] != built["end_node"]["cs1_placement_identity"]
            if median is not None:
                for x in median:
                    assert (x, 0.0, -0.15) in points
            if mode == "tunnel":
                roof0, roof1 = z0 + props.tunnel_clearance, z1 + props.tunnel_clearance
                half = start.total_width * 0.5
                assert (-half, -32.0, round(roof0, 6)) in points
                assert (half, 32.0, round(roof1, 6)) in points
            repeat = builder.build_height_transition_mode(scene, mode, transition)
            assert repeat["segment"] is segment
    assert builder._authoring_fingerprint(props) == before
assert coords(legacy[0]) == legacy_snapshot
assert legacy[0].name == "basic_segment"
# A repeated concrete identity is reused, including an explicit user vertex edit.
props = activate("jp-1x1l")
section = builder.resolve_placement_section(props.lanes, builder.RoadPlacementOptions(level="FLUSH"))
cached = builder.build_placement_mode(scene, "basic", section)
cached[0].data.vertices[0].co.z += 0.01
snapshot = coords(cached[0])
again = builder.build_placement_mode(scene, "basic", section)
assert again[0] is cached[0] and coords(again[0]) == snapshot
rebuilt = builder.build_placement_mode(scene, "basic", section, rebuild=True)
assert coords(rebuilt[0]) != snapshot
props.segment_slices += 2
try:
    builder.build_placement_mode(scene, "basic", section)
except ValueError as error:
    assert "rebuild explicitly" in str(error)
else:
    raise AssertionError("Stale placement mesh accepted after changing subdivision input")
assert rebuilt[0].data is not None
builder.build_placement_mode(scene, "basic", section, rebuild=True)

# Missing source or invalid option must not destroy any successfully built collection.
collections_before = {c.name for c in bpy.data.collections}
try:
    builder.build_placement_mode(scene, "unknown", section)
except ValueError:
    pass
else:
    raise AssertionError("Unknown mode accepted")
assert {c.name for c in bpy.data.collections} == collections_before
# Produce real cross-language fixtures; snapshots are not regular manifest roads.
output = ROOT / "build/smoke/placement-runtime"
output.mkdir(parents=True, exist_ok=True)
regular_manifest = output / "manifest.json"
regular_manifest.write_text('{"sentinel":"regular-road-library"}', encoding="utf-8")
activate("jp-4x4l")
index_path = builder.export_placement_choices(scene, (
    builder.RoadPlacementOptions(width="NARROW"), builder.RoadPlacementOptions(width="STANDARD"),
), output)
index = json.loads((output / index_path).read_text(encoding="utf-8"))
assert len(index["choices"]) == 2
assert index["schema_version"] == 2
assert [c["total_width"] for c in index["choices"]] == [32.0, 32.0]
assert [c["roadway_z"] for c in index["choices"]] == [-0.3, -0.3]
before_invalid = (output / index_path).read_bytes()
for invalid_choices in ((), (builder.RoadPlacementOptions(),) * 2,
                        (builder.RoadPlacementOptions(), builder.RoadPlacementOptions(width="UNKNOWN"))):
    try:
        builder.export_placement_choices(scene, invalid_choices, output)
        raise AssertionError("Invalid placement menu was published")
    except ValueError:
        pass
    assert (output / index_path).read_bytes() == before_invalid
assert index["choices"][0]["identity"] == index["choices"][1]["identity"]
assert index["choices"][0]["bundle_path"] == index["choices"][1]["bundle_path"]
assert json.loads(regular_manifest.read_text())["sentinel"] == "regular-road-library"
activate("jp-1x2l")
path = builder.export_placement_choices(scene, (builder.RoadPlacementOptions(roadside="NONE"),), output)
choice = json.loads((output / path).read_text())["choices"][0]
compressed_packet = (output / choice["bundle_path"]).read_bytes()
assert choice["bundle_path"].endswith(".json.gz")
assert compressed_packet[:2] == b"\x1f\x8b" and compressed_packet[4:8] == b"\0\0\0\0"
packet = json.loads(gzip.decompress(compressed_packet))
assert packet["schema_version"] == 2
assert packet["node_reference_pavement_width"] == 11.5
builder.export_placement_choices(scene, (builder.RoadPlacementOptions(roadside="NONE"),), output)
assert (output / choice["bundle_path"]).read_bytes() == compressed_packet
assert [(v["position"], v["width"], round(v["vertical_offset"], 6)) for v in packet["lanes"]] == [
    (-9.5, 12.5, 0.0), (-1.5, 3.0, -0.3), (1.5, 3.0, -0.3),
    (4.5, 3.0, -0.3), (11.0, 9.5, 0.0),
]
print("PLACEMENT_SNAPSHOT_FIXTURE_OK aliases_reused regular_manifest_preserved")
# A transition is an ordered edge between ordinary snapshots, never another
# permanent level option. Its payload owns only the five segment meshes.
height_options = (
    builder.RoadPlacementOptions(roadside="NONE", level="DEPRESSED"),
    builder.RoadPlacementOptions(roadside="NONE", level="FLUSH"),
)
path = builder.export_placement_choices(scene, height_options, output)
height_menu = json.loads((output / path).read_text())
edges = height_menu["height_transitions"]
assert len(edges) == 2
ordinary = {item["key"]: item for item in height_menu["choices"]}
assert len({edge["identity"] for edge in edges}) == 2
for edge in edges:
    assert edge["from_key"] != edge["to_key"]
    payload = json.loads(gzip.decompress((output / edge["bundle_path"]).read_bytes()))
    assert payload["schema_version"] == 1
    assert payload["start_identity"] == ordinary[edge["from_key"]]["identity"]
    assert payload["end_identity"] == ordinary[edge["to_key"]]["identity"]
    assert "lanes" not in payload and "section" not in payload
    assert {mode["mode"] for mode in payload["modes"]} == {"basic", "elevated", "bridge", "slope", "tunnel"}
    z0 = -0.3 if edge["from_key"].endswith("DEPRESSED") else 0.0
    z1 = 0.0 if z0 else -0.3
    for mode in payload["modes"]:
        assert {entry["kind"] for entry in mode["entries"]} == {"segment"}
        points = {tuple(round(v, 6) for v in entry["mesh"]["vertices"][i:i+3])
                  for entry in mode["entries"] for i in range(0, len(entry["mesh"]["vertices"]), 3)}
        # Blender's Z-up becomes Unity Y-up. Endpoint ordinates are independent
        # literals, not computed by the transition resolver under test.
        assert (-3.0, z0, -32.0) in points and (6.0, z1, 32.0) in points
assert json.loads(regular_manifest.read_text())["sentinel"] == "regular-road-library"
print("PLACEMENT_HEIGHT_TRANSITION_FIXTURE_OK endpoints_referenced segments_only")
activate("jp-3l-oneway")
builder.export_placement_choices(scene, (builder.RoadPlacementOptions(level="FLUSH"),), output)
# A full one-way menu must not retain all the newly generated preview objects.
# Existing placement meshes (and the ordinary preview) remain user-owned.
existing_objects = {obj.as_pointer() for obj in bpy.data.objects}
existing_meshes = {mesh.as_pointer() for mesh in bpy.data.meshes}
full_menu_root = ROOT / "build/smoke/placement-complete-menu"
full_path = builder.export_placement_choices(scene, None, full_menu_root)
full_menu = json.loads((full_menu_root / full_path).read_text())
assert len(full_menu["choices"]) == 18
assert len(full_menu["height_transitions"]) == 18
assert {c["key"].split("|")[1] for c in full_menu["choices"]} == {"CENTERED"}
assert {c["key"].split("|")[2] for c in full_menu["choices"]} == {"NONE"}
assert {obj.as_pointer() for obj in bpy.data.objects} == existing_objects
assert {mesh.as_pointer() for mesh in bpy.data.meshes} == existing_meshes
paths = {c["bundle_path"] for c in full_menu["choices"]}
packed = [ (full_menu_root / path).read_bytes() for path in paths ]
print("PLACEMENT_SNAPSHOT_BYTES", "compressed", sum(map(len, packed)),
      "json", sum(len(gzip.decompress(value)) for value in packed))
print("BLENDER_PLACEMENT_SMOKE_OK cases=5 modes=5 existing_preview_preserved")
