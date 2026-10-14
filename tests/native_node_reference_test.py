"""Independent reference for the shipped RoadBridge NET_NODE lateral equation.

This is the glcore program from the installed resources.assets, not a call to
production geometry/preset code. It proves the shader parameter mapping only;
control matrices, the active Direct3D program and rendered continuity still need
game evidence.
"""
import unittest


def lateral_parameter(x, total_width, reference_pavement, side_pavement_ratio,
                      neighbour_width_scale, longitudinal):
    a = abs(x) / total_width
    road = 0.5 - reference_pavement / total_width
    pavement = reference_pavement / total_width
    clamp = lambda value: max(0.0, min(1.0, value))
    target = (clamp(a / road) * (0.5 - side_pavement_ratio)
              + clamp((a - road) / pavement) * side_pavement_ratio
              + clamp(a - 0.5) * neighbour_width_scale)
    blend = a + longitudinal * longitudinal * (target - a)
    return (blend if x >= 0 else -blend) + 0.5


class NativeNodeReferenceTest(unittest.TestCase):
    def test_equal_reference_does_not_require_symmetric_authored_curbs(self):
        # 1+2, actual curbs -3/+6, 13/10 m sidewalks. The single shader
        # reference is 11.5 m; it is NOT a replacement for either actual curb.
        for x in (-16, -9.5, -3, -1.5, 0, 1.5, 4.5, 6, 11, 16):
            for longitudinal in (0, 0.25, 0.5, 0.75, 1):
                self.assertAlmostEqual(lateral_parameter(x, 32, 11.5, 11.5 / 32, 1, longitudinal),
                                       x / 32 + 0.5, places=12)

    def test_a_shared_scalar_is_not_proof_of_mixed_curb_continuity(self):
        # Both total widths are 32. The neighbour has symmetric curbs +/-12
        # (pavement 4 m). In an idealized rectangular control frame, the
        # authored curb interpolation should be -7.5/+9 at the halfway profile.
        # Native scalar averaging yields -5.5/+9.260869... instead. This is a
        # counterexample to claiming the reference alone solves mixed sections.
        left = (lateral_parameter(-3, 32, 11.5, (11.5 + 4) / 64, 1, 1) - 0.5) * 32
        right = (lateral_parameter(6, 32, 11.5, (11.5 + 4) / 64, 1, 1) - 0.5) * 32
        self.assertAlmostEqual(left, -5.5)
        self.assertAlmostEqual(right, 9.26086956521739)
        self.assertGreater(abs(left - (-7.5)), 1.9)
        self.assertGreater(abs(right - 9), 0.25)


if __name__ == "__main__":
    unittest.main()
