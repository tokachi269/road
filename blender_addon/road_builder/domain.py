"""Blender-independent derivation used by the current road prototype.

This isolates behavior that already existed in the Blender add-on. It does not
declare the current persisted schema to be the final product model.
"""

from __future__ import annotations

from typing import Iterable, Protocol


MODE_LENGTH = 64.0
SEGMENT_SLICES = 20
NODE_SLICES = 8
ROADWAY_DEPRESSION = 0.30
SIDEWALK_LANE_TOTAL_INSET = 0.50
MEDIAN_END_OVERHANG = 0.002
MEDIAN_Z_FIGHT_EPSILON = 0.002


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
