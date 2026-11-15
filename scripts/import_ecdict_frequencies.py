"""Extract ECDICT's BNC and contemporary-corpus ranks for ambiguous OCR readings."""

import argparse
import csv
import gzip
import unicodedata
from pathlib import Path

from import_ecdict import HEADWORD


def import_frequencies(source, destination):
    ranks = {}
    with source.open(encoding="utf-8-sig", newline="") as csv_file:
        for row in csv.DictReader(csv_file):
            word = unicodedata.normalize("NFC", row["word"].strip().replace("’", "'"))
            if len(word) > 64 or not HEADWORD.fullmatch(word):
                continue
            values = [int(row[field]) for field in ("bnc", "frq")
                      if row[field].isdigit() and int(row[field]) > 0]
            if values:
                key = word.lower()
                ranks[key] = min(ranks.get(key, min(values)), min(values))
    if not ranks:
        raise ValueError("The source contains no usable frequency ranks.")

    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("wb") as output:
        with gzip.GzipFile(fileobj=output, mode="wb", filename="", mtime=0, compresslevel=9) as archive:
            archive.write("".join(f"{word}\t{rank}\n" for word, rank in sorted(ranks.items())).encode("utf-8"))
    print(f"{len(ranks):,} ranked words, {destination.stat().st_size:,} bytes")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Path to the upstream ECDICT CSV")
    parser.add_argument("destination", type=Path, help="Path to the output .tsv.gz")
    arguments = parser.parse_args()
    import_frequencies(arguments.source, arguments.destination)
