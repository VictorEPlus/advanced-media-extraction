# Roadmap

## First runnable milestone

- [x] Windows WPF application with local settings and SQLite catalog.
- [x] Recursive media discovery, search, media-type filter and scrolling folder filmstrip.
- [x] Photo preview and VLC video/audio playback.
- [x] Persistent favorites, Favorites-only view, and relative-path export/import.
- [x] Actual timestamp indexing, forward/backward frame selection and one-action PNG export.
- [x] All-frame and selected-frame extraction with count checks, progress and cancellation.
- [x] Inclusive frame markers and precise H.264/AAC trims.
- [x] WAV extraction by time range, existing audio track and channel.
- [x] Unit/integration tests, Windows CI, startup smoke check and setup/publish documentation.

## Workspace redesign

- [x] Compact, media-aware layout with consistent action sizing and collapsible Sources panel.
- [x] Details inspector with dimensions, aspect ratio, image metadata/EXIF, and video format details.
- [x] Natural filename/date/size/type/path sorting and remembered filmstrip sizing.
- [x] JSON staging collections, reference-only add/remove, filtered batch staging and missing-path reporting.
- [x] Persistent tags, confirmed metadata tags, cross-folder related-file lookup and JSON relationship export.
- [x] Transient pixel-accurate crop/copy for images and paused video frames; EXIF-aware previews.
- [x] Bounded thumbnail LRU, cancellation, native image decoding, batched indexing and 5,000-item virtualization regression.

## Responsiveness and feedback

- [x] Requested-versus-displayed frame state; the previous still stays visible while the next frame decodes.
- [x] Background frame indexing after the probe, persisted per file identity; window decode on cache misses.
- [x] Frame timeline with playhead, in/out handles, shaded range, hover readout and play in→out.
- [x] Notifications, Export tab badge, no tab hijack, persistent export history with Open output.
- [x] Selection survives filters and scan batches; crop survives frame steps; visible export destination and counted export labels with large-batch confirmation.
- [x] Recent folders, drag-and-drop of folders/files, inspector collapse, focus-aware arrow keys and comma/period stepping.
- [x] Sources panel reorganised around folders, collections and tagged files; Details tab leads with a summary and makes metadata tagging an explicit mode.

## Library map, tour and native dialogs

- [x] Centre tabs: Library while nothing is selected, Preview for the selected file.
- [x] Folder tree of every subfolder with media, with descendant counts, natural order and collapsed single-child chains; clicking a folder filters the filmstrip.
- [x] Folder graph: files per subfolder split into photos, videos and audio, with legend, hover details and click-to-open.
- [x] Tour the UI: a guided overlay that explains every panel and the main buttons; offered once on first launch.
- [x] Native Windows folder and file dialogs replace the in-app picker.
- [x] Lighter, more clearly layered dark palette; sliders, progress bars and checkboxes follow the app accent.

## Video tools, sound and viewing

- [x] Audio waveform under the frame timeline and as the preview of audio files; drag to select (frame-snapped for video), click to seek, play the selection, snip it to WAV.
- [x] Frame rate readout beside the frame counter, measured from the frame index, with variable-frame-rate detection.
- [x] Folder-style centre tabs with the selected-file header on the same row.
- [x] Hover preview on the frame timeline, later removed: once the wheel could step frames and the preview could zoom, the floating picture only got in the way.
- [x] Crop and rotate a whole video: four edge grabbers, arrow-key nudging of a chosen edge, auto-fit to the picture inside black bars, quarter-turn rotation shown in the preview, export of the whole video or the trimmed range.
- [x] Smooth, exact pause and resume: the paused file is resumed instead of reopened, playback starts at the right moment instead of seeking after it starts, the player's coarse clock is carried forward, the paused frame is confirmed by matching a snapshot against decoded frames, and the paused video picture stays up until that still is ready.
- [x] Compact Details tab with one line per fact; closest everyday aspect ratio named beside the exact one.
- [x] Zoom in the preview at the pointer with the wheel, pan by dragging, kept across frames; focus view that leaves only the picture, timeline and buttons.
- [x] Mouse wheel steps frames over the timeline and sound (Ctrl+wheel over the picture). Compact tools under the preview (smaller readout, slim sound strip without its own ruler, snip button in the action row) and a side column of buttons for portrait pictures.
- [x] Preview follows scroll: a filmstrip marker, instant thumbnail stand-in in the preview, the file opened when scrolling settles, and a gliding mouse wheel.
- [x] Dotted loading bar with a percentage beside the frame counter while a video is being indexed.
- [x] Verified seek for deep cache misses, in-memory frame cache with directional read-ahead, decode overlay only for slow decodes.
- [x] Percentages in the folder tree and graph; filmstrip header removed and its height given to the thumbnails; side-panel scrollbars at the panel edge.

