from __future__ import annotations

bl_info = {
    "name": "CS1 Road Builder",
    "author": "Local project",
    "version": (0, 6, 0),
    "blender": (5, 1, 0),
    "location": "3D View > Sidebar > Road",
    "description": "Edit and preview standalone Cities: Skylines 1 road modes",
    "category": "Object",
}

import json
import importlib
from dataclasses import dataclass
from pathlib import Path

import bmesh
import bpy
from bpy.props import BoolProperty, CollectionProperty, EnumProperty, FloatProperty, IntProperty, PointerProperty, StringProperty
from bpy.types import Operator, Panel, PropertyGroup, UIList
from bpy_extras.io_utils import ExportHelper, ImportHelper

# Blender's Reload Scripts reloads this package, but Python otherwise keeps
# package children cached. Reload the Blender-independent modules explicitly
# even on the first reload from an older add-on version.
from . import domain as _domain
from . import geometry_plan as _geometry_plan
from . import runtime_export as _runtime_export

importlib.reload(_domain)
importlib.reload(_geometry_plan)
importlib.reload(_runtime_export)

from .domain import (
    MODE_LENGTH,
    NODE_SLICES,
    SEGMENT_SLICES,
    cross_section_widths,
    expected_boundaries,
    strip_id,
)
from .geometry_plan import plan_main_girders
from .runtime_export import export_prop_bundle, export_runtime_bundle, geometry_fingerprint, safe_road_id


MODE_ITEMS = (
    ("basic", "Ground", "Ground road"),
    ("elevated", "Elevated", "Elevated road"),
    ("bridge", "Bridge", "Bridge road"),
    ("slope", "Tunnel Entrance", "Transition between ground and tunnel"),
    ("tunnel", "Tunnel", "Underground road"),
)
MODE_PREVIEW_SPACING = 16.0
SURFACE_PROFILE_ITEMS = (
    ("FLUSH", "Ground level", "Road surface is level with surrounding ground"),
    ("DEPRESSED", "Curb depth", "Road surface is lower by the curb height"),
)
NODE_TRANSITION_ITEMS = (
    ("MATCH", "No transition", "Keep the node at the segment surface level"),
    ("FLUSH", "To ground level", "Slope the node surface toward ground level"),
    ("DEPRESSED", "To curb depth", "Slope the node surface toward curb depth"),
)
LANE_ZONE_ITEMS = (
    ("ROAD", "Roadway", "Lane occupies the roadway cross-section"),
    ("LEFT_SIDEWALK", "Left sidewalk", "Network lane lies on the left sidewalk"),
    ("RIGHT_SIDEWALK", "Right sidewalk", "Network lane lies on the right sidewalk"),
)
DIRECTION_ITEMS = (
    ("FORWARD", "Forward", "CS1 forward direction"),
    ("BACKWARD", "Backward", "CS1 backward direction"),
    ("BOTH", "Both", "Both directions"),
)
LANE_TYPE_ITEMS = (
    ("VEHICLE", "Vehicle", "Vehicle lane"),
    ("PEDESTRIAN", "Pedestrian", "Pedestrian lane"),
    ("TRANSPORT_VEHICLE", "Transport vehicle", "Public transport lane"),
    ("PARKING", "Parking", "Parking lane"),
    ("NONE", "None", "Metadata or prop lane such as a median"),
)
VEHICLE_TYPE_ITEMS = (
    ("NONE", "None", "No vehicle type"),
    ("CAR", "Car", "Car-compatible traffic"),
    ("BICYCLE", "Bicycle", "Bicycle traffic"),
    ("BUS", "Bus", "Bus traffic"),
    ("TRAM", "Tram", "Tram traffic"),
    ("TRAIN", "Train", "Train traffic"),
    ("METRO", "Metro", "Metro traffic"),
    ("MONORAIL", "Monorail", "Monorail traffic"),
    ("TROLLEYBUS", "Trolleybus", "Trolleybus traffic"),
)
BOUNDARY_ROLE_ITEMS = (
    ("CURB", "Curb", "Boundary between sidewalk and the road body"),
    ("CARRIAGEWAY_EDGE", "Carriageway edge", "Boundary between carriageway and shoulder"),
    ("LANE_DIVIDER", "Lane divider", "Boundary between adjacent carriageway strips"),
)
MARKING_ROLE_ITEMS = (
    ("CARRIAGEWAY_EDGE", "Roadside line", "Carriageway edge marking"),
    ("CENTER_LINE", "Center line", "Boundary between opposing traffic"),
    ("LANE_SEPARATOR", "Lane separator", "Boundary between lanes in the same direction"),
)
MARKING_STYLE_ITEMS = (
    ("SOLID_WHITE", "Solid white", "Shared solid white marking style"),
)
MODE_COLORS = {
    "basic": (0.12, 0.14, 0.16, 1.0),
    "elevated": (0.20, 0.25, 0.30, 1.0),
    "bridge": (0.24, 0.30, 0.36, 1.0),
    "slope": (0.18, 0.22, 0.27, 1.0),
    "tunnel": (0.10, 0.12, 0.15, 1.0),
}
_AUTO_EXPORT_STATE = {}
DEFAULT_RUNTIME_OUTPUT = str(Path(__file__).resolve().parents[2] / "build" / "runtime-preview")
def _enum_value(value: str, available, fallback: str) -> str:
    normalized = value.upper().replace(" ", "_")
    compact = normalized.replace("_", "")
    for key, _, _ in available:
        if key.replace("_", "") == compact:
            return key
    return fallback


def _mode_collection(scene: bpy.types.Scene, mode: str) -> bpy.types.Collection:
    root = bpy.data.collections.get("CS1_ROAD")
    if root is None:
        root = bpy.data.collections.new("CS1_ROAD")
        scene.collection.children.link(root)
    name = f"CS1_ROAD_{mode}"
    collection = bpy.data.collections.get(name)
    if collection is None:
        collection = bpy.data.collections.new(name)
        root.children.link(collection)
    return collection


