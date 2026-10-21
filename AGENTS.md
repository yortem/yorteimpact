# AGENTS.md

Guidance for AI/human contributors. Read this first, then only the files named
by the relevant change recipe.

## Working with the user

- The user writes Hebrew; reply in Hebrew.
- Be concise and direct. Explain only what is needed to use or verify a change.
- For ambiguous features, ask 3-5 focused questions **before** coding.
- Make the smallest change that satisfies the request. No unasked refactors,
  redesigns, dependencies or "while I was there" edits.
- Do not start, stop or restart the app unless the user asks.
- Do not commit, amend, push or force-push unless the user asks.

## Build and runtime checks

```powershell
dotnet build
```

- There are no automated tests. A clean build with 0 errors is the baseline.
- For UI/XAML changes, build and ask the user to verify. Do **not** create a
  temporary console/WPF harness just to instantiate a window or check a control;
  that is slow and mostly duplicates what the user can see in seconds.
- Use a headless harness (kept outside the repo) only for pure pixel math or a
  reproducible non-visual bug that cannot be checked from the UI.
- Always run a plain `dotnet build` into the normal `bin\Debug` output.
- If the app is running and the build fails because
  `bin\Debug\net8.0-windows\YorteImpact.exe` is locked, ask the user to close
  the app, then run `dotnet build` again.
- Only build to a temp output (`dotnet build -o <temp-dir>`) when you need to
  run a check that the user cannot perform themselves.
- Build after each vertical slice, not only at the end.
- If the app will not open, check Windows Event Viewer → Application for
  `.NET Runtime` (event 1026). WPF startup crashes are often event handlers
  firing during `InitializeComponent`.
- For UI changes, ask the user to verify. Do not launch the app unless asked.

## Reading protocol (token budget)

- Never list or read `bin/`, `obj/`, `*_wpftmp.csproj` or other generated files.
- Start with this file and the relevant README section.
- Use the matching change recipe; read only the 1-3 files it names.
- Search by symbol first. Read method ranges, not whole 4,000-line files.
- `rg` is not installed in this environment. Use the `grep` tool (or
  `Select-String` for file/line queries), not `rg`.
- For multi-file features, list the touchpoints from the recipe before editing;
  do not explore the whole codebase first.
- Local reference material/PDFs are local-only: do not link, copy or commit it.
  Read it only when the user explicitly asks.
- The app is code-behind WPF. Do not invent MVVM frameworks, DI or new layers
  unless the task truly requires them.

## Project in one paragraph

YorteImpact is a WPF layout/slicing tool for Windows on .NET 8. A
`DesignDocument` holds a flat list of `DesignElement`s (shapes, images, text,
and one optional bottom paint layer) plus selection and artboard geometry.
`MainWindow` is the shell/controller: tools, mouse pipeline, attribute bars,
inspector, clipboard and slicing. Projects save as `.yimpact` JSON with
embedded PNG data; flat images export through `SliceExportService`. Preferences
live in `%AppData%\YorteImpact\settings.json`.

## UI and user preferences

- Keep the existing dark palette:
  `#080A11` background, `#0D131D` chrome, `#111A27` cards,
  `#04070D`/`#05080E` inputs, `#1A2636`/`#223248` borders,
  `#2E9BFF` accent, `#E6EEF6` text, `#8EA0B4` muted.
- Numeric settings use `ValueSlider`. Colors use `AttrSwatch`.
- `ValueSlider` has an `Inline` mode (label + live value + always-visible
  slider), used by the Photo tab. Use it instead of adding popup inputs.
  Compact number fields scrub on press-drag and close the slider on release;
  double-click selects the text, so do not reintroduce click-to-open.
- `ValueSlider.TrackBrush` renders a colored track; Photo uses it only for
  Temperature and Tint. Add gradient resources in `MainWindow.Resources`.
- Tool modes use compact icon groups (`SegBtn` toggle buttons), **not**
  ComboBox selects. Popup lists use `PresetItemBtn` rows.
- The two copy buttons use `PlayCopyFeedback`: a one-second centered overlay
  that dims the window and fades away. Keep it hit-test transparent. Copy as
  image passes the frozen render as a preview; release it when the overlay
  finishes.
- The Elements sidebar animates its width with eased curves while the viewport
  follows. Do not force `ScrollHost.UpdateLayout()` from `SizeChanged` during
  that animation; it causes a nested-layout stutter.