## Theme, stitching and library editing

- [x] Cyberpunk palette: indigo night surfaces, one electric-cyan accent with a glowing tab edge, magenta favorite stars, and brighter graph colours revalidated for colour-blind separation against the new panel colour.
- [x] Application icon: a neon film frame, drawn once and built into a multi-size `.ico` for the executable, the window and the taskbar.
- [x] Stitch tab: add a few pictures (a photo, a chosen video frame, or any file from the filmstrip), arrange them side by side, stacked or in a grid with a gap and a backdrop, match their sizes without enlarging, and export the result as PNG or JPEG.
- [x] Album covers on folder rows: the first and last picture in the folder, fetched only when the row is on screen.
- [x] Flatten a folder to its subfolders, or take a folder out of the library view, with one way back and no rescan.
- [x] Kind badges on thumbnails, the sort drop-down repeated under the filmstrip, a favorites-first sort, and the aspect ratio as a lit chip under the file name.
- [x] Smoother playback: background tools run at below-normal priority and indexing progress is throttled, so indexing a video no longer stutters what is playing.
- [x] Marked range fixes: playing a range stops on the out frame instead of up to a quarter of a second past it, and marking while playing lands on the frame that was on screen.

## Stability pass

- [x] Nothing jumps: the strip under the picture has one height for photos, videos and sound; controls that do not apply are hidden with their space kept; portrait pictures no longer move the buttons beside them; crop tools and readouts are laid over the picture.
- [x] Video drawn by the app instead of a separate VLC window: no VLC text over the picture (the snapshot path that flashed on every pause), no wrong frame when pausing, zoom works while playing.
- [x] Play starts on exactly the frame on screen; Restart button (Home).
- [x] Output folder shown by name in the top bar and changed in one step, saved at once.
- [x] Details no longer blank out and refill on every click; a video's cached first frame stands in while it opens; frame 0 decodes while the file is probed.
- [x] `tools/motion-probe`: 60 fps recording of a scripted session with optical flow, flash detection and a frame-number barcode in the test video.
- [x] Clean-up proposals in `docs/UI-INVENTORY.md` (applied in the pass below).

## Workspace and glass-terminal overhaul

- [x] Several folders open at once in one tree; adding a folder never closes another; collections and tag searches open beside them.
- [x] Saved index per folder: a folder opened before appears at once and only changes are read; folders are watched while the app runs.
- [x] Folder tabs over the filmstrip; switching tabs or folders keeps the open file and never rescans.
- [x] Thumbnails stay with their files (up to 400), so switching folders and tabs shows them at once; unchanged tree rows are reused.
- [x] Glass-terminal theme: translucent navy panels with cyan edges, square corners, monospace labels, one button height, one gap per row; own scroll bars, menus, tooltips and folding sections.
- [x] One top bar; Settings renamed Advanced and holds the rarely used tools.

## Next milestones

- [ ] Interactive usability testing with representative user footage and large libraries.
- [ ] Zoomable video-thumbnail timeline distinct from the folder filmstrip.
- [ ] Waveform zoom for placing cuts in long recordings; per-channel waveforms.
- [ ] Long-lived sequential decoder for sustained playback-speed stepping.
- [ ] Fast stream-copy cuts clearly distinguished from frame-accurate re-encodes.
- [ ] Arbitrary multi-selection, batch favorite/stage, session undo for favorites/tags/collections, and saved reusable ranges.
- [ ] Paged catalog views, filesystem watching, tag remapping after file moves, and a graph UI.
- [ ] HDR/color-management behavior, RAW workflow and additional export formats.
- [ ] Installer, signed releases and distribution/license review.

AI voice/music/instrument isolation is separate from existing-track/channel extraction and requires its own scope decision.
