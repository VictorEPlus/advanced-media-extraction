# Avalonia rewrite: handoff

Status as of 2 October 2026, branch `avalonia-shell` (not merged, not pushed unless noted in git). Read this first when picking
the work up again.

## Why and what

The WPF app works but felt clunky and the owner did not like its look. Decision: keep `MediaWorkbench.Core` (scanning, catalog,
FFmpeg, frame index, tags, workspace logic) and build a new **Avalonia 12** desktop app over it with a **fresh design**. The WPF
app stays in the repo and keeps working until the new one reaches parity; both build from the same solution.

Approach agreed with the owner: build a **vertical slice** first (library, filmstrip, viewer with exact stepping and playback,
new look with real motion), let the owner judge the smoothness on their own machine, then port the rest.

## Run it

- Double-click **`Launch-Avalonia.cmd`** (builds Release, then starts `src/MediaWorkbench.Avalonia/bin/Release/net10.0/MediaWorkbench.Avalonia.exe`).
  `Launch.cmd` still starts the WPF app.
- Or: `dotnet run --project src/MediaWorkbench.Avalonia`.
- Flags for checking by hand: `--open "<file>"` selects that file at start; `--play` also starts playing it.
- Data lives in `%LOCALAPPDATA%\MediaWorkbench.Avalonia` (settings, catalog.db, frame cache), separate from the WPF app's
  `%LOCALAPPDATA%\MediaWorkbench`. On a first start (no saved folders) it imports the WPF app's `WorkspaceRoots`.
- The owner tests on a **separate machine**: changes must be committed **and pushed** before they can try them, and local logs
  on this machine say nothing about their runs.

## Layout

```
src/MediaWorkbench.Avalonia/
  App.axaml(.cs)        Fluent theme with a custom dark palette (accent #F2A541) + Theme.axaml; creates the shell and window
  Theme.axaml           Design tokens (brushes, fonts, radii, icon geometries) and every control style + transition
  Program.cs            Avalonia bootstrap (Inter font)
  Views/MainWindow.*    Layout; code-behind: folder picker, keys, eased filmstrip wheel scroll, toast, window bounds
  Controls/MediaSurface Viewer control: fit, eased zoom to pointer (wheel), drag pan, double-click fit, fade between files
  ViewModels/
    ShellViewModel      Everything: workspace folders, tree, filter/search, open/step/play/pause, settings
    FolderNodeViewModel Tree node; children built lazily on first expand; Update() keeps open subfolders open
    MediaItemViewModel  Filmstrip item; the Thumbnail getter starts the fetch, so only on-screen cards load
    WorkspaceRoot       One workspace folder: path, label, items by full path (thumbnails survive rescans)
    Converters          Small value converters
  Services/
    VideoBridge         VLC video callbacks -> WriteableBitmap (ported from the WPF LiveVideo); no separate VLC window
    ThumbnailService    3 workers, LRU of 360 thumbnails, photo via PhotoDecoder, video via engine thumbnail
    PhotoDecoder        SkiaSharp decode with EXIF orientation, scale-while-decoding, never upscales
tests/MediaWorkbench.Avalonia.Tests/   xunit v3 + Avalonia.Headless.XUnit (real FFmpeg and VLC); run by scripts/Verify.ps1
tools/screenshot/capture-window.ps1    PrintWindow capture of a running window (DPI-aware) for visual checks
```

## Design ("Darkroom")

- Graphite surfaces over the Windows 11 **Mica** backdrop (`TransparencyLevelHint="Mica, AcrylicBlur, None"`, window tint
  `#E00E0F12`), stage `#08090B`, text `#ECEDEF` / `#9AA0AA` / `#5F6570`, one warm **amber accent `#F2A541`** like a darkroom
  safelight, kind colours for badges.
- **Inter** for the interface, **Cascadia Mono** (embedded, OFL) for numbers, timecodes and counts.
- Rounded cards (10), controls (8), round amber play button.
- Motion: eased hover/press on every button (scale 0.96 on press), filmstrip cards lift 3 px on hover and get an amber ring
  when chosen, thumbnails fade in, the sidebar slides (Ctrl+B), the filmstrip glides on the wheel, the viewer eases zoom
  and fades between files, a toast fades in and out.
- The title bar is the app's own top bar (`ExtendClientAreaToDecorationsHint`); Avalonia 12 draws the caption buttons and also
  a title, which is hidden by the style `WindowDrawnDecorations TextBlock { IsVisible: False }` in Theme.axaml.

