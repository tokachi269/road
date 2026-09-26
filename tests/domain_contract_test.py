from __future__ import annotations

import importlib.util
from dataclasses import dataclass
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
DOMAIN_PATH = ROOT / "blender_addon" / "road_builder" / "domain.py"
SPEC = importlib.util.spec_from_file_location("road_builder_domain", DOMAIN_PATH)
assert SPEC is not None and SPEC.loader is not None
domain = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(domain)


@dataclass
class Lane:
    lane_id: str
    name: str
    zone: str
    width: float
    direction: str


def road_lane(index: int, direction: str) -> Lane:
    return Lane(f"lane-{index}", f"Lane {index}", "ROAD", 3.25, direction)


class DomainContractTest(unittest.TestCase):
    def test_two_three_and_four_lane_widths_are_derived_from_lane_input(self) -> None:
        for count in (2, 3, 4):
            lanes = [road_lane(index, "FORWARD") for index in range(count)]
            roadway, total, half = domain.cross_section_widths(lanes, 0.5, 2.0)
            self.assertAlmostEqual(roadway, count * 3.25 + 1.0)
            self.assertAlmostEqual(total, roadway + 4.0)
            self.assertAlmostEqual(half, total * 0.5)

    def test_boundary_identity_is_derived_from_adjacent_stable_lane_ids(self) -> None:
        lanes = [
            road_lane(10, "BACKWARD"),
            road_lane(20, "BACKWARD"),
            road_lane(30, "FORWARD"),
            road_lane(40, "FORWARD"),
        ]
        boundaries = domain.expected_boundaries(lanes, 0.5)
        ids = [item[0] for item in boundaries]
        self.assertEqual(
            ids,
            [
                "boundary-left-curb",
                "boundary-left-carriageway",
                "boundary-lane-10-lane-20",
                "boundary-lane-20-lane-30",
                "boundary-lane-30-lane-40",
                "boundary-right-carriageway",
                "boundary-right-curb",
            ],
        )
        roles = {item[0]: item[6] for item in boundaries}
        self.assertEqual(roles["boundary-lane-10-lane-20"], "LANE_SEPARATOR")
        self.assertEqual(roles["boundary-lane-20-lane-30"], "CENTER_LINE")

    def test_inserting_lane_preserves_unaffected_boundary_identity(self) -> None:
        before = [road_lane(10, "BACKWARD"), road_lane(30, "FORWARD")]
        after = [
            road_lane(10, "BACKWARD"),
            road_lane(20, "BACKWARD"),
            road_lane(30, "FORWARD"),
        ]
        before_ids = {item[0] for item in domain.expected_boundaries(before, 0.5)}
        after_ids = {item[0] for item in domain.expected_boundaries(after, 0.5)}
        self.assertTrue(
            {
                "boundary-left-curb",
                "boundary-left-carriageway",
                "boundary-right-carriageway",
                "boundary-right-curb",
            }.issubset(before_ids & after_ids)
        )
        self.assertNotIn("boundary-lane-10-lane-30", after_ids)
        self.assertIn("boundary-lane-10-lane-20", after_ids)
        self.assertIn("boundary-lane-20-lane-30", after_ids)

    def test_cs1_length_and_slice_contract_is_owned_by_domain(self) -> None:
        self.assertEqual(domain.MODE_LENGTH, 64.0)
        self.assertEqual(domain.SEGMENT_SLICES, 20)
        self.assertEqual(domain.NODE_SLICES, 8)

    def test_depression_and_lane_offsets_have_one_domain_owner(self) -> None:
        self.assertEqual(domain.ROADWAY_DEPRESSION, 0.30)
        self.assertEqual(domain.roadway_depression(True), 0.30)
        self.assertEqual(domain.roadway_depression(False), 0.0)
        self.assertEqual(domain.lane_vertical_offset("LEFT_SIDEWALK", True), 0.30)
        self.assertEqual(domain.lane_vertical_offset("RIGHT_SIDEWALK", False), 0.0)
        self.assertEqual(domain.lane_vertical_offset("ROAD", True), 0.0)
        self.assertEqual(domain.SIDEWALK_LANE_TOTAL_INSET, 0.50)
        self.assertAlmostEqual(domain.sidewalk_lane_width(2.5), 2.0)
        self.assertAlmostEqual(domain.sidewalk_lane_width(3.0), 2.5)
        self.assertAlmostEqual(domain.sidewalk_lane_width(3.0, 2), 1.25)


if __name__ == "__main__":
    unittest.main()
