# Design notes

Media Workbench is an edit-suite light table: you find a moment in footage and take the exact pixels out. The interface should read like a tool for that job. These notes record the choices so later changes stay consistent.

## Colours (`src/MediaWorkbench.App/App.xaml`)

The theme is a cyberpunk night: deep indigo surfaces with one electric-cyan accent. It is dark but not black, and each layer is a clear step lighter than the one behind it, so panels, buttons and fields are easy to tell apart. Only lit things are coloured — the accent marks what is live or chosen, never decoration.

| Token | Value | Where it is used |
| --- | --- | --- |
| Canvas | `#161326` | Window background, export job cards |
| Panel | `#211D38` | Sources, inspector, filmstrip, folder tree and graph panels, tour callout |
| Raised | `#332C55` | Buttons, tag chips, hover and selected rows |
| Input | `#100E1C` | Text boxes, drop-downs and checkboxes (inset, darker than the canvas) |
| Monitor | `#0B0A14` | Preview well and thumbnail wells, the darkest surface so pictures stand out |
| Border | `#514A86` | Control outlines, hairlines, slider and progress tracks |
| Ink | `#EDEAFF` | Text, selected inspector tab, timeline playhead |
| Muted | `#A9A2CC` | Secondary text, section headings, ruler labels |
| Accent | `#00E5FF` | Timeline handles and range, the one primary button in view, selected thumbnail and folder, active centre tab (its lit top edge glows), keyboard focus, tour outline |
| Accent alt | `#FF4BD8` | The favorite star on a thumbnail, and nothing else — one magenta mark that never competes with the cyan |
| Error / Success | `#FF4D6D` / `#2BE58B` | Notification kind only |
| Chart photo / video / audio | `#5A8CFF` / `#E85C2A` / `#00AC75` | Folder graph series, always in this order |

Custom-drawn controls (timeline, crop surface, folder graph) read these tokens at run time through `Tokens.cs`, so the palette is defined once. The three graph colours were checked with the data-visualisation palette validator against the Panel surface: they pass the colour-blind separation, normal-vision separation and 3:1 contrast checks. Graph text always uses Ink or Muted, never a series colour, and a legend is always drawn.

Sliders, progress bars and checkboxes have their own templates because the Windows Fluent theme otherwise paints them in the system accent colour. The Fluent theme also wins over app-level implicit styles inside a window, so each window re-declares the implicit styles it needs, based on keyed styles in `App.xaml`.

## Type

- Display: **Bahnschrift** (DIN-derived, tabular figures), falling back to Segoe UI. Used for the title, tabs, section headings, the selected file name, library totals, the frame readout, the timeline ruler and folder counts.
- Text: **Segoe UI** 13 for everything else.

Headings are sentence case. Numbers that change while you work (frame counter, folder counts) use tabular figures so they do not jitter.

## Layout