## Done in the slice

- Library sidebar: workspace folders and a lazy subfolder tree with counts, busy dot while reading, right-click Show in
  Explorer / Remove from workspace, Add (+) with the native folder picker (multi-select), All media.
- Adding a folder already open says so; removing never touches disk.
- Filmstrip: virtualized, natural sort, search box filters names, arrow keys move between files when it has focus.
- Viewer: photos upright (EXIF) up to 4K wide; videos open on their exact first frame, are indexed in the background, step frame
  by frame (Left/Right, buttons, scrubber), play through VLC drawn by the app from the frame on screen, pause on the frame at
  the player's time (live picture stays until the exact decoded frame replaces it), Restart (Home), end of video lands on the
  last frame. Before indexing finishes the player clock drives the time readout, and pause/resume keeps the time.
- Info strip over the picture: name, size, fps, duration, file size; Open in Explorer (Ctrl+Shift+E).
- Remembers: workspace folders, chosen library folder (stored in `AppSettings.WorkspaceTabs[0]` as a tree key), selected file,
  sidebar open, window place and size.
- Tests: 4 headless tests pass (folder listing, subfolder filter, photo, video open/step/play/pause, window draw, double add,
  remove; EXIF orientation and no-upscale). The WPF suite (141) still passes. `dotnet build AdvancedMediaExtraction.slnx`
  is clean.
- Checked by hand on this PC against the owner's real folders (about 6,800 files): thumbnails, video first frame, 403-frame
  index, playback of a 26-minute screen recording.

## Not ported yet (WPF features, roughly in suggested order)

1. Favorites (star, F) and the media type / favorites filters; sort choices.
2. Copy (Ctrl+C: saves a PNG to the output folder and puts picture + PNG + file on the clipboard), crop area, Export frame PNG,
   output folder choice, export queue and history, notifications list.
3. Frame ranges (in/out), export selected/all frames, frame-accurate trim, audio waveform with selection, snip WAV,
   comma/period stepping, Ctrl+wheel stepping, frame prefetch and memory cache, pause refinement by frame matching
   (WPF `RefinePausedFrameAsync`), "decoding" overlay for slow decodes.
4. Tags (file tags, live folder tags, suggestions, following moved files via TagReconciler), collections (Tags tab
   membership, header button), tag search.
5. Overview (totals, tag chips as filters, folder cards with covers, breakdown graph).
6. Workspace extras: tabs, rename (display names in `AppSettings.WorkspaceNames`), move a subfolder out, hide folders,
   folder watching (FileSystemWatcher + debounce), rescan as a diff (`Workspace.Diff`) instead of the current full replace,
   open-folder state restore, empty-folder notes, "already open" feedback (done), reconcile tags after scans.
7. Video crop and rotate, Stitch, focus view, follow-scroll preview, Details panel with full metadata, Advanced (FFmpeg path,
   cache size, favorites/collection JSON), tour.
8. Packaging: `scripts/Publish.ps1` still publishes the WPF app only.

## Known rough edges

- Pause lands on the frame at VLC's reported time (coarse, ~4 updates per second); the WPF app refined this by matching the live
  picture against decoded neighbours. Port that if pauses land a frame or two off.
- Long videos (40 min at 60 fps) take a while to index; stepping waits for it, playing does not.
- A rescan replaces items wholesale after a full scan (keeps thumbnails for unchanged files) and rebuilds the tree; no watcher.
- `ThumbnailService` eviction drops references without disposing bitmaps (GC frees them).
- The selected library folder is stored in `WorkspaceTabs`; give it its own setting when tabs are ported.
- The drawn-title hiding style depends on Avalonia 12's decorations template containing the title as a TextBlock.
- Only Windows has been tried. Core's `FileIdentity` uses Windows APIs.

## Working agreements with the owner (also in Claude's memory)

- Plain, short explanations; no jargon.
- Commit/push only when asked; a new branch for big work; the owner usually asks to commit, push and merge in one go.
- Python tools only in a project-local venv (`tools/<tool>/.venv`), never the system Python.
- Ask Astra (llm-gateway) only when stuck, one tight question at a time.
- Verify visually: run the real app and capture it with `tools/screenshot/capture-window.ps1`; the WPF app also has the
  hidden-window smoke test (`MediaWorkbench.exe --smoke-test --data-dir <dir>`).
