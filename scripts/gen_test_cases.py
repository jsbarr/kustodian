#!/usr/bin/env python3
"""
Generate JSON test cases from KQL files by firing each query against the /analyse endpoint
and recording the actual response as the expected output.

Usage:
    python gen_test_cases.py <kql_folder> <output_folder> [options]

Options:
    --environment ENV       Environment name passed to /analyse (default: defender-xdr)
    --impacted-entity FIELD Impacted entity field (default: DeviceId)
    --url URL               Base URL of the kustodian service (default: http://localhost:5000)
    --naming-convention RE  Optional naming convention regex forwarded to /analyse
"""

import argparse
import json
import os
import re
import sys
import urllib.request
import urllib.error


def slug_to_id(filename: str) -> str:
    return os.path.splitext(filename)[0].upper()


def slug_to_name(filename: str) -> str:
    stem = os.path.splitext(filename)[0]
    return re.sub(r"[-_]+", " ", stem).title()


def call_analyse(base_url: str, payload: dict) -> dict:
    url = base_url.rstrip("/") + "/analyse"
    body = json.dumps(payload).encode()
    req = urllib.request.Request(url, data=body, headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read())
    except urllib.error.HTTPError as e:
        raise SystemExit(f"HTTP {e.code} from {url}: {e.read().decode()}") from e
    except urllib.error.URLError as e:
        raise SystemExit(f"Could not reach {url}: {e.reason}\nIs the kustodian service running?") from e


def main():
    parser = argparse.ArgumentParser(description="Generate JSON test cases from KQL files via /analyse.")
    parser.add_argument("kql_folder", help="Folder containing .kql files")
    parser.add_argument("output_folder", help="Folder to write .json test cases into")
    parser.add_argument("--environment", default="defender-xdr")
    parser.add_argument("--impacted-entity", default="DeviceId")
    parser.add_argument("--url", default="http://localhost:5000")
    parser.add_argument("--naming-convention", default=None, help="Naming convention regex")
    args = parser.parse_args()

    if not os.path.isdir(args.kql_folder):
        print(f"Error: '{args.kql_folder}' is not a directory.", file=sys.stderr)
        sys.exit(1)

    os.makedirs(args.output_folder, exist_ok=True)

    kql_files = sorted(f for f in os.listdir(args.kql_folder) if f.lower().endswith(".kql"))
    if not kql_files:
        print(f"No .kql files found in '{args.kql_folder}'.", file=sys.stderr)
        sys.exit(1)

    for filename in kql_files:
        kql_path = os.path.join(args.kql_folder, filename)
        with open(kql_path, encoding="utf-8") as f:
            kql = f.read().strip()

        payload = {
            "query": kql,
            "impactedEntityField": args.impacted_entity,
            "environment": args.environment,
        }
        if args.naming_convention:
            payload["namingConvention"] = args.naming_convention

        result = call_analyse(args.url, payload)

        test_case = {
            "id": slug_to_id(filename),
            "name": slug_to_name(filename),
            "input": {
                "query": kql,
                "impactedEntityField": args.impacted_entity,
                "environment": args.environment,
            },
            "expected": result,
        }
        if args.naming_convention:
            test_case["input"]["namingConvention"] = args.naming_convention

        out_path = os.path.join(args.output_folder, os.path.splitext(filename)[0] + ".json")
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(test_case, f, indent=2)
            f.write("\n")

        msg_count = len(result.get("messages", []))
        print(f"  {filename} -> {out_path}  ({msg_count} message(s))")

    print(f"\nGenerated {len(kql_files)} test case(s) in '{args.output_folder}'.")


if __name__ == "__main__":
    main()
