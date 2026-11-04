#!/usr/bin/env python3
"""Render the fixed public analysis-50 COVID point cohort as a margin-free PNG.
The image is a local visualization, not an original task asset or an MMOS task.
"""
import csv
import gzip
import hashlib
import json
import math
import os
import struct
import zlib

WIDTH = 530
HEIGHT = 530
BASE = os.path.dirname(os.path.abspath(__file__))
FIXTURES = os.path.join(BASE, "..", "dotnet", "tests", "Eve.Host.Tests", "fixtures", "project-discovery")
OUTPUT = os.path.join(BASE, "..", "dotnet", "src", "Eve.Host", "Assets", "ProjectDiscovery")
POINTS_PATH = os.path.join(FIXTURES, "covid-analysis50.csv.gz")
GATES_PATH = os.path.join(FIXTURES, "covid-analysis50-gates.json")
PNG_PATH = os.path.join(OUTPUT, "covid-analysis50-science.png")
MANIFEST_PATH = os.path.join(OUTPUT, "asset-manifest.json")

def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()

def chunk(kind, data):
    body = kind + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xffffffff)

def write_png(path, pixels):
    raw = b"".join(b"\0" + bytes(channel for rgb in row for channel in rgb) for row in pixels)
    header = struct.pack(">IIBBBBB", WIDTH, HEIGHT, 8, 2, 0, 0, 0)
    with open(path, "wb") as image:
        image.write(b"\x89PNG\r\n\x1a\n")
        image.write(chunk(b"IHDR", header))
        image.write(chunk(b"IDAT", zlib.compress(raw, 9)))
        image.write(chunk(b"IEND", b""))

def main():
    os.makedirs(OUTPUT, exist_ok=True)
    with open(GATES_PATH, encoding="utf-8") as source:
        cohort = json.load(source)
    if len(cohort) != 30 or any(len(row["polygons"]) != 2 for row in cohort):
        raise ValueError("Expected the fixed 30-member, two-gate public cohort")
    bounds = cohort[0]["minMax"]
    if any(row["minMax"] != bounds for row in cohort):
        raise ValueError("Cohort minMax frames differ; one image frame cannot represent them")
    min_x, min_y, max_x, max_y = bounds
    if not (max_x > min_x and max_y > min_y):
        raise ValueError("Invalid minMax frame")

    counts = [[0 for _ in range(WIDTH)] for _ in range(HEIGHT)]
    count = 0
    with gzip.open(POINTS_PATH, "rt", newline="", encoding="utf-8") as source:
        reader = csv.DictReader(source)
        if reader.fieldnames != ["GATA3", "CD4"]:
            raise ValueError(f"Unexpected point columns: {reader.fieldnames}")
        for row in reader:
            x = float(row["GATA3"])
            y = float(row["CD4"])
            if not (min_x <= x <= max_x and min_y <= y <= max_y):
                raise ValueError("Point outside the shared cohort minMax frame")
            x_fraction = (x - min_x) / (max_x - min_x)
            y_fraction = (y - min_y) / (max_y - min_y)
            column = min(WIDTH - 1, int(x_fraction * WIDTH))
            bottom_origin_row = min(HEIGHT - 1, int(y_fraction * HEIGHT))
            row_index = HEIGHT - 1 - bottom_origin_row
            counts[row_index][column] += 1
            count += 1
    if count != 62452:
        raise ValueError(f"Expected 62452 source points, got {count}")

    # A density scatter: each source point contributes to its image pixel. Alpha
    # accumulation keeps sparse points visible and dense regions dark without axes.
    ink = (22, 48, 88)
    pixels = []
    for y in range(HEIGHT):
        raster_row = []
        for x in range(WIDTH):
            alpha = 1.0 - math.exp(-0.38 * counts[y][x])
            raster_row.append(tuple(round(255 * (1 - alpha) + channel * alpha) for channel in ink))
        pixels.append(raster_row)
    write_png(PNG_PATH, pixels)

    manifest = {
        "asset_key": "projectdiscovery/covid-analysis50-science.png",
        "file": os.path.basename(PNG_PATH),
        "mime_type": "image/png",
        "width": WIDTH,
        "height": HEIGHT,
        "sha256": sha256(PNG_PATH),
        "points": count,
        "channels": ["GATA3", "CD4"],
        "normalization": {"min_x": min_x, "min_y": min_y, "max_x": max_x, "max_y": max_y},
        "pixel_mapping": "x=floor((GATA3-min_x)/(max_x-min_x)*width); y=height-1-floor((CD4-min_y)/(max_y-min_y)*height); clamped to image bounds; no plot margins or axis decorations",
        "gate_mapping": "Client gate proportions are [x_fraction, 1-y_fraction]; image rows are top-origin, so image_y=(1-gate_y)*height (nearest pixel; at most one-pixel raster quantization).",
        "cohort_members": len(cohort),
        "cohort_min_max_frames": len({tuple(row["minMax"]) for row in cohort}),
        "source": {
            "citation": "Brinkman, R., & Yokosawa, D. Y. O. (2025). Crowdsourced Flow Cytometry Dataset from EVE Online’s Project Discovery for Machine Learning Applications [Dataset]. Federated Research Data Repository. https://doi.org/10.20383/103.01043",
            "license": "CC BY 4.0",
            "license_url": "https://creativecommons.org/licenses/by/4.0/",
            "dataset_member": "A078/FR-FCM-Z2KP_export_COVID19_samples_23_04_20_ST3_COVID19_W_046_O_ST3_230420_011_Live_cells.fcs_123154_GATA3_CD4.csv",
            "analysis_id": 50,
            "data_id": 30927,
            "uncompressed_csv_sha256": "e7d6ce58d8e54ecd429651c474bc7d68777464dd8735e964a1b701b13844f32c",
            "selected_fixture_csv_gzip_sha256": sha256(POINTS_PATH),
            "selected_fixture_gates_sha256": sha256(GATES_PATH),
            "whole_archive_sha256_verified": False
        },
        "semantics": "Local scientific replay visualization of public COVID research points, not an original CCP asset, live MMOS task, game task ID, gold set, score, or reward."
    }
    with open(MANIFEST_PATH, "w", encoding="utf-8", newline="\n") as target:
        json.dump(manifest, target, indent=2, ensure_ascii=False)
        target.write("\n")
    print(json.dumps({"png": PNG_PATH, "manifest": MANIFEST_PATH, "png_sha256": manifest["sha256"], "dimensions": [WIDTH, HEIGHT], "points": count, "max_density": max(max(row) for row in counts)}))

if __name__ == "__main__":
    main()
