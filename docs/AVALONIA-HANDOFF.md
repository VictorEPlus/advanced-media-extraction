# Avalonia rewrite: handoff

Status as of 2 October 2026, branch `avalonia-shell` (**not pushed**). Read this first when picking the work up.

## Goal and decisions

- The owner found the WPF UI clunky and wants smoothness. Decision: keep `MediaWorkbench.Core` and rebuild the desktop app in
  **Avalonia 12.1.3** (`src/MediaWorkbench.Avalonia`), beside the WPF app (`src/MediaWorkbench.App`), which keeps working.
- The owner's instruction (latest): **the Avalonia app must behave exactly like master (the WPF app)**. Styling and colours are
  to be refined **last**.
- Approach taken: the WPF app's logic was **ported almost verbatim** (not rewritten), so behaviour matches; only WPF-specific
  types were swapped. The window was rebuilt with the same parts, names, menus, shortcuts and tour, in the new "Darkroom" look.

## Run it

- `Launch-Avalonia.cmd` (builds Release, starts `src/MediaWorkbench.Avalonia/bin/Release/net10.0/MediaWorkbench.Avalonia.exe`).
  `Launch.cmd` starts the WPF app.
- Flags: `--data-dir <dir>` (own data folder; no import), `--open "<file>"` (select a file at start), `--play` (with `--open`, play it).
- Data: `%LOCALAPPDATA%\MediaWorkbench.Avalonia`. On first start (no `imported-from-wpf-app.txt` marker) it **copies** the WPF
  app's `settings.json`, `catalog.db` (index, favorites, tags), `collections/` and `export-history.json`, then writes the marker.
  The WPF data is never changed.
- The owner tests on a **separate machine**: they need the branch **pushed** (ask first).
- Capture a running window: `tools/screenshot/capture-window.ps1 -ProcessId <pid> -Out x.png` (DPI-aware PrintWindow).

## Layout of the Avalonia project

```
App.axaml(.cs)        Fluent dark palette (accent #F2A541) + Theme.axaml; args, first-start import, creates MainViewModel + MainWindow
Theme.axaml           All tokens (brushes, fonts, icons as StreamGeometry), style classes, NotificationTemplate
MainWindow.axaml(.cs) The WPF window's layout and code-behind; right-click menus built in code (ShowMenu/Item)
MainWindow.Filmstrip.cs  Gliding wheel scroll, follow-scroll marker, focus view (from WPF)
MainWindow.Tour.cs    The same tour steps as WPF
Logic/                The WPF view-model files, ported: MainViewModel*.cs, AssetViewModel, MetadataRow, Notification,
                      FrameMemoryCache, ThumbnailLoader, LiveVideo (VLC callbacks -> WriteableBitmap, Freeze/Thaw/Capture),
                      ImageLoader (Skia decode + MetadataExtractor EXIF), StitchRenderer (Skia), NativeDialogs (storage provider,
                      async), Tokens, AssetView (replaces WPF ListCollectionView: filter, CustomSort, minimal insert/remove events)
Controls/             CropSurface (gliding wheel zoom, fade on new file), FrameTimeline, WaveformView, FolderChart (ported)
Services/             Pixels (Avalonia<->Skia, encode/decode), PhotoDecoder (EXIF-upright, scale-while-decoding),
                      BitmapSizes (C# 14 extension: Bitmap.PixelWidth/PixelHeight; PictureOps.Grey/Crop)
```

Porting rules used (keep using them): `BitmapSource/BitmapImage/ImageSource` -> `Bitmap`; WPF `Visibility.Hidden` (keeps space)
-> style class `hidden` (opacity 0, not hit-testable); `Visibility` -> `IsVisible`; `Dispatcher.BeginInvoke` -> `Dispatcher.UIThread.Post`;
`ColumnDefinition` names are not fields (use `MainGrid.ColumnDefinitions[i]`); commands in DataTemplates use
`$parent[Window].((local:MainViewModel)DataContext).X`; menus inside list rows are built in code; clipboard via `DataTransfer`.

## Verified

- `dotnet build AdvancedMediaExtraction.slnx` clean; WPF unit tests (141) pass.
- `tests/MediaWorkbench.Avalonia.Tests` (xunit v3 + Avalonia.Headless.XUnit, real FFmpeg/VLC): 6 pass.
  - `DesktopChecks/`: **the whole WPF on-screen check, ported and passing** (same sections and the same number of checks as
    `src/MediaWorkbench.App/DesktopSmokeTest*.cs`): video stepping, sound and waveform, pause matching, filters, favorites,
    export queue and history, instant preview, workspace (photo crop, Copy, tags, collections, related files, EXIF, 5,000-file
    filmstrip, follow marker, thumbnails), audio files, video crop and turn, compact layout, zoom and focus view, stitch, tags and
    suggestions, workspace tree and tabs, the tour. Set `MEDIAWORKBENCH_KEEP_CHECKS=1` to keep the pictures it draws
    (`%TEMP%\MediaWorkbench.Avalonia.Tests\checks-*`).
  - `ShellTests`: the folder tree through real clicks and keys; photo + Copy; exact stepping, play and pause through VLC.
  - `PhotoDecoderTests`: EXIF orientation, no upscaling, RGBA pictures keep their colours.
- Ran by hand on this PC with the owner's real folders (~6,800 files): tree with covers, video, timeline, waveform row, transport,
  filmstrip and tabs all appear and work.

Adapting the checks changed only Avalonia-specific details: `Layout(window, w, h)` sets the window size and lays it out
(headless lays out only when asked, so `Shown()` does that first); Copy is checked through the `DataTransfer` it builds; the EXIF
JPEG is written byte by byte (`JpegWithExif`); the button-height rule compares text buttons with each other (icon, play, folder
card, row, link, star and small buttons are sized on purpose); `ShownText()` reads the label showing in a button.

App bugs the checks found and that are fixed: thumbnails of very tall pictures were only limited in width (now fit 220 x 220,
like WPF); `Pixels.ToSkia` assumed BGRA, so RGBA pictures came out with red and blue swapped.

## Known differences / to check

- Next: the styling and colour pass (owner's order: last). Seen in the check pictures: the tree's open/close arrows are tiny;
  Expander headers (COLLECTIONS, TAGS, BREAKDOWN) and some labels are in capitals.
- Text casing: the ported view model still produces WPF's capitals (`EXPORT FRAME`, `+ TRAINING-CLAUDE`, overview titles). Change
  in the styling pass (Avalonia `Logic/` is a copy, independent of WPF tests).
- Expander, ComboBox, TabControl still mostly Fluent defaults; the styling pass is last (owner's order).
- Pause refinement (frame matching of the live picture) is ported but only lightly exercised.
- `scripts/Publish.ps1` still publishes only the WPF app.

## Working agreements (also in Claude's memory)

Plain short explanations; commit/push only when asked; big work on a branch; project-local venvs for Python; Astra only when stuck;
verify visually with the capture script.
