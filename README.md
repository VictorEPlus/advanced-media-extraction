# Media Workbench

A local-first Windows desktop application for browsing a media folder, favoriting useful assets, stepping through video frames, and exporting reusable images, clips, and audio without repeatedly opening save dialogs.

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

## First use

The application always uses a dark theme, independent of the Windows light/dark setting. The main window, controls, dropdowns, folder/favorites pickers, and startup error window share dark surfaces. The in-app picker supports drive navigation, parent folders, and typed folder/UNC paths: click **Go** after typing a path. Favorites replacement requires an explicit checkbox. **Reveal in Explorer** and **Open exports** launch the separate Windows Explorer application, whose appearance is controlled by Windows.

1. Click **Open folder**, pick a recent folder under **Sources**, or drop a folder or media file onto the window. A dropped file opens its folder and selects it. Supported media extensions are discovered recursively; inaccessible folders and directory links are skipped. The library fills progressively. Use **Rescan** to pick up later filesystem changes.
2. Search by filename/subfolder, filter by media type, choose a sort order, and scroll the bottom **filmstrip**. Natural name sorting puts `frame2` before `frame10`. Use its size slider to fit more thumbnails. A file you are inspecting stays open when a filter hides it; a **Hidden by filters** note appears and **Clear filters** brings it back. **Sources** and **Inspector** collapse the side panels; drag the inspector divider to resize the preview.
3. Click **Settings**, choose an absolute export destination and click **Check tools and save**. The default is `Pictures\MediaWorkbench Exports`. The **Export** tab always shows the current destination. Sort order, thumbnail size, panel visibility and recent folders are remembered when the app closes.
4. **F** toggles a persistent favorite for a photo, video, or audio file. Enable **Favorites only** to narrow the current folder library. Favorite thumbnails carry a star.
5. Tools change with the selection: photos get copy/crop/PNG actions, videos get playback/frame stepping plus those still-image actions, and audio gets playback and audio exports. **Details** leads with a one-line summary, then dimensions, reduced aspect ratio, file size, DPI/EXIF when available, or video codec, duration and frame rate. No irrelevant video timeline is shown for a photo.
6. For a video, the first frame appears right after probing while timestamp indexing continues in the background; the timeline enables when the index is ready and is reused on later opens. Step with the **timeline**, **Left/Right** while the preview has focus, or **comma/period** anywhere. The previous frame stays visible, dimmed, until the requested frame is decoded, and the overlay states which frame is showing. **E** exports the uncropped full-resolution PNG of the displayed frame, with no save dialog. Set inclusive in/out points with **I/O**, the timeline handles, or the fields in **Export**. **Play in→out** auditions the range. Export selected/all frames or a precise MP4 trim. Frame exports above 500 files ask for a second click. Jobs run one at a time; browsing remains available.
7. In **Export**, audio-bearing media offer track/channel selection and start/end times in seconds. **Export audio to WAV** produces PCM WAV. Video markers initialize the audio range; the end time is exclusive. Standalone audio works too.
8. Queued jobs badge the **Export** tab and show progress inline without switching tabs. Finished, failed and cancelled exports raise a notification over the preview with an **Open output** action, and are kept in **Previous exports** across restarts. Single-file failed/cancelled exports are removed; cancelled frame sequences intentionally retain partial files with an `incomplete` manifest.

**Shortcuts:** `F` favorite, `S` stage current item, `Ctrl+C` copy image/crop, `C` toggle crop, `Escape` reset crop, `E` export full PNG, `Left/Right` previous/next file while the filmstrip has focus or previous/next video frame while the preview or timeline has focus, `,`/`.` previous/next frame for a video, `Page Up/Page Down` previous/next file for any media type, `Space` play/pause, `I/O` video markers, `Shift+Left/Right` ten frames on the timeline. Shortcuts are suppressed while typing or selecting combo-box options.

### Crop and copy without saving

Select an image or pause a video on the desired frame. Click **Crop**, drag on the preview, then **Copy crop** (or `Ctrl+C`). The selection maps to full-resolution source pixels, accounting for letterboxing and EXIF orientation. **Reset crop** returns to full-image copying. Crops are transient: they reset when you change file, survive frame steps within the same video as long as they still fit, and never write a media file. **For videos this copies a still frame, not a cropped playable video clip.** Explicit PNG/trim exports remain uncropped. Clipboard format/color/alpha support depends on the receiving application.

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

