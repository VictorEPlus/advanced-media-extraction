# Workspace acceptance checklist

**Status: UNVERIFIED - hands-on user acceptance is still pending.** Automated tests and synthetic desktop smoke checks passed during implementation, but they do not establish that the redesigned UI and workflows are correct on real user media and hardware. This commit is a work-in-progress checkpoint, not a verified release.

Run `scripts/Verify.ps1` first. It builds, runs unit/media tests, and exercises the UI with synthetic media in an isolated data directory. Screenshots are under `artifacts/smoke-*`; no personal media is used and the clipboard is not changed by automated tests.

For hands-on validation on your hardware:

1. Open a frame-extracted folder. Scroll quickly, resize thumbnails, sort naturally/reverse/date/size, and type a search. No growing thumbnail backlog should block selecting another file.
2. Select a photo: no playback timeline, frame-stepping or audio export controls. Details should show its dimensions/aspect ratio and available image metadata. Select a video: playback, exact frames and video exports return. Select standalone audio: only applicable playback/audio tools remain.
3. Collapse Sources and resize the right inspector. All action buttons should remain 32 pixels high; toolbars wrap rather than overlapping. Try 100%, 125% and 150% Windows scaling.
4. Crop a photo by dragging in either direction, including letterbox borders. Copy/paste into an image editor and check pixel dimensions. Reset, then repeat on a paused video frame and step one frame: the crop should persist. No output file should appear until an explicit export command is used.
5. Create a collection, stage individual files and filtered results from two folders, and open it by clicking its name under Sources. Remove an item and confirm the original remains. Save/load JSON, including a deliberately missing path.
6. Add tags to two files in different folders. Use "Create tags from metadata", confirm selected fields, browse all tagged files, and find related files. Restart and verify persistence. Confirm exports include only intended metadata before sharing.
7. Export a full PNG while a crop is selected: it should remain uncropped, matching the documented distinction. Verify trimming/audio export still work in the Export tab, that the tab badges instead of switching, and that a notification with Open output appears. Restart and check Previous exports.
8. Open a long video: the first frame and metadata should appear before the index finishes, then the timeline enables. Drag the playhead, step with comma/period, and confirm the previous frame stays visible (dimmed, labelled) while the next decodes. Reopen the same file: indexing should be instant.
9. Type a search that hides the selected file: it should stay open with a "Hidden by filters" note; clear the search and the filmstrip highlight returns. Drop a folder and then a single file onto the window; the file should be selected in its folder. Check recent folders under Sources and the Inspector toggle.

Native playback/GPU behavior, actual clipboard interoperability, huge libraries, filesystem/network latency, and all DPI/monitor combinations still require interactive verification.
