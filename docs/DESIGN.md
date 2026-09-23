# Design notes

Media Workbench is an indexing and extraction bench: several folders open at once, find the moment or the picture, take the exact pixels out. The look is a **glass terminal**: navy panels you can almost see through, a thin cyan edge around each, square corners, monospace labels. These notes record the rules so later changes stay consistent.

## Colours (`src/MediaWorkbench.App/App.xaml`)

| Token | Value | Where it is used |
| --- | --- | --- |
| Canvas | `#0A0F22` | Window background, behind the panels |
| Glass | `#EE18214A` → `#E6111834` | Panel fill, lighter at the top, with a faint white hairline along the top edge |
| Edge | `#1E8FA6` | The thin outline of every panel, the preview and the stitch well |
| Raised | `#1A2450` | Buttons at rest |
| Hover / Selected | `#202D60` / `#0E3346` | Pointed-at rows and buttons / the chosen row, tab or thumbnail |
| Input | `#080C1C` | Text boxes and drop-downs, a dark well |
| Monitor | `#04060E` | Preview and thumbnail wells, the darkest surface so pictures stand out |
| Border | `#2A3A6E` | Quiet outlines and hairlines inside panels, tracks |
| Ink / Muted | `#E4F1FF` / `#8C9EC6` | Text / secondary text and labels |
| Accent | `#00E5FF` | What is live or chosen: the primary button in a row, the open view, the chosen tab, row and thumbnail, focus, the frame number, workspace folder names, panel titles |
| Accent alt | `#FF2BD6` | Favorites only: the star on a thumbnail, the Favorites filter when on |
| Tag | `#D8E62A` | Tag chips, the "hidden folders" banner and the "not in view" chip |
| Error / Success | `#FF3D5E` / `#2BE58B` | Messages only |
| Chart photo / video / audio | `#5A8CFF` / `#E85C2A` / `#00AC75` | Folder graph and kind badges, always in this order |

Custom-drawn controls (timeline, crop surface, folder graph) read these tokens at run time through `Tokens.cs`. The window has its own `ThemeMode`, which merges the Windows 11 styles into the window's resources where they would win over the app's implicit styles, so `MainWindow.xaml` repeats every implicit style, based on the keyed ones in `App.xaml`. That includes the scroll bars: Windows' own fade in and out whenever content changes, which showed as a blink at a panel's edge, so the app draws its own, thin and square, present whenever there is something to scroll.

## Type

- **Cascadia Mono** (monospace, built into the app from `src/MediaWorkbench.App/Fonts`, SIL Open Font License) for anything that reads like a terminal: panel titles (`▍ WORKSPACE`), view and tab names, button labels, readouts, counts, the status line, tags. Titles, view names and button labels are capitals.
- **Segoe UI** 12.5 for body text: file names, details values, explanations.

Numbers that change while you work use tabular figures so they never jitter.

## Spacing

- Every button, box and drop-down is **28** tall; only the small inline icons (a tab's close cross, the tree's arrows) are 22 or less.
- Buttons in a row sit in a `SpacedPanel`: one gap for the whole row (6 by default), never margins on single buttons, so a hidden button leaves no double gap. Groups within a bar are 10 to 12 apart.
- Panels are 8 apart and have 12 × 10 padding.

## Layout

- **Top bar** (one row): the workspace-panel switch and the name on the left; search with its clear cross, media type, sort and the favorites switch in the middle; rescan, the **output folder** (a menu to open or change it), settings, the tour and the inspector switch on the right.
- **Workspace panel** (left): every folder open side by side, in one tree under **All folders**. Workspace folders are in Accent and start open. Clicking any folder shows it in the filmstrip without rescanning. Collections and Tags are folding sections at the bottom; a collection or tag search opens as one more entry in the workspace, beside the folders, never instead of them.
- **Centre**: OVERVIEW (totals and the graph of the folder in view), PREVIEW and STITCH, as radio buttons over overlapping panels so the preview keeps its state while another view is open. The selected file's name, shape chip and summary share the row with ★, STAGE and focus view.
- **Preview**: the picture, and under it a dock of fixed height (readout 26, timeline 50, sound 30, buttons 36) that is the same for a photo, a video and a sound file. Parts that do not apply are hidden with their space kept, never removed, so selecting another file never resizes the picture or moves a button. Crop and rotate tools, the crop size and the transform reminder are laid over the picture. The button row is play controls on the left and COPY, CROP, EXPORT and MORE on the right; less used actions live in MORE.
- **Inspector** (right): DETAILS, TAGS, EXPORT, ADVANCED. Rarely used things (tool paths, cache, collection files, favorites transfer, tag export, keyboard list) are in ADVANCED.
- **Filmstrip** (bottom): folder **tabs** above the thumbnails, each named after its folder with its file count; clicking a folder moves the selected tab, **+** opens another, middle-click closes one. The file you are working on stays open when you switch tabs. The count, thumbnail size and Follow scroll sit at the right of the tab row.
- **Status line**: what just happened; READING FOLDERS on the right while a folder is being read.
- Messages appear at the bottom right of the centre view, at most two at a time.

## Stability rules

These are checked by `DesktopSmokeTest.Layout.cs` and measured by `tools/motion-probe`:

1. Selecting a file, switching folders or tabs never moves or resizes a panel or a button.
2. A control that comes and goes with the file is hidden with its space kept (`BoolHidden`), not collapsed.
3. Text that changes length has a fixed slot or is trimmed, never pushing its neighbours.
4. Nothing is cleared and refilled: details keep their first rows while the rest load; thumbnails stay with their files (up to 400) instead of blanking when they scroll away; tree rows that did not change are kept.
5. Playing and paused video are drawn by the same surface, so pausing shows the very frame that was playing and never a flash of another.

## Words

Buttons say what happens. Empty states say what to do next. Counts appear where they change the decision (EXPORT 120 PNGs, 70 of 99 files in Shoots\shoot A). The tour is written for someone who has never seen the app: what the control is, what it does, and its shortcut.
