#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import sys

from harness.architecture_lint import lint_architecture


REQUIRED_DOCUMENT_TOKENS = {
    "docs/README.md": (
        "# 文書案内",
        "## 正本の優先順位",
        "## 文書一覧",
    ),
    "docs/architecture.md": (
        "# Road generator architecture",
        "## State ownership",
        "## Derived topology",
        "## Mesh registration boundary",
        "## Dependency direction",
    ),
    "docs/testing.md": (
        "# 検証方針",
        "## Primary proof",
        "## Structural proof",
        "## Representative end-to-end",
    ),
}

DOCUMENT_ROLES = {
    "architecture.md": "現行構成",
    "cs1-road-requirements.md": "仕様資料",
    "design-decisions.md": "比較検討",
    "engineering/agent_harness.md": "作業手順",
    "handoff.md": "履歴",
    "reference/implementation-details.md": "詳細資料",
    "runtime-preview.md": "運用手順",
    "runtime-workboard.md": "状態記録",
    "testing.md": "検証",
}


def check_documents(root: Path) -> list[str]:
    errors: list[str] = []
    for source, tokens in REQUIRED_DOCUMENT_TOKENS.items():
        path = root / source
        if not path.exists():
            errors.append(f"{source}: required architecture document is missing")
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for token in tokens:
            if token not in text:
                errors.append(f"{source}: required contract is missing {token!r}")

    docs_root = root / "docs"
    actual_documents = {
        path.relative_to(docs_root).as_posix()
        for path in docs_root.rglob("*.md")
        if path.name != "README.md"
    }
    expected_documents = set(DOCUMENT_ROLES)
    for missing in sorted(expected_documents - actual_documents):
        errors.append(f"docs/{missing}: indexed document is missing")
    for unindexed in sorted(actual_documents - expected_documents):
        errors.append(f"docs/{unindexed}: document is not listed in DOCUMENT_ROLES")

    index_path = docs_root / "README.md"
    index_text = (
        index_path.read_text(encoding="utf-8", errors="replace")
        if index_path.exists()
        else ""
    )
    for relative, role in DOCUMENT_ROLES.items():
        path = docs_root / relative
        link = f"]({relative})"
        if link not in index_text:
            errors.append(f"docs/README.md: missing document link {relative!r}")
        if path.exists():
            text = path.read_text(encoding="utf-8", errors="replace")
            marker = f"> 文書種別: {role}"
            if marker not in text:
                errors.append(f"docs/{relative}: missing role marker {marker!r}")
    return errors


def check_domain_authority(root: Path) -> list[str]:
    errors: list[str] = []
    sources = list((root / "blender_addon" / "road_builder").glob("*.py"))
    expected_owner = "blender_addon/road_builder/domain.py"
    for symbol in ("MODE_LENGTH", "SEGMENT_SLICES", "NODE_SLICES"):
        owners: list[str] = []
        assignment = re.compile(rf"^{symbol}\s*=", re.MULTILINE)
        for path in sources:
            text = path.read_text(encoding="utf-8", errors="replace")
            if assignment.search(text):
                owners.append(path.relative_to(root).as_posix())
        if owners != [expected_owner]:
            errors.append(
                f"{symbol}: expected one assignment owner {expected_owner}, found {owners}"
            )

    adapter = root / "blender_addon" / "road_builder" / "__init__.py"
    adapter_text = adapter.read_text(encoding="utf-8", errors="replace")
    for token in ("expected_boundaries", "cross_section_widths", "strip_id"):
        if token not in adapter_text:
            errors.append(f"{adapter.relative_to(root).as_posix()}: missing domain consumer {token}")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--manifest", type=Path, default=Path("tools/arch_manifest.json"))
    args = parser.parse_args()
    root = args.root.resolve()
    manifest_path = args.manifest if args.manifest.is_absolute() else root / args.manifest
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        print(f"arch-lint: cannot load manifest {manifest_path}: {error}", file=sys.stderr)
        return 2

    result = lint_architecture(root, manifest)
    errors = result.errors + check_documents(root) + check_domain_authority(root)
    if errors:
        for error in errors:
            print(f"arch-lint: {error}", file=sys.stderr)
        return 1
    print(
        f"arch-lint: OK ({len(result.files)} files, "
        f"{len(manifest.get('layers', []))} layers)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
