from __future__ import annotations

import argparse
import csv
import hashlib
import json
import os
import re
import tempfile
from pathlib import Path


TABLES = (
    "roads",
    "lanes",
    "props",
    "prop_placements",
    "conditions",
    "geometry_bindings",
    "test_scenarios",
)
SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")


class CatalogError(ValueError):
    pass


def _read_tsv(path: Path) -> list[dict[str, str]]:
    if not path.exists():
        raise CatalogError(f"missing table: {path}")
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream, dialect="excel-tab")
        if not reader.fieldnames:
            raise CatalogError(f"table has no header: {path}")
        return [
            {key: (value or "").strip() for key, value in row.items()}
            for row in reader
            if any((value or "").strip() for value in row.values())
        ]


def _unique(rows: list[dict[str, str]], key: str, table: str) -> dict[str, dict[str, str]]:
    result: dict[str, dict[str, str]] = {}
    for line, row in enumerate(rows, 2):
        identity = row.get(key, "")
        if not SAFE_ID.fullmatch(identity):
            raise CatalogError(f"{table}:{line}: invalid {key}: {identity!r}")
        if identity in result:
            raise CatalogError(f"{table}:{line}: duplicate {key}: {identity}")
        result[identity] = row
    return result


def _integer(value: str, label: str, default: int = 0) -> int:
    try:
        return int(value) if value else default
    except ValueError as error:
        raise CatalogError(f"{label} must be an integer: {value!r}") from error


def _number(value: str, label: str, default: float = 0.0) -> float:
    try:
        return float(value) if value else default
    except ValueError as error:
        raise CatalogError(f"{label} must be a number: {value!r}") from error


def _boolean(value: str, label: str, default: bool = False) -> bool:
    if not value:
        return default
    normalized = value.lower()
    if normalized in {"1", "true", "yes", "on"}:
        return True
    if normalized in {"0", "false", "no", "off"}:
        return False
    raise CatalogError(f"{label} must be true or false: {value!r}")


def _json_object(value: str, label: str) -> dict:
    if not value:
        return {}
    try:
        parsed = json.loads(value)
    except json.JSONDecodeError as error:
        raise CatalogError(f"{label} is not valid JSON: {error}") from error
    if not isinstance(parsed, dict):
        raise CatalogError(f"{label} must be a JSON object")
    return parsed


def _named_values(value: dict) -> list[dict]:
    return [
        {
            "name": key,
            "value_json": json.dumps(value[key], ensure_ascii=False, sort_keys=True),
        }
        for key in sorted(value)
    ]


def canonical_bytes(value: object) -> bytes:
    return json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":"),
    ).encode("utf-8")


def content_hash(value: object) -> str:
    return hashlib.sha256(canonical_bytes(value)).hexdigest()