In **Tags**, enter comma-separated labels and click **Add tags**. Remove labels individually. Tags persist in the local catalog against full file paths and follow a file between folder and collection views. Files are not modified. In **Details**, check specific metadata rows and click **Confirm selected metadata tags**. Nothing is auto-tagged; confirmed values become labels such as `aspect ratio:16:9`, with provenance recorded.

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

Unit tests cover recursive indexing, favorites persistence and relocation, unsafe manifest paths, settings, range boundaries, and collision-safe outputs. Integration tests generate their own tiny media fixtures in an isolated temporary directory: H.264 with B-frames, variable-frame-rate video, and stereo audio with distinct tones. Tests compare independent decoded RGB hashes, prove preview/export byte identity, check all-frame completeness, trimming, channel isolation, invalid files and process cancellation. No personal files or downloaded sample media are needed. Missing FFmpeg fails integration tests explicitly rather than silently skipping them.

Test results: `artifacts\test-results\tests.trx`. Startup diagnostics: `artifacts\smoke-*`. The desktop smoke scenario also scans generated media, checks persistent photo/video favorites, changes selections during an export, exercises rapid selection, and saves layout screenshots. Regression checks validate Unicode labels, explicit dark-window styling, opaque white control/popup backgrounds, and dark picker selection/overwrite behavior. CI uploads results/diagnostics even when verification fails. Core integration-test temporary directories are removed after tests; desktop smoke artifacts remain under `artifacts` for inspection.

## Build a portable Windows release

```powershell
.\scripts\Publish.ps1
```

This verifies first, then creates `artifacts\MediaWorkbench-win-x64.zip` containing a self-contained .NET application and the native VLC runtime. Extract the **entire ZIP**, not just the executable, on the other Windows x64 machine; run `MediaWorkbench.exe`. A .NET SDK is not required on the destination. FFmpeg/FFprobe are **not bundled** and must be installed/configured separately there. User settings and favorites remain per-machine.

Review `THIRD-PARTY-NOTICES.md` and upstream distribution requirements before publishing binary releases. No GitHub remote, public repository, license selection, commit, or release is created automatically.

## Local data and privacy

Runtime state lives under `%LOCALAPPDATA%\MediaWorkbench`:

- `settings.json`: export path, FFmpeg location, last and recent libraries, cache limit, sort order and panel preferences.
- `catalog.db`: indexed file metadata, favorites and tags; not the media files themselves.
- `collections/*.json`: staging collection names and absolute file references.
- `export-history.json`: the last 200 finished exports with their status and output path.
- `cache`: PNG thumbnails, exact frame previews and per-file frame indexes (`*.index.json`); disk budget defaults to 512 MB. A cache miss decodes a window of about twenty neighbouring frames in one pass so stepping stays warm; a single oversized window may exceed the budget briefly.
- `app.log` / `startup-error.log`: local diagnostic details, which can include file paths. Review before sharing.

The repository ignores build outputs, databases, settings, caches, exports and media under `media/`. Keep personal media outside the repository; arbitrary media files added elsewhere are not automatically ignored. Deleting the catalog removes local favorites; export them first. Original media are read-only inputs; every edit is a separate export.

## Current boundaries

- This is a media workbench, not a complete nonlinear editor. A zoomable **video-thumbnail timeline**, audio waveform, arbitrary multi-selection, and stream-copy fast cuts remain on the roadmap. Cropping is clipboard-only for photos/paused video frames, not a saved video-crop effect.
- Precision prioritizes correctness: opening a video scans its frame timestamps once per file identity and every uncached frame decode still starts from the beginning of the file. The first frame and metadata appear before the index finishes, the index is persisted, and a cache miss fills the neighbouring frames in one pass, so long/high-resolution videos are slow only on the first visit to a distant region. Keyframe-aware seek acceleration remains future work until it is proven ordinal-exact.
- Trims re-encode to H.264/AAC MP4 and retain the first audio track. WAV exports can select other existing tracks/channels. Voice/music/instrument stem separation is not implemented.
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

Architecture and next steps: `docs/ARCHITECTURE.md` and `docs/ROADMAP.md`.
