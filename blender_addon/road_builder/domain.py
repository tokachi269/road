"""Blender-independent derivation used by the current road prototype.

This isolates behavior that already existed in the Blender add-on. It does not
declare the current persisted schema to be the final product model.
"""

from __future__ import annotations

from copy import deepcopy
from typing import Any, Iterable, NamedTuple, Protocol


MODE_LENGTH = 64.0
SEGMENT_SLICES = 20
NODE_SLICES = 8
ROADWAY_DEPRESSION = 0.30
SIDEWALK_LANE_TOTAL_INSET = 0.50
MEDIAN_END_OVERHANG = 0.002
MEDIAN_Z_FIGHT_EPSILON = 0.002
PARKING_LANE_DEFAULT_WIDTH = 2.0
AUTHORING_SCHEMA_VERSION = 4
PROFILE_SCHEMA_VERSION = 1
DEFAULT_PROFILE_ID = "urban.default"
GLOBAL_LANE_DEFAULTS = {
    "road_speed_limit": 1.0,
    "pedestrian_speed_limit": 0.1,
    "stop_offset": 0.0,
    "allow_connect": True,
}


class ResolvedSetting(NamedTuple):
    value: Any
    source: str


def resolve_setting(
    global_value: Any,
    profile_id: str,
    profile_has_override: bool,
    profile_value: Any,
    road_has_override: bool = False,
    road_value: Any = None,
) -> ResolvedSetting:
    """Resolve the fixed global -> profile -> road ownership chain."""
    if road_has_override:
        return ResolvedSetting(road_value, "road override")
    if profile_has_override:
        return ResolvedSetting(profile_value, profile_id)
    return ResolvedSetting(global_value, "global")


def redundant_override(override_value: Any, inherited_value: Any) -> bool:
    """Return whether an explicit override carries no effective difference."""
    if isinstance(override_value, float) or isinstance(inherited_value, float):
        try:
            return abs(float(override_value) - float(inherited_value)) <= 1e-8
        except (TypeError, ValueError):
            return False
    return override_value == inherited_value


def migrate_authoring_spec_v3_to_v4(
    source: dict[str, Any], profile_id: str,
) -> tuple[dict[str, Any], dict[str, Any]]:
    """Preserve every v3 lane value as an explicit v4 road override.

    The returned IMT mapping is intentionally separate.  The Blender adapter
    owns profile creation because a single imported road must not silently
    mutate an existing shared profile.
    """
    data = deepcopy(source)
    version = int(data.get("schema_version", 2))
    if version >= AUTHORING_SCHEMA_VERSION:
        return data, {}
    legacy_imt = deepcopy(data.get("styles", {}).get("imt_preview", {}))
    data["schema_version"] = AUTHORING_SCHEMA_VERSION
    data["profile_id"] = profile_id
    for lane in data.get("lanes", []):
        overrides = dict(lane.get("overrides", {}))
        for field in ("speed_limit", "stop_offset", "allow_connect"):
            if field in lane:
                overrides[field] = lane.pop(field)
        if overrides:
            lane["overrides"] = overrides
    styles = data.get("styles")
    if isinstance(styles, dict):
        styles.pop("imt_preview", None)
    return data, legacy_imt


class CrossSectionAllocation(NamedTuple):
    """Resolved use of the fixed space between the two sidewalks."""

    target_width: float
    lane_width: float
    median_width: float
    shoulder_width: float
    actual_width: float
    overflow: float

    @property
    def fits(self) -> bool:
        return self.overflow <= 1e-8


def roadway_depression(enabled: bool) -> float:
    """Return the single standard carriageway depression used by the prototype."""
    return ROADWAY_DEPRESSION if enabled else 0.0


def lane_vertical_offset(zone: str, roadway_is_depressed: bool) -> float:
    """Derive the CS1 lane height from lane zone and the road-level switch."""
    if zone in {"LEFT_SIDEWALK", "RIGHT_SIDEWALK"}:
        return roadway_depression(roadway_is_depressed)
    return 0.0