def _clear_collection(collection: bpy.types.Collection) -> None:
    for obj in list(collection.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def _material(mode: str) -> bpy.types.Material:
    name = f"CS1 Road {mode.title()}"
    material = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    material.diffuse_color = MODE_COLORS[mode]
    return material


def _marking_material(mode: str, paint_width: float, region_width: float) -> bpy.types.Material:
    name = f"CS1 Road {mode.title()} Marking Preview"
    material = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    nodes.clear()
    output = nodes.new("ShaderNodeOutputMaterial")
    shader = nodes.new("ShaderNodeBsdfPrincipled")
    texcoord = nodes.new("ShaderNodeTexCoord")
    separate = nodes.new("ShaderNodeSeparateXYZ")
    low = nodes.new("ShaderNodeMath")
    high = nodes.new("ShaderNodeMath")
    mask = nodes.new("ShaderNodeMath")
    mix = nodes.new("ShaderNodeMixRGB")
    low.operation, high.operation, mask.operation = "GREATER_THAN", "LESS_THAN", "MULTIPLY"
    half_ratio = min(0.49, paint_width / max(region_width, 0.001) * 0.5)
    low.inputs[1].default_value = 0.5 - half_ratio
    high.inputs[1].default_value = 0.5 + half_ratio
    mix.blend_type = "MIX"
    mix.inputs[1].default_value = MODE_COLORS[mode]
    mix.inputs[2].default_value = (0.92, 0.92, 0.88, 1.0)
    links.new(texcoord.outputs["UV"], separate.inputs[0])
    links.new(separate.outputs["X"], low.inputs[0])
    links.new(separate.outputs["X"], high.inputs[0])
    links.new(low.outputs[0], mask.inputs[0])
    links.new(high.outputs[0], mask.inputs[1])
    links.new(mask.outputs[0], mix.inputs[0])
    links.new(mix.outputs[0], shader.inputs["Base Color"])
    links.new(shader.outputs[0], output.inputs[0])
    return material


def _structure_material(mode: str) -> bpy.types.Material:
    name = f"CS1 Road {mode.title()} Structure Preview"
    material = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    material.diffuse_color = (0.34, 0.36, 0.38, 1.0)
    return material


def _mesh_object(
    collection, name, vertices, faces, face_kinds, face_uvs, materials,
    material_indices=None,
) -> bpy.types.Object:
    mesh = bpy.data.meshes.new(f"{name}_mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    uv_layer = mesh.uv_layers.new(name="RoadUV")
    mesh_y_min = min(vertex.co.y for vertex in mesh.vertices)
    mesh_y_max = max(vertex.co.y for vertex in mesh.vertices)
    for polygon, kind, explicit_uvs in zip(mesh.polygons, face_kinds, face_uvs):
        polygon.material_index = (material_indices or {}).get(kind, 0)
        if explicit_uvs is not None:
            for loop_index, uv in zip(polygon.loop_indices, explicit_uvs):
                uv_layer.data[loop_index].uv = uv
            continue
        coordinates = [mesh.vertices[index].co for index in polygon.vertices]
        ranges = {
            "x": (min(value.x for value in coordinates), max(value.x for value in coordinates)),
            "y": (min(value.y for value in coordinates), max(value.y for value in coordinates)),
            "z": (min(value.z for value in coordinates), max(value.z for value in coordinates)),
        }
        if ranges["x"][1] - ranges["x"][0] > 1e-8 and ranges["y"][1] - ranges["y"][0] > 1e-8:
            axes = ("x", "y")
        elif ranges["y"][1] - ranges["y"][0] > 1e-8:
            axes = ("y", "z")
        else:
            axes = ("x", "z")
        for loop_index in polygon.loop_indices:
            coordinate = mesh.vertices[mesh.loops[loop_index].vertex_index].co
            first, second = axes
            if axes == ("x", "y"):
                u = (coordinate.x - ranges["x"][0]) / max(ranges["x"][1] - ranges["x"][0], 1e-8)
                v = (coordinate.y - mesh_y_min) / max(mesh_y_max - mesh_y_min, 1e-8)
            elif axes == ("y", "z"):
                u = (coordinate.y - mesh_y_min) / max(mesh_y_max - mesh_y_min, 1e-8)
                v = (coordinate.z - ranges["z"][0]) / max(ranges["z"][1] - ranges["z"][0], 1e-8)
            else:
                u = (getattr(coordinate, first) - ranges[first][0]) / max(ranges[first][1] - ranges[first][0], 1e-8)
                v = (getattr(coordinate, second) - ranges[second][0]) / max(ranges[second][1] - ranges[second][0], 1e-8)
            uv_layer.data[loop_index].uv = (u, v)
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    for material in materials:
        obj.data.materials.append(material)
    return obj


class _SurfaceMesh:
    """Build one welded open render mesh with optional intentional seams."""

    def __init__(self) -> None:
        self.vertices = []
        self.faces = []
        self.face_kinds = []
        self.face_uvs = []
        self._vertex_map = {}

    def _vertex(self, coordinate, seam="") -> int:
        key = (seam, tuple(round(float(value), 6) for value in coordinate))
        if key not in self._vertex_map:
            self._vertex_map[key] = len(self.vertices)
            self.vertices.append(tuple(coordinate))
        return self._vertex_map[key]

    def polygon(self, points, seams=None, kind="surface", uvs=None) -> None:
        if len(points) < 3:
            return
        if seams is None:
            seams = ("",) * len(points)
        self.faces.append(tuple(self._vertex(point, seam) for point, seam in zip(points, seams)))
        self.face_kinds.append(kind)
        self.face_uvs.append(tuple(uvs) if uvs is not None else None)

    def quad(self, a, b, c, d, seams=("", "", "", ""), kind="surface", uvs=None) -> None:
        self.polygon((a, b, c, d), seams, kind, uvs)

    def horizontal_strip(
        self, x_min, x_max, y_min, y_max, start_z, end_z,
        slices=1, flip=False, kind="surface",
    ) -> None:
        for index in range(slices):
            t0, t1 = index / slices, (index + 1) / slices
            ya, yb = y_min + (y_max - y_min) * t0, y_min + (y_max - y_min) * t1
            za, zb = start_z + (end_z - start_z) * t0, start_z + (end_z - start_z) * t1
            points = ((x_min, ya, za), (x_max, ya, za), (x_max, yb, zb), (x_min, yb, zb))
            self.quad(*tuple(reversed(points)) if flip else points, kind=kind)

    def vertical_strip(
        self, x, y_min, y_max, start_bottom, end_bottom, start_top, end_top,
        flip=False, slices=1, kind="surface",
    ) -> None:
        for index in range(slices):
            t0, t1 = index / slices, (index + 1) / slices
            ya, yb = y_min + (y_max - y_min) * t0, y_min + (y_max - y_min) * t1
            ba, bb = start_bottom + (end_bottom - start_bottom) * t0, start_bottom + (end_bottom - start_bottom) * t1
            ta, tb = start_top + (end_top - start_top) * t0, start_top + (end_top - start_top) * t1
            points = ((x, ya, ba), (x, yb, bb), (x, yb, tb), (x, ya, ta))
            self.quad(*tuple(reversed(points)) if flip else points, kind=kind)

    def create(self, collection, name, materials, material_indices=None) -> bpy.types.Object:
        return _mesh_object(
            collection, name, self.vertices, self.faces, self.face_kinds,
            self.face_uvs, materials, material_indices,
        )


@dataclass(frozen=True)
class _ElevatedEdgePlan:
    polygons: tuple
    connector_x: float
    source_name: str
    mirrored: bool


def _mesh_object_poll(_self, obj) -> bool:
    return obj is None or obj.type == "MESH"


def _group_vertex_indices(source, group_name: str) -> set[int]:
    if not group_name.strip():
        return set()
    group = source.vertex_groups.get(group_name)
    if group is None:
        return set()
    return {
        vertex.index
        for vertex in source.data.vertices
        if any(item.group == group.index and item.weight > 0.0 for item in vertex.groups)
    }


def _segment_crosses_origin_xz(first, second, tolerance=1e-4) -> bool:
    dx, dz = second.x - first.x, second.z - first.z
    length_squared = dx * dx + dz * dz
    if length_squared <= 1e-16:
        return first.x * first.x + first.z * first.z <= tolerance * tolerance
    t = max(0.0, min(1.0, -(first.x * dx + first.z * dz) / length_squared))
    x, z = first.x + dx * t, first.z + dz * t
    return x * x + z * z <= tolerance * tolerance


def _prepare_elevated_edge_mesh(
    source, side: str, anchor_x: float, surface_z: float, deck_depth: float,
    slices: int, group_name: str,
) -> _ElevatedEdgePlan:
    if source is None or source.type != "MESH":
        raise ValueError(f"Elevated {side} edge must be a Mesh Object")

    no_split_vertices = _group_vertex_indices(source, group_name)
    selected_faces = {
        polygon.index for polygon in source.data.polygons
        if not any(index in no_split_vertices for index in polygon.vertices)
    }
    if not selected_faces:
        raise ValueError(f"{source.name}: no faces remain for longitudinal slicing")
    selected_vertices = {
        index
        for polygon in source.data.polygons if polygon.index in selected_faces
        for index in polygon.vertices
    }

    basis = source.matrix_world.to_3x3()
    mirrored = side == "left"
    coordinates = []
    for vertex in source.data.vertices:
        coordinate = basis @ vertex.co
        if abs(coordinate.x) <= 1e-4:
            coordinate.x = 0.0
        if abs(coordinate.z) <= 1e-4:
            coordinate.z = 0.0
        if mirrored:
            coordinate.x = -coordinate.x
        coordinates.append(coordinate)
    curve_coordinates = [coordinates[index] for index in selected_vertices]
    y_min = min(value.y for value in curve_coordinates)
    y_max = max(value.y for value in curve_coordinates)
    half_length = MODE_LENGTH * 0.5
    if abs(y_min + half_length) > 1e-4 or abs(y_max - half_length) > 1e-4:
        raise ValueError(
            f"{source.name}: sliced faces must span local Y "
            f"{-half_length:g}..{half_length:g} m (found {y_min:g}..{y_max:g})"
        )

    reference_bottom = min(value.z for value in curve_coordinates)
    if reference_bottom >= -1e-6:
        raise ValueError(
            f"{source.name}: sliced faces need geometry below local Z=0 "
            "for deck-depth scaling"
        )
    anchor_candidates = sorted(
        ((index, coordinates[index]) for index in selected_vertices),
        key=lambda item: item[1].x * item[1].x + item[1].z * item[1].z,
    )
    anchor_crossing = False
    for polygon in source.data.polygons:
        if polygon.index not in selected_faces:
            continue
        indices = tuple(polygon.vertices)
        for offset, first_index in enumerate(indices):
            second_index = indices[(offset + 1) % len(indices)]
            if _segment_crosses_origin_xz(coordinates[first_index], coordinates[second_index]):
                anchor_crossing = True
                break
        if anchor_crossing:
            break
    if not anchor_crossing:
        nearest_index, nearest = anchor_candidates[0]
        raise ValueError(
            f"{source.name}: sliced face edges must cross local X=0, Z=0 "
            f"to anchor the road corner; nearest vertex {nearest_index} is "
            f"X={nearest.x:.9g}, Y={nearest.y:.9g}, Z={nearest.z:.9g}; "
            f"checked {len(selected_vertices)} vertices from {len(selected_faces)} faces; "
            f"object location=({source.location.x:.9g},{source.location.y:.9g},{source.location.z:.9g}), "
            f"scale=({source.scale.x:.9g},{source.scale.y:.9g},{source.scale.z:.9g}), "
            f"no-split vertices={len(no_split_vertices)}"
        )

    bottom_indices = {
        index for index in selected_vertices
        if abs(coordinates[index].z - reference_bottom) <= 1e-4
    }
    bottom_x_values = [coordinates[index].x for index in bottom_indices]
    if max(bottom_x_values) - min(bottom_x_values) > 1e-4:
        raise ValueError(
            f"{source.name}: lowest sliced edge must have one local X position"
        )
    bottom_y_values = [coordinates[index].y for index in bottom_indices]
    if (
        abs(min(bottom_y_values) + half_length) > 1e-4
        or abs(max(bottom_y_values) - half_length) > 1e-4
    ):
        raise ValueError(f"{source.name}: lowest edge must span the full 64 m length")

    connector_local_x = sum(bottom_x_values) / len(bottom_x_values)
    connector_x = anchor_x + connector_local_x
    if side == "left" and connector_x < anchor_x - 1e-4:
        raise ValueError(f"{source.name}: left lowest edge must extend inward (+X) or vertically")
    if side == "right" and connector_x > anchor_x + 1e-4:
        raise ValueError(f"{source.name}: right lowest edge must extend inward (-X) or vertically")

    working = source.data.copy()
    depth_scale = deck_depth / -reference_bottom
    for vertex, coordinate in zip(working.vertices, coordinates):
        z = coordinate.z * depth_scale if coordinate.z < 0.0 else coordinate.z
        if vertex.index in bottom_indices:
            z = -deck_depth
        vertex.co = (coordinate.x + anchor_x, coordinate.y, z + surface_z)

    bm = bmesh.new()
    result_mesh = None
    try:
        bm.from_mesh(working)
        bm.faces.ensure_lookup_table()
        split_layer = bm.faces.layers.int.new("cs1_curve_split")
        for face in bm.faces:
            face[split_layer] = 1 if face.index in selected_faces else 0

        anchor_faces = [face for face in bm.faces if face.is_valid and face[split_layer] == 1]
        anchor_edges = {edge for face in anchor_faces for edge in face.edges}
        anchor_vertices = {vertex for face in anchor_faces for vertex in face.verts}
        bmesh.ops.bisect_plane(
            bm,
            geom=[*anchor_vertices, *anchor_edges, *anchor_faces],
            plane_co=(0.0, 0.0, surface_z),
            plane_no=(0.0, 0.0, 1.0),
            dist=1e-6,
            clear_inner=False,
            clear_outer=False,
        )

        for index in range(1, slices):
            plane_y = -half_length + MODE_LENGTH * index / slices
            split_faces = [face for face in bm.faces if face.is_valid and face[split_layer] == 1]
            split_edges = {edge for face in split_faces for edge in face.edges}
            split_vertices = {vertex for face in split_faces for vertex in face.verts}
            bmesh.ops.bisect_plane(
                bm,
                geom=[*split_vertices, *split_edges, *split_faces],
                plane_co=(0.0, plane_y, 0.0),
                plane_no=(0.0, 1.0, 0.0),
                dist=1e-6,
                clear_inner=False,
                clear_outer=False,
            )

        result_mesh = bpy.data.meshes.new(f"{source.name}_road_edge_work")
        bm.to_mesh(result_mesh)
        result_mesh.update()
        uv_layer = result_mesh.uv_layers.active
        polygons = []
        for polygon in result_mesh.polygons:
            points = tuple(tuple(result_mesh.vertices[index].co) for index in polygon.vertices)
            uvs = None
            if uv_layer is not None:
                uvs = tuple(tuple(uv_layer.data[index].uv) for index in polygon.loop_indices)
            if mirrored:
                points = tuple(reversed(points))
                if uvs is not None:
                    uvs = tuple(reversed(uvs))
            polygons.append((points, uvs))
        return _ElevatedEdgePlan(tuple(polygons), connector_x, source.name, mirrored)
    finally:
        bm.free()
        bpy.data.meshes.remove(working)
        if result_mesh is not None:
            bpy.data.meshes.remove(result_mesh)


def _append_elevated_edge(mesh, plan: _ElevatedEdgePlan) -> None:
    for points, uvs in plan.polygons:
        mesh.polygon(points, kind="structure", uvs=uvs)


def _marking_layout(props) -> tuple[list[float], list[float]]:
    _sync_boundaries(props)
    road_lanes = [lane for lane in props.lanes if lane.zone == "ROAD"]
    lane_width = sum(lane.width for lane in road_lanes)
    cursor = -lane_width * 0.5
    positions = {}
    if road_lanes:
        left_id = "boundary-left-carriageway" if props.shoulder_width > 1e-8 else "boundary-left-curb"
        positions[left_id] = cursor
    for left, right in zip(road_lanes, road_lanes[1:]):
        cursor += left.width
        positions[f"boundary-{left.lane_id}-{right.lane_id}"] = cursor
    if road_lanes:
        right_id = "boundary-right-carriageway" if props.shoulder_width > 1e-8 else "boundary-right-curb"
        positions[right_id] = lane_width * 0.5
    enabled_centers = [positions[item.boundary_id] for item in props.boundaries if item.marking_enabled and item.boundary_id in positions]
    edge_centers = [positions[key] for key in positions if key in {"boundary-left-carriageway", "boundary-left-curb", "boundary-right-carriageway", "boundary-right-curb"}]
    return enabled_centers, edge_centers


def _segment_boundaries(road_half, marking_centers, marking_region_width) -> list[float]:
    values = [-road_half, road_half]
    half_region = marking_region_width * 0.5
    for center in marking_centers:
        values.extend((max(-road_half, center - half_region), min(road_half, center + half_region)))
    return sorted({round(value, 9) for value in values})


def _node_boundaries(props, road_half, edge_centers) -> list[float]:
    values = [-road_half, 0.0, road_half]
    if props.node_shoulder_bands:
        values.extend(edge_centers)
    return sorted({round(value, 9) for value in values})


def _cross_section_uv_row(t, road_half, total_half, road_start, road_end, sidewalk_start, sidewalk_end):
    road_z = road_start + (road_end - road_start) * t
    sidewalk_z = sidewalk_start + (sidewalk_end - sidewalk_start) * t
    sidewalk_width = total_half - road_half
    rise = abs(sidewalk_z - road_z)
    unfolded_width = 2.0 * sidewalk_width + 2.0 * rise + 2.0 * road_half
    left_top = sidewalk_width / unfolded_width
    left_bottom = (sidewalk_width + rise) / unfolded_width
    right_bottom = (sidewalk_width + rise + 2.0 * road_half) / unfolded_width
    right_top = (sidewalk_width + 2.0 * rise + 2.0 * road_half) / unfolded_width
    return {
        "road_z": road_z,
        "sidewalk_z": sidewalk_z,
        "left_top": left_top,
        "left_bottom": left_bottom,
        "right_bottom": right_bottom,
        "right_top": right_top,
        "road_u": lambda x: (sidewalk_width + rise + x + road_half) / unfolded_width,
    }


def _add_road_strips(
    mesh, boundaries, marking_centers, marking_width, road_half, total_half, y_min, y_max,
    road_start, road_end, sidewalk_start, sidewalk_end, split_center, slices,
) -> None:
    for x_min, x_max in zip(boundaries, boundaries[1:]):
        if x_max - x_min < 1e-8:
            continue
        for index in range(slices):
            t0, t1 = index / slices, (index + 1) / slices
            ya, yb = y_min + (y_max - y_min) * t0, y_min + (y_max - y_min) * t1
            row0 = _cross_section_uv_row(t0, road_half, total_half, road_start, road_end, sidewalk_start, sidewalk_end)
            row1 = _cross_section_uv_row(t1, road_half, total_half, road_start, road_end, sidewalk_start, sidewalk_end)
            za, zb = row0["road_z"], row1["road_z"]
            seams = ["", "", "", ""]
            if split_center and abs(x_max) < 1e-8:
                seams[1] = seams[2] = "center_left"
            if split_center and abs(x_min) < 1e-8:
                seams[0] = seams[3] = "center_right"
            midpoint = (x_min + x_max) * 0.5
            kind = "marking" if any(abs(midpoint - center) <= marking_width * 0.5 + 1e-8 for center in marking_centers) else "surface"
            if kind == "marking":
                uvs = ((0.0, t0), (1.0, t0), (1.0, t1), (0.0, t1))
            else:
                uvs = (
                    (row0["road_u"](x_min), t0), (row0["road_u"](x_max), t0),
                    (row1["road_u"](x_max), t1), (row1["road_u"](x_min), t1),
                )
            mesh.quad((x_min, ya, za), (x_max, ya, za), (x_max, yb, zb), (x_min, yb, zb), seams, kind, uvs)


def _add_cross_section_top(
    mesh, boundaries, marking_centers, marking_width, road_half, total_half, y_min, y_max,
    road_start, road_end, sidewalk_start, sidewalk_end, split_center, slices,
) -> None:
    _add_road_strips(
        mesh, boundaries, marking_centers, marking_width, road_half, total_half, y_min, y_max,
        road_start, road_end, sidewalk_start, sidewalk_end, split_center, slices,
    )
    for index in range(slices):
        t0, t1 = index / slices, (index + 1) / slices
        ya, yb = y_min + (y_max - y_min) * t0, y_min + (y_max - y_min) * t1
        row0 = _cross_section_uv_row(t0, road_half, total_half, road_start, road_end, sidewalk_start, sidewalk_end)
        row1 = _cross_section_uv_row(t1, road_half, total_half, road_start, road_end, sidewalk_start, sidewalk_end)
        mesh.quad(
            (-total_half, ya, row0["sidewalk_z"]), (-road_half, ya, row0["sidewalk_z"]),
            (-road_half, yb, row1["sidewalk_z"]), (-total_half, yb, row1["sidewalk_z"]),
            uvs=((0.0, t0), (row0["left_top"], t0), (row1["left_top"], t1), (0.0, t1)),
        )
        mesh.quad(
            (road_half, ya, row0["sidewalk_z"]), (total_half, ya, row0["sidewalk_z"]),
            (total_half, yb, row1["sidewalk_z"]), (road_half, yb, row1["sidewalk_z"]),
            uvs=((row0["right_top"], t0), (1.0, t0), (1.0, t1), (row1["right_top"], t1)),
        )
        if row0["road_z"] != row0["sidewalk_z"] or row1["road_z"] != row1["sidewalk_z"]:
            mesh.quad(
                (-road_half, ya, row0["road_z"]), (-road_half, yb, row1["road_z"]),
                (-road_half, yb, row1["sidewalk_z"]), (-road_half, ya, row0["sidewalk_z"]),
                uvs=(
                    (row0["left_bottom"], t0), (row1["left_bottom"], t1),
                    (row1["left_top"], t1), (row0["left_top"], t0),
                ),
            )
            mesh.quad(
                (road_half, ya, row0["sidewalk_z"]), (road_half, yb, row1["sidewalk_z"]),
                (road_half, yb, row1["road_z"]), (road_half, ya, row0["road_z"]),
                uvs=(
                    (row0["right_top"], t0), (row1["right_top"], t1),
                    (row1["right_bottom"], t1), (row0["right_bottom"], t0),
                ),
            )


def _add_structure_bottom_region(
    mesh, x_min, x_max, half_width, y_min, y_max, start_z, end_z, slices,
) -> None:
    for index in range(slices):
        t0, t1 = index / slices, (index + 1) / slices
        ya, yb = y_min + (y_max - y_min) * t0, y_min + (y_max - y_min) * t1
        za, zb = start_z + (end_z - start_z) * t0, start_z + (end_z - start_z) * t1
        u0 = (x_min + half_width) / (2.0 * half_width)
        u1 = (x_max + half_width) / (2.0 * half_width)
        mesh.quad(
            (x_min, yb, zb), (x_max, yb, zb),
            (x_max, ya, za), (x_min, ya, za),
            kind="structure",
            uvs=((u0, t1), (u1, t1), (u1, t0), (u0, t0)),
        )


def _girder_profile(layout, center_x, top_z):
    half_width = layout.width * 0.5
    bottom_z = top_z - layout.depth
    return (
        (center_x - half_width, top_z),
        (center_x - half_width, bottom_z),
        (center_x + half_width, bottom_z),
        (center_x + half_width, top_z),
    )


def _add_open_profile_extrusion(
    mesh, start_profile, end_profile, y_min, y_max, slices,
) -> None:
    edge_lengths = [
        ((b[0] - a[0]) ** 2 + (b[1] - a[1]) ** 2) ** 0.5
        for a, b in zip(start_profile, start_profile[1:])
    ]
    perimeter = max(sum(edge_lengths), 1e-8)
    u_values = [0.0]
    for length in edge_lengths:
        u_values.append(u_values[-1] + length / perimeter)

    for edge_index in range(len(start_profile) - 1):
        for slice_index in range(slices):
            t0, t1 = slice_index / slices, (slice_index + 1) / slices
            ya = y_min + (y_max - y_min) * t0
            yb = y_min + (y_max - y_min) * t1
            start_a, start_b = start_profile[edge_index], start_profile[edge_index + 1]
            end_a, end_b = end_profile[edge_index], end_profile[edge_index + 1]
            a0 = (start_a[0], ya, start_a[1] + (end_a[1] - start_a[1]) * t0)
            a1 = (start_a[0], yb, start_a[1] + (end_a[1] - start_a[1]) * t1)
            b1 = (start_b[0], yb, start_b[1] + (end_b[1] - start_b[1]) * t1)
            b0 = (start_b[0], ya, start_b[1] + (end_b[1] - start_b[1]) * t0)
            u0, u1 = u_values[edge_index], u_values[edge_index + 1]
            mesh.quad(
                a0, a1, b1, b0,
                kind="structure",
                uvs=((u0, t0), (u0, t1), (u1, t1), (u1, t0)),
            )


def _add_deck_structure(
    mesh, half_width, y_min, y_max, start_z, end_z, depth, slices,
    girder_layout=None, bottom_left=None, bottom_right=None,
    include_left_fascia=True, include_right_fascia=True,
) -> None:
    underside_start = start_z - depth
    underside_end = end_z - depth
    bottom_left = -half_width if bottom_left is None else bottom_left
    bottom_right = half_width if bottom_right is None else bottom_right
    if bottom_left >= bottom_right:
        raise ValueError("Elevated edge mesh lowest edges leave no deck underside width")
    cutouts = []
    if girder_layout is not None:
        cutouts = [
            (
                max(bottom_left, center - girder_layout.width * 0.5),
                min(bottom_right, center + girder_layout.width * 0.5),
            )
            for center in girder_layout.centers
            if (
                center + girder_layout.width * 0.5 > bottom_left
                and center - girder_layout.width * 0.5 < bottom_right
            )
        ]

    cursor = bottom_left
    for cutout_start, cutout_end in cutouts:
        if cutout_start > cursor + 1e-8:
            _add_structure_bottom_region(
                mesh, cursor, cutout_start, half_width, y_min, y_max,
                underside_start, underside_end, slices,
            )
        cursor = max(cursor, cutout_end)
    if cursor < bottom_right - 1e-8:
        _add_structure_bottom_region(
            mesh, cursor, bottom_right, half_width, y_min, y_max,
            underside_start, underside_end, slices,
        )

    if include_left_fascia:
        mesh.vertical_strip(
            -half_width, y_min, y_max, underside_start, underside_end,
            start_z, end_z, flip=True, slices=slices, kind="structure",
        )
    if include_right_fascia:
        mesh.vertical_strip(
            half_width, y_min, y_max, underside_start, underside_end,
            start_z, end_z, slices=slices, kind="structure",
        )

    if girder_layout is not None:
        for center in girder_layout.centers:
            _add_open_profile_extrusion(
                mesh,
                _girder_profile(girder_layout, center, underside_start),
                _girder_profile(girder_layout, center, underside_end),
                y_min,
                y_max,
                slices,
            )


def _add_tunnel_envelope(mesh, half_width, y_min, y_max, side_z, roof_z, slices) -> None:
    mesh.vertical_strip(-half_width, y_min, y_max, side_z, side_z, roof_z, roof_z, slices=slices, kind="structure")
    mesh.vertical_strip(half_width, y_min, y_max, side_z, side_z, roof_z, roof_z, flip=True, slices=slices, kind="structure")
    mesh.horizontal_strip(-half_width, half_width, y_min, y_max, roof_z, roof_z, slices, flip=True, kind="structure")


def _add_default_lanes(props) -> None:
    defaults = (
        ("lane-left-sidewalk", "Left sidewalk", "LEFT_SIDEWALK", 2.0, "BOTH", "PEDESTRIAN", "NONE", 0.1),
        ("lane-backward-1", "Backward 1", "ROAD", 3.0, "BACKWARD", "VEHICLE", "CAR", 1.0),
        ("lane-forward-1", "Forward 1", "ROAD", 3.0, "FORWARD", "VEHICLE", "CAR", 1.0),
        ("lane-right-sidewalk", "Right sidewalk", "RIGHT_SIDEWALK", 2.0, "BOTH", "PEDESTRIAN", "NONE", 0.1),
    )
    for lane_id, name, zone, width, direction, lane_type, vehicle_type, speed in defaults:
        lane = props.lanes.add()
        lane.lane_id = lane_id
        lane.name, lane.zone, lane.width = name, zone, width
        lane.direction, lane.lane_type, lane.vehicle_type = direction, lane_type, vehicle_type
        lane.speed_limit = speed
        lane.vertical_offset = (
            props.curb_height
            if zone in {"LEFT_SIDEWALK", "RIGHT_SIDEWALK"}
            and props.surface_profile == "DEPRESSED"
            else 0.0
        )


def _ensure_default_lanes(props) -> None:
    if not props.lanes:
        _add_default_lanes(props)
    _ensure_lane_ids(props)
    _sync_boundaries(props)


def _ensure_lane_ids(props) -> None:
    used = set()
    next_id = max(1, props.next_lane_id)
    for lane in props.lanes:
        lane_id = lane.lane_id.strip()
        if not lane_id or lane_id in used:
            while f"lane-{next_id}" in used:
                next_id += 1
            lane_id = f"lane-{next_id}"
            next_id += 1
            lane.lane_id = lane_id
        used.add(lane_id)
    props.next_lane_id = next_id


def _strip_id(lane) -> str:
    return strip_id(lane)


def _expected_boundaries(props):
    return expected_boundaries(props.lanes, props.shoulder_width)


def _sync_boundaries(props, legacy_edge_lines=None, legacy_lane_lines=None) -> None:
    _ensure_lane_ids(props)
    previous = {
        item.boundary_id: {
            "boundary_role": item.role,
            "enabled": item.marking_enabled,
            "role": item.marking_role,
            "style": item.marking_style,
        }
        for item in props.boundaries
    }
    props.boundaries.clear()
    for boundary_id, name, role, left_strip, right_strip, default_enabled, default_marking_role in _expected_boundaries(props):
        item = props.boundaries.add()
        item.boundary_id, item.name = boundary_id, name
        item.left_strip_id, item.right_strip_id = left_strip, right_strip
        saved = previous.get(boundary_id)
        item.role = saved["boundary_role"] if saved else role
        item.marking_enabled = saved["enabled"] if saved else default_enabled
        item.marking_role = saved["role"] if saved else default_marking_role
        item.marking_style = saved["style"] if saved else "SOLID_WHITE"
        if legacy_edge_lines is not None and item.marking_role == "CARRIAGEWAY_EDGE":
            item.marking_enabled = bool(legacy_edge_lines)
        if legacy_lane_lines is not None and item.role == "LANE_DIVIDER":
            item.marking_enabled = bool(legacy_lane_lines)


def _initialize_scene_lanes():
    """Initialize defaults after Blender leaves its restricted register context."""
    try:
        scenes = list(bpy.data.scenes)
    except AttributeError:
        return 0.1
    for scene in scenes:
        _ensure_default_lanes(scene.cs1_road_builder)
    return None


def _cross_section(props) -> tuple[float, float, float]:
    return cross_section_widths(
        props.lanes, props.shoulder_width, props.sidewalk_width
    )


def _lane_positions(props):
    roadway_lanes = [lane for lane in props.lanes if lane.zone == "ROAD"]
    cursor = -sum(lane.width for lane in roadway_lanes) * 0.5
    positions = {}
    for lane in roadway_lanes:
        positions[lane.as_pointer()] = cursor + lane.width * 0.5
        cursor += lane.width
    roadway_width, _, total_half = _cross_section(props)
    for zone, start in (("LEFT_SIDEWALK", -total_half), ("RIGHT_SIDEWALK", roadway_width * 0.5)):
        cursor = start
        for lane in (item for item in props.lanes if item.zone == zone):
            positions[lane.as_pointer()] = cursor + lane.width * 0.5
            cursor += lane.width
    return [(lane, positions.get(lane.as_pointer(), 0.0)) for lane in props.lanes]


def build_mode(scene: bpy.types.Scene, mode: str) -> list[bpy.types.Object]:
    props = scene.cs1_road_builder
    roadway_width, _, total_half = _cross_section(props)
    custom_edge_segments = None
    custom_edge_nodes = None
    if mode == "elevated":
        edge_source = props.elevated_edge_mesh
        if edge_source is not None:
            side_z = 0.0
            group_name = props.elevated_edge_no_split_group
            half_width, surface_z, depth = total_half, side_z, props.deck_depth
            custom_edge_segments = (
                _prepare_elevated_edge_mesh(
                    edge_source, "left", -half_width, surface_z, depth,
                    SEGMENT_SLICES, group_name,
                ),
                _prepare_elevated_edge_mesh(
                    edge_source, "right", half_width, surface_z, depth,
                    SEGMENT_SLICES, group_name,
                ),
            )
            custom_edge_nodes = (
                _prepare_elevated_edge_mesh(
                    edge_source, "left", -half_width, surface_z, depth,
                    NODE_SLICES, group_name,
                ),
                _prepare_elevated_edge_mesh(
                    edge_source, "right", half_width, surface_z, depth,
                    NODE_SLICES, group_name,
                ),
            )
            if custom_edge_segments[0].connector_x >= custom_edge_segments[1].connector_x:
                raise ValueError("Elevated edge mesh lowest edges cross at the deck underside")

    collection = _mode_collection(scene, mode)
    _clear_collection(collection)
    material = _material(mode)
    marking_material = _marking_material(mode, props.marking_paint_width, props.marking_region_width)
    structure_material = _structure_material(mode)
    road_half = roadway_width * 0.5
    segment_markings, edge_centers = _marking_layout(props)
    segment_boundaries = _segment_boundaries(road_half, segment_markings, props.marking_region_width)
    node_boundaries = _node_boundaries(props, road_half, edge_centers)
    node_markings = []
    curb_rise = props.curb_height if props.surface_profile == "DEPRESSED" else 0.0
    props.half_width = total_half
    y_min, y_max = -MODE_LENGTH * 0.5, MODE_LENGTH * 0.5
    segment_mesh, node_mesh = _SurfaceMesh(), _SurfaceMesh()
    girder_layout = None

    if mode == "basic":
        segment_z = 0.0 if props.surface_profile == "FLUSH" else -props.curb_height
        target = props.surface_profile if props.node_transition_target == "MATCH" else props.node_transition_target
        far_z = 0.0 if target == "FLUSH" else -props.curb_height
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, segment_z, segment_z, 0.0, 0.0, False, SEGMENT_SLICES)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, segment_z, far_z, 0.0, 0.0, True, NODE_SLICES)
    elif mode == "elevated":
        road_z, side_z = -curb_rise, 0.0
        girder_layout = plan_main_girders(total_half * 2.0)
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, False, SEGMENT_SLICES)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, True, NODE_SLICES)
        if custom_edge_segments is None:
            _add_deck_structure(segment_mesh, total_half, y_min, y_max, side_z, side_z, props.deck_depth, SEGMENT_SLICES, girder_layout)
            _add_deck_structure(node_mesh, total_half, y_min, y_max, side_z, side_z, props.deck_depth, NODE_SLICES)
        else:
            left_segment, right_segment = custom_edge_segments
            left_node, right_node = custom_edge_nodes
            _add_deck_structure(
                segment_mesh, total_half, y_min, y_max, side_z, side_z,
                props.deck_depth, SEGMENT_SLICES, girder_layout,
                left_segment.connector_x, right_segment.connector_x, False, False,
            )
            _add_deck_structure(
                node_mesh, total_half, y_min, y_max, side_z, side_z,
                props.deck_depth, NODE_SLICES, None,
                left_node.connector_x, right_node.connector_x, False, False,
            )
            _append_elevated_edge(segment_mesh, left_segment)
            _append_elevated_edge(segment_mesh, right_segment)
            _append_elevated_edge(node_mesh, left_node)
            _append_elevated_edge(node_mesh, right_node)
    elif mode == "bridge":
        road_z, side_z = -curb_rise, 0.0
        girder_layout = plan_main_girders(total_half * 2.0)
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, False, SEGMENT_SLICES)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, True, NODE_SLICES)
        _add_deck_structure(segment_mesh, total_half, y_min, y_max, side_z, side_z, props.bridge_deck_depth, SEGMENT_SLICES, girder_layout)
        _add_deck_structure(node_mesh, total_half, y_min, y_max, side_z, side_z, props.bridge_deck_depth, NODE_SLICES)
    elif mode == "slope":
        road_start = road_end = -curb_rise
        side_start = side_end = 0.0
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_start, road_end, side_start, side_end, False, SEGMENT_SLICES)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_end, road_end, side_end, side_end, True, NODE_SLICES)
        _add_deck_structure(segment_mesh, total_half, y_min, y_max, side_start, side_end, props.deck_depth, SEGMENT_SLICES)
        _add_deck_structure(node_mesh, total_half, y_min, y_max, side_end, side_end, props.deck_depth, NODE_SLICES)
    else:
        road_z = -curb_rise
        side_z, roof_z = 0.0, road_z + props.tunnel_clearance
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, False, SEGMENT_SLICES)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, True, NODE_SLICES)
        _add_tunnel_envelope(segment_mesh, total_half, y_min, y_max, side_z, roof_z, SEGMENT_SLICES)
        _add_tunnel_envelope(node_mesh, total_half, y_min, y_max, side_z, roof_z, NODE_SLICES)

    segment_materials = (material, marking_material)
    segment_indices = {"marking": 1}
    node_materials = (material,)
    node_indices = {}
    if mode != "basic":
        segment_materials += (structure_material,)
        segment_indices["structure"] = 2
        node_materials += (structure_material,)
        node_indices["structure"] = 1
    segment = segment_mesh.create(
        collection, f"{mode}_segment", segment_materials, segment_indices,
    )
    node = node_mesh.create(
        collection, f"{mode}_node", node_materials, node_indices,
    )
    mode_index = next(index for index, item in enumerate(MODE_ITEMS) if item[0] == mode)
    preview_z = mode_index * MODE_PREVIEW_SPACING
    segment.location = (0.0, -MODE_LENGTH * 0.5, preview_z)
    node.location = (0.0, MODE_LENGTH * 0.5, preview_z)
    for obj, part in ((segment, "segment"), (node, "node")):
        obj["cs1_road_mode"], obj["cs1_road_name"], obj["cs1_road_part"] = mode, props.road_name, part
        obj["cs1_local_length"] = MODE_LENGTH
        obj["cs1_longitudinal_slices"] = SEGMENT_SLICES if part == "segment" else NODE_SLICES
    node["cs1_center_split"] = True
    if girder_layout is not None:
        segment["cs1_geometry_groups"] = (
            "surface,deck,custom_edge,girder"
            if custom_edge_segments is not None
            else "surface,deck,fascia,girder"
        )
        segment["cs1_girder_count"] = girder_layout.count
        segment["cs1_girder_centers"] = list(girder_layout.centers)
        segment["cs1_girder_spacing"] = girder_layout.spacing
        segment["cs1_girder_outer_offset"] = girder_layout.outer_offset
        segment["cs1_girder_reference_span"] = girder_layout.reference_span
        segment["cs1_girder_standard"] = girder_layout.standard_name
    if custom_edge_segments is not None:
        segment["cs1_elevated_edge_source"] = custom_edge_segments[0].source_name
        segment["cs1_elevated_left_edge_mirrored"] = custom_edge_segments[0].mirrored
        segment["cs1_elevated_edge_no_split_group"] = props.elevated_edge_no_split_group
        node["cs1_elevated_edge_source"] = custom_edge_nodes[0].source_name
        node["cs1_elevated_left_edge_mirrored"] = custom_edge_nodes[0].mirrored
    return [segment, node]


