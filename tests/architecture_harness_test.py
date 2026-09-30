from __future__ import annotations

import json
from pathlib import Path
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

from harness.architecture_lint import lint_architecture, path_matches


class ArchitectureHarnessTest(unittest.TestCase):
    def test_recursive_pattern_matches_direct_and_nested_files(self) -> None:
        self.assertTrue(path_matches("source/value.py", "source/**/*.py"))
        self.assertTrue(path_matches("source/nested/value.py", "source/**/*.py"))

    def test_forbidden_dependency_is_detected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "core" / "domain.py"
            source.parent.mkdir(parents=True)
            source.write_text("import bpy\n", encoding="utf-8")
            result = lint_architecture(
                root,
                {
                    "scan": {
                        "roots": ["core"],
                        "extensions": [".py"],
                        "exclude_patterns": [],
                    },
                    "layers": [
                        {
                            "name": "core",
                            "patterns": ["core/*.py"],
                            "forbidden_tokens": ["import bpy"],
                        }
                    ],
                },
            )
            self.assertEqual(
                result.errors,
                ["core/domain.py: core forbids 'import bpy'"],
            )

    def test_repository_manifest_classifies_every_scanned_file_once(self) -> None:
        manifest = json.loads(
            (ROOT / "tools" / "arch_manifest.json").read_text(encoding="utf-8")
        )
        result = lint_architecture(ROOT, manifest)
        self.assertEqual(result.errors, [])
        self.assertEqual(len(result.classified), len(result.files))

    def test_runtime_imt_new_segments_use_one_event_hook_without_polling(self) -> None:
        entry = (ROOT / "src" / "RoadRuntimeHost.Runtime" / "RuntimeEntry.cs").read_text(
            encoding="utf-8"
        )
        service = (
            ROOT / "src" / "RoadRuntimeHost.Runtime" / "ImtPreviewService.cs"
        ).read_text(encoding="utf-8")
        hook = (
            ROOT / "src" / "RoadRuntimeHost.Runtime" / "NetSegmentCreationHook.cs"
        ).read_text(encoding="utf-8")

        self.assertNotIn("_imtPreview.Poll()", entry)
        self.assertNotIn("public void Poll()", service)
        self.assertIn("ProcessPendingSegments", service)
        self.assertEqual(hook.count("_harmony.Patch("), 1)
        self.assertIn("typeof(TreeInfo)", hook)
        self.assertIn("if (__result && segment != 0)", hook)


if __name__ == "__main__":
    unittest.main()