def sidewalk_lane_width(surface_width: float, lane_count: int = 1) -> float:
    """Fit pedestrian network lanes inside the sidewalk surface."""
    usable_width = max(surface_width - SIDEWALK_LANE_TOTAL_INSET, 0.05)
    return usable_width / max(lane_count, 1)


def allocate_cross_section(
    lanes: Iterable[LaneLike],
    between_sidewalks_width: float,
    median_width: float = 0.0,
) -> CrossSectionAllocation:
    """Allocate a fixed sidewalk-to-sidewalk span without hiding overflow.

    Lane and median dimensions remain authoritative.  Any non-negative
    remainder is divided equally between the two roadside shoulders.  If the
    requested content does not fit, shoulders become zero and ``overflow``
    tells the Blender adapter how much to warn about; generation is not
    rejected and dimensions are not silently shrunk.
    """
    lane_width = max(
        sum(lane.width for lane in lanes if lane.zone == "ROAD"), 0.01
    )
    target_width = max(float(between_sidewalks_width), 0.01)
    resolved_median = max(float(median_width), 0.0)
    required_width = lane_width + resolved_median
    remainder = target_width - required_width
    shoulder_width = max(remainder * 0.5, 0.0)
    overflow = max(-remainder, 0.0)
    return CrossSectionAllocation(
        target_width=target_width,
        lane_width=lane_width,
        median_width=resolved_median,
        shoulder_width=shoulder_width,
        actual_width=max(target_width, required_width),
        overflow=overflow,
    )


def parking_fits(
    lanes: Iterable[LaneLike],
    between_sidewalks_width: float,
    median_width: float,
    parking_lane_width: float = PARKING_LANE_DEFAULT_WIDTH,
) -> bool:
    """Return whether one parking lane on each side fits the fixed span."""
    allocation = allocate_cross_section(
        lanes, between_sidewalks_width, median_width,
    )
    return allocation.shoulder_width + 1e-8 >= max(parking_lane_width, 0.0)


def marking_rule(
    boundary_role: str,
    marking_role: str,
    roadside_lines: bool,
    lane_separator_style: str,
    center_line_style: str,
    line_capable: bool = True,
) -> tuple[bool, str]:
    """Resolve one boundary from the road-level line policy."""
    if boundary_role == "MEDIAN_EDGE":
        return False, "SOLID_WHITE"
    if marking_role == "CARRIAGEWAY_EDGE":
        return bool(roadside_lines and line_capable), "SOLID_WHITE"
    if boundary_role == "LANE_DIVIDER" and marking_role == "CENTER_LINE":
        return True, center_line_style
    if boundary_role == "LANE_DIVIDER" and marking_role == "LANE_SEPARATOR":
        return True, lane_separator_style
    return False, "SOLID_WHITE"


class LaneLike(Protocol):
    lane_id: str
    name: str
    zone: str
    width: float
    direction: str


def median_split_index(lanes: Iterable[LaneLike]) -> int:
    """Return the first boundary between opposing road-lane directions.

    The returned index is relative to the road-lane sequence and identifies
    where the median strip is inserted.  A divided road needs traffic on both
    sides; callers surface the ValueError instead of silently placing a median
    through an arbitrary lane.
    """
    road_lanes = [lane for lane in lanes if lane.zone == "ROAD"]
    for index, (left, right) in enumerate(zip(road_lanes, road_lanes[1:]), 1):
        if left.direction != right.direction:
            return index
    raise ValueError("Median needs adjacent road lanes with opposing directions")


def strip_id(lane: LaneLike) -> str:
    if lane.zone == "LEFT_SIDEWALK":
        return "strip-left-sidewalk"
    if lane.zone == "RIGHT_SIDEWALK":
        return "strip-right-sidewalk"
    return f"strip-{lane.lane_id}"