class CS1RoadLane(PropertyGroup):
    lane_id: StringProperty(name="Lane ID")
    zone: EnumProperty(name="Zone", items=LANE_ZONE_ITEMS, default="ROAD")
    width: FloatProperty(name="Width", default=3.0, min=0.05, max=16.0, unit="LENGTH")
    direction: EnumProperty(name="Direction", items=DIRECTION_ITEMS, default="FORWARD")
    lane_type: EnumProperty(name="Lane type", items=LANE_TYPE_ITEMS, default="VEHICLE")
    vehicle_type: EnumProperty(name="Vehicle type", items=VEHICLE_TYPE_ITEMS, default="CAR")
    speed_limit: FloatProperty(name="Speed limit", default=1.0, min=0.0, max=10.0)
    vertical_offset: FloatProperty(name="Vertical offset", default=0.0, min=-16.0, max=16.0, unit="LENGTH")
    stop_offset: FloatProperty(name="Stop offset", default=0.0, min=-32.0, max=32.0, unit="LENGTH")
    allow_connect: BoolProperty(name="Allow connect", default=True)


class CS1RoadBoundary(PropertyGroup):
    boundary_id: StringProperty(name="Boundary ID")
    left_strip_id: StringProperty(name="Left strip ID")
    right_strip_id: StringProperty(name="Right strip ID")
    role: EnumProperty(name="Boundary role", items=BOUNDARY_ROLE_ITEMS, default="LANE_DIVIDER")
    marking_enabled: BoolProperty(name="Marking", default=False)
    marking_role: EnumProperty(name="Marking role", items=MARKING_ROLE_ITEMS, default="LANE_SEPARATOR")
    marking_style: EnumProperty(name="Marking style", items=MARKING_STYLE_ITEMS, default="SOLID_WHITE")


