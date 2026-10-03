# Avalonia rewrite: handoff

Status as of 2 October 2026, branch `avalonia-shell` (pushed to origin). Read this first when picking the work up.

## Goal and decisions

- The owner found the WPF UI clunky and wants smoothness. Decision: keep `MediaWorkbench.Core` and rebuild the desktop app in
  **Avalonia 12.1.3** (`src/MediaWorkbench.Avalonia`), beside the WPF app (`src/MediaWorkbench.App`), which keeps working.
- The owner's instruction (latest): **the Avalonia app must behave exactly like master (the WPF app)**. Styling and colours are
  to be refined **last**.
- Approach taken: the WPF app's logic was **ported almost verbatim** (not rewritten), so behaviour matches; only WPF-specific
  types were swapped. The window was rebuilt with the same parts, names, menus, shortcuts and tour, in a new look (now "Night glass", see below).

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
App.axaml(.cs)        Fluent dark palette (navy, accent #3D86FA) + Theme.axaml; args, first-start import, creates MainViewModel + MainWindow
Theme.axaml           All tokens (brushes, fonts, icons as StreamGeometry), Fluent resource overrides, style classes, NotificationTemplate
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

## Tagging features (Avalonia only, not in the WPF app)

Asked for by the owner on 3 October 2026; the WPF app does not have them.
- **Suggestions while typing** in every tag box (`AutoCompleteBox` + `Logic/TagCompletion.cs`): only the tag after the last
  comma is matched; choosing one keeps the tags before it. Enter adds what is in the box.
- **Choose from your tags** (Tags tab): every tag in use as a bubble; click to put it on, click a filled one to take it off.
- **Picking files** (`Logic/MainViewModel.Picking.cs`): Ctrl+click, Shift+click (a run), the round mark on a thumbnail, Ctrl+A in
  the filmstrip, Esc clears. While files are picked, the Tags tab works on all of them: tags on all, tags on some ("4 of 5", +
  finishes it), and the one-file sections step aside. Picks hidden by a search or another folder are let go, so nothing unseen
  is tagged. `TagStore.AddToFiles` / `RemoveFromFiles` do it in one transaction.
- **Tag bar in the filmstrip header** (`FolderTagBar`): a box (T to focus; Enter tags and hands the keyboard back to the filmstrip,
  so arrows move to the next file) and the tags the shown files carry, most used first, as bubbles. It is in the header row on
  purpose: a row of its own cost the picture 23 px and broke the compact-layout check at 1080 x 700.
- **Tag colours** (`Logic/TagColors.cs`): each tag has a matte pastel from an FNV-1a hash of its lower-case name (stable across
  runs); filled with dark ink when on, a faint wash when not, an outline when on some.
- **Tag filter** (bottom left, `Logic/MainViewModel.TagFilter.cs`): replaces the WPF text box + single-choice list. A drop-down of
  every tag in use (count, colour dot, find box) with tick boxes; ticked tags show as bubbles with ×; Clear unticks all; with two or
  more, "Files with any / all of them". Exact tag matches. The Overview's tag bubbles tick and untick the same filter.
  Show every tagged file uses the ticked tags (any/all), or every tag when none is ticked.
- **Graph tab** (4th centre view; `Controls/TagGraphView.cs`, `Logic/MainViewModel.Graph.cs`, `Core/TagGraph.cs`): every tag as a
  glowing node (size = files, vivid colour of its hue), links between tags used on the same files (thickness = Jaccard strength).
  Click focuses a tag (neighbours on a ring, closer = stronger, the rest fade), related tags in the side card travel on, Back walks
  the trail, double-click / Show its files narrows the filmstrip, Add to filter ticks it. Drag pans, wheel zooms; the map lays out
  left of the card (`RightInset`). Overview shows the 150 most used tags; the layout is a seeded force layout, the same each time.
- **Suggestions reworked** (`Core/TagSuggestions.cs`; shared, so the WPF app gets it too): a tag is suggested when at least 35% of
  the resembling files' evidence carries it and it is 1.5x more common among them than among the other tagged files; same
  resolution/codec and same camera on another day no longer count; a few (<= 3) tagged near copies pass their tags on directly,
  many look-alikes (one app on screen) only count as ordinary evidence. The Tags tab says "3 of 4 similar files, same day".
- **Thumbnail badge** is the file extension (MP4, JPG, WAV), in the kind's colour.
- **Hover preview** (`MainWindow.Hover.cs`, `MainViewModel.Hover.cs`, `MediaEngine.PreviewTimes/GetPreviewFrameAsync`): resting
  250 ms on a video plays five pictures, first to just before the last at even steps, 550 ms each; fast seeks, cached, own
  two-at-a-time gate; the last 16 videos' pictures are kept decoded.
- The thumbnail-size slider uses a compact Fluent size (`SliderHorizontalHeight` 24) so it fits the 30 px header row.
- Tests: `TaggingTests` (typing, picking, bar, colours, slider, hover on a generated video); `TagTrackingTests` for the store.

## Known differences / to check

- Styling pass done (owner asked: no orange, blues and dark navy, better contrast, soft round glass). "Night glass":
  navy window gradient with a faint top-left glow; panels are translucent navy with a light top edge (`GlassEdgeBrush`) and a
  soft shadow; one azure accent (`AccentBrush` #5CABFF for lines/text, `AccentFillBrush` gradient with white text for the main
  buttons and play); text Ink #F1F5FC / Secondary #B6C4DD / Faint #8293B4. Fluent's TextBox, ComboBox, menus, tooltips,
  Expander, TabItem, Slider and CheckBox take the same colours through resource-key overrides in Theme.axaml.
  Brushes read from C# through `Tokens.Brush` must stay `SolidColorBrush` (only `WindowTint`, `PanelBrush` and the
  `Accent*Fill*`/`GlassEdge`/`WindowGlow` brushes are gradients).
