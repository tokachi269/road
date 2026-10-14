"""Independent physical expectations for placement-time cross sections.

The expected metre values below do not call production preset/allocation code.
These tests are NOT evidence for CS1 node drawing, zoning mutation or save/load.
"""
from dataclasses import dataclass
import csv
import itertools
import json
from pathlib import Path
import unittest

from tests.domain_contract_test import domain

ROOT = Path(__file__).resolve().parents[1]


@dataclass(frozen=True)
class Lane:
    lane_id: str
    zone: str
    width: float
    direction: str


def lanes(backward, forward):
    return tuple(
        Lane(f"b{i}", "ROAD", 3.0, "BACKWARD") for i in range(backward)
    ) + tuple(Lane(f"f{i}", "ROAD", 3.0, "FORWARD") for i in range(forward))


class PlacementSectionTest(unittest.TestCase):
    def test_height_transition_has_two_endpoint_states_not_a_persistent_level(self):
        for backward, forward in ((b, f) for b in range(5) for f in range(max(1, b), 5)):
            road = (Lane("ped-left", "LEFT_SIDEWALK", 2.0, "BOTH"),
                    *lanes(backward, forward), Lane("ped-right", "RIGHT_SIDEWALK", 2.0, "BOTH"))
            options = dict(width="WIDE", roadside="WIDE",
                           divider="OFFSET_LEFT" if backward else "CENTERED",
                           median="STANDARD" if backward else "NONE")
            start = domain.resolve_placement_section(road, domain.RoadPlacementOptions(**options, level="DEPRESSED"))
            end = domain.resolve_placement_section(road, domain.RoadPlacementOptions(**options, level="FLUSH"))
            transition = domain.resolve_height_transition(start, end)
            for fraction, expected in ((0, -0.3), (0.25, -0.225), (0.5, -0.15), (0.75, -0.075), (1, 0.0)):
                self.assertAlmostEqual(transition.road_height(fraction), expected)
            endpoints = domain.height_transition_lane_endpoints(transition, road)
            self.assertEqual(len(endpoints), backward + forward + 2)
            for a, b in endpoints:
                self.assertEqual((a.lane_id, a.position, a.width), (b.lane_id, b.position, b.width))
                self.assertEqual((a.vertical_offset, b.vertical_offset),
                                 (0.0, 0.0) if a.lane_id.startswith("ped-") else (-0.3, 0.0))
            reverse = domain.resolve_height_transition(end, start)
            self.assertNotEqual(transition.variant_identity("road"), reverse.variant_identity("road"))
            self.assertNotEqual(transition.variant_identity("road"), start.variant_identity("road"))
            self.assertAlmostEqual(reverse.road_height(0.25), -0.075)
            with self.assertRaises(ValueError):
                domain.resolve_height_transition(start, start)
        start = self.resolve(1, 1)
        for target in (self.resolve(1, 1, level="FLUSH", width="WIDE"),
                       self.resolve(1, 1, level="FLUSH", divider="OFFSET_RIGHT"),
                       self.resolve(1, 1, level="FLUSH", median="STANDARD"),
                       start._replace(roadway_z=0.1)):
            with self.assertRaises(ValueError):
                domain.resolve_height_transition(start, target)
        transition = domain.resolve_height_transition(start, start._replace(roadway_z=0.0))
        for invalid in (-0.1, 1.1, float("nan"), float("inf"), True):
            with self.assertRaises(ValueError):
                transition.road_height(invalid)

    def test_native_pavement_reference_is_not_an_actual_left_or_right_curb(self):
        section = self.resolve(1, 2, roadside="NONE")
        self.assertEqual(section.node_reference_pavement_width, 11.5)
        self.assertEqual((section.curb_left, section.curb_right), (-3.0, 6.0))
        self.assertEqual((section.sidewalk_left, section.sidewalk_right), (13.0, 10.0))
        symmetric = self.resolve(2, 2, roadside="NONE")
        self.assertEqual(symmetric.node_reference_pavement_width, 10.0)
        mirrored = self.resolve(2, 1, roadside="NONE")
        self.assertEqual(mirrored.node_reference_pavement_width, 11.5)

    def test_placement_menu_covers_the_semantic_choices_for_each_topology(self):
        # The menu oracle is deliberately explicit and independent of the
        # production preset dictionaries. No case is silently mapped to another.
        for backward, forward in ((0, n) for n in range(1, 5)):
            matrix = domain.placement_option_matrix(lanes(backward, forward))
            expected = set(itertools.product(
                ("NARROW", "STANDARD", "WIDE"), ("CENTERED",), ("NONE",),
                ("NONE", "STANDARD", "WIDE"), ("FLUSH", "DEPRESSED"),
            ))
            self.assertEqual({tuple(option) for option, section in matrix}, expected)
            self.assertEqual(len(matrix), 18)
            self.assertTrue(all(section.traffic_boundary is None for option, section in matrix))
        expected = set(itertools.product(
            ("NARROW", "STANDARD", "WIDE"),
            ("CENTERED", "OFFSET_LEFT", "OFFSET_RIGHT"),
            ("NONE", "STANDARD", "WIDE"), ("NONE", "STANDARD", "WIDE"),
            ("FLUSH", "DEPRESSED"),
        ))
        for backward in range(1, 5):
            for forward in range(backward, 5):
                matrix = domain.placement_option_matrix(lanes(backward, forward))
                self.assertEqual({tuple(option) for option, section in matrix}, expected)
                self.assertEqual(len(matrix), 162)
                for option, section in matrix:
                    self.assertEqual(section.traffic_boundary,
                                     {"CENTERED": 0.0, "OFFSET_LEFT": -1.5,
                                      "OFFSET_RIGHT": 1.5}[option.divider])
                    self.assertEqual(section.roadway_z, 0.0 if option.level == "FLUSH" else -0.3)
        with self.assertRaises(ValueError):
            domain.placement_option_matrix(())
        with self.assertRaises(ValueError):
            domain.placement_option_matrix((Lane("bad", "ROAD", 3.0, "BOTH"),))

    def test_network_lane_geometry_uses_actual_sidewalks_and_surface_height(self):
        source = (Lane("ped-left", "LEFT_SIDEWALK", 2.0, "BOTH"),
                  *lanes(1, 2), Lane("ped-right", "RIGHT_SIDEWALK", 2.0, "BOTH"))
        section = domain.resolve_placement_section(source, domain.RoadPlacementOptions(roadside="NONE"))
        geometry = domain.placement_lane_geometry(section, source)
        self.assertEqual([(v.lane_id, v.position, v.width, v.vertical_offset) for v in geometry],
                         [("ped-left", -9.5, 12.5, 0.0), ("b0", -1.5, 3.0, -0.3),
                          ("f0", 1.5, 3.0, -0.3), ("f1", 4.5, 3.0, -0.3),
                          ("ped-right", 11.0, 9.5, 0.0)])
        flush = domain.resolve_placement_section(source, domain.RoadPlacementOptions(level="FLUSH"))
        self.assertEqual({v.vertical_offset for v in domain.placement_lane_geometry(flush, source)}, {0.0})

    def test_new_median_has_generated_shape_unless_base_explicitly_uses_mesh(self):
        self.assertFalse(domain.placement_median_uses_mesh("NONE"))
        self.assertFalse(domain.placement_median_uses_mesh("CURB"))
        self.assertTrue(domain.placement_median_uses_mesh("MESH"))
        with self.assertRaises(ValueError):
            domain.placement_median_uses_mesh("UNKNOWN")

    def resolve(self, backward=1, forward=1, **options):
        return domain.resolve_placement_section(
            lanes(backward, forward), domain.RoadPlacementOptions(**options)
        )

    def test_centered_asymmetric_traffic_is_not_centered_lane_pack(self):
        section = self.resolve(1, 2, width="NARROW", roadside="NONE")
        self.assertEqual(section.total_width, 32.0)  # right sidewalk must retain 2.5 m
        self.assertEqual(section.traffic_boundary, 0.0)
        self.assertEqual([lane.position for lane in section.lanes], [-1.5, 1.5, 4.5])
        self.assertEqual((section.curb_left, section.curb_right), (-3.0, 6.0))
        self.assertEqual((section.sidewalk_left, section.sidewalk_right), (13.0, 10.0))
        self.assertEqual(section.center_line_position, 0.0)

    def test_offset_moves_topology_and_boundaries_not_just_paint(self):
        section = self.resolve(2, 2, divider="OFFSET_RIGHT", median="STANDARD",
                               roadside="STANDARD", width="NARROW")
        self.assertEqual(section.total_width, 32.0)  # requested 16 cannot fit
        self.assertEqual(section.traffic_boundary, 1.5)
        self.assertEqual((section.median_left, section.median_right), (1.0, 2.0))
        self.assertEqual([lane.position for lane in section.lanes], [-3.5, -0.5, 3.5, 6.5])
        self.assertEqual((section.curb_left, section.curb_right), (-5.5, 8.5))
        self.assertEqual((section.sidewalk_left, section.sidewalk_right), (10.5, 7.5))
        self.assertIsNone(section.center_line_position)  # median is not painted center
        self.assertEqual([b.role for b in section.boundaries],
                         ["SAME_DIRECTION", "MEDIAN_EDGE", "MEDIAN_EDGE", "SAME_DIRECTION"])
        self.assertEqual([b.position for b in section.boundaries], [-2.0, 1.0, 2.0, 5.0])

    def test_one_way_has_no_opposing_boundary_or_invented_median(self):
        section = self.resolve(0, 3, width="NARROW", roadside="NONE")
        self.assertIsNone(section.traffic_boundary)
        self.assertIsNone(section.center_line_position)
        self.assertEqual([lane.position for lane in section.lanes], [-3.0, 0.0, 3.0])
        self.assertEqual([b.role for b in section.boundaries], ["SAME_DIRECTION"] * 2)
        for options in ({"median": "STANDARD"}, {"divider": "OFFSET_LEFT"}):
            with self.assertRaises(ValueError):
                self.resolve(0, 2, **options)

    def test_full_semantic_matrix_has_fixed_lanes_and_explicit_allocation(self):
        # Separate physical oracle: occupied endpoints + minimum 2.5 m sidewalks;
        # legal network widths and preset metre values are literal expectations.
        for back, forward in itertools.product(range(1, 5), repeat=2):
            for width, divider, median, roadside, level in itertools.product(
                ("NARROW", "STANDARD", "WIDE"),
                ("CENTERED", "OFFSET_LEFT", "OFFSET_RIGHT"),
                ("NONE", "STANDARD", "WIDE"),
                ("NONE", "STANDARD", "WIDE"),
                ("FLUSH", "DEPRESSED"),
            ):
                with self.subTest(topology=(back, forward), width=width, divider=divider,
                                  median=median, roadside=roadside, level=level):
                    section = self.resolve(back, forward, width=width, divider=divider,
                                           median=median, roadside=roadside, level=level)
                    center = {"CENTERED": 0.0, "OFFSET_LEFT": -1.5, "OFFSET_RIGHT": 1.5}[divider]
                    med = {"NONE": 0.0, "STANDARD": 1.0, "WIDE": 2.0}[median]
                    shoulder = {"NONE": 0.0, "STANDARD": 0.5, "WIDE": 2.0}[roadside]
                    expected_left = center - med / 2 - back * 3.0 - shoulder
                    expected_right = center + med / 2 + forward * 3.0 + shoulder
                    requested = {"NARROW": 16.0, "STANDARD": 32.0, "WIDE": 48.0}[width]
                    legal = [w for w in (16.0, 32.0, 48.0, 64.0)
                             if w >= requested and -w / 2 + 2.5 <= expected_left
                             and expected_right <= w / 2 - 2.5]
                    self.assertEqual(section.total_width, legal[0])
                    self.assertEqual((section.curb_left, section.curb_right),
                                     (expected_left, expected_right))
                    self.assertEqual(section.roadway_z, 0.0 if level == "FLUSH" else -0.3)
                    self.assertEqual(section.roadside_left, shoulder)
                    self.assertEqual(section.roadside_right, shoulder)
                    self.assertEqual([lane.width for lane in section.lanes], [3.0] * (back + forward))
                    self.assertGreaterEqual(section.sidewalk_left, 2.5)
                    self.assertGreaterEqual(section.sidewalk_right, 2.5)
                    left_end = section.lanes[back - 1].position + 1.5
                    right_start = section.lanes[back].position - 1.5
                    self.assertEqual((left_end + right_start) / 2, section.traffic_boundary)
                    self.assertAlmostEqual(right_start - left_end, med)

    def test_identity_uses_resolved_geometry_not_the_requested_width_or_paint(self):
        narrow = self.resolve(4, 4, width="NARROW")
        standard = self.resolve(4, 4, width="STANDARD")
        self.assertEqual(narrow.total_width, 32.0)
        self.assertEqual(narrow.variant_identity("jp-4x4l"), standard.variant_identity("jp-4x4l"))
        self.assertNotEqual(narrow.variant_identity("jp-4x4l"), narrow.variant_identity("another-base"))
        self.assertNotEqual(narrow.variant_identity("jp-4x4l"),
                            self.resolve(4, 4, level="FLUSH").variant_identity("jp-4x4l"))
        self.assertEqual(narrow.variant_identity("jp-4x4l"),
                         self.resolve(4, 4).variant_identity("jp-4x4l"))

    def test_mirror_preserves_width_and_reflects_the_actual_traffic_boundary(self):
        left = self.resolve(1, 4, divider="OFFSET_LEFT", median="WIDE")
        right = self.resolve(4, 1, divider="OFFSET_RIGHT", median="WIDE")
        self.assertEqual(left.total_width, right.total_width)
        self.assertEqual(left.traffic_boundary, -right.traffic_boundary)
        self.assertEqual(left.curb_left, -right.curb_right)
        self.assertEqual(left.sidewalk_left, right.sidewalk_right)

    def test_nonstandard_lane_widths_remain_inputs_and_ids_stay_stable(self):
        source = (Lane("custom-left", "ROAD", 3.25, "FORWARD"),
                  Lane("custom-right", "ROAD", 2.75, "BACKWARD"))
        section = domain.resolve_placement_section(source, domain.RoadPlacementOptions())
        self.assertEqual([(lane.lane_id, lane.width, lane.direction) for lane in section.lanes],
                         [("custom-left", 3.25, "FORWARD"), ("custom-right", 2.75, "BACKWARD")])
        self.assertEqual([lane.position for lane in section.lanes], [-1.625, 1.375])
        self.assertEqual(section.boundaries[0].position, 0.0)
        self.assertEqual(section.boundaries[0].role, "OPPOSING_CENTER")

    def test_all_one_way_sizes_keep_center_controls_inapplicable(self):
        for count, width, roadside, level in itertools.product(
            range(1, 5), ("NARROW", "STANDARD", "WIDE"),
            ("NONE", "STANDARD", "WIDE"), ("FLUSH", "DEPRESSED"),
        ):
            section = self.resolve(0, count, width=width, roadside=roadside, level=level)
            self.assertIsNone(section.traffic_boundary)
            self.assertEqual(section.curb_left, -section.curb_right)
            self.assertEqual(section.sidewalk_left, section.sidewalk_right)
            self.assertEqual(len(section.boundaries), count - 1)

    def test_unknown_options_and_unsupported_topology_do_not_get_guessed(self):
        for option in ("width", "divider", "median", "roadside", "level"):
            with self.assertRaises(ValueError):
                self.resolve(**{option: "GUESS"})
        for bad in (
            (), (Lane("x", "ROAD", float("nan"), "FORWARD"),),
            (Lane("x", "ROAD", 0.0, "FORWARD"),),
            (Lane("x", "ROAD", 3.0, "BOTH"),),
            lanes(1, 1) + (Lane("extra", "ROAD", 3.0, "BACKWARD"),),
        ):
            with self.assertRaises(ValueError):
                domain.resolve_placement_section(bad, domain.RoadPlacementOptions())

    def test_existing_fourteen_roads_are_distinct_traffic_topologies(self):
        with (ROOT / "catalog/roads.tsv").open(encoding="utf-8", newline="") as stream:
            rows = list(csv.DictReader(stream, delimiter="\t"))
        tuples = [(json.loads(row["metadata"])["backward_lanes"],
                   json.loads(row["metadata"])["forward_lanes"]) for row in rows]
        expected = {(b, f) for b in range(5) for f in range(max(1, b), 5)}
        self.assertEqual(set(tuples), expected)
        self.assertEqual(len(tuples), len(set(tuples)))


if __name__ == "__main__":
    unittest.main()
