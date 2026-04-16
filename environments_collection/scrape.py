import json
import pathlib
import re
import sys
import time

import requests
from bs4 import BeautifulSoup

BASE_URL = "https://learn.microsoft.com/en-us/defender-xdr/"
INDEX_URL = BASE_URL + "advanced-hunting-schema-tables"
HEADERS = {"User-Agent": "Mozilla/5.0"}
OUTPUT = pathlib.Path(__file__).parent.parent / "src" / "environments" / "defender-xdr.json"


def fetch(url):
    r = requests.get(url, headers=HEADERS)
    r.raise_for_status()
    return BeautifulSoup(r.text, "html.parser")


def get_table_links(soup):
    seen = set()
    tables = {}
    for a in soup.find_all("a", href=True):
        href = a["href"]
        if "advanced-hunting-" not in href or not re.search(r"-table(?:/)?$", href):
            continue
        name = a.get_text(strip=True).replace("(Preview)", "").strip()
        if not name or name in seen:
            continue
        seen.add(name)
        if href.startswith("http"):
            url = href
        elif href.startswith("/"):
            url = "https://learn.microsoft.com" + href
        else:
            url = BASE_URL + href
        tables[name] = url.split("?")[0]
    return tables


def get_columns(soup):
    for table in soup.find_all("table"):
        headers = [th.get_text(strip=True).lower() for th in table.find_all("th")]
        if "column name" in headers and "data type" in headers:
            columns = {}
            for row in table.find_all("tr")[1:]:
                cells = [td.get_text(strip=True) for td in row.find_all("td")]
                if len(cells) >= 2:
                    columns[cells[0].strip("`")] = cells[1].strip("`")
            return columns
    return {}


def main():
    print("Fetching index...", file=sys.stderr)
    table_links = get_table_links(fetch(INDEX_URL))
    print(f"Found {len(table_links)} tables", file=sys.stderr)

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
    print(f"Written to {OUTPUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
