# UI inventory and clean-up plan

**Applied in the glass-terminal overhaul (2026-09-23, second pass):** the top bar and filter bar are one row; the side-panel
switches, rescan, settings, favorites-only, favorite and focus view are icons; the tour is the **?** button; the output folder is
one button with a menu; the Sources panel became the **workspace** (every folder open side by side in one tree), with the duplicate
Open folder, Stage current file, recent-folder list and hint paragraphs gone; Collections and Tags fold away; favorites transfer,
collection JSON, Stage N filtered and tag export moved to **Advanced** (the renamed Settings tab), where the duplicate export-folder
controls were merged; the library tree moved out of the Overview; the graph and tree paragraphs became tooltips; the per-folder
kind line became a tooltip; In/Out are `[ IN` and `OUT ]`; at most two messages show at a time. The exact In/Out frame and
sound From/To boxes stay in Export, one compact row each, because typing an exact number is part of precise work.

Everything the window shows today, and what to do with each item. Nothing marked **Advanced**, **Merge** or **Remove** has been
changed yet; those are proposals to review. Items marked *done* changed in the stability pass of 2026-09-23.

Key: **Keep** stays where it is. **Icon** keeps it but as a small icon button with a tooltip. **Advanced** moves it to a new
Advanced tab (today's Settings tab) or an Advanced section. **Merge** folds it into another control. **Remove** deletes it
because something else already does the same job. **Tooltip** turns always-visible explanation text into hover text.

## Top bar

| Item | Proposal | Why |
|---|---|---|
| Sources toggle | Icon | Frequent, but a word button is heavy for a show/hide switch. |
| Inspector toggle | Icon, moved to the far right | It should sit next to the panel it opens. |
| "Media Workbench" title | Remove | The window's own title bar already says it. |
| Tour the UI | Merge into a **?** Help button | Used once; the ? button can also hold the keyboard shortcuts. |
| Open folder | Keep | The main way in. |
| Rescan | Icon (⟳) | Occasional. |
| Output: *folder* ↗ | Keep (*done*) | Shows where exports go; click opens the folder. |
| Change… (output folder) | Merge into the Output button as a small ▾ menu | One control for the output folder. |
| Settings | Icon (⚙), opens Advanced | Rarely needed. |

**Merge the top bar and the filter bar into one row.** Both are 44 tall; together they would give the picture 44 more.

## Filter bar

| Item | Proposal | Why |
|---|---|---|
| Search box | Keep | |
| Media type (All / Photos / Videos / Audio) | Keep | |
| Sort order | Keep | The second copy under the filmstrip is gone (*done*). |
| Favorites only | Icon (★ toggle) | Same meaning, a third of the width. |
| "N of M files" | Keep (*done*: fixed width, no longer pushes the search box) | |
| Clear filters | Keep (*done*: its place is kept, so nothing shifts when it appears) | |

## Sources panel (left)

| Item | Proposal | Why |
|---|---|---|
| Current source name and path | Keep name; path to Tooltip | The path wraps to three lines. |
| Cancel scan | Keep | Only shows while scanning. |
| Open folder… (second copy) | Remove | Same as the top bar button. |
| Recent folders list | Keep | |
| "Recent folders. Drop a folder…" hint | Tooltip | |
| Collections list | Keep | |
| Stage to (collection picker) | Keep, smaller | |
| Stage current file (S) | Remove | Same as the Stage button above the picture and the S key. |
| Stage N filtered | Advanced | Bulk action, rarely used. |
| Remove current item | Right-click menu on the file | Only applies inside a collection. |
| "References only…" hint | Tooltip | |
| Manage collections (create, load JSON, save JSON) | Keep "+ New"; JSON load/save to Advanced | |
| Tag filter box | Keep | |
| Known tags drop-down | Merge into the tag filter box as suggestions | Two controls for one choice. |
| Browse all tagged files | Keep | |
| "Searches files you have tagged…" hint | Tooltip | |
| Favorites transfer (export, import) | Advanced | Used when moving PCs. |

## Centre header

| Item | Proposal | Why |
|---|---|---|
| Library / Preview / Stitch tabs | Keep | Stitch could stay hidden until something is added to it. |
| File name, aspect chip, summary line | Keep (*done*: fixed height, the chip keeps its place) | |
| Hidden by filters note | Keep (*done*: keeps its place) | |
| ★ Favorite | Icon | |
| Stage | Keep | |
| Focus view | Icon (⛶) | |

## Library tab

| Item | Proposal | Why |
|---|---|---|
| Totals (files, folders, size; photo/video/audio split) | Keep | |
| Folder tree: covers, name, count, share | Keep | |
| Second line per folder (photo/video/audio split) | Tooltip | Halves the row height; the graph shows the split anyway. |
| Expand all / Collapse all | Right-click menu | |
| Flattened/removed banner with Show all folders | Keep | |
| Paragraph under the tree | Remove | The tour explains it; tooltips carry the rest. |
| Graph with title and subtitle | Keep title; subtitle to Tooltip | |
| Paragraph under the graph | Remove | |

## Preview tab

| Item | Proposal | Why |
|---|---|---|
| Picture with zoom and pan | Keep | Now also zooms while playing (*done*). |
| Empty-state text and buttons | Keep | |
| Crop and rotate tools | Keep (*done*: laid over the top of the picture, no extra row) | |
| Crop/rotate reminder | Keep (*done*: small chip on the picture) | |
| Crop size readout | Keep (*done*: chip on the picture) | |
| Frame number, total, time, frames per second | Keep (*done*: fixed columns, numbers never push each other) | |
| Indexing dots and percentage | Keep (*done*: own column) | |
| "In 0 to out 299, inclusive: 300 frames" | Tooltip on the timeline | Long and rarely read. |
| Mark in / Mark out | Icon ( [ and ] ) | Shortcuts I and O. |
| Timeline, sound strip | Keep | |
| Restart, previous frame, Play/Pause, next frame, play marked range | Keep (*done*: new Restart button; one row) | |
| Copy, Crop, Export frame | Keep | |
| More ▾: Add to stitch, Crop and rotate the whole video, Snip marked sound, Clear the crop area | Keep (*done*) | Used less; kept out of the main row. |
| Notifications (bottom right) | Keep, but at most 2 at a time | Four stacked messages covered a third of the picture. |

## Inspector (right)

| Item | Proposal | Why |
|---|---|---|
| **Details:** kind, summary line | Keep | |
| Reveal file | Keep | |
| Tag from details… | Move to the Tags tab | It is a tagging action. |
| Detail rows | Keep; Folder row to Tooltip | The folder path is the tallest row. |
| **Tags:** tag box, Add tags, tag list, Find files with shared tags | Keep | |
| Tags "More": export tag relationships (JSON) | Advanced | |
| **Export:** destination with Change | Keep (*done*: Change picks a folder directly) | |
| In / Out frame number boxes | Advanced | The timeline and I/O already set them. |
| Range and size estimate text | Keep, one line | |
| Trim selection to MP4 | Keep | |
| Export selected frames / Export all frames | Keep | |
| Audio track and channel | Keep | |
| Audio start / end boxes | Advanced | The sound strip already sets them. |
| Export audio to WAV | Merge with More ▸ Snip marked sound | Same action twice. |
| Photo hint paragraph | Remove | |
| Export queue, Previous exports | Keep | |
| **Settings** (rename to **Advanced**): export folder box and Choose folder | Remove | The top bar's Output button does this. |
| FFmpeg folder, cache limit, Check tools and save | Keep in Advanced | |
| Notes paragraph | Tooltip | |
| Keyboard shortcuts list | Move to the ? Help button | |

## Bottom

| Item | Proposal | Why |
|---|---|---|
| Filmstrip with kind badge and favorite star | Keep | |
| Follow marker | Keep | |
| Status line | Keep | |
| Thumbnail size slider | Keep, smaller | |
| Preview follows scroll | Keep | |
| Second sort drop-down | Removed (*done*) | Same as the filter bar. |

## What the proposals add up to

- About 10 controls removed, because another control already does the same job.
- About 12 moved to Advanced or a right-click menu.
- About 8 explanation paragraphs turned into tooltips.
- About 8 word buttons turned into icons.
- One row of height (44) given back to the picture by merging the two top bars.
