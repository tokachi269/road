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
import time
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
    ROADWAY_DEPRESSION,
    SIDEWALK_LANE_TOTAL_INSET,
    SEGMENT_SLICES,
    cross_section_widths,
    expected_boundaries,
    lane_vertical_offset,
    roadway_depression,
    sidewalk_lane_width,
    strip_id,
)
from .geometry_plan import plan_main_girders
from .runtime_export import export_prop_bundle, export_runtime_bundle, geometry_fingerprint, safe_road_id


MODE_ITEMS = (
    ("basic", "Ground", "Ground road"),
    ("elevated", "Elevated", "Elevated road"),
    ("bridge", "Bridge", "Bridge road"),
    ("slope", "Entrance", "Transition between ground and tunnel"),
    ("tunnel", "Tunnel", "Underground road"),
)
MODE_PREVIEW_SPACING = 16.0
LANE_ZONE_ITEMS = (
    ("ROAD", "Roadway", "Lane occupies the roadway cross-section"),
    ("LEFT_SIDEWALK", "Pedestrian L", "Network lane lies on the left sidewalk"),
    ("RIGHT_SIDEWALK", "Pedestrian R", "Network lane lies on the right sidewalk"),
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
ROAD_VEHICLE_TYPE_ITEMS = tuple(item for item in VEHICLE_TYPE_ITEMS if item[0] != "NONE")
SIDEWALK_VEHICLE_TYPE_ITEMS = (("NONE", "None", "Pedestrian lanes do not carry vehicles"),)


def _vehicle_type_items_for_lane(lane, context):
    if lane.zone in {"LEFT_SIDEWALK", "RIGHT_SIDEWALK"}:
        return SIDEWALK_VEHICLE_TYPE_ITEMS
    if lane.lane_type == "VEHICLE":
        return ROAD_VEHICLE_TYPE_ITEMS
    return VEHICLE_TYPE_ITEMS


def _on_lane_zone_update(lane, context) -> None:
    if lane.zone in {"LEFT_SIDEWALK", "RIGHT_SIDEWALK"}:
        lane.direction = "BOTH"
        lane.lane_type = "PEDESTRIAN"
        lane.vehicle_type = "NONE"
    elif lane.lane_type == "PEDESTRIAN":
        lane.lane_type = "VEHICLE"
        lane.vehicle_type = "CAR"
    props = getattr(getattr(context, "scene", None), "cs1_road_builder", None)
    if props is not None:
        _sync_lane_derived_values(props)
        _sync_boundaries(props)
        _schedule_live_preview(props, context)
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
SHARED_SURFACE_MATERIAL = "CS1 Road Shared Surface"
SHARED_STRUCTURE_MATERIAL = "CS1 Road Shared Structure"
SHARED_TUNNEL_MATERIAL = "CS1 Road Shared Tunnel"
SHARED_SURFACE_COLOR = (0.12, 0.14, 0.16, 1.0)
FACE_KIND_VALUES = {"surface": 0, "marking": 1, "structure": 2}
_AUTO_EXPORT_STATE = {}
_LIVE_PREVIEW_PENDING = {}
_LIVE_PREVIEW_DELAY = 0.15
_LIVE_PREVIEW_SETTLE_DELAY = 0.60
_LIVE_PREVIEW_REBUILDING = False
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


def _material(paint_width: float, region_width: float) -> bpy.types.Material:
    material = (
        bpy.data.materials.get(SHARED_SURFACE_MATERIAL)
        or bpy.data.materials.new(SHARED_SURFACE_MATERIAL)
    )
    material.diffuse_color = SHARED_SURFACE_COLOR
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
    kind = nodes.new("ShaderNodeAttribute")
    kind.attribute_name = "cs1_face_kind"
    is_marking = nodes.new("ShaderNodeMath")
    is_marking.operation = "COMPARE"
    is_marking.inputs[1].default_value = FACE_KIND_VALUES["marking"]
    is_marking.inputs[2].default_value = 0.1
    visible_mask = nodes.new("ShaderNodeMath")
    visible_mask.operation = "MULTIPLY"
    mix = nodes.new("ShaderNodeMixRGB")
    low.operation, high.operation, mask.operation = "GREATER_THAN", "LESS_THAN", "MULTIPLY"
    half_ratio = min(0.49, paint_width / max(region_width, 0.001) * 0.5)
    low.inputs[1].default_value = 0.5 - half_ratio
    high.inputs[1].default_value = 0.5 + half_ratio
    mix.blend_type = "MIX"
    mix.inputs[1].default_value = SHARED_SURFACE_COLOR
    mix.inputs[2].default_value = (0.92, 0.92, 0.88, 1.0)
    links.new(texcoord.outputs["UV"], separate.inputs[0])
    links.new(separate.outputs["X"], low.inputs[0])
    links.new(separate.outputs["X"], high.inputs[0])
    links.new(low.outputs[0], mask.inputs[0])
    links.new(high.outputs[0], mask.inputs[1])
    links.new(kind.outputs["Fac"], is_marking.inputs[0])
    links.new(mask.outputs[0], visible_mask.inputs[0])
    links.new(is_marking.outputs[0], visible_mask.inputs[1])
    links.new(visible_mask.outputs[0], mix.inputs[0])
    links.new(mix.outputs[0], shader.inputs["Base Color"])
    links.new(shader.outputs[0], output.inputs[0])
    return material


def _structure_material() -> bpy.types.Material:
    material = (
        bpy.data.materials.get(SHARED_STRUCTURE_MATERIAL)
        or bpy.data.materials.new(SHARED_STRUCTURE_MATERIAL)
    )
    material.diffuse_color = (0.34, 0.36, 0.38, 1.0)
    return material


def _tunnel_material() -> bpy.types.Material:
    material = (
        bpy.data.materials.get(SHARED_TUNNEL_MATERIAL)
        or bpy.data.materials.new(SHARED_TUNNEL_MATERIAL)
    )
    material.diffuse_color = (0.20, 0.22, 0.24, 1.0)
    return material


def _mesh_object(
    collection, name, vertices, faces, face_kinds, face_uvs, materials,
    material_indices=None,
) -> bpy.types.Object:
    mesh = bpy.data.meshes.new(f"{name}_mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    uv_layer = mesh.uv_layers.new(name="RoadUV")
    kind_attribute = mesh.attributes.new(
        name="cs1_face_kind", type="INT", domain="FACE",
    )
    mesh_y_min = min(vertex.co.y for vertex in mesh.vertices)
    mesh_y_max = max(vertex.co.y for vertex in mesh.vertices)
    for polygon, kind, explicit_uvs in zip(mesh.polygons, face_kinds, face_uvs):
        polygon.material_index = (material_indices or {}).get(kind, 0)
        kind_attribute.data[polygon.index].value = FACE_KIND_VALUES.get(kind, 0)
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
    bottom_indices = {
        index for index in selected_vertices
        if abs(coordinates[index].z - reference_bottom) <= 1e-4
    }
    bottom_x_values = [coordinates[index].x for index in bottom_indices]
    # A solid side piece has two longitudinal edges at its lowest face.  The
    # edge nearest the road centre is the deck connector; the outer edge stays
    # part of the supplied cross-section.
    connector_local_x = (
        max(bottom_x_values) if side == "left" else min(bottom_x_values)
    )
    connector_indices = {
        index for index in bottom_indices
        if abs(coordinates[index].x - connector_local_x) <= 1e-4
    }
    bottom_y_values = [coordinates[index].y for index in connector_indices]
    if (
        abs(min(bottom_y_values) + half_length) > 1e-4
        or abs(max(bottom_y_values) - half_length) > 1e-4
    ):
        raise ValueError(
            f"{source.name}: road-centre-side lowest edge must span "
            "the full 64 m length"
        )

    connector_x = anchor_x + connector_local_x

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

        # Only the road-centre-side face can be buried inside the generated
        # deck.  Split that interface at the surface and discard its lower
        # portion.  A global Z split would add a needless horizontal loop to
        # every exposed face of a solid custom edge.
        connector_faces = [
            face for face in bm.faces
            if face.is_valid
            and face[split_layer] == 1
            and all(abs(vertex.co.x - connector_x) <= 1e-4 for vertex in face.verts)
            and min(vertex.co.z for vertex in face.verts) < surface_z - 1e-6
            and max(vertex.co.z for vertex in face.verts) > surface_z + 1e-6
        ]
        if connector_faces:
            connector_edges = {edge for face in connector_faces for edge in face.edges}
            connector_vertices = {vertex for face in connector_faces for vertex in face.verts}
            bmesh.ops.bisect_plane(
                bm,
                geom=[*connector_vertices, *connector_edges, *connector_faces],
                plane_co=(0.0, 0.0, surface_z),
                plane_no=(0.0, 0.0, 1.0),
                dist=1e-6,
                clear_inner=False,
                clear_outer=False,
            )
            buried_faces = [
                face for face in bm.faces
                if face.is_valid
                and all(abs(vertex.co.x - connector_x) <= 1e-4 for vertex in face.verts)
                and max(vertex.co.z for vertex in face.verts) <= surface_z + 1e-6
                and min(vertex.co.z for vertex in face.verts) < surface_z - 1e-6
            ]
            if buried_faces:
                bmesh.ops.delete(bm, geom=buried_faces, context="FACES")

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
            # Segment/node ends meet another network mesh.  Source end caps
            # are hidden there and only add overlapping faces.
            if (
                max(point[1] for point in points) - min(point[1] for point in points) <= 1e-6
                and abs(abs(points[0][1]) - half_length) <= 1e-4
            ):
                continue
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
        lane.vertical_offset = lane_vertical_offset(zone, props.depress_roadway)


def _sync_lane_derived_values(props) -> None:
    sidewalk_counts = {
        zone: sum(lane.zone == zone for lane in props.lanes)
        for zone in ("LEFT_SIDEWALK", "RIGHT_SIDEWALK")
    }
    for lane in props.lanes:
        if lane.zone in {"LEFT_SIDEWALK", "RIGHT_SIDEWALK"}:
            if lane.direction != "BOTH":
                lane.direction = "BOTH"
            if lane.lane_type != "PEDESTRIAN":
                lane.lane_type = "PEDESTRIAN"
            if lane.vehicle_type != "NONE":
                lane.vehicle_type = "NONE"
            derived_width = sidewalk_lane_width(
                props.sidewalk_width, sidewalk_counts[lane.zone]
            )
            if abs(lane.width - derived_width) > 1e-6:
                lane.width = derived_width
        elif lane.lane_type == "VEHICLE" and lane.vehicle_type == "NONE":
            lane.vehicle_type = "CAR"
        derived_offset = lane_vertical_offset(lane.zone, props.depress_roadway)
        if abs(lane.vertical_offset - derived_offset) > 1e-6:
            lane.vertical_offset = derived_offset


def _on_sidewalk_width_update(props, context) -> None:
    _sync_lane_derived_values(props)
    _schedule_live_preview(props, context)


def _existing_preview_modes() -> tuple[str, ...]:
    return tuple(
        mode for mode, _, _ in MODE_ITEMS
        if (collection := bpy.data.collections.get(f"CS1_ROAD_{mode}")) is not None
        and bool(collection.objects)
    )


def _schedule_live_preview(props, context) -> None:
    if _LIVE_PREVIEW_REBUILDING:
        return
    scene = getattr(context, "scene", None)
    scene_props = getattr(scene, "cs1_road_builder", None) if scene is not None else None
    if (
        scene_props is None
        or scene_props.as_pointer() != props.as_pointer()
    ):
        return
    modes = _existing_preview_modes()
    if not modes:
        return
    scene_key = scene.as_pointer()
    now = time.monotonic()
    pending = _LIVE_PREVIEW_PENDING.get(scene_key)
    if pending is None:
        _LIVE_PREVIEW_PENDING[scene_key] = {
            "live_due": now + _LIVE_PREVIEW_DELAY,
            "settle_due": now + _LIVE_PREVIEW_SETTLE_DELAY,
            "last_live": 0.0,
            "dirty": True,
            "active_mode": props.mode,
            "modes": modes,
        }
    else:
        pending_modes = set(pending["modes"]) | set(modes)
        pending["modes"] = tuple(
            mode for mode, _, _ in MODE_ITEMS if mode in pending_modes
        )
        pending["active_mode"] = props.mode
        pending["settle_due"] = now + _LIVE_PREVIEW_SETTLE_DELAY
        if not pending["dirty"]:
            pending["live_due"] = max(
                now, pending["last_live"] + _LIVE_PREVIEW_DELAY
            )
        pending["dirty"] = True
    if not bpy.app.timers.is_registered(_live_preview_timer):
        bpy.app.timers.register(_live_preview_timer, first_interval=_LIVE_PREVIEW_DELAY)


def _live_preview_timer():
    if not _LIVE_PREVIEW_PENDING:
        return None
    now = time.monotonic()
    next_delay = None
    scenes = {scene.as_pointer(): scene for scene in bpy.data.scenes}
    for scene_key, pending in tuple(_LIVE_PREVIEW_PENDING.items()):
        scene = scenes.get(scene_key)
        if scene is None:
            _LIVE_PREVIEW_PENDING.pop(scene_key, None)
            continue
        try:
            if pending["dirty"] and pending["live_due"] <= now:
                mode = pending["active_mode"]
                collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
                if collection is not None and collection.objects:
                    build_mode(scene, mode)
                pending["dirty"] = False
                pending["last_live"] = time.monotonic()
            if pending["settle_due"] <= now:
                for mode in pending["modes"]:
                    if mode == pending["active_mode"] and not pending["dirty"]:
                        continue
                    collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
                    if collection is not None and collection.objects:
                        build_mode(scene, mode)
                _LIVE_PREVIEW_PENDING.pop(scene_key, None)
        except (ValueError, TypeError) as error:
            _LIVE_PREVIEW_PENDING.pop(scene_key, None)
            print(f"CS1 Road Builder live preview: {error}")
        pending = _LIVE_PREVIEW_PENDING.get(scene_key)
        if pending is not None:
            deadlines = [pending["settle_due"]]
            if pending["dirty"]:
                deadlines.append(pending["live_due"])
            delay = max(min(deadlines) - time.monotonic(), 0.01)
            next_delay = delay if next_delay is None else min(next_delay, delay)
    if _LIVE_PREVIEW_PENDING:
        return next_delay or _LIVE_PREVIEW_DELAY
    return None


def _on_geometry_update(props, context) -> None:
    _schedule_live_preview(props, context)


def _on_cross_section_update(props, context) -> None:
    _sync_boundaries(props)
    _schedule_live_preview(props, context)


def _on_depress_roadway_update(props, context) -> None:
    _sync_lane_derived_values(props)
    _schedule_live_preview(props, context)


def _on_lane_width_update(lane, context) -> None:
    if lane.zone == "ROAD":
        props = getattr(getattr(context, "scene", None), "cs1_road_builder", None)
        if props is not None:
            _schedule_live_preview(props, context)


def _on_lane_direction_update(lane, context) -> None:
    props = getattr(getattr(context, "scene", None), "cs1_road_builder", None)
    if props is not None:
        _sync_boundaries(props)
        _schedule_live_preview(props, context)


def _on_boundary_marking_update(boundary, context) -> None:
    props = getattr(getattr(context, "scene", None), "cs1_road_builder", None)
    if props is not None:
        _schedule_live_preview(props, context)


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


def _build_mode_impl(scene: bpy.types.Scene, mode: str) -> list[bpy.types.Object]:
    props = scene.cs1_road_builder
    _sync_lane_derived_values(props)
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
    material = _material(
        props.marking_paint_width, props.marking_region_width,
    )
    structure_material = _structure_material()
    tunnel_material = _tunnel_material()
    road_half = roadway_width * 0.5
    segment_markings, edge_centers = _marking_layout(props)
    segment_boundaries = _segment_boundaries(road_half, segment_markings, props.marking_region_width)
    node_boundaries = _node_boundaries(props, road_half, edge_centers)
    node_markings = []
    curb_rise = roadway_depression(props.depress_roadway)
    props.half_width = total_half
    y_min, y_max = -MODE_LENGTH * 0.5, MODE_LENGTH * 0.5
    segment_mesh, node_mesh = _SurfaceMesh(), _SurfaceMesh()
    girder_layout = None

    if mode == "basic":
        segment_z = -curb_rise
        # A depressed road already meets the recessed node surface, so its
        # regular node stays level.  Only a flush road needs the preview
        # profile which descends to the shared recessed node surface.
        far_z = segment_z if props.depress_roadway else -ROADWAY_DEPRESSION
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

    segment_materials = (material,)
    segment_indices = {}
    node_materials = (material,)
    node_indices = {}
    if mode != "basic":
        secondary_material = (
            tunnel_material if mode in {"slope", "tunnel"} else structure_material
        )
        segment_materials += (secondary_material,)
        segment_indices["structure"] = 1
        node_materials += (secondary_material,)
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


def build_mode(scene: bpy.types.Scene, mode: str) -> list[bpy.types.Object]:
    global _LIVE_PREVIEW_REBUILDING
    previous = _LIVE_PREVIEW_REBUILDING
    _LIVE_PREVIEW_REBUILDING = True
    try:
        return _build_mode_impl(scene, mode)
    finally:
        _LIVE_PREVIEW_REBUILDING = previous


class CS1RoadLane(PropertyGroup):
    lane_id: StringProperty(name="Lane ID")
    zone: EnumProperty(
        name="Kind", items=LANE_ZONE_ITEMS, default="ROAD",
        update=_on_lane_zone_update,
    )
    width: FloatProperty(
        name="Width", default=3.0, min=0.05, max=16.0,
        unit="LENGTH", update=_on_lane_width_update,
    )
    direction: EnumProperty(
        name="Direction", items=DIRECTION_ITEMS, default="FORWARD",
        update=_on_lane_direction_update,
    )
    lane_type: EnumProperty(
        name="Lane type", items=LANE_TYPE_ITEMS, default="VEHICLE", options={"HIDDEN"},
    )
    vehicle_type: EnumProperty(
        name="Vehicle", items=_vehicle_type_items_for_lane,
    )
    speed_limit: FloatProperty(name="Speed limit", default=1.0, min=0.0, max=10.0)
    vertical_offset: FloatProperty(
        name="Derived vertical offset", default=0.0, min=-16.0, max=16.0,
        unit="LENGTH", options={"HIDDEN"},
    )
    stop_offset: FloatProperty(
        name="Stop offset", default=0.0, min=-32.0, max=32.0,
        unit="LENGTH", options={"HIDDEN"},
    )
    allow_connect: BoolProperty(name="Allow connect", default=True, options={"HIDDEN"})


class CS1RoadBoundary(PropertyGroup):
    boundary_id: StringProperty(name="Boundary ID")
    left_strip_id: StringProperty(name="Left strip ID")
    right_strip_id: StringProperty(name="Right strip ID")
    role: EnumProperty(name="Boundary role", items=BOUNDARY_ROLE_ITEMS, default="LANE_DIVIDER")
    marking_enabled: BoolProperty(
        name="Marking", default=False, update=_on_boundary_marking_update,
    )
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
    shoulder_width: FloatProperty(
        name="Shoulder width", default=0.5, min=0.0, max=8.0,
        unit="LENGTH", update=_on_cross_section_update,
    )
    sidewalk_width: FloatProperty(
        name="Sidewalk width", default=2.5, min=0.0, max=16.0,
        unit="LENGTH", update=_on_sidewalk_width_update,
    )
    depress_roadway: BoolProperty(
        name=f"Lower roadway {ROADWAY_DEPRESSION:.2f} m", default=True,
        update=_on_depress_roadway_update,
    )
    edge_lines: BoolProperty(name="Legacy roadside lines", default=True, options={"HIDDEN"})
    lane_lines: BoolProperty(name="Legacy lane divider lines", default=True, options={"HIDDEN"})
    marking_paint_width: FloatProperty(
        name="Paint width", default=0.15, min=0.05, max=0.5,
        unit="LENGTH", update=_on_geometry_update,
    )
    marking_region_width: FloatProperty(
        name="Texture band width", default=0.4, min=0.2, max=1.0,
        unit="LENGTH", update=_on_geometry_update,
    )
    node_shoulder_bands: BoolProperty(
        name="Node shoulder bands", default=False, update=_on_geometry_update,
    )
    elevated_height: FloatProperty(name="Elevated height", default=8.0, min=1.0, max=64.0, unit="LENGTH")
    bridge_height: FloatProperty(name="Bridge height", default=12.0, min=1.0, max=128.0, unit="LENGTH")
    deck_depth: FloatProperty(
        name="Elevated deck depth", default=1.0, min=0.1, max=8.0,
        unit="LENGTH", update=_on_geometry_update,
    )
    elevated_edge_mesh: PointerProperty(
        name="Edge mesh (right basis)", type=bpy.types.Object,
        poll=_mesh_object_poll, update=_on_geometry_update,
    )
    elevated_edge_no_split_group: StringProperty(
        name="No-split group", default="CS1_NO_SPLIT", update=_on_geometry_update,
    )
    bridge_deck_depth: FloatProperty(
        name="Bridge deck depth", default=1.5, min=0.1, max=12.0,
        unit="LENGTH", update=_on_geometry_update,
    )
    tunnel_depth: FloatProperty(name="Tunnel depth", default=12.0, min=1.0, max=64.0, unit="LENGTH")
    tunnel_clearance: FloatProperty(
        name="Tunnel clearance", default=5.0, min=2.0, max=16.0,
        unit="LENGTH", update=_on_geometry_update,
    )
    runtime_road_id: StringProperty(name="Runtime road ID", default="example-road")
    runtime_prefab_name: StringProperty(name="Runtime prefab name", default="Road Runtime Example")
    runtime_template_name: StringProperty(name="Template prefab", default="Basic Road")
    runtime_output_dir: StringProperty(
        name="Runtime output", default=DEFAULT_RUNTIME_OUTPUT, subtype="DIR_PATH",
    )
    runtime_auto_export: BoolProperty(name="Auto export every second", default=False)
    runtime_prop_id: StringProperty(name="Runtime Prop ID", default="example-decal")
    runtime_prop_shader: StringProperty(name="Prop shader", default="Custom/Props/Decal/Blend")


class CS1ROAD_UL_boundaries(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_propname, index):
        if self.layout_type in {"DEFAULT", "COMPACT"}:
            row = layout.row(align=True)
            row.prop(item, "marking_enabled", text="")
            row.label(text=item.name or item.boundary_id)
            if item.marking_enabled:
                row.label(text=item.marking_role.replace("_", " ").title())
            else:
                row.label(text="No line")
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
        _schedule_live_preview(props, context)
        return {"FINISHED"}


class CS1ROAD_OT_lanes_reset(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lanes_reset", "Create default lanes", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = context.scene.cs1_road_builder
        props.lanes.clear()
        _add_default_lanes(props)
        props.active_lane_index = 0
        _sync_boundaries(props)
        _schedule_live_preview(props, context)
        return {"FINISHED"}


class CS1ROAD_OT_lane_remove(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lane_remove", "Remove lane", {"REGISTER", "UNDO"}
    index: IntProperty(default=-1)

    def execute(self, context):
        props = context.scene.cs1_road_builder
        if props.lanes:
            index = self.index if 0 <= self.index < len(props.lanes) else props.active_lane_index
            props.lanes.remove(min(index, len(props.lanes) - 1))
            props.active_lane_index = max(0, min(props.active_lane_index, len(props.lanes) - 1))
            _sync_boundaries(props)
            _schedule_live_preview(props, context)
        return {"FINISHED"}


class CS1ROAD_OT_lane_move(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lane_move", "Move lane", {"REGISTER", "UNDO"}
    direction: EnumProperty(items=(("UP", "Up", ""), ("DOWN", "Down", "")))
    index: IntProperty(default=-1)

    def execute(self, context):
        props = context.scene.cs1_road_builder
        source = self.index if 0 <= self.index < len(props.lanes) else props.active_lane_index
        if not 0 <= source < len(props.lanes):
            return {"FINISHED"}
        zone = props.lanes[source].zone
        peers = [index for index, lane in enumerate(props.lanes) if lane.zone == zone]
        peer_index = peers.index(source)
        target_peer = peer_index - 1 if self.direction == "UP" else peer_index + 1
        if 0 <= target_peer < len(peers):
            target = peers[target_peer]
            props.lanes.move(source, target)
            props.active_lane_index = target
            _sync_boundaries(props)
            _schedule_live_preview(props, context)
        return {"FINISHED"}


_CROSS_SECTION_COLUMN_WIDTHS = (0.18, 0.28, 0.16, 0.16, 0.10, 0.12)


def _cross_section_table_cells(layout):
    remainder = layout.row(align=True)
    remaining_width = 1.0
    cells = []
    for width in _CROSS_SECTION_COLUMN_WIDTHS[:-1]:
        split = remainder.split(factor=width / remaining_width, align=True)
        cells.append(split.column(align=True))
        remainder = split.column(align=True)
        remaining_width -= width
    cells.append(remainder)
    return cells


def _draw_lane_table_row(layout, props, lane, index: int) -> None:
    cells = _cross_section_table_cells(layout)
    cells[0].prop(lane, "zone", text="")
    width_cell = cells[1].row(align=True)
    if lane.zone in {"LEFT_SIDEWALK", "RIGHT_SIDEWALK"}:
        width_cell.prop(props, "sidewalk_width", text="")
        cells[2].label(text="Both")
        cells[3].label(text="Pedestrian")
    else:
        width_cell.prop(lane, "width", text="")
        cells[2].prop(lane, "direction", text="")
        cells[3].prop(lane, "vehicle_type", text="")
    cells[4].prop(lane, "speed_limit", text="")
    actions = cells[5].row(align=True)
    up = actions.operator("cs1_road.lane_move", text="", icon="TRIA_UP")
    up.direction, up.index = "UP", index
    down = actions.operator("cs1_road.lane_move", text="", icon="TRIA_DOWN")
    down.direction, down.index = "DOWN", index
    remove = actions.operator("cs1_road.lane_remove", text="", icon="X")
    remove.index = index


def _draw_shoulder_table_row(layout, props, side: str) -> None:
    cells = _cross_section_table_cells(layout)
    cells[0].label(text=f"Shoulder {side}")
    cells[1].prop(props, "shoulder_width", text="")
    cells[2].label(text="-")
    cells[3].label(text="Not lane")
    cells[4].label(text="-")


class CS1ROAD_OT_boundaries_sync(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.boundaries_sync", "Synchronize boundaries", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = context.scene.cs1_road_builder
        _sync_boundaries(props)
        _schedule_live_preview(props, context)
        return {"FINISHED"}


def _show_only_mode(active_mode: str) -> None:
    for mode, _, _ in MODE_ITEMS:
        collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
        if collection is not None:
            collection.hide_viewport = collection.hide_render = mode != active_mode


def _show_all_modes() -> None:
    for mode, _, _ in MODE_ITEMS:
        collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
        if collection is not None:
            collection.hide_viewport = collection.hide_render = False


class CS1ROAD_OT_build_preview(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.build_preview", "Build active mode", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = context.scene.cs1_road_builder
        try:
            build_mode(context.scene, props.mode)
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        _show_only_mode(props.mode)
        return {"FINISHED"}


class CS1ROAD_OT_build_all(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.build_all", "Build all modes", {"REGISTER", "UNDO"}

    def execute(self, context):
        try:
            for mode, _, _ in MODE_ITEMS:
                build_mode(context.scene, mode)
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        _show_all_modes()
        return {"FINISHED"}


def _runtime_lanes(props):
    _sync_lane_derived_values(props)
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
            lane.vehicle_type, lane.speed_limit,
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
        props.sidewalk_width, props.depress_roadway, props.marking_paint_width,
        props.marking_region_width, props.node_shoulder_bands,
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


def _load_lane(target, source) -> None:
    target.lane_id = str(source.get("id", source.get("lane_id", "")))
    target.name = str(source.get("name", "Lane"))
    target.zone = _enum_value(str(source.get("zone", "ROAD")), LANE_ZONE_ITEMS, "ROAD")
    target.width = float(source.get("width", target.width))
    target.direction = _enum_value(str(source.get("direction", "FORWARD")), DIRECTION_ITEMS, "FORWARD")
    target.lane_type = _enum_value(str(source.get("lane_type", "VEHICLE")), LANE_TYPE_ITEMS, "VEHICLE")
    vehicle_type = _enum_value(str(source.get("vehicle_type", "CAR")), VEHICLE_TYPE_ITEMS, "CAR")
    if target.zone == "ROAD" and target.lane_type == "VEHICLE" and vehicle_type == "NONE":
        vehicle_type = "CAR"
    target.vehicle_type = vehicle_type
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
    _sync_lane_derived_values(props)
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
        profile = str(cross.get("surface_profile", "DEPRESSED")).upper()
        props.depress_roadway = bool(cross.get("depress_roadway", profile == "DEPRESSED"))
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
        _sync_lane_derived_values(props)
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
                "curb_height": ROADWAY_DEPRESSION,
                "surface_profile": "DEPRESSED" if props.depress_roadway else "FLUSH",
            },
            "styles": {"markings": {"SOLID_WHITE": {
                "paint_width": props.marking_paint_width,
                "region_width": props.marking_region_width,
                "texture_tile": "solid_white",
            }}},
            "layout": _export_layout(props),
            "node": {
                "length": MODE_LENGTH,
                "center_split": True,
                "shoulder_bands": props.node_shoulder_bands,
                "transition_target": None if props.depress_roadway else "DEPRESSED",
            },
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
        tabs = layout.row(align=True)
        tabs.prop(props, "mode", expand=True)
        actions = layout.row(align=True)
        actions.operator("cs1_road.build_preview", text="Build selected", icon="MOD_BUILD")
        actions.operator("cs1_road.build_all", text="Build all", icon="OUTLINER_COLLECTION")


class _CS1RoadChildPanel:
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Road"
    bl_parent_id = "CS1ROAD_PT_main"


class CS1ROAD_PT_shared(_CS1RoadChildPanel, Panel):
    bl_label = "Shared settings"
    bl_idname = "CS1ROAD_PT_shared"

    def draw(self, context):
        layout, props = self.layout, context.scene.cs1_road_builder
        layout.prop(props, "depress_roadway")
        row = layout.row(align=True)
        row.prop(props, "marking_paint_width")
        row.prop(props, "marking_region_width")
        if props.marking_region_width <= props.marking_paint_width:
            layout.label(text="Texture band must include asphalt margin.", icon="ERROR")


class CS1ROAD_PT_cross_section(_CS1RoadChildPanel, Panel):
    bl_label = "Cross-section"
    bl_idname = "CS1ROAD_PT_cross_section"

    def draw(self, context):
        box, props = self.layout, context.scene.cs1_road_builder
        if not props.lanes:
            box.label(text="No lanes. Initialize the road definition.", icon="INFO")
            box.operator("cs1_road.lanes_reset", icon="FILE_REFRESH")
        header = _cross_section_table_cells(box)
        header[0].label(text="Element / lane")
        header[1].label(text="Width")
        header[2].label(text="Direction")
        header[3].label(text="Traffic")
        header[4].label(text="Speed")
        indexed_lanes = list(enumerate(props.lanes))
        left_lanes = [(index, lane) for index, lane in indexed_lanes if lane.zone == "LEFT_SIDEWALK"]
        road_lanes = [(index, lane) for index, lane in indexed_lanes if lane.zone == "ROAD"]
        right_lanes = [(index, lane) for index, lane in indexed_lanes if lane.zone == "RIGHT_SIDEWALK"]
        for index, lane in left_lanes:
            _draw_lane_table_row(box, props, lane, index)
        if not left_lanes:
            empty_left = _cross_section_table_cells(box)
            empty_left[0].label(text="Sidewalk L")
            empty_left[1].prop(props, "sidewalk_width", text="")
            empty_left[3].label(text="Not lane")
        _draw_shoulder_table_row(box, props, "L")
        for index, lane in road_lanes:
            _draw_lane_table_row(box, props, lane, index)
        _draw_shoulder_table_row(box, props, "R")
        for index, lane in right_lanes:
            _draw_lane_table_row(box, props, lane, index)
        if not right_lanes:
            empty_right = _cross_section_table_cells(box)
            empty_right[0].label(text="Sidewalk R")
            empty_right[1].prop(props, "sidewalk_width", text="")
            empty_right[3].label(text="Not lane")
        box.operator("cs1_road.lane_add", text="Add network lane", icon="ADD")
        box.label(text=f"Pedestrian lane fits inside sidewalk (-{SIDEWALK_LANE_TOTAL_INSET:.2f} m)")
        counts = {key: sum(lane.direction == key for lane in props.lanes) for key in ("FORWARD", "BACKWARD", "BOTH")}
        roadway_width, total_width, _ = _cross_section(props)
        box.label(text=(
            f"{len(props.lanes)} lanes (F{counts['FORWARD']} / B{counts['BACKWARD']} / P{counts['BOTH']}) | "
            f"{roadway_width:.2f} m road / {total_width:.2f} m total | 64 m / 20 slices"
        ))


class CS1ROAD_PT_markings(_CS1RoadChildPanel, Panel):
    bl_label = "Road lines"
    bl_idname = "CS1ROAD_PT_markings"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        layout, props = self.layout, context.scene.cs1_road_builder
        layout.label(text="Lines on road sections; intersections have none.")
        layout.template_list("CS1ROAD_UL_boundaries", "", props, "boundaries", props, "active_boundary_index", rows=6)
        layout.operator("cs1_road.boundaries_sync", text="Refresh line positions", icon="FILE_REFRESH")


class CS1ROAD_PT_mode(_CS1RoadChildPanel, Panel):
    bl_label = "Selected mode"
    bl_idname = "CS1ROAD_PT_mode"
    bl_options = {"DEFAULT_CLOSED"}

    def draw_header(self, context):
        labels = {key: label for key, label, _ in MODE_ITEMS}
        self.layout.label(text=labels.get(context.scene.cs1_road_builder.mode, ""))

    def draw(self, context):
        mode_box, props = self.layout, context.scene.cs1_road_builder
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
        else:
            mode_box.label(text="No mode-specific settings.")


class CS1ROAD_PT_files(_CS1RoadChildPanel, Panel):
    bl_label = "Road definition files"
    bl_idname = "CS1ROAD_PT_files"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        row = self.layout.row(align=True)
        row.operator("cs1_road.import_spec", icon="IMPORT")
        row.operator("cs1_road.export_spec", icon="EXPORT")


class CS1ROAD_PT_runtime(_CS1RoadChildPanel, Panel):
    bl_label = "Runtime preview"
    bl_idname = "CS1ROAD_PT_runtime"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        runtime, props = self.layout, context.scene.cs1_road_builder
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


class CS1ROAD_PT_development(_CS1RoadChildPanel, Panel):
    bl_label = "Development"
    bl_idname = "CS1ROAD_PT_development"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        development = self.layout
        development.operator("script.reload", text="Reload Scripts", icon="FILE_REFRESH")
        development.label(text="Rebuild generated meshes after reloading.", icon="INFO")
        development.label(text="Generated objects remain editable meshes.", icon="EDITMODE_HLT")


CLASSES = (
    CS1RoadLane, CS1RoadBoundary, CS1RoadBuilderProperties, CS1ROAD_UL_boundaries,
    CS1ROAD_OT_lane_add, CS1ROAD_OT_lanes_reset, CS1ROAD_OT_lane_remove, CS1ROAD_OT_lane_move,
    CS1ROAD_OT_boundaries_sync,
    CS1ROAD_OT_build_preview, CS1ROAD_OT_build_all,
    CS1ROAD_OT_import_spec, CS1ROAD_OT_export_spec, CS1ROAD_OT_export_runtime,
    CS1ROAD_OT_export_runtime_prop,
    CS1ROAD_PT_main, CS1ROAD_PT_shared, CS1ROAD_PT_cross_section,
    CS1ROAD_PT_markings, CS1ROAD_PT_mode, CS1ROAD_PT_files,
    CS1ROAD_PT_runtime, CS1ROAD_PT_development,
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
    if bpy.app.timers.is_registered(_live_preview_timer):
        bpy.app.timers.unregister(_live_preview_timer)
    _AUTO_EXPORT_STATE.clear()
    _LIVE_PREVIEW_PENDING.clear()
    del bpy.types.Scene.cs1_road_builder
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)
