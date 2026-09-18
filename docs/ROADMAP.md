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

## Next milestones

- [ ] Interactive usability testing with representative user footage and large libraries.
- [ ] Zoomable video-thumbnail timeline distinct from the folder filmstrip.
- [x] Audio waveform under the frame timeline and as the preview of audio files; drag to select (frame-snapped for video), click to seek, play the selection, snip it to WAV.
- [x] Frame rate readout beside the frame counter, measured from the frame index, with variable-frame-rate detection.
- [x] Folder-style centre tabs with the selected-file header on the same row.
- [x] Hover and drag preview on the frame timeline: a small picture of the video at the pointer, from a cached strip of key-frame or every-Nth-frame thumbnails that each know their frame.
- [x] Preview follows scroll: a filmstrip marker, instant thumbnail stand-in in the preview, the file opened when scrolling settles, and a gliding mouse wheel.
- [x] Dotted loading bar with a percentage beside the frame counter while a video is being indexed.
- [ ] Waveform zoom for placing cuts in long recordings; per-channel waveforms.
- [x] Verified seek for deep cache misses, in-memory frame cache with directional read-ahead, decode overlay only for slow decodes.
- [x] Percentages in the folder tree and graph; filmstrip header removed and its height given to the thumbnails; side-panel scrollbars at the panel edge.
- [ ] Long-lived sequential decoder for sustained playback-speed stepping.
- [ ] Fast stream-copy cuts clearly distinguished from frame-accurate re-encodes.
- [ ] Arbitrary multi-selection, batch favorite/stage, session undo for favorites/tags/collections, and saved reusable ranges.
- [ ] Paged catalog views, filesystem watching, tag remapping after file moves, and a graph UI.
- [ ] HDR/color-management behavior, RAW workflow and additional export formats.
- [ ] Installer, signed releases and distribution/license review.

AI voice/music/instrument isolation is separate from existing-track/channel extraction and requires its own scope decision.
