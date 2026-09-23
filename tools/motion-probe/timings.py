"""How long each probe action took to show its result, from layout.jsonl: click to picture, Play to first moving picture.

Usage: python timings.py <run folder> [<another run folder> ...]   (several runs are printed side by side)
"""

import json
import sys
from pathlib import Path


def measure(run):
    events = [json.loads(line) for line in (Path(run) / "layout.jsonl").read_text(encoding="utf-8").splitlines() if line.strip()]
    results = {}
    current = None
    for event in events:
        if event["kind"] == "action":
            current = event
            results[event["label"]] = None
            continue
        if current is None or results[current["label"]] is not None or event["kind"] != "prop":
            continue
        label = current["label"]
        name, value = event["name"], event["value"]
        shown = (label.startswith("click") or label.startswith("open")) and name in ("PreviewImage", "DisplayedFrame") and value not in ("null", "-1")
        playing = label.startswith("play") and name in ("LiveImage", "ShowVideoSurface", "PlaybackFrame") and value not in ("null", "False", "-1")
        if shown or playing:
            results[label] = event["t"] - current["t"]
    return results


def main():
    runs = sys.argv[1:]
    tables = [measure(run) for run in runs]
    labels = [label for label in tables[0] if any(table.get(label) is not None for table in tables)]
    print("action".ljust(52) + "".join(Path(run).name[-15:].rjust(18) for run in runs))
    for label in labels:
        cells = [table.get(label) for table in tables]
        print(label.ljust(52) + "".join(("-" if cell is None else f"{cell:.0f} ms").rjust(18) for cell in cells))


if __name__ == "__main__":
    main()