class CS1RoadBuilderProperties(PropertyGroup):
    road_name: StringProperty(name="Road name", default="Example Road")
    mode: EnumProperty(name="Mode", items=MODE_ITEMS, default="basic")
    lanes: CollectionProperty(type=CS1RoadLane)
    active_lane_index: IntProperty(default=0, min=0)
    next_lane_id: IntProperty(default=1, min=1)
    boundaries: CollectionProperty(type=CS1RoadBoundary)
    active_boundary_index: IntProperty(default=0, min=0)
    half_width: FloatProperty(name="Half width", default=5.75, min=0.01, max=128.0, unit="LENGTH")
    shoulder_width: FloatProperty(name="Shoulder width", default=0.5, min=0.0, max=8.0, unit="LENGTH")
    sidewalk_width: FloatProperty(name="Sidewalk width", default=2.5, min=0.0, max=16.0, unit="LENGTH")
    curb_height: FloatProperty(name="Curb height", default=0.15, min=0.0, max=1.0, unit="LENGTH")
    edge_lines: BoolProperty(name="Legacy roadside lines", default=True, options={"HIDDEN"})
    lane_lines: BoolProperty(name="Legacy lane divider lines", default=True, options={"HIDDEN"})
    marking_paint_width: FloatProperty(name="Paint width", default=0.15, min=0.05, max=0.5, unit="LENGTH")
    marking_region_width: FloatProperty(name="Marking region", default=0.4, min=0.2, max=1.0, unit="LENGTH")
    node_shoulder_bands: BoolProperty(name="Node shoulder bands", default=False)
    surface_profile: EnumProperty(name="Segment surface", items=SURFACE_PROFILE_ITEMS, default="DEPRESSED")
    node_transition_target: EnumProperty(name="Transition target", items=NODE_TRANSITION_ITEMS, default="MATCH")
    elevated_height: FloatProperty(name="Elevated height", default=8.0, min=1.0, max=64.0, unit="LENGTH")
    bridge_height: FloatProperty(name="Bridge height", default=12.0, min=1.0, max=128.0, unit="LENGTH")
    deck_depth: FloatProperty(name="Elevated deck depth", default=1.0, min=0.1, max=8.0, unit="LENGTH")
    elevated_edge_mesh: PointerProperty(
        name="Edge mesh (right basis)", type=bpy.types.Object, poll=_mesh_object_poll,
    )
    elevated_edge_no_split_group: StringProperty(name="No-split group", default="CS1_NO_SPLIT")
    bridge_deck_depth: FloatProperty(name="Bridge deck depth", default=1.5, min=0.1, max=12.0, unit="LENGTH")
    tunnel_depth: FloatProperty(name="Tunnel depth", default=12.0, min=1.0, max=64.0, unit="LENGTH")
    tunnel_clearance: FloatProperty(name="Tunnel clearance", default=5.0, min=2.0, max=16.0, unit="LENGTH")
    hide_other_modes: BoolProperty(name="Hide other modes", default=True)
    runtime_road_id: StringProperty(name="Runtime road ID", default="example-road")
    runtime_prefab_name: StringProperty(name="Runtime prefab name", default="Road Runtime Example")
    runtime_template_name: StringProperty(name="Template prefab", default="Basic Road")
    runtime_output_dir: StringProperty(
        name="Runtime output", default=DEFAULT_RUNTIME_OUTPUT, subtype="DIR_PATH",
    )
    runtime_auto_export: BoolProperty(name="Auto export every second", default=False)
    runtime_prop_id: StringProperty(name="Runtime Prop ID", default="example-decal")
    runtime_prop_shader: StringProperty(name="Prop shader", default="Custom/Props/Decal/Blend")