- Labels are sentence case (`Export frame`, `+ Review staging`, overview folder names in their own case); the small
  letter-spaced section labels (`THIS FILE`, `QUEUE`) stay capitals on purpose.
- `MonitorBrush` is WPF's #04060E: it is the stitch backdrop written into exported files, so it must not follow the theme.
- Pause refinement (frame matching of the live picture) is ported but only lightly exercised.
- `scripts/Publish.ps1` still publishes only the WPF app.

## Review findings, 2 October 2026 (all fixed)

Found in a review of `avalonia-shell` at `fe36688` (PR #2, merged). Item 1 was fixed in that PR, items 2 to 6 right after it.

1. **Fixed: `ShellTests` wrote into the owner's real Pictures folder.** `Open()` built a `MainViewModel` on a fresh data
   folder, so settings fell back to `AppSettings.ExportDirectory`'s default (`MyPictures\MediaWorkbench Exports`), and the
   Copy test saved `still_copy_crop_80x60.png` there. Setting the `ExportDirectory` *property* does not help: exports and
   Copy read the saved `settings`, which change only on Save settings or the folder picker. The test now saves its own
   `settings.json` (export folder inside its temp workspace) before the view model starts. The two `DesktopChecks` were
   already safe because they call `SaveSettingsCommand`. **Rule for new tests: pre-seed settings; never rely on the property.**
2. **Fixed: first-start import could half-succeed and never retry** (`App.ImportClassicData`). The marker, whose text says
   "Copied from ...", is written *before* the copy, and `IOException`/`UnauthorizedAccessException` are swallowed. A copy
   that fails partway leaves a marker claiming success, so the import is never attempted again. Write the marker last,
   and only after every file copied.
3. **Fixed: the import could take an inconsistent catalog.** `CatalogStore` uses `PRAGMA journal_mode=WAL`. Copying `catalog.db`,
   `-wal` and `-shm` one by one while the WPF app is open can pair a database with a WAL from a different moment, losing
   recent favorites/tags or corrupting the copy. Use SQLite's online backup (`SqliteConnection.BackupDatabase`), which
   gives a consistent snapshot even while the WPF app is writing.
4. **Fixed: `Launch-Avalonia.cmd` failed when the WPF app was open, and vice versa.** `scripts/Launch.bat` builds the whole solution.
   If either app is running and its code changed (any pull), MSBuild retries copying the locked exe 10 times (~10 s of
   `MSB3026`), fails with `MSB3027`, and the launcher says "Build failed. Run scripts\Verify.ps1", which is the wrong
   cause. Reproduced for the WPF app in an isolated worktree. Suggested fix: build only the project being launched, and
   before building, detect a running copy of that exe and ask the owner to close it, without closing it automatically.
5. **Fixed: `README.md` described the old launcher**: it names `Media Workbench.lnk` (no longer in the repo; use
   `Launch.cmd`) and says the launcher "builds ... if none exists" (it now always builds and prints `Built:`/`Commit:`).
6. Fixed (minor): `scripts/Verify.ps1` ran the Avalonia tests without the `trx` logger, so CI uploads no Avalonia results.
   The view-model logic is a second copy (~5,000 lines); every `master` fix must be ported until the WPF app is retired
   (the recent folder-tree fixes are ported correctly).

How they were fixed:
- Import (2, 3): `App.ImportClassicData(dataDirectory, classic)` writes the marker last; any failure is logged to app.log
  and the whole copy runs again next start. The catalog is copied with SQLite's online backup through a **read-only**
  connection (consistent while the WPF app writes; never checkpoints or changes the WPF files), then `PRAGMA quick_check`.
  `ImportTests` cover: WPF app holding the catalog open with rows only in the WAL (copy complete, WPF data files byte for
  byte unchanged; `-shm` is SQLite's shared lock table and is excluded), a crash-left WAL, a failure then retry, no WPF data.
  On a PC where the first start may have gone wrong, deleting `%LOCALAPPDATA%\MediaWorkbench.Avalonia\imported-from-wpf-app.txt`
  makes it copy again (replacing what the Avalonia app changed since).
- Launcher (4): builds only the project it starts; if that exe is running (tasklist in CSV form: the table form cuts names to
  25 characters) it says so and waits, never closing it. Batch wrappers must have CRLF line endings: an LF-only `.cmd` made
  `goto` misbehave in testing.
- README (5) and Verify (6): updated; Avalonia results go to `artifacts/test-results/avalonia-tests.trx`.

## Working agreements (also in Claude's memory)

Plain short explanations; commit/push only when asked; big work on a branch; project-local venvs for Python; Astra only when stuck;
verify visually with the capture script.

**Owner data is off limits for testing.** Never start either app without `--data-dir <temp folder>` (without it the
Avalonia app copies the WPF data on first start). Tests and runs must keep data, temp files and exports inside their own
folder; do not read or write `%LOCALAPPDATA%\MediaWorkbench`, `%LOCALAPPDATA%\MediaWorkbench.Avalonia` or
`Pictures\MediaWorkbench Exports`. Check those locations are unchanged after a test run.