- Keep the top attribute bar compact and horizontally scrollable; cards and
  fields stay low-contrast and near-black.
- Keep comments minimal and factual. No comments that restate code.
- Prefer existing controls/services over new dependencies.

## Invariants

- `Elements` is flat. Normal Z-order is `0..N`; `PaintLayerElement` is reserved
  at `BackgroundZ = -10000` and must never be selectable, snapped or reordered.
- Undo is a whole-document snapshot. Call `PushUndo()` **before** the first
  mutation of a gesture; never per mouse move.
- Multi-selection style controls loop over `SelectedElements` of the matching
  type and snapshot once; never mutate only `_doc.Selected` (the last click).
- Coordinates are pasteboard DIPs. The artboard is `ArtboardRect`; zoom is a
  layout transform on the pasteboard.
- Slice markers are hidden only by `Space` (or cleared by Esc). A plain Slice
  click resets the active region and must not reveal hidden markers; only an
  actual drag shows a new slice.
- `.yimpact` is backward compatible: add DTO fields with defaults; do not
  renumber existing fields.
- Images may hold a live `WriteableBitmap`. Do not freeze an `ImageBrush` whose
  source is a `WriteableBitmap`, or it stops updating.
- Photo edits are non-destructive: `ImageElement.Source` is the original raster;
  `DisplaySource`/`ImageBrush` are the adjusted render. Never write adjusted
  pixels back into `Source`.
- Sampling/eyedropper uses `DisplaySource`; brush, background removal and
  `.yimpact` use `Source`.
- Canvas painting uses a bottom raster layer; image retouching edits the
  embedded raster copy, not the original file.
- `ProjectFile` stores the exact artboard rect (`ArtboardX/Y` plus size).
  `ApplyTo` must restore it, not just call `NewCanvas`; otherwise undo after
  `Ctrl+R` moves the canvas under the elements.
- AI edits arrive as JSON commands and are validated on clones before the real
  document is touched. Apply a whole batch with one `PushUndo()`, never touch
  `PaintLayerElement`, and keep photo commands non-destructive.
- AI coordinates are artboard-relative: `AiCanvasBrief` emits `X/Y` relative to
  `(ArtboardX, ArtboardY)`, and `AiCommandApplier` adds that origin before
  touching the document. Keep the brief, prompt and applier in the same space.
- Two-pass refinement is one user action: both passes share a single
  `PushUndo()`. The second pass rebuilds the brief/map/PNG from the result of
  the first pass and applies only fixes from a fresh validation.
- `add_shape` accepts `Behind=true` to place a new background panel under
  existing content without reordering it by an unknown new id.
- RTL text created inside a shape gets a `WrapWidth` from the container when
  the model omits it; right alignment needs a full text column, not a tight box.
- Chat bubbles show one natural summary per round. Do not append the
  per-command `report.Applied` labels to the chat; those are for status counts.
- Never invent a price or number that is absent from the current instruction.
  Chat history is context only; do not reuse a price from an earlier turn.
- `AiCanvasBrief` marks images with a meaningful alpha area as
  `transparent=true`. Keep the prompt rule that a transparent cutout gets no
  white card or artificial background behind it.

## Change recipes

### Add a tool

1. `MainWindow.xaml.cs`: add to `ActiveTool` (and `DragMode` if it drags).
2. `MainWindow.xaml`: add a `SideTool` button to the left rail.
3. `MainWindow.xaml`: add an `AttrXxxPanel` to the attribute bar.
4. `MainWindow.xaml.cs`: implement `ToolXxx_Click`, add a `SetTool` status
   case, and update `UpdateToolUI` (toggle state, panel visibility, tool name,
   hint).
5. Input: handle `Pasteboard_MouseDown/Move/Up/Leave`; clean up on `Escape`
   and tab switch.
6. Undo: one `PushUndo()` at gesture start.
7. README: tool table, attribute bar, shortcuts.

### Add a tool setting

1. Add a runtime field with a sensible default.
2. Put the control in the tool's `AttrXxxPanel`: `ValueSlider` for numbers,
   `SegBtn` icon group for modes, `AttrSwatch` for colors.
3. Add the handler with the panel's `_suppressXxxToolbar` guard; update the
   live value and refresh dependent UI.
4. Persist in `AppSettings` only if it should survive restarts; use the
   fresh-load pattern in `PersistXxx` so unrelated settings are not clobbered.
5. README.

### Add an inspector card or setting

