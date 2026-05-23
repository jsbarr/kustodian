import json
import pathlib
import re
import sys
import time

import requests
from bs4 import BeautifulSoup

SENTINEL_URL = "https://learn.microsoft.com/en-us/azure/sentinel/data-source-schema-reference"
AM_INDEX_URL = "https://learn.microsoft.com/en-us/azure/azure-monitor/reference/tables-index"
AM_BASE_URL = "https://learn.microsoft.com/en-us/azure/azure-monitor/reference/"
HEADERS = {"User-Agent": "Mozilla/5.0"}
OUTPUT = pathlib.Path(__file__).parent.parent / "src" / "environments" / "sentinel.json"


def fetch(url):
    r = requests.get(url, headers=HEADERS)
    r.raise_for_status()
    return BeautifulSoup(r.text, "html.parser")


def get_columns(soup):
    for table in soup.find_all("table"):
        headers = [th.get_text(strip=True).lower() for th in table.find_all("th")]
        # Azure Monitor reference pages use "Property"; Sentinel inline pages use "Column name"
        col_idx = next(
            (i for i, h in enumerate(headers)
             if h in ("property", "column name", "field name", "name", "column")),
            None,
        )
        type_idx = next((i for i, h in enumerate(headers) if "type" in h), None)
        if col_idx is None or type_idx is None:
            continue
        columns = {}
        for row in table.find_all("tr")[1:]:
            cells = [td.get_text(strip=True) for td in row.find_all("td")]
            if len(cells) > max(col_idx, type_idx):
                col_name = cells[col_idx].strip("`").strip()
                col_type = cells[type_idx].strip("`").strip()
                if col_name:
                    columns[col_name] = col_type
        if columns:
            return columns
    return {}


def get_table_links(soup, base_url="https://learn.microsoft.com"):
    seen = set()
    tables = {}
    for a in soup.find_all("a", href=True):
        href = a["href"]
        # Match absolute paths (/en-us/.../tables/name) and relative (tables/name)
        m = re.search(r"(?:^|/)tables/(\w+)$", href)
        if not m:
            continue
        link_text = a.get_text(strip=True)
        # Prefer bare PascalCase link text; fall back to "Azure Monitor X reference" pattern; then URL slug
        if re.match(r"^[A-Z]\w+$", link_text):
            name = link_text
        else:
            m2 = re.search(r"Azure Monitor (\w+) reference", link_text, re.I)
            name = m2.group(1) if m2 else m.group(1)
        if not name or name in seen:
            continue
        seen.add(name)
        if href.startswith("http"):
            url = href
        elif href.startswith("/"):
            url = "https://learn.microsoft.com" + href
        else:
            url = base_url + href
        tables[name] = url.split("?")[0]
    return tables


def main():
    print("Fetching Sentinel schema reference...", file=sys.stderr)
    sentinel_links = get_table_links(fetch(SENTINEL_URL))
    print(f"  {len(sentinel_links)} tables from Sentinel reference", file=sys.stderr)

    print("Fetching Azure Monitor tables index...", file=sys.stderr)
    am_links = get_table_links(fetch(AM_INDEX_URL), base_url=AM_BASE_URL)
    print(f"  {len(am_links)} tables from Azure Monitor index", file=sys.stderr)

    # Merge; Sentinel-sourced entries take precedence for the 7 tables that appear in both
    table_links = {**am_links, **sentinel_links}
    print(f"Fetching {len(table_links)} unique tables...", file=sys.stderr)

    tables = {}
    for name, url in table_links.items():
        print(f"  {name}", file=sys.stderr)
        try:
            tables[name] = get_columns(fetch(url))
        except Exception as e:
            print(f"  ERROR: {e}", file=sys.stderr)
            tables[name] = {}
        time.sleep(0.3)

    OUTPUT.write_text(json.dumps({"tables": tables}, indent=2))
    print(f"Written {len(tables)} tables to {OUTPUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