def atomic_write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".tmp", dir=str(path.parent),
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def load_catalog(table_dir: Path) -> dict:
    raw = {name: _read_tsv(table_dir / f"{name}.tsv") for name in TABLES}
    roads = _unique(raw["roads"], "road_id", "roads")
    props = _unique(raw["props"], "prop_id", "props")
    conditions = _unique(raw["conditions"], "condition_id", "conditions")
    scenarios = _unique(raw["test_scenarios"], "scenario_id", "test_scenarios")

    sorted_roads = sorted(
        roads.values(),
        key=lambda row: (
            row.get("category", ""),
            _integer(row.get("family_order", ""), f"{row['road_id']}.family_order"),
            row.get("family", ""),
            _integer(row.get("variant_order", ""), f"{row['road_id']}.variant_order"),
            row["road_id"],
        ),
    )

    compiled_roads: dict[str, dict] = {}
    for priority, row in enumerate(sorted_roads, 1):
        scenario_id = row.get("test_scenario_id", "")
        if scenario_id and scenario_id not in scenarios:
            raise CatalogError(f"road {row['road_id']} refers to unknown scenario {scenario_id}")
        compiled_roads[row["road_id"]] = {
            "road_id": row["road_id"],
            "prefab_name": row.get("prefab_name") or row["road_id"],
            "template_name": row.get("template_name") or "Basic Road",
            "display_name": row.get("display_name") or row["road_id"],
            "category": row.get("category", "RoadRuntimeHost"),
            "family": row.get("family", ""),
            "ui_priority": priority,
            "test_scenario_id": scenario_id,
            "lanes": [],
            "prop_placements": [],
            "geometry_bindings": [],
            "metadata": _json_object(row.get("metadata", ""), f"{row['road_id']}.metadata"),
        }

    lane_ids: dict[str, set[str]] = {road_id: set() for road_id in roads}
    for line, row in enumerate(raw["lanes"], 2):
        road_id, lane_id = row.get("road_id", ""), row.get("lane_id", "")
        if road_id not in compiled_roads:
            raise CatalogError(f"lanes:{line}: unknown road_id {road_id}")
        if not SAFE_ID.fullmatch(lane_id) or lane_id in lane_ids[road_id]:
            raise CatalogError(f"lanes:{line}: invalid or duplicate lane_id {lane_id!r}")
        lane_ids[road_id].add(lane_id)
        compiled_roads[road_id]["lanes"].append({
            "lane_id": lane_id,
            "order": _integer(row.get("order", ""), f"lanes:{line}.order"),
            "width": _number(row.get("width", ""), f"lanes:{line}.width"),
            "position": _number(row.get("position", ""), f"lanes:{line}.position"),
            "vertical_offset": _number(row.get("vertical_offset", ""), f"lanes:{line}.vertical_offset"),
            "stop_offset": _number(row.get("stop_offset", ""), f"lanes:{line}.stop_offset"),
            "direction": row.get("direction") or "Forward",
            "lane_type": row.get("lane_type") or "Vehicle",
            "vehicle_type": row.get("vehicle_type") or "Car",
            "speed_limit": _number(row.get("speed_limit", ""), f"lanes:{line}.speed_limit", 1.0),
            "allow_connect": _boolean(row.get("allow_connect", ""), f"lanes:{line}.allow_connect", True),
        })

    compiled_props = {}
    for prop_id, row in props.items():
        compiled_props[prop_id] = {
            "prop_id": prop_id,
            "prefab_name": row.get("prefab_name") or prop_id,
            "template_name": row.get("template_name", ""),
            "kind": row.get("kind") or "PROP",
            "shader": row.get("shader", ""),
            "mesh_bundle": row.get("mesh_bundle", ""),
            "textures": _named_values(_json_object(row.get("textures", ""), f"{prop_id}.textures")),
            "material_properties": _named_values(_json_object(
                row.get("material_properties", ""), f"{prop_id}.material_properties",
            )),
        }

    compiled_conditions = {}
    for condition_id, row in conditions.items():
        compiled_conditions[condition_id] = {
            "condition_id": condition_id,
            "scope": row.get("scope") or "lane",
            "required": _named_values(_json_object(row.get("required", ""), f"{condition_id}.required")),
            "forbidden": _named_values(_json_object(row.get("forbidden", ""), f"{condition_id}.forbidden")),
        }

    for line, row in enumerate(raw["geometry_bindings"], 2):
        road_id = row.get("road_id", "")
        condition_id = row.get("condition_id", "")
        if road_id not in compiled_roads:
            raise CatalogError(f"geometry_bindings:{line}: unknown road_id {road_id}")
        if condition_id and condition_id not in compiled_conditions:
            raise CatalogError(f"geometry_bindings:{line}: unknown condition_id {condition_id}")
        mode = (row.get("mode") or "basic").lower()
        kind = (row.get("kind") or "segment").lower()
        if mode not in {"basic", "elevated", "bridge", "slope", "tunnel"}:
            raise CatalogError(f"geometry_bindings:{line}: invalid mode {mode}")
        if kind not in {"segment", "node"}:
            raise CatalogError(f"geometry_bindings:{line}: invalid kind {kind}")
        compiled_roads[road_id]["geometry_bindings"].append({
            "binding_id": row.get("binding_id") or f"{mode}.{kind}.{line}",
            "mode": mode,
            "kind": kind,
            "material_name": row.get("material_name", ""),
            "order": _integer(row.get("order", ""), f"geometry_bindings:{line}.order"),
            "condition_id": condition_id,
            "direct_connect": _boolean(row.get("direct_connect", ""), f"geometry_bindings:{line}.direct_connect"),
        })

    for line, row in enumerate(raw["prop_placements"], 2):
        road_id, lane_id, prop_id = row.get("road_id", ""), row.get("lane_id", ""), row.get("prop_id", "")
        condition_id = row.get("condition_id", "")
        if road_id not in compiled_roads:
            raise CatalogError(f"prop_placements:{line}: unknown road_id {road_id}")
        if lane_id not in lane_ids[road_id]:
            raise CatalogError(f"prop_placements:{line}: unknown lane_id {lane_id} for {road_id}")
        if prop_id not in compiled_props:
            raise CatalogError(f"prop_placements:{line}: unknown prop_id {prop_id}")
        if condition_id and condition_id not in compiled_conditions:
            raise CatalogError(f"prop_placements:{line}: unknown condition_id {condition_id}")
        compiled_roads[road_id]["prop_placements"].append({
            "placement_id": row.get("placement_id") or f"{lane_id}.{prop_id}.{line}",
            "lane_id": lane_id,
            "prop_id": prop_id,
            "condition_id": condition_id,
            "position": [
                _number(row.get("x", ""), f"prop_placements:{line}.x"),
                _number(row.get("y", ""), f"prop_placements:{line}.y"),
                _number(row.get("z", ""), f"prop_placements:{line}.z"),
            ],
            "angle": _number(row.get("angle", ""), f"prop_placements:{line}.angle"),
            "repeat_distance": _number(row.get("repeat_distance", ""), f"prop_placements:{line}.repeat_distance"),
            "probability": _integer(row.get("probability", ""), f"prop_placements:{line}.probability", 100),
        })

    compiled_scenarios = {}
    for scenario_id, row in scenarios.items():
        compiled_scenarios[scenario_id] = {
            "scenario_id": scenario_id,
            "layout": row.get("layout") or "STRAIGHT_BEND_JUNCTION",
            "enabled": _boolean(row.get("enabled", ""), f"{scenario_id}.enabled", True),
            "origin": [
                _number(row.get("origin_x", ""), f"{scenario_id}.origin_x"),
                _number(row.get("origin_y", ""), f"{scenario_id}.origin_y"),
                _number(row.get("origin_z", ""), f"{scenario_id}.origin_z"),
            ],
            "spacing": _number(row.get("spacing", ""), f"{scenario_id}.spacing", 96.0),
        }

    for road in compiled_roads.values():
        road["lanes"].sort(key=lambda lane: (lane["order"], lane["lane_id"]))
        road["geometry_bindings"].sort(key=lambda item: (item["mode"], item["kind"], item["order"], item["binding_id"]))
        structural = {
            "template_name": road["template_name"],
            "lanes": road["lanes"],
        }
        road["structural_signature"] = content_hash(structural)

    catalog = {
        "schema_version": 1,
        "roads": list(compiled_roads.values()),
        "props": list(compiled_props.values()),
        "conditions": list(compiled_conditions.values()),
        "test_scenarios": list(compiled_scenarios.values()),
    }
    catalog["revision"] = content_hash(catalog)
    return catalog


def compile_catalog(table_dir: Path, output_dir: Path) -> dict:
    catalog = load_catalog(table_dir)
    atomic_write_json(output_dir / "catalog.json", catalog)
    return catalog


def main() -> int:
    parser = argparse.ArgumentParser(description="Compile runtime preview TSV tables")
    parser.add_argument("table_dir", type=Path)
    parser.add_argument("output_dir", type=Path)
    args = parser.parse_args()
    catalog = compile_catalog(args.table_dir, args.output_dir)
    print(f"compiled {len(catalog['roads'])} road(s), revision {catalog['revision'][:12]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