1. Add the card/controls to `InspectorPanel` in `MainWindow.xaml` with
   `x:Name` fields.
2. Populate and enable them in `RefreshInspector`; gate by selection and
   element type.
3. Hook `OnSelectionChanged` / `OnElementPropertyChanged` where needed.
4. Guard programmatic updates with `_suppressInspector`; use
   `ArmInspectorUndo()` or `PushUndo()` before mutations.
5. Add the model property and copy it in `Clone()`.
6. Persist via `YorteProjectService.ToDto/ApplyTo` if it belongs in the file.
7. README.

### Add a model/element

1. Create the model under `Models/`; derive from `DesignElement`.
2. Add a `DataTemplate` in `MainWindow.xaml`.
3. Update `DesignDocument` for selection, Z-order and bounds behavior.
4. Implement `Clone()`.
5. Add DTO fields + `ToDto` + `ApplyTo` for `.yimpact` compatibility.
6. Update `SliceExportService` only if the element must copy/export.
7. README.

### Add a raster/painting feature

1. Work on a BGRA32 `WriteableBitmap`; keep a `byte[]` mirror for reads.
2. Write only dirty rectangles with `WritePixels`.
3. Capture undo before the first pixel change.
4. Map pasteboard points to target pixels explicitly; do not assume 1:1.
5. Preserve alpha; never freeze the live brush/bitmap.
6. Verify export/copy and `.yimpact` round-trip.
7. README.

### Add or change a photo adjustment/effect

Photo edits are non-destructive: `ImageElement.Source` stays untouched;
`DisplaySource`/`ImageBrush` are rendered from `PhotoAdjustments`.

1. `Models/PhotoAdjustments.cs`: add the value with clamping, then update
   `IsDefault`, `Reset`, `CopyFrom` and `Clone` (Clone uses CopyFrom).
2. `Services/PhotoAdjustmentService.cs`: apply it in `Apply` on the BGRA
   `byte[]`, preserving alpha. Keep this order:
   tone → clarity → sharpen → glow → blur → vignette → grain.
   Reuse `BoxBlur` for local/soft work; avoid per-pixel allocations.
3. Persistence: add the field to `YorteProjectService.PhotoAdjustDto`, then
   update `ToDto(PhotoAdjustments)` and `FromDto`.
4. UI: add an inline `ValueSlider` in `MainWindow.xaml` under `PhotoPanel`
   (`Inline="True"`, `Tag="..."`, `ValueChanged="PhotoAdjust_ValueChanged"`).
   Directional color sliders may set `TrackBrush` to a gradient resource.
5. Sync: `RefreshPhotoPanel`, `TryGetPhotoValue`, `SetPhotoValue`.
6. Optional look: add one `PhotoPreset` entry in `_photoPresets`; preset
   previews are generated automatically from the selected image.
7. Undo/export already work through the `Photo` DTO and `ImageBrush`/export
   render; do not add separate paths.
8. README.

### Add or change an AI command

1. `Services/Ai/AiCommandApplier.cs`: add the op to `AllowedFor`, validate its
   props in `ValidateCommand`, and apply it in `ApplyOne` (or
   `ApplyToElement`). `X/Y` props are artboard-relative: add `ArtboardX/Y`
   before clamping to the document. Reject the paint layer. Keep Hebrew/Arabic
   text RTL and right-aligned unless centered.
2. `Services/Ai/AiPromptBuilder.cs`: add the op and its props to the Hebrew
   schema so the model can emit it. Keep the relative-coordinate and RTL rules
   in sync with the applier.
3. `Services/Ai/AiChatService.cs`: destructive batches go through
   `confirmDelete`; everything else is applied after `Validate` succeeds.
   Two-pass review reuses the same callbacks and one shared `PushUndo()`.
4. `Services/Ai/AiCanvasBrief.cs`: expose any new element data the model needs
   to target it; emit `X/Y` relative to `(ArtboardX, ArtboardY)`.
5. README: describe the new capability in the AI section.

## MainWindow touchpoints

Search for these names instead of scanning the file:

- Tool state: `ActiveTool`, `DragMode`, `SetTool`, `UpdateToolUI`
- Mouse pipeline: `Pasteboard_MouseDown`, `Pasteboard_MouseMove`,
  `Pasteboard_MouseUp`, `Pasteboard_MouseLeave`
