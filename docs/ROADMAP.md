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

## Next milestones

- [ ] Interactive usability testing with representative user footage and large libraries.
- [ ] Zoomable video-thumbnail timeline distinct from the folder filmstrip.
- [ ] Audio waveform and mouse-based range selection/auditioning.
- [ ] Adjacent-frame prefetch, keyframe-aware seek acceleration and persistent frame indexes.
- [ ] Fast stream-copy cuts clearly distinguished from frame-accurate re-encodes.
- [ ] Arbitrary multi-selection, additional batch operations and saved reusable ranges.
- [ ] Paged catalog views, filesystem watching, tag remapping after file moves, and a graph UI.
- [ ] HDR/color-management behavior, RAW workflow and additional export formats.
- [ ] Installer, signed releases and distribution/license review.

AI voice/music/instrument isolation is separate from existing-track/channel extraction and requires its own scope decision.
