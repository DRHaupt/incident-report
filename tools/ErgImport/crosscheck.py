"""Independent check of an imported erg.json against the ERG PDF, using Poppler's pdftotext as a second extractor.

Usage:  python tools/ErgImport/crosscheck.py ERG2024-Eng-Web-a.pdf src/FieldToolkit/Content/erg/erg.json
Needs pdftotext (Poppler) on the PATH. Every material name and ID, guide title and line, Table 1 name and
distance row, and the General First Aid text must appear on its recorded page. Whitespace, quotes and hyphens are
ignored, because the two extractors lay text out differently. Mismatches are listed for manual review; some are
expected where a name wraps around a Table 1 distance line.
"""
import functools
import json
import re
import subprocess
import sys

pdf, data_path = sys.argv[1], sys.argv[2]
d = json.load(open(data_path, encoding="utf-8"))


@functools.lru_cache(None)
def page(p, layout=False):
    args = ["pdftotext", "-f", str(p), "-l", str(p)] + (["-layout"] if layout else []) + [pdf, "-"]
    return subprocess.run(args, capture_output=True, text=True, check=True).stdout


def squash(s):
    return re.sub(r"[\s\"“”'‘’\-–‑]", "", s)


problems = []
for m in d["materials"]:
    t = squash(page(int(m["page"])))
    if squash(m["name"]) not in t:
        problems.append(f"material {m['id'] or '—'} '{m['name']}' not found on page {m['page']}")
    if m["id"] and m["id"] not in t:
        problems.append(f"material id {m['id']} not found on page {m['page']}")
for g in d["guides"]:
    t = "".join(squash(page(int(p))) for p in g["pages"].split(","))
    for s in g["sections"]:
        for ss in s["subsections"]:
            for it in ss["items"]:
                if squash(it.lstrip("– ")) not in t:
                    problems.append(f"guide {g['number']}: '{it[:70]}' not found")
    for n in g.get("notes", []):
        if squash(n.lstrip("* ")) not in t:
            problems.append(f"guide {g['number']} note '{n[:70]}' not found")
for e in d["isolationTable"]:
    p = int(e["page"])
    row = lambda x: squash(" ".join([x["isolate"], x["protectDay"], x["protectNight"]]))
    lay = squash(page(p, True)) + squash(page(p + 1, True))
    if squash(e["name"]) not in squash(page(p)) + squash(page(p + 1)):
        problems.append(f"Table 1 {e['id']} '{e['name']}' name not found as one run on page {p} (check: it may wrap around the distance line)")
    expected = row(e["small"]) + (row(e["large"]) if "Refer" not in e["large"]["isolate"] else "RefertoTable3")
    if expected not in lay:
        problems.append(f"Table 1 {e['id']} '{e['name']}' distance row not found on page {p}")
if d.get("generalFirstAid"):
    gp = squash(page(int(d["generalFirstAidPage"])))
    for it in d["generalFirstAid"]["subsections"][0]["items"]:
        if squash(it.lstrip("– ")) not in gp:
            problems.append(f"General First Aid: '{it[:70]}' not found")

print(f"Checked {len(d['materials'])} materials, {len(d['guides'])} guides, {len(d['isolationTable'])} Table 1 entries.")
print(f"{len(problems)} item(s) to review:")
for p in problems:
    print("  " + p)
