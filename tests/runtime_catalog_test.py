import json
import tempfile
import unittest
from pathlib import Path

from generator.runtime_catalog import CatalogError, compile_catalog, load_catalog


ROOT = Path(__file__).resolve().parents[1]


class RuntimeCatalogTest(unittest.TestCase):
    def test_sample_catalog_compiles_with_stable_priority_and_signatures(self):
        catalog = load_catalog(ROOT / "catalog")
        self.assertEqual(1, catalog["schema_version"])
        self.assertEqual("example-road", catalog["roads"][0]["road_id"])
        self.assertEqual(1, catalog["roads"][0]["ui_priority"])
        self.assertEqual(4, len(catalog["roads"][0]["lanes"]))
        self.assertEqual(0.15, catalog["roads"][0]["lanes"][0]["vertical_offset"])
        self.assertEqual(0.0, catalog["roads"][0]["lanes"][0]["stop_offset"])
        self.assertEqual(64, len(catalog["roads"][0]["structural_signature"]))
        self.assertEqual(64, len(catalog["revision"]))

    def test_compile_is_deterministic_and_writes_valid_json(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            first = compile_catalog(ROOT / "catalog", output)
            second = compile_catalog(ROOT / "catalog", output)
            self.assertEqual(first, second)
            self.assertEqual(first, json.loads((output / "catalog.json").read_text(encoding="utf-8")))
            self.assertFalse(list(output.glob("*.tmp")))

    def test_unknown_lane_reference_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory)
            for table in (ROOT / "catalog").glob("*.tsv"):
                (source / table.name).write_bytes(table.read_bytes())
            placements = source / "prop_placements.tsv"
            placements.write_text(
                placements.read_text(encoding="utf-8")
                + "bad\texample-road\tmissing\texample-decal\t\t0\t0\t0\t0\t0\t100\n",
                encoding="utf-8",
            )
            with self.assertRaises(CatalogError):
                load_catalog(source)

    def test_namespaced_adaptive_condition_is_preserved_without_interpretation(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory)
            for table in (ROOT / "catalog").glob("*.tsv"):
                (source / table.name).write_bytes(table.read_bytes())
            conditions = source / "conditions.tsv"
            conditions.write_text(
                conditions.read_text(encoding="utf-8")
                + 'adaptive-preview\tsegment\t{"adaptive.segment":["Custom0","Custom3"]}\t{}\n',
                encoding="utf-8",
            )
            catalog = load_catalog(source)
            condition = next(item for item in catalog["conditions"] if item["condition_id"] == "adaptive-preview")
            self.assertEqual("adaptive.segment", condition["required"][0]["name"])
            self.assertEqual('["Custom0", "Custom3"]', condition["required"][0]["value_json"])


if __name__ == "__main__":
    unittest.main()
