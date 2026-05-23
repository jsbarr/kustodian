"""
Splits monolithic environment JSON files from src/environments/ into the
split-file layout under src/data/:
  src/data/environments/<name>.json  -- manifest (table/function name lists)
  src/data/tables/<name>.json        -- column definitions
  src/data/functions/<name>.json     -- paramSignature + body

Run from the repo root:
  python scripts/collect-environments.py
"""

import json
import os
import sys

# Maps source type strings (after lowercasing) to canonical Kusto scalar types.
# Kusto SDK accepts: bool, datetime, decimal, dynamic, guid, int, long, real, string, timespan.
# We also allow: object (DynamicBag), array (DynamicArray).
TYPE_ALIASES = {
    "boolean": "bool",
    "bigint": "long",
    "double": "real",
    "": "dynamic",   # missing type in source data
}

KNOWN_TYPES = {
    "bool", "datetime", "decimal", "dynamic", "guid", "int", "long",
    "real", "string", "timespan", "object", "array",
}


def normalize_type(raw: str) -> str:
    t = raw.lower()
    t = TYPE_ALIASES.get(t, t)
    if t not in KNOWN_TYPES:
        raise ValueError(f"Unrecognised column type: '{raw}'")
    return t


def load_json(path: str) -> dict:
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def write_json(path: str, data) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
        f.write("\n")


def collect(source_path: str, out_dir: str) -> None:
    env_name = os.path.splitext(os.path.basename(source_path))[0]
    data = load_json(source_path)

    tables: dict = data.get("tables") or {}
    functions: dict = data.get("functions") or {}

    for table_name, columns in tables.items():
        normalised = {col: normalize_type(typ) for col, typ in columns.items()}
        write_json(os.path.join(out_dir, "tables", f"{table_name}.json"), normalised)

    for fn_name, cfg in functions.items():
        write_json(os.path.join(out_dir, "functions", f"{fn_name}.json"), cfg)

    manifest = {
        "tables": sorted(tables.keys()),
        "functions": sorted(functions.keys()),
    }
    write_json(os.path.join(out_dir, "environments", f"{env_name}.json"), manifest)
    print(f"{env_name}: {len(tables)} tables, {len(functions)} functions")


def main() -> None:
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    source_dir = os.path.join(repo_root, "src", "environments")
    out_dir = os.path.join(repo_root, "src", "data")

    sources = [f for f in os.listdir(source_dir) if f.endswith(".json")]
    if not sources:
        print("No environment files found in", source_dir, file=sys.stderr)
        sys.exit(1)

    for filename in sorted(sources):
        collect(os.path.join(source_dir, filename), out_dir)


if __name__ == "__main__":
    main()