- Attribute bars: `Attr...Panel`, `Refresh...Toolbar`, `Attr..._ValueChanged`
- Inspector: `RefreshInspector`, `OnSelectionChanged`,
  `OnElementPropertyChanged`, `_suppressInspector`
- Photo tab: `InspectorTabBtn`, `PhotoTabBtn`, `SetRightPanelMode`,
  `RefreshPhotoPanel`, `PhotoAdjust_ValueChanged`, `PhotoPanel`, `_suppressPhoto`
- AI chat: `SideAiBtn`, `AiChatPanel`, `BindAiChat`, `RefreshAiChat`,
  `SendAiPromptAsync`, `CaptureAiCanvasPng`, `AfterAiApply`, `AiConfirmBar`,
  `ResolveAiConfirm`
- Undo: `PushUndo`, `ArmInspectorUndo`
- Persistence: `YorteProjectService.Capture/ToDto/ApplyTo`, `Persist...`
- Export/copy: `SaveDocument`, `SliceExportService`

## WPF pitfalls

- `x:Name` fields are null until `InitializeComponent` reaches them. If XAML
  sets a value that raises a handler, set a `_suppress...` flag before
  `InitializeComponent` and/or guard for null controls.
- Setting `ValueSlider.Minimum` can raise `ValueChanged` during XAML parsing.
  Do not call `Mouse.GetPosition` or touch later controls from such handlers.
- Set the matching `_suppress...` flag before `InitializeComponent` when the
  XAML raises handlers (Photo uses `_suppressPhoto`).
- The right panel is not a `TabControl`: `InspectorTabBtn`/`PhotoTabBtn` call
  `SetRightPanelMode`, which swaps `InspectorPanel`/`PhotoPanel` visibility.
  The Photo tab targets `ImageElement` only; otherwise switch back to Inspector.
- Keep Photo controls inline (`ValueSlider.Inline="True"`); do not reintroduce
  number/popup editors or multiple open slider popups.
- Programmatic control updates must use the matching `_suppress...` flag.
- Never create undo snapshots from UI event handlers; only document mutations.
- A successful build does not catch XAML event-order crashes. Runtime-test with
  permission, or ask the user to check the UI.
- Keep generated files out of the repo: `bin`, `obj`, `*_wpftmp.csproj`.

## Project map (compact)

| Area | Files |
|---|---|
| Models | `DesignElement` (base), `ShapeElement`, `ImageElement`, `TextElement`, `PaintLayerElement`, `SliceRegion`, `PhotoAdjustments`, `PhotoPreset`, `GlassEffect` |
| Document | `ViewModels/DesignDocument.cs` — elements, selection, Z-order, artboard/pasteboard |
| Shell | `MainWindow.xaml(.cs)`, `App.xaml(.cs)`, `NewCanvasDialog`, `SettingsDialog` |
| Controls | `SelectionOverlay`, `ValueSlider`, `ColorPickerDialog`, `TextElementView` |
| Raster services | `BrushEngine`, `BackgroundRemoverService`, `CanvasSampler`, `ContourService`, `ScreenColorSampler`, `PhotoAdjustmentService`, `GlassEffectService` |
| AI assistant | `Services/Ai/AiClient`, `AiChatService`, `AiCommandApplier`, `AiCanvasBrief`, `AiPromptBuilder`, `AiKeyProtector`, `AiProviderSettings` |
| IO/settings | `YorteProjectService` (`.yimpact`), `AppSettings`, `ClipboardHelper` |
| Render/slicing | `SliceExportService`, `SliceCutService`, `ResizeService` |
| Undo/window | `UndoService`, `DarkTitleBar` |
| Docs | `README.md` (public), `AGENTS.md` (this file) |

## Documentation and Git

- Never mention competitor products or brands in any project file (README,
  code, comments, UI, tooltips, commits). Describe features on their own terms.
- Update `README.md` for every behavior, UI, shortcut, default or file-format
  change. Keep it product-focused and free of internal file maps.
- Keep local research material out of the repo and out of Git.
- Never commit secrets, generated binaries or unrelated files.
- Stage only intended files. No commit/amend/push unless explicitly asked.

## Done checklist

- `dotnet build` succeeds with 0 errors.
- README reflects the change.
- Undo and persistence cover every document mutation.
- Photo changes keep `Source` untouched and render through `DisplaySource`;
  verify undo, `.yimpact` round-trip and export.
- No competitor names, generated files or local research material.
- No new dependency or abstraction without a clear reason.
- UI behavior verified by the user (or by a permitted test run).
