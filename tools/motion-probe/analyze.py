"""Reads a Media Workbench motion-probe run and reports what moved, what flashed, and which video frame was on screen.

Inputs (all written by `MediaWorkbench.exe --motion-probe --data-dir <run>`):
  probe.mkv     the window recorded at 60 frames a second
  probe.json    where the window sits in the recording and the display scale
  layout.jsonl  per drawn frame: named elements that moved, appeared or went away; view-model property changes
  steps.txt     the scripted actions, in order

Three findings, each tied back to the action that caused it:
  layout   named elements whose place or size changed (from the app's own log, exact to the device pixel)
  motion   areas of the recording that shifted, measured with Lucas-Kanade optical flow between consecutive frames
  flashes  areas that changed and changed back within a fifth of a second: something appeared briefly
It also reads the frame-number barcode in the test video, to check that Play starts on the frame you were on and that
Pause stays on the frame that was showing.

Usage: python analyze.py <run folder> [--images]
"""

import json
import sys
from collections import defaultdict
from pathlib import Path

import cv2
import numpy as np

FLASH_FRAMES = 12          # a change that is undone within this many recorded frames (200 ms) counts as a flash
DIFF_THRESHOLD = 18        # grey-level difference that counts as a change
MIN_AREA = 60              # device pixels; smaller changes (a caret, a dot of the progress bar) are ignored


def load(run):
    meta = json.loads((run / "probe.json").read_text())
    steps = {}
    for line in (run / "steps.txt").read_text(encoding="utf-8").splitlines():
        index, label = line.split("\t", 1)
        steps[int(index)] = label
    events = [json.loads(line) for line in (run / "layout.jsonl").read_text(encoding="utf-8").splitlines() if line.strip()]
    return meta, steps, events


def read_code(frame, meta):
    """The action number and the low eight bits of the drawn-frame counter, from the cells in the corner."""
    scale, cell = meta["scale"], meta["cellSize"]
    bits = []
    for index in range(meta["cells"]):
        x = int(round(meta["originX"] + (index + 0.5) * cell * scale))
        y = int(round(meta["originY"] + 0.5 * cell * scale))
        bits.append(1 if frame[y, x].mean() > 128 else 0)
    action = sum(bit << index for index, bit in enumerate(bits[:8]))
    tick = sum(bit << index for index, bit in enumerate(bits[8:]))
    return action, tick


def code_mask(shape, meta):
    mask = np.ones(shape[:2], np.uint8)
    size = int(np.ceil(meta["cellSize"] * meta["scale"] * meta["cells"])) + 4
    mask[: int(meta["cellSize"] * meta["scale"]) + 4, :size] = 0
    return mask


class Layout:
    """Replays layout.jsonl to know where every named element was at a given drawn frame."""

    def __init__(self, events):
        self.events = [event for event in events if event["kind"] in ("show", "move", "hide")]
        self.position = 0
        self.rects = {}

    def advance(self, tick):
        while self.position < len(self.events) and self.events[self.position]["tick"] <= tick:
            event = self.events[self.position]
            if event["kind"] == "hide":
                self.rects.pop(event["name"], None)
            else:
                self.rects[event["name"]] = (event["x"], event["y"], event["w"], event["h"])
            self.position += 1

    def name_at(self, box, scale):
        """The smallest named element that contains the middle of a box given in device pixels."""
        x, y, w, h = box
        cx, cy = (x + w / 2) / scale, (y + h / 2) / scale
        best, area = "window", float("inf")
        for name, (ex, ey, ew, eh) in self.rects.items():
            if ex <= cx <= ex + ew and ey <= cy <= ey + eh and ew * eh < area:
                best, area = name, ew * eh
        return best

    def rect(self, name):
        return self.rects.get(name)


def changed_boxes(before, after, mask):
    difference = cv2.absdiff(before, after)
    changed = ((difference > DIFF_THRESHOLD) & (mask > 0)).astype(np.uint8)
    changed = cv2.dilate(changed, np.ones((9, 9), np.uint8))
    count, _, stats, _ = cv2.connectedComponentsWithStats(changed)
    return [tuple(int(value) for value in stats[index][:4]) for index in range(1, count) if stats[index][4] >= MIN_AREA], changed


