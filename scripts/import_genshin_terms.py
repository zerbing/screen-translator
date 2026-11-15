"""Convert a Genshin Dictionary words.json snapshot to the embedded English–Chinese glossary."""

import argparse
import collections
import hashlib
import json
from pathlib import Path
import re


# These names can also be ordinary words, even when title-cased at the start of a sentence.
LABEL_WORDS = set("""
absolution adeptus advice adventurer agility amber apprentice archon arrow attack bait balance
bane barbara blessing blocked bloom blood blossom bow break burning burst call carve catalyst
character charge childe claymore clear cloud commission common compass concentrate constancy
cool courage craft crimson crystallize cure daily damage dawn deathmatch defense depth
destination devotion diligence discharge disguise domain dragon dust echo eclipse emergency
enlightenment error escape essence eternity event exile explorer faith fate feather fellowship
fire fish flame flower focus forbidden free freedom frost frozen fruit fusion gaming gem
gift glory glow god gold golden grace grass guardian guide guild half ham hangout harbinger
harmony hatred head heart hero hope hunter hunting ice insight item jade justice key knight
lament lamp lantern ley liberation light lightning lion lisa loss lotus love lucky lust magic
marionette mastery material memory messenger metal melt miracle mora morning moon mountain
music night nightmare nobility nothing north oasis oath oblivion offering oil order outbreak
outlander overflow owner particle past pattern pelt performance petra phantom pillar plague
plant player pleasure poison polearm power prey primordial prize punishment purity puzzle
quicken rain ranger realm recharge reflection refinement relic reminder resonance return
revenge reward rhythm ritual ring river roar romance root royal rush rust sacrament sage
sands sanctuary scarlet scholar scroll seal secret shadow shatter shield shiver shore sigh
silence silver sing skirmish skirt slime song soul spark spear spell spirit spread spring
star starglitter stardust stone storm strength strike stun sugar sundial sunset superconductor
sucrose sunset swirl sword talent tale target tea tempest thunder tide time tower trace
tradition traveler trial triumph truth twilight unity vaporize verdict vigil violet vision
void ward warrior water wave wayfinder weapon weather wet wheel white wild wind wish wood
world wreck zeal zither
alley apple bacon butter cabbage carrot cheese coffee crab cream egg flour garlic ham
lemon lettuce meat milk mint mushroom onion pepper potato rice salt sausage shrimp spice
steak sugar tofu tomato wheat cat dog frog horse lizard squirrel prune and then together tomorrow
""".split())
LABEL_TAGS = {"how-to-call", "title"}
QUEST_PREFIX = "quest-"


def clean(value):
    return " ".join((value or "").replace("’", "'").replace("‘", "'").split())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("snapshot", type=Path)
    args = parser.parse_args()
    folder = Path(__file__).resolve().parents[1] / "Glossaries"
    used = {
        row.split("\t")[0].casefold()
        for row in (folder / "GenshinTerms.tsv").read_text(encoding="utf-8-sig").splitlines()
        if row and not row.startswith("#")
    }
    raw = args.snapshot.read_bytes()
    rows = json.loads(raw)
    candidates = collections.defaultdict(set)
    for row in rows:
        english = clean(row.get("en"))
        chinese = clean(row.get("zhCN"))
        tags = set(row.get("tags", []))
        if not english or not chinese or "dialogue" in tags or " / " in chinese:
            continue
        for term in english.split(" / "):
            term = term.strip('"“”')
            key = term.casefold()
            if (key in used or not (3 <= len(term) <= 120) or not term[0].isalnum()
                    or not re.search(r"[A-Za-z]", term)):
                continue
            mode = "proper"
            if (key in LABEL_WORDS or not any(letter.isupper() for letter in term)
                    or (len(term.split()) < 3 and tags & LABEL_TAGS)):
                mode = "label"
            elif len(term.split()) < 3 and ("event" in tags or any(tag.startswith(QUEST_PREFIX) for tag in tags)):
                mode = "title"
            candidates[key].add((term, chinese, mode))

    header = [
        "# Source: Genshin Dictionary (Xicri and contributors), English–Simplified Chinese terms",
        "# https://genshin-dictionary.com/opendata | https://dataset.genshin-dictionary.com/words.json",
        "# Source SHA-256: " + hashlib.sha256(raw).hexdigest(),
        "# Source entry last updated: " + max(row.get("updatedAt", "") or "" for row in rows),
        "# proper=case-sensitive term in prose; label=standalone only; title=standalone or quoted",
    ]
    entries = ["\t".join(next(iter(values))) for _, values in sorted(candidates.items()) if len(values) == 1]
    output = folder / "GenshinDictionaryTerms.tsv"
    output.write_text("\n".join(header + entries) + "\n", encoding="utf-8", newline="\n")
    print(f"Imported {len(entries)} additional terms from {len(rows)} source records.")


if __name__ == "__main__":
    main()