def expected_boundaries(
    lanes: Iterable[LaneLike], shoulder_width: float, median_enabled: bool = False
) -> list[tuple[str, str, str, str, str, bool, str]]:
    """Derive stable boundary identities and default marking roles.

    The current prototype derives boundary identity from adjacent lane IDs.
    Whether this becomes the final persisted contract remains a design choice.
    """
    road_lanes = [lane for lane in lanes if lane.zone == "ROAD"]
    if not road_lanes:
        return []

    expected: list[tuple[str, str, str, str, str, bool, str]] = []
    left_road_strip = strip_id(road_lanes[0])
    right_road_strip = strip_id(road_lanes[-1])
    if shoulder_width > 1e-8:
        expected.extend(
            (
                (
                    "boundary-left-curb",
                    "Left curb",
                    "CURB",
                    "strip-left-sidewalk",
                    "strip-left-shoulder",
                    False,
                    "CARRIAGEWAY_EDGE",
                ),
                (
                    "boundary-left-carriageway",
                    "Left roadside line",
                    "CARRIAGEWAY_EDGE",
                    "strip-left-shoulder",
                    left_road_strip,
                    True,
                    "CARRIAGEWAY_EDGE",
                ),
            )
        )
    else:
        expected.append(
            (
                "boundary-left-curb",
                "Left curb / roadside",
                "CURB",
                "strip-left-sidewalk",
                left_road_strip,
                True,
                "CARRIAGEWAY_EDGE",
            )
        )

    split_index = median_split_index(road_lanes) if median_enabled else -1
    for boundary_index, (left, right) in enumerate(
        zip(road_lanes, road_lanes[1:]), 1
    ):
        if boundary_index == split_index:
            expected.extend(
                (
                    (
                        "boundary-median-left",
                        "Median left edge",
                        "MEDIAN_EDGE",
                        strip_id(left),
                        "strip-median",
                        False,
                        "CENTER_LINE",
                    ),
                    (
                        "boundary-median-right",
                        "Median right edge",
                        "MEDIAN_EDGE",
                        "strip-median",
                        strip_id(right),
                        False,
                        "CENTER_LINE",
                    ),
                )
            )
            continue
        marking_role = (
            "CENTER_LINE"
            if left.direction != right.direction
            else "LANE_SEPARATOR"
        )
        expected.append(
            (
                f"boundary-{left.lane_id}-{right.lane_id}",
                f"{left.name} / {right.name}",
                "LANE_DIVIDER",
                strip_id(left),
                strip_id(right),
                True,
                marking_role,
            )
        )

    if shoulder_width > 1e-8:
        expected.extend(
            (
                (
                    "boundary-right-carriageway",
                    "Right roadside line",
                    "CARRIAGEWAY_EDGE",
                    right_road_strip,
                    "strip-right-shoulder",
                    True,
                    "CARRIAGEWAY_EDGE",
                ),
                (
                    "boundary-right-curb",
                    "Right curb",
                    "CURB",
                    "strip-right-shoulder",
                    "strip-right-sidewalk",
                    False,
                    "CARRIAGEWAY_EDGE",
                ),
            )
        )
    else:
        expected.append(
            (
                "boundary-right-curb",
                "Right curb / roadside",
                "CURB",
                right_road_strip,
                "strip-right-sidewalk",
                True,
                "CARRIAGEWAY_EDGE",
            )
        )
    return expected


def cross_section_widths(
    lanes: Iterable[LaneLike], shoulder_width: float, sidewalk_width: float,
    median_width: float = 0.0,
) -> tuple[float, float, float]:
    road_lane_width = max(
        sum(lane.width for lane in lanes if lane.zone == "ROAD"), 0.01
    )
    roadway_width = road_lane_width + 2.0 * shoulder_width + max(median_width, 0.0)
    total_width = roadway_width + 2.0 * sidewalk_width
    return roadway_width, total_width, total_width * 0.5
