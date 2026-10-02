"""Converts the sample region's OSM XML into PBF with libosmium, for the PBF reader test.

Usage (from the repository root):
    dotnet run --project tools/MapBuilder -- sample-osm tests/FieldMap.Tests/TestData/sample-county.osm
    pip install osmium && python tools/make-test-pbf.py
"""
import pathlib

import osmium

DATA = pathlib.Path(__file__).resolve().parent.parent / "tests" / "FieldMap.Tests" / "TestData"


def main() -> None:
    src = DATA / "sample-county.osm"
    out = DATA / "sample-county.osm.pbf"
    out.unlink(missing_ok=True)
    writer = osmium.SimpleWriter(str(out))
    count = 0
    for obj in osmium.FileProcessor(str(src)):
        writer.add(obj)
        count += 1
    writer.close()
    print(f"wrote {out} ({count} objects)")


if __name__ == "__main__":
    main()