def flow_vector(before, after, box):
    """Median displacement of trackable points inside a box, or None when nothing in it moved as a whole."""
    x, y, w, h = box
    pad = 12
    x0, y0 = max(0, x - pad), max(0, y - pad)
    x1, y1 = min(before.shape[1], x + w + pad), min(before.shape[0], y + h + pad)
    region_mask = np.zeros_like(before)
    region_mask[y0:y1, x0:x1] = 255
    points = cv2.goodFeaturesToTrack(before, maxCorners=200, qualityLevel=0.01, minDistance=4, mask=region_mask)
    if points is None or len(points) < 6:
        return None
    moved, status, _ = cv2.calcOpticalFlowPyrLK(before, after, points, None, winSize=(21, 21), maxLevel=3)
    good = status.ravel() == 1
    if good.sum() < 6:
        return None
    vectors = (moved[good] - points[good]).reshape(-1, 2)
    median = np.median(vectors, axis=0)
    if np.hypot(*median) < 1.5:
        return None
    agreeing = np.hypot(*(vectors - median).T) < 1.5
    if agreeing.sum() < max(6, 0.5 * len(vectors)):
        return None
    return float(median[0]), float(median[1]), int(agreeing.sum())


def picture_bounds(frame, monitor):
    """The grey test-video picture inside the preview: the largest block of colourless pixels."""
    x, y, w, h = monitor
    region = frame[y:y + h, x:x + w].astype(np.int16)
    grey = (np.abs(region[:, :, 0] - region[:, :, 1]) < 8) & (np.abs(region[:, :, 1] - region[:, :, 2]) < 8)
    rows, columns = np.where(grey.mean(axis=1) > 0.6)[0], np.where(grey.mean(axis=0) > 0.6)[0]
    if len(rows) < 40 or len(columns) < 40:
        return None
    return x + columns[0], y + rows[0], columns[-1] - columns[0] + 1, rows[-1] - rows[0] + 1


def read_barcode(frame, bounds, bits):
    x, y, w, h = bounds
    row = int(y + h / 16)
    value = 0
    for index in range(bits):
        column = int(x + (index + 0.5) * w / bits)
        if frame[row, column].mean() > 128:
            value |= 1 << index
    return value


