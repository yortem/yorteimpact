# AGENTS.md

Guidance for anyone (human or AI) changing this project.

## Language and tone

- The user communicates in Hebrew; answer in Hebrew unless asked otherwise.
- Be concise and direct. Explain only what is needed to use or verify a change.

## Project

YorteImpact is a WPF desktop layout/slicing tool for Windows on .NET 8.

- Entry point: `App.xaml(.cs)` → `MainWindow`.
- Documents: `DesignDocument` with a flat `Elements` list and `Selected`.
- Elements: `ShapeElement`, `ImageElement`, `TextElement` (all derive from
  `DesignElement`).
- Persistence: `.yimpact` JSON via `YorteProjectService`; flat export via
  `SliceExportService`.
- Settings: `%AppData%\YorteImpact\settings.json`.

## Build and run

```powershell
dotnet build
```

- Do **not** start, stop, or restart the app unless the user asks.
- If the app is running, `bin\Debug\net8.0-windows\YorteImpact.exe` is locked.
  In that case build to a temporary output (`dotnet build -o <temp-dir>`) or
  ask the user to close the app.
- There are no automated tests; verify with a clean build and, when
  appropriate, ask the user to check the UI.

## Documentation rules

1. **Never mention competitor products** (by name or brand) in the README,
   code, comments, UI strings, tooltips, commit messages or any other project
   file. Describe features on their own terms.
2. **Update `README.md` on every change** that affects behavior, UI, shortcuts,
   file formats, defaults or the project layout. Keep the README the single
   source of user-facing documentation.
3. Keep `README.md` organized by topic (getting started, workspace, objects,
   slicing, colors, navigation, files, shortcuts, project layout, notes).
4. Reference PDFs or other research material are local-only; do not link to
   them from the README or copy competitor branding into the project.

## Code conventions

- C# / WPF, .NET 8, nullable enabled, implicit usings.
- Follow the existing dark visual language and palette:
  - background `#080A11`, chrome `#0D131D`, cards `#111A27`,
    inputs `#04070D` / `#05080E`, borders `#1A2636` / `#223248`,
    accent `#2E9BFF`, text `#E6EEF6`, muted `#8EA0B4`.
- Keep numeric fields borderless and near-black; card titles brighter than
  field labels.
- Use `ValueSlider` for numeric settings and `DarkCombo` for selects.
- Preserve `.yimpact` backward compatibility: add new DTO fields with
  sensible defaults; do not renumber existing fields.
- Keep comments minimal and factual. Do not add comments that merely restate
  code.
- Prefer existing services/controls over new dependencies.
- Do not commit generated outputs (`bin`, `obj`, `*_wpftmp.csproj`) or local
  reference material; they are in `.gitignore`.

## Project layout

| Path | Role |
|---|---|
| `Models/DesignElement.cs` | Base element: geometry, rotation, flip, opacity, soft edge, shadows, transparency gradient |
| `Models/ShapeElement.cs` | Rounded rectangle with solid/gradient fill and border |
| `Models/ImageElement.cs` | Raster element with rounded corners |
| `Models/TextElement.cs` | Editable text: font, size, styles, direction, spacing, wrapping, metrics |
| `Models/SliceRegion.cs` | Slice/export rectangle |
| `ViewModels/DesignDocument.cs` | Elements, selection, Z-order, canvas/pasteboard geometry |
| `Controls/SelectionOverlay.cs` | Selection outline, handles and hit-testing |
| `Controls/ValueSlider.xaml(.cs)` | Numeric field with steppers and dropdown slider |
| `Controls/TextElementView.cs` | Text renderer for letter spacing, gradients and RTL |
| `Controls/ColorPickerDialog.xaml(.cs)` | In-app color picker with live preview |
| `NewCanvasDialog.xaml(.cs)` | Manual size and visual presets |
| `SettingsDialog.xaml(.cs)` | Startup canvas defaults |
| `MainWindow.xaml(.cs)` | Shell, input handling, inspector, clipboard and slicing |
| `App.xaml(.cs)` | Startup flow |
| `Services/AppSettings.cs` | Persisted preferences |
| `Services/ClipboardHelper.cs` | Clipboard image import and file drop |
| `Services/SliceExportService.cs` | Zoom-aware render/crop/export |
| `Services/SliceCutService.cs` | Slice punch and flat-color analysis |
| `Services/BackgroundRemoverService.cs` | Magic-wand / auto background removal |
| `Services/CanvasSampler.cs` | Eyedropper color math |
| `Services/ContourService.cs` | Pixel-hugging selection outlines |
| `Services/ScreenColorSampler.cs` | Global click hook and desktop pixel sampling |
| `Services/UndoService.cs` | Snapshot undo/redo |
| `Services/YorteProjectService.cs` | `.yimpact` save/load |
| `Services/DarkTitleBar.cs` | DWM dark title bar helper |
| `Services/ResizeService.cs` | Pure resize math for the selection handles |
| `README.md` | Public user documentation |
| `AGENTS.md` | This contributor/agent guide |
| `.gitignore` | Build, editor and local reference outputs |

`README.md` is the public document — keep it product-focused and free of
internal file maps. The table above belongs here. Reference PDFs and other
research material are local-only and ignored by Git.

## Git

- Do not commit, amend, push or force-push unless the user explicitly asks.
- When asked to clean history, create a local backup first (branch or bundle)
  and explain the plan before force-pushing.
- Stage only intended files; never commit secrets or generated binaries.

## Verification checklist

Before finishing a change:

- `dotnet build` succeeds with 0 errors.
- README reflects the change (features, shortcuts, code map).
- No competitor names were introduced anywhere.
- No generated files or local reference material were added to Git.