class CS1ROAD_UL_lanes(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_propname, index):
        if self.layout_type in {"DEFAULT", "COMPACT"}:
            row = layout.row(align=True)
            row.label(text=str(index + 1))
            row.prop(item, "direction", text="")
            row.prop(item, "lane_type", text="")
            row.prop(item, "width", text="")
        else:
            layout.label(text=str(index + 1))


class CS1ROAD_UL_boundaries(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_propname, index):
        if self.layout_type in {"DEFAULT", "COMPACT"}:
            row = layout.row(align=True)
            row.prop(item, "marking_enabled", text="")
            row.label(text=item.name or item.boundary_id)
            row.label(text=item.role.replace("_", " ").title())
        else:
            layout.label(text=item.name or item.boundary_id)


class CS1ROAD_OT_lane_add(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lane_add", "Add lane", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = context.scene.cs1_road_builder
        lane = props.lanes.add()
        _ensure_lane_ids(props)
        lane.name = f"Lane {len(props.lanes)}"
        props.active_lane_index = len(props.lanes) - 1
        _sync_boundaries(props)
        return {"FINISHED"}


class CS1ROAD_OT_lanes_reset(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lanes_reset", "Create default lanes", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = context.scene.cs1_road_builder
        props.lanes.clear()
        _add_default_lanes(props)
        props.active_lane_index = 0
        _sync_boundaries(props)
        return {"FINISHED"}


class CS1ROAD_OT_lane_remove(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lane_remove", "Remove lane", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = context.scene.cs1_road_builder
        if props.lanes:
            props.lanes.remove(min(props.active_lane_index, len(props.lanes) - 1))
            props.active_lane_index = max(0, min(props.active_lane_index, len(props.lanes) - 1))
            _sync_boundaries(props)
        return {"FINISHED"}


class CS1ROAD_OT_lane_move(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lane_move", "Move lane", {"REGISTER", "UNDO"}
    direction: EnumProperty(items=(("UP", "Up", ""), ("DOWN", "Down", "")))

    def execute(self, context):
        props = context.scene.cs1_road_builder
        source = props.active_lane_index
        target = source - 1 if self.direction == "UP" else source + 1
        if 0 <= source < len(props.lanes) and 0 <= target < len(props.lanes):
            props.lanes.move(source, target)
            props.active_lane_index = target
            _sync_boundaries(props)
        return {"FINISHED"}


class CS1ROAD_OT_boundaries_sync(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.boundaries_sync", "Synchronize boundaries", {"REGISTER", "UNDO"}

    def execute(self, context):
        _sync_boundaries(context.scene.cs1_road_builder)
        return {"FINISHED"}


def _show_only_mode(active_mode: str) -> None:
    for mode, _, _ in MODE_ITEMS:
        collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
        if collection is not None:
            collection.hide_viewport = collection.hide_render = mode != active_mode


class CS1ROAD_OT_build_preview(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.build_preview", "Build active mode", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = context.scene.cs1_road_builder
        try:
            build_mode(context.scene, props.mode)
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        if props.hide_other_modes:
            _show_only_mode(props.mode)
        return {"FINISHED"}


class CS1ROAD_OT_build_all(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.build_all", "Build all modes", {"REGISTER", "UNDO"}

    def execute(self, context):
        active = context.scene.cs1_road_builder.mode
        try:
            for mode, _, _ in MODE_ITEMS:
                build_mode(context.scene, mode)
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        _show_only_mode(active)
        return {"FINISHED"}


def _runtime_lanes(props):
    return [
        {
            "lane_id": lane.lane_id,
            "position": position,
            "width": lane.width,
            "vertical_offset": lane.vertical_offset,
            "stop_offset": lane.stop_offset,
            "speed_limit": lane.speed_limit,
            "direction": lane.direction,
            "lane_type": lane.lane_type,
            "vehicle_type": lane.vehicle_type,
            "allow_connect": lane.allow_connect,
        }
        for lane, position in _lane_positions(props)
    ]


def _runtime_mode_objects():
    result = {}
    for mode, _, _ in MODE_ITEMS:
        collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
        if collection is None:
            continue
        by_part = {
            obj.get("cs1_road_part"): obj
            for obj in collection.objects
            if obj.type == "MESH" and obj.get("cs1_road_mode") == mode
        }
        if "segment" in by_part and "node" in by_part:
            result[mode] = [by_part["segment"], by_part["node"]]
    return result


def _runtime_output_path(props) -> Path:
    return Path(bpy.path.abspath(props.runtime_output_dir)).resolve()


def _export_runtime_scene(scene):
    props = scene.cs1_road_builder
    _ensure_lane_ids(props)
    _, _, half_width = _cross_section(props)
    modes = _runtime_mode_objects()
    if not modes:
        raise ValueError("Build at least one road mode before runtime export")
    return export_runtime_bundle(
        _runtime_output_path(props),
        safe_road_id(props.runtime_road_id),
        props.runtime_prefab_name.strip() or props.road_name,
        props.runtime_template_name.strip() or "Basic Road",
        half_width,
        props.sidewalk_width,
        _runtime_lanes(props),
        modes,
    )


def _authoring_fingerprint(props):
    lanes = tuple(
        (
            lane.lane_id, lane.zone, lane.width, lane.direction, lane.lane_type,
            lane.vehicle_type, lane.speed_limit, lane.vertical_offset,
            lane.stop_offset, lane.allow_connect,
        )
        for lane in props.lanes
    )
    boundaries = tuple(
        (
            item.boundary_id, item.role, item.marking_enabled,
            item.marking_role, item.marking_style,
        )
        for item in props.boundaries
    )
    edge = props.elevated_edge_mesh
    edge_fingerprint = geometry_fingerprint([edge]) if edge is not None else ""
    return repr((
        props.road_name, lanes, boundaries, props.shoulder_width,
        props.sidewalk_width, props.curb_height, props.marking_paint_width,
        props.marking_region_width, props.node_shoulder_bands,
        props.surface_profile, props.node_transition_target,
        props.elevated_height, props.bridge_height, props.deck_depth,
        props.bridge_deck_depth, props.tunnel_depth, props.tunnel_clearance,
        props.elevated_edge_no_split_group, edge_fingerprint,
        props.runtime_road_id, props.runtime_prefab_name,
        props.runtime_template_name, props.runtime_output_dir,
    ))


def _runtime_geometry_fingerprint():
    objects = [obj for values in _runtime_mode_objects().values() for obj in values]
    return geometry_fingerprint(objects)


def _runtime_auto_export_timer():
    for scene in bpy.data.scenes:
        props = getattr(scene, "cs1_road_builder", None)
        if props is None or not props.runtime_auto_export:
            continue
        scene_key = scene.as_pointer()
        current_authoring = _authoring_fingerprint(props)
        previous = _AUTO_EXPORT_STATE.get(scene_key)
        try:
            if previous is None or previous[0] != current_authoring:
                for mode, _, _ in MODE_ITEMS:
                    build_mode(scene, mode)
            current_geometry = _runtime_geometry_fingerprint()
            if previous is None or previous != (current_authoring, current_geometry):
                _export_runtime_scene(scene)
                _AUTO_EXPORT_STATE[scene_key] = (current_authoring, current_geometry)
        except (OSError, ValueError, TypeError) as error:
            print(f"CS1 Road Builder runtime export: {error}")
    return 1.0


class CS1ROAD_OT_export_runtime(Operator):
    bl_idname, bl_label = "cs1_road.export_runtime", "Build and export runtime bundle"

    def execute(self, context):
        try:
            for mode, _, _ in MODE_ITEMS:
                build_mode(context.scene, mode)
            payload = _export_runtime_scene(context.scene)
            _AUTO_EXPORT_STATE[context.scene.as_pointer()] = (
                _authoring_fingerprint(context.scene.cs1_road_builder),
                _runtime_geometry_fingerprint(),
            )
        except (OSError, ValueError, TypeError) as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        self.report({"INFO"}, f"Runtime bundle {payload['revision'][:12]}")
        return {"FINISHED"}


class CS1ROAD_OT_export_runtime_prop(Operator):
    bl_idname, bl_label = "cs1_road.export_runtime_prop", "Export selected Prop/Decal mesh"

    def execute(self, context):
        props = context.scene.cs1_road_builder
        obj = context.active_object
        if obj is None or obj.type != "MESH":
            self.report({"ERROR"}, "Select one Mesh object to export")
            return {"CANCELLED"}
        try:
            payload = export_prop_bundle(
                _runtime_output_path(props), props.runtime_prop_id, obj,
                props.runtime_prop_shader,
            )
        except (OSError, ValueError, TypeError) as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        self.report({"INFO"}, f"Prop bundle {payload['revision'][:12]}")
        return {"FINISHED"}


class CS1ROAD_OT_show_mode(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.show_mode", "Show active mode", {"REGISTER", "UNDO"}

    def execute(self, context):
        _show_only_mode(context.scene.cs1_road_builder.mode)
        return {"FINISHED"}


def _load_lane(target, source) -> None:
    target.lane_id = str(source.get("id", source.get("lane_id", "")))
    target.name = str(source.get("name", "Lane"))
    target.zone = _enum_value(str(source.get("zone", "ROAD")), LANE_ZONE_ITEMS, "ROAD")
    target.width = float(source.get("width", target.width))
    target.direction = _enum_value(str(source.get("direction", "FORWARD")), DIRECTION_ITEMS, "FORWARD")
    target.lane_type = _enum_value(str(source.get("lane_type", "VEHICLE")), LANE_TYPE_ITEMS, "VEHICLE")
    target.vehicle_type = _enum_value(str(source.get("vehicle_type", "CAR")), VEHICLE_TYPE_ITEMS, "CAR")
    target.speed_limit = float(source.get("speed_limit", target.speed_limit))
    target.vertical_offset = float(source.get("vertical_offset", target.vertical_offset))
    target.stop_offset = float(source.get("stop_offset", target.stop_offset))
    target.allow_connect = bool(source.get("allow_connect", target.allow_connect))


def _layout_strips(props):
    strips = [{"id": "strip-left-sidewalk", "function": "SIDEWALK", "width": props.sidewalk_width, "surface_style": "SIDEWALK"}]
    if props.shoulder_width > 1e-8:
        strips.append({"id": "strip-left-shoulder", "function": "SHOULDER", "width": props.shoulder_width, "surface_style": "ASPHALT"})
    for lane in (item for item in props.lanes if item.zone == "ROAD"):
        strips.append({"id": _strip_id(lane), "function": "CARRIAGEWAY", "width": lane.width, "surface_style": "ASPHALT"})
    if props.shoulder_width > 1e-8:
        strips.append({"id": "strip-right-shoulder", "function": "SHOULDER", "width": props.shoulder_width, "surface_style": "ASPHALT"})
    strips.append({"id": "strip-right-sidewalk", "function": "SIDEWALK", "width": props.sidewalk_width, "surface_style": "SIDEWALK"})
    return strips


def _export_lanes(props):
    offsets = {"strip-left-sidewalk": 0.0, "strip-right-sidewalk": 0.0}
    lanes = []
    for lane in props.lanes:
        strip_id = _strip_id(lane)
        start = 0.0 if lane.zone == "ROAD" else offsets[strip_id]
        end = start + lane.width
        if lane.zone != "ROAD":
            offsets[strip_id] = end
        lanes.append({
            "id": lane.lane_id, "name": lane.name, "zone": lane.zone,
            "surface_strip_id": strip_id, "lateral_start": start, "lateral_end": end,
            "vertical_offset": lane.vertical_offset, "stop_offset": lane.stop_offset,
            "speed_limit": lane.speed_limit, "direction": lane.direction,
            "lane_type": lane.lane_type, "vehicle_type": lane.vehicle_type,
            "allow_connect": lane.allow_connect,
        })
    return lanes


def _export_layout(props):
    _sync_boundaries(props)
    strips = _layout_strips(props)
    boundaries = []
    for item in props.boundaries:
        marking = None
        if item.marking_enabled:
            marking = {
                "role": item.marking_role,
                "style_id": item.marking_style,
                "placement": "CENTER",
            }
        boundaries.append({
            "id": item.boundary_id, "role": item.role,
            "left_strip_id": item.left_strip_id, "right_strip_id": item.right_strip_id,
            "profile_id": "CURB" if item.role == "CURB" else "FLAT",
            "marking": marking,
        })
    total_width = sum(strip["width"] for strip in strips)
    return {"alignment_offset_from_left": total_width * 0.5, "strips": strips, "boundaries": boundaries}


class CS1ROAD_OT_import_spec(Operator, ImportHelper):
    bl_idname, bl_label, filename_ext = "cs1_road.import_spec", "Load road JSON", ".json"
    filter_glob: StringProperty(default="*.json", options={"HIDDEN"})

    def execute(self, context):
        data = json.loads(Path(self.filepath).read_text(encoding="utf-8"))
        props = context.scene.cs1_road_builder
        props.road_name = data.get("name", props.road_name)
        schema_version = int(data.get("schema_version", 2))
        cross = data.get("shared_geometry", data.get("cross_section", {}))
        props.curb_height = cross.get("curb_height", props.curb_height)
        props.surface_profile = cross.get("surface_profile", props.surface_profile)
        legacy_markings = data.get("markings", {})
        if schema_version >= 3:
            layout = data.get("layout", {})
            strips = {str(item.get("id", "")): item for item in layout.get("strips", [])}
            left_sidewalk = float(strips.get("strip-left-sidewalk", {}).get("width", props.sidewalk_width))
            right_sidewalk = float(strips.get("strip-right-sidewalk", {}).get("width", left_sidewalk))
            left_shoulder = float(strips.get("strip-left-shoulder", {}).get("width", 0.0))
            right_shoulder = float(strips.get("strip-right-shoulder", {}).get("width", left_shoulder))
            if abs(left_sidewalk - right_sidewalk) > 1e-6 or abs(left_shoulder - right_shoulder) > 1e-6:
                self.report({"ERROR"}, "Current Blender editor supports symmetric sidewalk and shoulder widths only")
                return {"CANCELLED"}
            props.sidewalk_width, props.shoulder_width = left_sidewalk, left_shoulder
            marking_styles = data.get("styles", {}).get("markings", {})
            solid = marking_styles.get("SOLID_WHITE", {})
            props.marking_paint_width = solid.get("paint_width", props.marking_paint_width)
            props.marking_region_width = solid.get("region_width", props.marking_region_width)
        else:
            props.shoulder_width = cross.get("shoulder_width", props.shoulder_width)
            props.sidewalk_width = cross.get("sidewalk_width", props.sidewalk_width)
            props.edge_lines = legacy_markings.get("edge_lines", props.edge_lines)
            props.lane_lines = legacy_markings.get("lane_lines", props.lane_lines)
            props.marking_paint_width = legacy_markings.get("paint_width", props.marking_paint_width)
            props.marking_region_width = legacy_markings.get("region_width", props.marking_region_width)
        node = data.get("node", {})
        props.node_transition_target = node.get("transition_target", node.get("far_profile", props.node_transition_target))
        props.node_shoulder_bands = node.get("shoulder_bands", props.node_shoulder_bands)
        props.lanes.clear()
        for source in data.get("lanes", []):
            lane_source = dict(source)
            if schema_version >= 3:
                strip = strips.get(str(source.get("surface_strip_id", "")), {})
                lane_source["width"] = float(source.get("lateral_end", 0.0)) - float(source.get("lateral_start", 0.0))
                if lane_source["width"] <= 0.0:
                    lane_source["width"] = strip.get("width", 3.0)
            _load_lane(props.lanes.add(), lane_source)
        _ensure_default_lanes(props)
        if schema_version >= 3:
            saved_boundaries = {str(item.get("id", "")): item for item in data.get("layout", {}).get("boundaries", [])}
            for item in props.boundaries:
                source = saved_boundaries.get(item.boundary_id)
                if source is None:
                    continue
                item.role = _enum_value(str(source.get("role", item.role)), BOUNDARY_ROLE_ITEMS, item.role)
                marking = source.get("marking")
                item.marking_enabled = marking is not None
                if marking is not None:
                    item.marking_role = _enum_value(str(marking.get("role", item.marking_role)), MARKING_ROLE_ITEMS, item.marking_role)
                    item.marking_style = _enum_value(str(marking.get("style_id", item.marking_style)), MARKING_STYLE_ITEMS, item.marking_style)
        else:
            _sync_boundaries(props, props.edge_lines, props.lane_lines)
        modes = data.get("modes", {})
        props.elevated_height = modes.get("elevated", {}).get("height", props.elevated_height)
        props.deck_depth = modes.get("elevated", {}).get("deck_depth", props.deck_depth)
        props.bridge_height = modes.get("bridge", {}).get("height", props.bridge_height)
        props.bridge_deck_depth = modes.get("bridge", {}).get("deck_depth", props.bridge_deck_depth)
        props.tunnel_depth = modes.get("tunnel", {}).get("depth", modes.get("slope", {}).get("tunnel_depth", props.tunnel_depth))
        props.tunnel_clearance = modes.get("tunnel", {}).get("clearance_height", props.tunnel_clearance)
        return {"FINISHED"}


class CS1ROAD_OT_export_spec(Operator, ExportHelper):
    bl_idname, bl_label, filename_ext = "cs1_road.export_spec", "Save road JSON", ".json"
    filter_glob: StringProperty(default="*.json", options={"HIDDEN"})

    def execute(self, context):
        props = context.scene.cs1_road_builder
        _ensure_lane_ids(props)
        _sync_boundaries(props)
        data = {
            "schema_version": 3, "name": props.road_name,
            "shared_geometry": {
                "segment_length": MODE_LENGTH, "node_length": MODE_LENGTH,
                "segment_slices": SEGMENT_SLICES, "node_slices": NODE_SLICES,
                "curb_height": props.curb_height, "surface_profile": props.surface_profile,
            },
            "styles": {"markings": {"SOLID_WHITE": {
                "paint_width": props.marking_paint_width,
                "region_width": props.marking_region_width,
                "texture_tile": "solid_white",
            }}},
            "layout": _export_layout(props),
            "node": {"length": MODE_LENGTH, "center_split": True, "shoulder_bands": props.node_shoulder_bands, "transition_target": props.node_transition_target},
            "lanes": _export_lanes(props),
            "modes": {
                "basic": {"shader": "Custom/Net/Road"},
                "elevated": {"height": props.elevated_height, "deck_depth": props.deck_depth, "shader": "Custom/Net/RoadBridge"},
                "bridge": {"height": props.bridge_height, "deck_depth": props.bridge_deck_depth, "shader": "Custom/Net/RoadBridge"},
                "slope": {"tunnel_depth": props.tunnel_depth, "shader": "Custom/Net/RoadBridge"},
                "tunnel": {"depth": props.tunnel_depth, "clearance_height": props.tunnel_clearance, "shader": "Custom/Net/RoadBridge"},
            },
        }
        Path(self.filepath).write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        return {"FINISHED"}


class CS1ROAD_PT_main(Panel):
    bl_label, bl_idname = "CS1 Road Builder", "CS1ROAD_PT_main"
    bl_space_type, bl_region_type, bl_category = "VIEW_3D", "UI", "Road"

    def draw(self, context):
        layout, props = self.layout, context.scene.cs1_road_builder
        layout.prop(props, "road_name")
        layout.prop(props, "mode")
        box = layout.box()
        box.label(text="Lanes (ordered left to right)")
        if not props.lanes:
            box.label(text="No lanes. Initialize the road definition.", icon="INFO")
            box.operator("cs1_road.lanes_reset", icon="FILE_REFRESH")
        row = box.row()
        row.template_list("CS1ROAD_UL_lanes", "", props, "lanes", props, "active_lane_index", rows=5)
        buttons = row.column(align=True)
        buttons.operator("cs1_road.lane_add", text="", icon="ADD")
        buttons.operator("cs1_road.lane_remove", text="", icon="REMOVE")
        buttons.operator("cs1_road.lane_move", text="", icon="TRIA_UP").direction = "UP"
        buttons.operator("cs1_road.lane_move", text="", icon="TRIA_DOWN").direction = "DOWN"
        if props.lanes:
            lane = props.lanes[min(props.active_lane_index, len(props.lanes) - 1)]
            details = box.column(align=True)
            details.label(text=f"ID: {lane.lane_id or '(assigned on build)'}")
            for field in ("zone", "width", "direction", "lane_type", "vehicle_type", "speed_limit", "vertical_offset", "stop_offset", "allow_connect"):
                details.prop(lane, field)
        counts = {key: sum(lane.direction == key for lane in props.lanes) for key in ("FORWARD", "BACKWARD", "BOTH")}
        box.label(text=f"Total {len(props.lanes)} / F {counts['FORWARD']} / B {counts['BACKWARD']} / Both {counts['BOTH']}")

        shared = layout.box()
        shared.label(text="Shared cross-section (all modes)")
        shared.prop(props, "shoulder_width")
        shared.prop(props, "sidewalk_width")
        shared.prop(props, "curb_height")
        roadway_width, total_width, _ = _cross_section(props)
        shared.label(text=f"Roadway {roadway_width:.3f} m / Total {total_width:.3f} m")
        shared.label(text="Segment length: fixed 64 m")
        shared.label(text="Curve mesh: 20 slices / 3.2 m")

        markings = layout.box()
        markings.label(text="Boundaries and segment markings")
        markings.template_list("CS1ROAD_UL_boundaries", "", props, "boundaries", props, "active_boundary_index", rows=6)
        markings.operator("cs1_road.boundaries_sync", icon="FILE_REFRESH")
        if props.boundaries:
            boundary = props.boundaries[min(props.active_boundary_index, len(props.boundaries) - 1)]
            markings.label(text=f"ID: {boundary.boundary_id}")
            markings.prop(boundary, "role")
            markings.prop(boundary, "marking_enabled")
            if boundary.marking_enabled:
                markings.prop(boundary, "marking_role")
                markings.prop(boundary, "marking_style")
        markings.label(text="Shared marking style")
        markings.prop(props, "marking_paint_width")
        markings.prop(props, "marking_region_width")
        if props.marking_region_width <= props.marking_paint_width:
            markings.label(text="Region must include asphalt margin.", icon="ERROR")
        markings.label(text="Node markings: none")

        node_box = layout.box()
        node_box.label(text="Ground node: fixed 64 m")
        node_box.label(text="Node mesh: 8 slices / 8 m")
        node_box.prop(props, "surface_profile")
        node_box.prop(props, "node_transition_target")
        node_box.prop(props, "node_shoulder_bands")
        node_box.label(text="Road surface split at X=0", icon="MOD_MIRROR")

        mode_box = layout.box()
        mode_box.label(text="Active mode only")
        if props.mode == "elevated":
            mode_box.prop(props, "elevated_height"); mode_box.prop(props, "deck_depth")
            mode_box.prop(props, "elevated_edge_mesh")
            mode_box.prop(props, "elevated_edge_no_split_group")
            if props.elevated_edge_mesh:
                mode_box.label(text="Origin X/Z: road surface outer corner")
                mode_box.label(text="Right side basis; left side is X-mirrored")
                mode_box.label(text="Local Y: -32..32 m; negative Z follows deck depth")
                mode_box.label(text="Faces touching no-split vertices stay unsliced")
            _, total_width, _ = _cross_section(props)
            girders = plan_main_girders(total_width)
            mode_box.label(text=f"JIS PC compo: {girders.count} girders / {girders.spacing:.1f} m centers")
            mode_box.label(text=f"35 m reference: 0.70 x {girders.depth:.2f} m rectangle")
            mode_box.label(text=f"Edge offset: {girders.outer_offset:.2f} m")
        elif props.mode == "bridge":
            mode_box.prop(props, "bridge_height"); mode_box.prop(props, "bridge_deck_depth")
            _, total_width, _ = _cross_section(props)
            girders = plan_main_girders(total_width)
            mode_box.label(text=f"JIS PC compo: {girders.count} girders / {girders.spacing:.1f} m centers")
            mode_box.label(text=f"35 m reference: 0.70 x {girders.depth:.2f} m rectangle")
            mode_box.label(text=f"Edge offset: {girders.outer_offset:.2f} m")
        elif props.mode in {"slope", "tunnel"}:
            mode_box.prop(props, "tunnel_depth")
            if props.mode == "tunnel":
                mode_box.prop(props, "tunnel_clearance")

        layout.prop(props, "hide_other_modes")
        layout.operator("cs1_road.build_preview", icon="MOD_BUILD")
        layout.operator("cs1_road.build_all", icon="OUTLINER_COLLECTION")
        layout.operator("cs1_road.show_mode", icon="HIDE_OFF")
        row = layout.row(align=True)
        row.operator("cs1_road.import_spec", icon="IMPORT")
        row.operator("cs1_road.export_spec", icon="EXPORT")
        runtime = layout.box()
        runtime.label(text="Runtime preview")
        runtime.prop(props, "runtime_road_id")
        runtime.prop(props, "runtime_prefab_name")
        runtime.prop(props, "runtime_template_name")
        runtime.prop(props, "runtime_output_dir")
        runtime.prop(props, "runtime_auto_export")
        runtime.operator("cs1_road.export_runtime", icon="EXPORT")
        runtime.separator()
        runtime.prop(props, "runtime_prop_id")
        runtime.prop(props, "runtime_prop_shader")
        runtime.operator("cs1_road.export_runtime_prop", icon="MESH_PLANE")

        development = layout.box()
        development.label(text="Development")
        development.operator("script.reload", text="Reload Scripts", icon="FILE_REFRESH")
        development.label(text="Rebuild generated meshes after reloading.", icon="INFO")
        layout.label(text="Generated objects remain editable meshes.", icon="EDITMODE_HLT")


CLASSES = (
    CS1RoadLane, CS1RoadBoundary, CS1RoadBuilderProperties, CS1ROAD_UL_lanes, CS1ROAD_UL_boundaries,
    CS1ROAD_OT_lane_add, CS1ROAD_OT_lanes_reset, CS1ROAD_OT_lane_remove, CS1ROAD_OT_lane_move,
    CS1ROAD_OT_boundaries_sync,
    CS1ROAD_OT_build_preview, CS1ROAD_OT_build_all, CS1ROAD_OT_show_mode,
    CS1ROAD_OT_import_spec, CS1ROAD_OT_export_spec, CS1ROAD_OT_export_runtime,
    CS1ROAD_OT_export_runtime_prop,
    CS1ROAD_PT_main,
)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.Scene.cs1_road_builder = PointerProperty(type=CS1RoadBuilderProperties)
    if not bpy.app.timers.is_registered(_initialize_scene_lanes):
        bpy.app.timers.register(_initialize_scene_lanes, first_interval=0.0)
    if not bpy.app.timers.is_registered(_runtime_auto_export_timer):
        bpy.app.timers.register(_runtime_auto_export_timer, first_interval=1.0, persistent=True)


def unregister():
    if bpy.app.timers.is_registered(_initialize_scene_lanes):
        bpy.app.timers.unregister(_initialize_scene_lanes)
    if bpy.app.timers.is_registered(_runtime_auto_export_timer):
        bpy.app.timers.unregister(_runtime_auto_export_timer)
    _AUTO_EXPORT_STATE.clear()
    del bpy.types.Scene.cs1_road_builder
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)