def main():
    run = Path(sys.argv[1])
    save_images = "--images" in sys.argv
    # --quick reads only the video barcode and the app's layout log: seconds instead of minutes.
    quick = "--quick" in sys.argv
    meta, steps, events = load(run)
    scale = meta["scale"]
    layout = Layout(events)
    props = [event for event in events if event["kind"] == "prop"]

    capture = cv2.VideoCapture(str(run / "probe.mkv"))
    ok, frame = capture.read()
    if not ok:
        sys.exit("The recording could not be read.")
    mask = code_mask(frame.shape, meta)
    images = run / "motion-images"
    if save_images:
        images.mkdir(exist_ok=True)

    history = []            # (grey frame, action, tick) for the flash check
    motion = defaultdict(list)
    flashes = defaultdict(list)
    shown = defaultdict(list)   # action -> [(frame index, video frame number)]
    last_tick, wraps, index = None, 0, 0
    previous_grey = None
    while ok:
        action, low = read_code(frame, meta)
        if last_tick is not None and low < last_tick - 128:
            wraps += 1
        last_tick = low
        tick = wraps * 256 + low
        layout.advance(tick)
        grey = None if quick else cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        if previous_grey is not None:
            boxes, _ = changed_boxes(previous_grey, grey, mask)
            for box in boxes:
                vector = flow_vector(previous_grey, grey, box)
                if vector:
                    motion[action].append((index, layout.name_at(box, scale), box, vector))
                    if save_images:
                        annotated = frame.copy()
                        cv2.rectangle(annotated, box[:2], (box[0] + box[2], box[1] + box[3]), (0, 0, 255), 2)
                        cv2.imwrite(str(images / f"move_{index:05d}_a{action}.png"), annotated)
        if not quick:
            history.append((grey, action, index, frame if save_images else None))
        # Flash: a box that changed between frame k-1 and k, while frame k-1 and a frame a little later are the same there.
        if len(history) > FLASH_FRAMES + 1:
            base, base_action, base_index, _ = history[-FLASH_FRAMES - 2]
            first, _, first_index, first_frame = history[-FLASH_FRAMES - 1]
            boxes, _ = changed_boxes(base, first, mask)
            for box in boxes:
                x, y, w, h = box
                later = [item[0][y:y + h, x:x + w] for item in history[-FLASH_FRAMES:]]
                back = [np.mean(cv2.absdiff(base[y:y + h, x:x + w], patch) > DIFF_THRESHOLD) < 0.02 for patch in later]
                if any(back):
                    lasted = back.index(True) + 1
                    flashes[base_action].append((first_index, layout.name_at(box, scale), box, lasted))
                    if save_images and first_frame is not None:
                        annotated = first_frame.copy()
                        cv2.rectangle(annotated, box[:2], (x + w, y + h), (255, 0, 255), 2)
                        cv2.imwrite(str(images / f"flash_{first_index:05d}_a{base_action}.png"), annotated)
            history.pop(0)
        monitor = layout.rect("PreviewMonitor")
        if monitor:
            device = tuple(int(round(value * scale)) for value in monitor)
            device = (device[0] + int(meta["originX"]), device[1] + int(meta["originY"]), device[2], device[3])
            bounds = picture_bounds(frame, device)
            if bounds and bounds[2] > 200:
                shown[action].append((index, read_barcode(frame, bounds, meta["barcodeBits"])))
        previous_grey = grey
        ok, frame = capture.read()
        index += 1

    report = [f"# Motion probe report\n\nRun: `{run}`  \nRecorded frames: {index} at 60 per second; display scale {scale:g}.\n"]
    moves = defaultdict(list)
    for event in events:
        if event["kind"] == "move":
            moves[event["action"]].append(event)
    for action in sorted(steps):
        report.append(f"\n## {action}. {steps[action]}\n")
        layout_moves = moves.get(action, [])
        if layout_moves:
            summary = defaultdict(lambda: [0, 0.0, 0.0, 0.0, 0.0])
            for event in layout_moves:
                entry = summary[event["name"]]
                entry[0] += 1
                entry[1] += abs(event["dx"]); entry[2] += abs(event["dy"]); entry[3] += abs(event["dw"]); entry[4] += abs(event["dh"])
            report.append("Layout changes (app log; total movement in window units):\n")
            for name, (count, dx, dy, dw, dh) in sorted(summary.items(), key=lambda item: -(item[1][1] + item[1][2] + item[1][3] + item[1][4]))[:25]:
                report.append(f"- `{name}` changed {count}x: moved {dx:.0f} across, {dy:.0f} down; resized {dw:.0f} wide, {dh:.0f} tall")
        if motion.get(action):
            report.append("\nShifts seen in the recording (optical flow):\n")
            for frame_index, name, box, (vx, vy, points) in motion[action][:20]:
                report.append(f"- frame {frame_index}: `{name}` moved ({vx:+.1f}, {vy:+.1f}) px, box {box}, {points} points agree")
        if flashes.get(action):
            report.append("\nFlashes (changed, then back within 200 ms):\n")
            for frame_index, name, box, lasted in flashes[action][:20]:
                report.append(f"- frame {frame_index}: over `{name}`, box {box}, lasted {lasted} frame(s) ({lasted * 1000 / 60:.0f} ms)")
        if shown.get(action):
            numbers = [number for _, number in shown[action]]
            compact = []
            for number in numbers:
                if not compact or compact[-1][0] != number:
                    compact.append([number, 1])
                else:
                    compact[-1][1] += 1
            steps_back = [(a[0], b[0]) for a, b in zip(compact, compact[1:]) if b[0] < a[0]]
            report.append("\nVideo frame on screen (barcode), in order, with how many recorded frames each stayed:\n")
            report.append("  " + " ".join(f"{number}x{count}" for number, count in compact[:80]))
            if steps_back:
                report.append(f"\n  Went backwards: {steps_back[:10]}")
        changes = [event for event in props if event["action"] == action and event["name"] in
                   ("CurrentFrame", "DisplayedFrame", "PlaybackFrame", "ShowPlayback", "ShowVideoSurface", "ShowPendingOverlay", "PendingLabel", "ShowInstantLayer", "IsPortraitLayout", "MainTab")]
        if changes:
            report.append("\nKey state changes (drawn frame: value):\n")
            line = []
            for event in changes[:60]:
                line.append(f"{event['tick']}:{event['name']}={event['value']}")
            report.append("  " + "; ".join(line))
    (run / "report.md").write_text("\n".join(report), encoding="utf-8")
    print(run / "report.md")


if __name__ == "__main__":
    main()
