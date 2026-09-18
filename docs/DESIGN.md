# Design notes

Media Workbench is an edit-suite light table: you find a moment in footage and take the exact pixels out. The interface should read like a tool for that job. These notes record the choices so later changes stay consistent.

## Colours (`src/MediaWorkbench.App/App.xaml`)

The theme is dark but not black. Each layer is a clear step lighter than the one behind it, so panels, buttons and fields are easy to tell apart.

| Token | Value | Where it is used |
| --- | --- | --- |
| Canvas | `#23211C` | Window background, export job cards |
| Panel | `#2E2B25` | Sources, inspector, filmstrip, folder tree and graph panels, tour callout |
| Raised | `#403C33` | Buttons, tag chips, hover and selected rows |
| Input | `#1B1915` | Text boxes, drop-downs and checkboxes (inset, darker than the canvas) |
| Monitor | `#141310` | Preview well and thumbnail wells, the darkest surface so pictures stand out |
| Border | `#5A5447` | Control outlines, hairlines, slider and progress tracks |
| Ink | `#F3EEE3` | Text, selected inspector tab, timeline playhead |
| Muted | `#BBB3A0` | Secondary text, section headings, ruler labels |
| Accent | `#EBBC45` | Timeline handles and range, the one primary button in view, selected thumbnail and folder, active centre tab, favorite star, keyboard focus, tour outline |
| Error / Success | `#E0645F` / `#7CB86A` | Notification kind only |
| Chart photo / video / audio | `#3987E5` / `#D95926` / `#199E70` | Folder graph series, always in this order |

Custom-drawn controls (timeline, crop surface, folder graph) read these tokens at run time through `Tokens.cs`, so the palette is defined once. The three graph colours were checked with the data-visualisation palette validator against the Panel surface: they pass the colour-blind separation, normal-vision separation and 3:1 contrast checks. Graph text always uses Ink or Muted, never a series colour, and a legend is always drawn.

Sliders, progress bars and checkboxes have their own templates because the Windows Fluent theme otherwise paints them in the system accent colour. The Fluent theme also wins over app-level implicit styles inside a window, so each window re-declares the implicit styles it needs, based on keyed styles in `App.xaml`.

## Type

- Display: **Bahnschrift** (DIN-derived, tabular figures), falling back to Segoe UI. Used for the title, tabs, section headings, the selected file name, library totals, the frame readout, the timeline ruler and folder counts.
- Text: **Segoe UI** 13 for everything else.

Headings are sentence case. Numbers that change while you work (frame counter, folder counts) use tabular figures so they do not jitter.

## Layout

- Left: Sources. Right: Inspector (Details, Tags, Export, Settings). Both side panels can be hidden, and their scrollbars sit at the panel edge with a gutter beside the content. Bottom: the filmstrip, thumbnails only, with no header row. The thumbnail-size slider lives at the right end of the status line.
- Centre: two tabs. **Library** shows the folder tree and the folder graph and is shown while nothing is selected. Both show percentages: each folder's split by media type and its share of its parent; each bar's share of the charted folder. **Preview** shows the selected file with the frame counter, timeline and actions. The tabs are radio buttons over two overlapping panels rather than a TabControl, because the video surface must stay in the visual tree while hidden.
- Messages appear at the bottom right of the centre view, where they can cover picture or hint text but never a button. Progress messages are delayed: the decode overlay and progress line only appear when a decode has been pending for about a third of a second, so normal frame stepping shows nothing but the picture.
- The strongest element is the frame counter and ruler timeline under the preview. Corners are 3 px; the preview bezel is 2 px. There is no motion beyond progress bars.

## Words

Buttons say what happens (Stage current file, Play marked range, Add checked values as tags). Empty states say what to do next. Counts appear in labels when they change the decision (Export 120 PNGs, Stage 42 filtered, 70 of 99 files in shoot A). Meta strings use commas, not middle dots. The Tour the UI copy is written for someone who has never seen the app: what the control is, what it does, and its shortcut.