- Left: Sources. Right: Inspector (Details, Tags, Export, Settings). Both side panels can be hidden, and their scrollbars sit at the panel edge with a gutter beside the content. Bottom: the filmstrip, thumbnails only, with no header row; each thumbnail carries a favorite star at the top right in the magenta second accent and a small kind badge at the bottom left, in the same colour the folder graph uses for photos, videos and sound. The sort drop-down is repeated at the right end of the status line, next to the files it orders. The thumbnail-size slider and the **Preview follows scroll** checkbox live at the right end of the status line. With follow on, an Accent marker (a small flag and a hairline) shows where on the filmstrip the preview is looking. While scrolling, only the picture, the file name and its one-line description change; the tools under the preview wait until the file is opened, so nothing jumps.
- Centre: three tabs. **Library** shows the folder tree and the folder graph and is shown while nothing is selected. Both show percentages: each folder's split by media type and its share of its parent; each bar's share of the charted folder. **Preview** shows the selected file with the frame counter, timeline and actions. **Stitch** combines a few pictures into one and stays put while files are selected, because choosing the next picture must not throw you out of the tab you are adding it to. The tabs are radio buttons over overlapping panels rather than a TabControl, because the video surface must stay in the visual tree while hidden. They are drawn as file-folder tabs so the open one is unmistakable: it is taller, takes the Canvas colour of the page below it, has a glowing Accent top edge (the one lit element in the row) and breaks the hairline under the row; closed tabs are shorter, Panel coloured and sit behind that hairline. The selected file's name, details line, Favorite and Stage share this row, so the preview starts directly under it. The shape of the picture is an Accent chip on the details line, because it is looked for far more often than anything else there.
- The wheel means "zoom" over the picture and "step frames" over the timeline and sound strip; each stays true to what is under the pointer. While zoomed, a small chip at the bottom left of the picture gives the magnification and how to get back. Past three screen pixels per picture pixel the picture is drawn unsmoothed, so single pixels can be judged.
- Focus view removes every row and panel except the preview, its timeline and the button row, and adds one Accent-outlined Exit button to that row. It restores the side panels to whatever they were.
- Height under the preview is kept small, because portrait video needs it: the frame counter and frames per second are 21 px figures with 12 px labels, the timeline is 54 px, the sound strip is 38 px with no ruler of its own (the frame ruler is right above it), and the snip button lives in the action row rather than a row of its own. For a landscape video everything under the preview fits in about 170 px.
- When the picture shown is taller than wide, the action buttons leave the bottom and form a 178 px column to the right of the preview, one full-width button per row with the export button first. The decision is kept while the next file loads, so a folder of portrait files does not flip back and forth, and a video being turned in Crop & rotate counts as turned.
- Under the preview: the frame counter and, after a hairline, the **frames per second** in the same figures, then the frame timeline with the **waveform** directly beneath it in the same column and with the same side padding, so a moment in the sound sits under its frame. For an audio file the waveform fills the preview well instead.
- **Crop & rotate video** opens a Panel-coloured tool strip between the preview and the frame counter. The picture gets an Accent outline with a pill-shaped grabber in the middle of each edge; the chosen edge and its grabber turn Ink so it is obvious which one the arrow keys move. Everything outside the crop is dimmed. The picture is drawn turned, so the edge you see on the left is the edge the Left and Right keys move. When the tools are closed but a crop or rotation is still set, a one-line reminder with an Accent bar stays under the preview, because exports will use it. The waveform uses the audio chart colour; the selected section is Accent tinted and everything outside it is dimmed; the playhead is Ink, or the Success colour while playing.
- Messages appear at the bottom right of the centre view, where they can cover picture or hint text but never a button. While a video is being indexed for the first time, a row of fourteen dots beside the frame counter fills with Accent dots and a percentage (estimated from the header frame rate and duration; three travelling dots when there is no estimate). It only appears once FFprobe has reported frames, so a cached index never flashes it. Progress messages are delayed: the decode overlay and progress line only appear when a decode has been pending for about a third of a second, so normal frame stepping shows nothing but the picture.
- The strongest element is the frame counter and ruler timeline under the preview. Corners are 3 px; the preview bezel is 2 px. There is no motion beyond progress bars.

## Stitch tab

The list of pictures on the left is the order they are drawn in, so moving one in the list moves it in the picture. The options sit in one strip above the preview, and the preview is the finished picture drawn smaller, so nothing can be true of the export that is not visible first. The combined pixel size sits next to the export button, because it is the number that decides whether the settings are right. Pictures are held as decoded pixels rather than paths: what was added is what is saved.

## Folder tree

Each row has an album cover, the first and last picture in that folder, fetched only once the row has been scrolled into view and sharing the filmstrip's thumbnail cache. Flattening and removing are right-click actions on a row, and while either is in force a banner above the tree says so and offers one way back. Neither reads the disk again, and neither is remembered: they belong to the folder you opened, not to the app.

## Details tab

One line per fact: a 96 px muted label column and the value beside it, 12.5 px, with a faint hairline between rows, so roughly twice as many facts fit without scrolling. Facts that repeat something already on screen (the file name, the kind) are not rows; pairs that belong together share a row (dimensions with orientation and megapixels, encoded width with height, the two frame rates when they agree). Reveal file and Tag from details sit on the heading line.

## Words

Buttons say what happens (Stage current file, Play marked range, Add checked values as tags). Empty states say what to do next. Counts appear in labels when they change the decision (Export 120 PNGs, Stage 42 filtered, 70 of 99 files in shoot A). Meta strings use commas, not middle dots. The Tour the UI copy is written for someone who has never seen the app: what the control is, what it does, and its shortcut.
