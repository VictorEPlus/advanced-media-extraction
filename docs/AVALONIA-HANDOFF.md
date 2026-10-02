# Avalonia rewrite: handoff

Status as of 2 October 2026, branch `avalonia-shell` (committed, **not pushed**). Read this first when picking the work up.

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
- `tests/MediaWorkbench.Avalonia.Tests` (xunit v3 + Avalonia.Headless.XUnit, real FFmpeg/VLC): 4 pass - folder tree through real
  clicks and keys (open/close by arrow, quick double click, Right/Left keep working), photo + Copy (file, bitmap, PNG), video
  exact stepping, play and pause through VLC, EXIF orientation and no-upscale.
- Ran by hand on this PC with the owner's real folders (~6,800 files): tree with covers, video, timeline, waveform row, transport,
  filmstrip and tabs all appear and work.

## In progress: porting the WPF on-screen checks

`tests/MediaWorkbench.Avalonia.Tests/DesktopChecks/` holds the WPF `DesktopSmokeTest*.cs` copied over plus `DesktopChecksTest.cs`
(the `[AvaloniaFact]` that runs them). It is **excluded from compilation** in the csproj (`<Compile Remove="DesktopChecks/**" />`)
until it builds. About 40 errors remain, all WPF-isms in the checks themselves:
- `Workspace.cs`: SHA256 needs `using System.Security.Cryptography;`; Copy check -> use `((IDataTransfer)copied).Contains(DataFormat.Bitmap / MainViewModel.PngFormat)`
  (no file item in headless); pixel compare via `Pixels.Bytes`; `CheckExifAsync` must write the EXIF JPEG itself (see
  `PhotoDecoderTests.MakeJpeg`); `CroppedBitmap` -> `PictureOps.Crop`; tall image via SkiaSharp; button-height rule uses WPF styles
  (`QuietIconButton`, `FolderCard`) -> check by class (`quiet`, `folderCard`, `icon`, `play`) and the new heights (32/34/44);
  `IsFrozen` -> drop; `content.Measure(1080,700)` -> set window Width/Height and `Settle(window)`.
- `TransportControls.Visibility == Hidden` -> `IsHidden(window.TransportControls)` (helper exists).
- `Audio.cs`, `Layout.cs`, `Zoom.cs`: `UIElement/FrameworkElement` -> `Control/Visual`; `TranslatePoint` returns `Point?`.
- `Follow.cs`: `FollowDiagnostics` (add to MainWindow.Filmstrip if wanted) and `ContainerFromItem` (ListBox has it directly).
- `Playback.cs` / `Stitch.cs`: `BitmapSource` -> `Bitmap`.
Expect real app bugs to surface once it runs; fix them in the app, keeping WPF behaviour.

## Known differences / to check

- Text casing: the ported view model still produces WPF's capitals (`EXPORT FRAME`, `+ TRAINING-CLAUDE`, overview titles). Change
  in the styling pass (Avalonia `Logic/` is a copy, independent of WPF tests).
- Expander, ComboBox, TabControl still mostly Fluent defaults; the styling pass is last (owner's order).
- Pause refinement (frame matching of the live picture) is ported but only lightly exercised.
- `scripts/Publish.ps1` still publishes only the WPF app.

## Working agreements (also in Claude's memory)

Plain short explanations; commit/push only when asked; big work on a branch; project-local venvs for Python; Astra only when stuck;
verify visually with the capture script.
