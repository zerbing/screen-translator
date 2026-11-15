"""Convert ECDICT's UTF-8 CSV into the embedded, prefix-indexed dictionary."""

import argparse
import csv
import re
import unicodedata
from collections import defaultdict
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo


LETTERS = r"[A-Za-z\u00C0-\u00D6\u00D8-\u00F6\u00F8-\u024F]"
HEADWORD = re.compile(LETTERS + r"+(?:['-]" + LETTERS + r"+)*")
CHINESE = re.compile(r"[\u3400-\u9FFF]")
PARTS_OF_SPEECH = {
    "a.": "形容词", "adj.": "形容词", "adv.": "副词", "n.": "名词",
    "v.": "动词", "vt.": "及物动词", "vi.": "不及物动词", "prep.": "介词",
    "conj.": "连词", "pron.": "代词", "interj.": "感叹词", "num.": "数词",
    "aux.": "助动词", "abbr.": "缩写",
}


def clean_definition(translation):
    definitions = []
    for line in translation.replace(r"\n", "\n").splitlines():
        line = " ".join(line.split())
        if line.startswith("[网络]") or not CHINESE.search(line):
            continue
        first, _, rest = line.partition(" ")
        if rest and first in PARTS_OF_SPEECH:
            line = f"{PARTS_OF_SPEECH[first]}  {rest}"
        if line not in definitions:
            definitions.append(line)
    return "\t".join(definitions[:6])


def import_dictionary(source, destination):
    entries = {}
    with source.open(encoding="utf-8-sig", newline="") as csv_file:
        for row in csv.DictReader(csv_file):
            word = unicodedata.normalize("NFC", row["word"].strip().replace("’", "'"))
            if len(word) > 64 or not HEADWORD.fullmatch(word):
                continue
            definition = clean_definition(row["translation"] or "")
            if definition:
                entries.setdefault(word, definition)

    buckets = defaultdict(list)
    for word, definition in sorted(entries.items()):
        buckets[word.lower()[:2]].append(f"{word}\t{definition}\n")
    if not buckets:
        raise ValueError("The source contains no usable English-Chinese entries.")

    destination.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(destination, "w") as archive:
        for prefix, lines in sorted(buckets.items()):
            archive.writestr(ZipInfo(prefix), "".join(lines).encode("utf-8"), ZIP_DEFLATED, compresslevel=8)
    print(f"{len(entries):,} entries, {len(buckets):,} groups, {destination.stat().st_size:,} bytes")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Path to the upstream ecdict.csv")
    parser.add_argument("destination", type=Path, help="Path to the output ECDICT.zip")
    arguments = parser.parse_args()
    import_dictionary(arguments.source, arguments.destination)
