# Media Workbench

A local-first Windows desktop application for browsing a media folder, favoriting useful assets, stepping through video frames, and exporting reusable images, clips, and audio without repeatedly opening save dialogs.

What it does, in short: a map of where your media lives, a filmstrip you can scroll through with instant previews, exact frame stepping with a zoomable preview, frame and frame-sequence PNG export, frame-accurate trims, crop and rotate for whole videos, waveforms with sound snipping, tags, staging collections and favorites. Originals are never modified.

**Contents:** [Requirements](#requirements) · [Clone, verify and launch](#clone-verify-and-launch) · [Using the app](#using-the-app) · [Shortcuts](#shortcuts) · [Export folder](#what-the-export-folder-looks-like) · [Tests](#tests) · [Portable release](#build-a-portable-windows-release) · [Local data and privacy](#local-data-and-privacy) · [Current boundaries](#current-boundaries) · [Troubleshooting](#troubleshooting)

## Requirements

- Windows 10/11 **x64**. ARM64 and other operating systems are not tested.
- Git, for cloning and updating the source.
- **.NET 10 SDK, 10.0.200 or newer in the 10.0 family** for source builds. `global.json` allows newer 10.0 feature bands.
- **FFmpeg and FFprobe** from the same full Windows build, available on PATH or configured explicitly. Integration tests require the `libx264`, `ffv1`, PNG, PCM and AAC encoders plus lavfi sources. VLC native playback libraries are restored automatically from NuGet; installing the VLC desktop player is unnecessary.
- Network access for the first NuGet restore. Media processing itself is local; the app does not upload files or require an account.

Optional prerequisite installation from PowerShell:

```powershell
winget install --id Git.Git --exact
winget install --id Microsoft.DotNet.SDK.10 --exact
winget install --id Gyan.FFmpeg --exact
```

Close and reopen PowerShell after installation so PATH changes take effect. Installing prerequisites may require administrative approval. The repository scripts do not install software or change machine-wide settings.

## Clone, verify and launch

Clone your GitHub repository, change into its root, and run:

```powershell
.\scripts\Verify.ps1 -Launch
```

This single command checks tools, restores locked dependencies, builds, runs **all unit and generated-media integration tests**, performs a hidden WPF/native-library startup check, then launches the application only if everything passes. Omit `-Launch` for verification only.

If PowerShell blocks scripts, review the script first, then allow this invocation only:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify.ps1 -Launch
```

If FFmpeg is not on PATH:

```powershell
.\scripts\Verify.ps1 -FfmpegDirectory 'C:\tools\ffmpeg\bin' -Launch
```

You may also place both tools under `tools\ffmpeg\bin` in the repository; the verification script detects that directory. It is intentionally Git-ignored. Alternatively, set `$env:AME_FFMPEG_DIR` for your PowerShell session. When launching the executable directly, use Settings to save the tool folder, or put `tools\ffmpeg\bin` beside the executable.

For everyday starts, double-click **Media Workbench.lnk** in the repository root (or run `scripts\Launch.bat`). It finds the newest `MediaWorkbench.exe` under the repository, builds the Release configuration first if none exists, and launches it. Both the shortcut and the script use only relative paths, so they work wherever the repository is cloned.

Normal application launches perform lightweight tool checks, **not the entire test suite**. Use `Verify.ps1` after cloning, updating, or changing dependencies. GitHub Actions performs the same full verification on Windows for pushes and pull requests.

## Using the app

The application always uses a dark theme, independent of the Windows light/dark setting, with warm graphite surfaces stepped apart so panels, buttons and fields read as separate layers. Choosing a folder, a favorites file or a collection file uses the **built-in Windows Explorer dialogs**. **Reveal file** and **Open exports** open Windows Explorer. Original media are never changed; every edit is a separate export.

New to the app? Click **Tour the UI** in the top bar. It dims the window, outlines one panel or button at a time and says what it does. Use Next and Back or the arrow keys, and Esc to leave. The tour is offered once on first launch and is always available from the top bar.

### The window

- **Top bar:** Sources and Inspector show or hide the side panels; Tour the UI, Open folder, Rescan, Open exports and Settings.
- **Filter bar:** search, media type, sort order, Favorites only, the file count, and Clear filters when a filter is active.
- **Sources (left):** the current source, recent folders, collections and tagged files.
- **Centre:** two folder-style tabs. **Library** shows where the media lives; **Preview** shows the selected file. The open tab is taller, has an amber top edge and joins the page below it. The selected file's name, details line, **Favorite**, **Stage** and **Focus view** sit on the same row as the tabs.
- **Inspector (right):** Details, Tags, Export and Settings. Drag its divider to resize.
- **Filmstrip (bottom):** thumbnails of the files that pass the filters. The status line under it holds **Thumbnail size** and **Preview follows scroll**.

### Open a folder and find files

1. Click **Open folder**, pick a recent folder under **Sources**, or drop a folder or a media file onto the window. A dropped file opens its folder and selects it. Supported media extensions are discovered recursively; inaccessible folders and directory links are skipped. The library fills progressively. **Rescan** picks up later changes on disk.
2. The **Library** tab shows a **folder tree** of every subfolder that contains media, with counts that include subfolders, beside a **graph** of the selected folder's subfolders split into photos, videos and audio. Every folder shows its split as percentages (for example "65 photos (93%), 5 videos (7%)") and its share of its parent folder. Click a folder or a bar to show only that folder's files in the filmstrip; the file count names the folder and **Clear filters** returns to everything.
3. Search by file name or subfolder, filter by media type and choose a sort order. Natural name sorting puts `frame2` before `frame10`. A file you are inspecting stays open when a filter hides it; a **Hidden by filters** note appears.
4. Click a thumbnail, or use Page Up and Page Down to move one file at a time. Selecting a file switches the centre to **Preview** and shows its thumbnail full size at once; the sharp picture replaces it when it has loaded.
5. The mouse wheel glides the filmstrip. Tick **Preview follows scroll** and an amber marker appears on the filmstrip: whatever thumbnail is under the marker is previewed straight away as you scroll, and the file is opened fully about a seventh of a second after you stop. The marker rests in the middle and slides to the edge near either end so the first and last files can be reached.
6. Click **Settings**, choose an absolute export destination and click **Check tools and save**. The default is `Pictures\MediaWorkbench Exports`. Sort order, thumbnail size, panel visibility, Preview follows scroll and recent folders are remembered when the app closes.
7. **F** toggles a persistent favorite. **Favorites only** narrows the library to them. Favorite thumbnails carry a star.

### The preview

Tools change with the selection: photos get copy, crop and PNG export; videos add playback, frame stepping, the timeline, sound and whole-video crop and rotate; audio files get the waveform, playback and WAV export.

- **Zoom:** roll the mouse wheel over the picture to zoom in on the spot under the pointer (up to 32 times). Drag to move around; double-click to fit again. The zoom and position stay put while you step through frames, so you can watch one area change; they reset when you select another file. Exports and Copy always use the full frame, whatever the zoom. Video playback itself is not zoomed.
- **Focus view** (beside Favorite and Stage, or **F11**) puts away the top bar, filters, tabs, side panels and filmstrip, leaving the picture, the timeline and the buttons. F11, Esc or **Exit focus view** brings everything back as it was.
- **Portrait pictures** (9:16 and the like) get the buttons in a column beside the preview, so the picture keeps its height; landscape and square pictures keep them underneath.
- **Details** leads with a one-line summary, then one compact line per fact: folder, type, size, dimensions with orientation and megapixels, aspect ratio, DPI/EXIF when available, or duration, sound, codec, bit rate and frame rate. The aspect ratio is the exact reduced one followed by the closest everyday shape when they differ, for example `683:384 (about 16:9)` or `88:115 (about 3:4)`; shapes that are exactly an everyday one use its usual name (`16:10`, `9:16`).

### Working with a video

- **Opening:** the first frame appears right after probing while the frame times are indexed in the background; a row of dots beside the frame counter fills up with a percentage while that happens. The index is saved, so later opens are instant.
- **Frames per second** is shown in large figures beside the frame counter and in the line under the file name. The header value appears at once and is replaced by the rate measured from the real frame times; `~` and "variable" mean the time between frames changes during the video.
- **Stepping frames:** drag the timeline playhead; **Left/Right** while the preview has focus; **comma/period** anywhere; or the **mouse wheel** over the timeline or the sound under it, one frame per notch towards you for the next frame, ten with Shift. Over the picture the wheel zooms, so there it is **Ctrl+wheel** that steps frames. The previous frame stays visible until the requested one is decoded; a "Decoding" note only appears for slow decodes.
- **Playback:** Space plays and pauses. Pausing lands on the exact frame the player stopped on: the app estimates the position, then compares a small snapshot of the paused picture with the decoded frames around it and picks the one that matches. The paused video picture stays up until that still is ready, so nothing flashes. Playing again from that frame simply carries on, with no reopening and no seek; if you stepped to another frame first, the still stays in view until the player has jumped there.
- **Marking a range:** set inclusive in and out points with **I/O**, the timeline handles, or the fields in **Export**. **Play marked range** auditions it.
- **Exporting:** **E** saves the full-resolution PNG of the displayed frame with no save dialog. The **Export** tab exports the selected or all frames as PNGs, or a frame-accurate MP4 trim. Frame exports above 500 files ask for a second click. Jobs run one at a time and browsing remains available.
- **Crop & rotate video** changes the entire video rather than one frame. It puts a grabber on each of the four edges of the picture. **Auto-fit edges** looks for black bars at several moments of the video and puts the edges on the picture for you. Drag a grabber, or click one (or press Tab in the preview) to choose an edge and use the **arrow keys**: one pixel per press, ten with Shift; Esc lets go of the edge. **Turn left** and **Turn right** rotate in quarter turns and the preview turns with them. A line under the buttons says how much is cut from each side and the size the video will export at (widths and heights are made even, as H.264 needs). **Export whole video** saves a new MP4 with the sound; **Trim selection to MP4** uses the same crop and rotation. The crop stays set for that video until you press Reset or select another file, and a reminder line shows while it is set. Frame PNGs and Copy are not affected.

### Working with sound

Sound is drawn as a **waveform**: a slim strip under the frame timeline for a video with sound, lined up with it, and filling the preview area for an audio file. **Drag** across it to select a section, drag a section edge to adjust it, **click** to move the playhead, **double-click** to select everything. For a video the section snaps to whole frames and moves the in and out markers. **Play marked range** (called **Play selection** for an audio file) plays just that section and pauses at its end; **I** and **O** mark at the playhead, also while an audio file is playing. **Snip sound to WAV** saves only that section to the export folder. The waveform is read once per file and track and then cached.

In the **Export** tab, media with sound offer track and channel selection and start and end times in seconds; the end time is exclusive. **Export audio to WAV** produces PCM WAV.

### Export jobs

Queued jobs badge the **Export** tab and show progress inline without switching tabs. Finished, failed and cancelled exports raise a notification over the preview with an **Open output** action, and are kept in **Previous exports** across restarts. Single-file failed or cancelled exports are removed; cancelled frame sequences keep their partial files with an `incomplete` manifest.

### Shortcuts

| Key | What it does |
| --- | --- |
| `F` | Favorite on or off |
| `S` | Stage the current file |
| `E` | Export the displayed frame or photo as PNG |
| `Ctrl+C` | Copy the image, frame or crop |
| `C`, `Esc` | Clipboard crop on or off; clear it |
| `Space` | Play or pause |
| `,` and `.` | Previous and next frame |
| `Left`, `Right` | Previous and next file in the filmstrip; previous and next frame in the preview or timeline (`Shift`: ten frames on the timeline) |
| `Page Up`, `Page Down` | Previous and next file, anywhere |
| `I`, `O` | In and out markers for a video; start and end of the selection for an audio file |
| Mouse wheel | Over the picture: zoom. Over the timeline or sound: step frames (`Shift`: ten). Over the filmstrip: scroll. `Ctrl`+wheel over the picture: step frames |
| `F11` | Focus view on or off (`Esc` also leaves it) |
| `Tab`, arrow keys | With Crop & rotate video open: choose an edge, then move it 1 pixel (`Shift`: 10) |

Shortcuts are suppressed while typing or choosing from a drop-down.

### What the export folder looks like

Everything is saved in the one export folder from Settings (default `Pictures\MediaWorkbench Exports`). The app does not sort or group it; the file names do that work, and Explorer's own sort decides the order you see.

| Export | Where it goes | Name |
| --- | --- | --- |
| Single frame or photo (`E`) | Export folder, flat | `<source name>_frame_000123.png` |
| Trim | Export folder, flat | `<source name>_trim.mp4` |
| Whole video, cropped or rotated | Export folder, flat | `<source name>_edit.mp4` |
| Audio | Export folder, flat | `<source name>_audio.wav` |
| Collection JSON | Export folder, flat | `<collection name>_collection.json` |
| Tag relationships | Export folder, flat | `media-tag-relationships.json` |
| Frame sequence (selected or all frames) | Its own subfolder | `<source name>_frames_<UTC date_time>_<id>\frame_00000123.png` plus `export.json` |

- Nothing is overwritten. If a name is taken the next export gets `_001`, `_002` and so on.
- Sorting the folder by name keeps the frames of one video together and in frame order, because the source name comes first and frame numbers are zero-padded. Sequence files are numbered by the real frame number, not from 1.
- `export.json` in a sequence folder records the range and whether the run completed; a cancelled run stays marked `incomplete`.
- The library's subfolder structure is not reproduced, and the source path is not recorded. Two videos both called `clip.mp4` in different folders export side by side, told apart only by the numeric suffix.
- Characters Windows does not allow become `_`; very long source names are cut at 100 characters (70 for sequence folders).
- Favorites exports are the exception: they go wherever the save dialog points, which starts in the export folder.

### Crop and copy without saving

Select an image or pause a video on the desired frame. Click **Crop**, drag on the preview, then **Copy crop** (or `Ctrl+C`). The selection maps to full-resolution source pixels, accounting for letterboxing, zoom and EXIF orientation. **Reset crop** returns to full-image copying. Crops are transient: they reset when you change file, survive frame steps within the same video as long as they still fit, and never write a media file. **For videos this copies a still frame, not a cropped playable video clip;** use **Crop & rotate video** for that. This clipboard crop never affects PNG or video exports, and turning one of the two crop tools on turns the other off. Clipboard format/color/alpha support depends on the receiving application.

### Staging collections (virtual folders)

Under **Sources**, open **Manage collections** to name and create a collection, then pick it under **Stage to**. **Stage current file** (or `S`) adds the current file; **Stage N filtered** adds all currently filtered results, not just visible thumbnails, and says how many. Clicking a collection name browses it as a virtual folder. **Remove current item** removes only its reference. Collections persist immediately as JSON under `%LOCALAPPDATA%\MediaWorkbench\collections`; no files are moved or copied. Duplicate paths are ignored, and missing files are reported without removing their references.

**Save JSON** exports the selected collection to the export folder; **Load JSON** imports a local editable copy and opens it. The versioned format is:

```json
{
  "Version": 1,
  "Name": "Reference shots",
  "Paths": ["C:\\Media\\photo.png", "D:\\Clips\\video.mp4"]
}
```

Paths must be absolute. On another PC, edit paths to match its media locations before loading. Collection files contain paths, not media or tags. Loading does not modify your original JSON file. The active collection can differ from the dropdown's staging target.

### Tags, metadata confirmation and related files

In **Tags**, enter comma-separated labels and click **Add tags**. Remove labels individually. Tags persist in the local catalog against full file paths and follow a file between folder and collection views. Files are not modified. In **Details**, click **Tag from details…**, check the values you want and click **Add checked values as tags**. Nothing is auto-tagged; confirmed values become labels such as `aspect ratio:16:9`, with provenance recorded.

The Sources tag filter narrows the current view. **Search all tagged files** searches the local tag catalog across folders/drives, and **Find files with shared tags** opens a related-files view. This is shared-label matching, not visual/AI similarity; it only knows files you have tagged, not your entire PC. **Export tag relationships (JSON)** writes path/tag/source relationships for future graph tools. Renaming/moving a file does not automatically remap tags. Exported relationships can contain sensitive metadata and absolute paths; review before sharing.

### Favorites on another machine

Git synchronizes application source, **not your personal media or favorites database**.

1. On the first machine, open the library and choose **Export favorites**.
2. Copy your media folder and the exported JSON file separately to the other machine.
3. On the other machine, open the corresponding media root and wait for scanning to finish.
4. Choose **Import favorites here**. Relative paths are mapped to the current root, so drive letters and root directory names can differ. Existing favorites are retained; unmatched paths are skipped.

Keep the subfolder structure and filenames the same. Renaming or moving individual files within a library does not automatically migrate their favorites. Import/export dialogs are only for explicit favorites transfer, not media extraction.

## Tests

```powershell
.\scripts\Verify.ps1
dotnet test tests/MediaWorkbench.Tests --configuration Release --filter 'Category!=Integration'
dotnet test tests/MediaWorkbench.Tests --configuration Release --filter 'Category=Integration'
```

Unit tests cover recursive indexing, favorites persistence and relocation, unsafe manifest paths, settings, range boundaries, collision-safe outputs, the folder tree, waveform reduction, frame-rate measurement, crop and rotation maths, and aspect-ratio naming. Integration tests generate their own tiny media fixtures in an isolated temporary directory: H.264 with B-frames, variable-frame-rate video, and stereo audio with distinct tones. Tests compare independent decoded RGB hashes, prove preview/export byte identity, check all-frame completeness, trimming, channel isolation, invalid files and process cancellation. They also prove that a verified seek returns the same pixels as decoding from the start, that a waveform follows the sound in time, and that black borders are detected and a whole video comes out cropped and turned at the right size. No personal files or downloaded sample media are needed. Missing FFmpeg fails integration tests explicitly rather than silently skipping them.

Test results: `artifacts\test-results\tests.trx`. Startup diagnostics: `artifacts\smoke-*`. The desktop smoke scenario also scans generated media, checks persistent photo/video favorites, changes selections during an export, exercises rapid selection, and saves layout screenshots. Regression checks validate Unicode labels, explicit dark-window styling, the absence of white control/popup backgrounds, the folder tree, folder filter and graph data, that pickers are the native Windows dialogs, and that every Tour step points at a control that exists. The same hidden window also exercises the frame-rate readout, waveform selection and snipping, recognising the paused frame from a player snapshot, instant previews and the filmstrip marker, video crop nudging, turning, auto-fit and export, the compact and portrait layouts, wheel frame stepping, zoom and focus view. It cannot play video or move a real mouse, so playback, dragging and wheel feel are covered by `docs/WORKSPACE-ACCEPTANCE.md` instead. CI uploads results/diagnostics even when verification fails. Core integration-test temporary directories are removed after tests; desktop smoke artifacts remain under `artifacts` for inspection.

## Build a portable Windows release

```powershell
.\scripts\Publish.ps1
```

This verifies first, then creates `artifacts\MediaWorkbench-win-x64.zip` containing a self-contained .NET application and the native VLC runtime. Extract the **entire ZIP**, not just the executable, on the other Windows x64 machine; run `MediaWorkbench.exe`. A .NET SDK is not required on the destination. FFmpeg/FFprobe are **not bundled** and must be installed/configured separately there. User settings and favorites remain per-machine.

Review `THIRD-PARTY-NOTICES.md` and upstream distribution requirements before publishing binary releases. No GitHub remote, public repository, license selection, commit, or release is created automatically.

## Local data and privacy

Runtime state lives under `%LOCALAPPDATA%\MediaWorkbench`:

- `settings.json`: export path, FFmpeg location, last and recent libraries, cache limit, sort order, thumbnail size, panel visibility and Preview follows scroll.
- `catalog.db`: indexed file metadata, favorites and tags; not the media files themselves.
- `collections/*.json`: staging collection names and absolute file references.
- `export-history.json`: the last 200 finished exports with their status and output path.
- `cache`: PNG thumbnails, exact frame previews, per-file frame indexes (`*.index.json`) and waveforms (`*.wave.bin`); disk budget defaults to 512 MB. A cache miss decodes a window of about twenty neighbouring frames in one pass so stepping stays warm; a single oversized window may exceed the budget briefly.
- `app.log` / `startup-error.log`: local diagnostic details, which can include file paths. Review before sharing.

The repository ignores build outputs, databases, settings, caches, exports and media under `media/`. Keep personal media outside the repository; arbitrary media files added elsewhere are not automatically ignored. Deleting the catalog removes local favorites; export them first. Original media are read-only inputs; every edit is a separate export.

## Current boundaries

- This is a media workbench, not a complete nonlinear editor. A zoomable **video-thumbnail timeline**, waveform zoom, arbitrary multi-selection, and stream-copy fast cuts remain on the roadmap. The **Crop** button is clipboard-only for photos/paused video frames, not a saved video-crop effect.
- Precision prioritizes correctness: opening a video scans its frame timestamps once per file identity, and the first frame and metadata appear before that index finishes. Frame stepping is fast in three layers: neighbouring frames are kept decoded in memory and read ahead in the direction you are moving, so a step normally shows instantly with no loading message; frames already on disk are read and decoded off the UI thread; and a cache miss deep in a file jumps to a point 48 frames before the window and decodes about twenty frames from there instead of from the start. That jump is **verified on every use**: each frame decoded after it must report exactly the timestamp the index holds for its ordinal, otherwise the result is discarded, the file is not seeked again that session, and the window is decoded from the start. The "Decoding frame" overlay only appears when a decode takes longer than about a third of a second.
- Playback uses VLC in its own surface: it is not zoomed, and while Crop & rotate video is open the crop and rotation are shown on still frames, not during playback. The very first Play of a file may show a brief black moment while the player starts.
- Trims and whole-video exports re-encode to H.264/AAC MP4 and retain the first audio track. Rotation is in quarter turns; a video crop with an odd width or height loses its last pixel column or row. WAV exports can select other existing tracks/channels. Voice/music/instrument stem separation is not implemented.
- Full-resolution PNG is the only image export format. This is not an HDR/color-managed mastering tool; HDR/10-bit sources can lose color precision. Odd-dimension videos are padded by one pixel for H.264/YUV420 trimming. Animated images preview/export their first frame.
- Discovery is extension-based, not a promise that every codec decodes. HEIC/HEIF/AVIF and unusual codecs depend on the FFmpeg build. Camera RAW development is not implemented. Unsupported/corrupt files report errors without modifying originals.
- The filmstrip uses recycled, pixel-scrolling containers with limited look-ahead. Native photo thumbnails are downscaled to at most 220 pixels per side, cached in a 96-item LRU, and released by recycled UI elements. Two workers bound decoding; offscreen work is cancelled. Common image previews/metadata use Windows imaging directly, avoiding FFmpeg per extracted PNG. Unsupported formats fall back to FFmpeg. Sorted discovery batches update the view once per batch, and search/selection requests are debounced.
- File records, tags and video frame indexes still live in memory; this is not yet a paged database UI for millions of files. The automated scenario checks 5,000 filmstrip entries and cache limits, not a performance guarantee for every disk/codec. Full-resolution selected images may consume substantial memory. No file watcher, background tray service, installer or automatic updater yet.
- Selection is single-file; multi-selection, batch favorites/staging and undo are not implemented yet.
- Cancelling/closing stops active processing. Multi-frame jobs may leave partial output; inspect the manifest. Jobs interrupted by closing the app are recorded as interrupted in the export history.

## Troubleshooting

- **SDK not found:** run `dotnet --list-sdks`; install an x64 .NET 10 SDK compatible with `global.json`.
- **Tools not found:** ensure both `ffmpeg -version` and `ffprobe -version` work in a reopened shell, or configure their shared bin folder.
- **NuGet restore fails:** check connectivity/proxy configuration and access to `api.nuget.org`. Locked restore intentionally fails if dependency declarations and lockfiles disagree.
- **Startup check fails:** inspect the reported `startup-error.log`; keep the native `libvlc` directories beside the app and use Windows x64.
- **An export fails:** read its queue status and `%LOCALAPPDATA%\MediaWorkbench\app.log`. Verify disk space, destination permissions, codec support, and valid in/out ranges.
- **Settings cannot load:** back up `settings.json`, then rename it to reset settings. Do not delete the catalog unless you intend to reset favorites.

More documentation:

- `docs/ARCHITECTURE.md`: how frame accuracy, caching, playback hand-over, waveforms, zoom and the video crop work, and why.
- `docs/DESIGN.md`: colours, type, layout rules and wording.
- `docs/ROADMAP.md`: what is done and what is next.
- `docs/WORKSPACE-ACCEPTANCE.md`: the hands-on checklist for everything automated tests cannot judge.
