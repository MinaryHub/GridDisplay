# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows display-wall app (.NET 8 / WinForms / x64) that tiles browsers and native
applications into a grid of cells on one monitor. Cells zoom to fullscreen on click, remember
their content across restarts, and are grouped into named "sets" (presets).

## Commands

```powershell
# build / run (Windows)
dotnet build src\GridDisplay\GridDisplay.csproj -c Release
dotnet run   --project src\GridDisplay\GridDisplay.csproj

# ship
dotnet publish src\GridDisplay\GridDisplay.csproj -c Release -r win-x64 --self-contained false -o publish
```

Compile-checking from macOS/Linux works and is the fastest feedback loop when no Windows box is
at hand — it type-checks everything but obviously cannot run the app:

```bash
dotnet build src/GridDisplay/GridDisplay.csproj -c Release -p:EnableWindowsTargeting=true
```

There is no test project. Behaviour here is almost entirely Win32 window-management and input
interception, which only reproduces on a real desktop session; verify changes by running the app.

The `MSB3277 WindowsBase` warning during build is expected — the WebView2 package ships a WPF
assembly this project never loads.

## Architecture

Two hosting strategies, chosen per cell, are the central design decision:

- **Browser cells embed WebView2** (`CellHost.StartBrowserAsync`). Not a reparented external
  browser: an embedded engine is the only way to read the current address, which is what makes
  "remember the URL and restore it next launch" possible. It also bounds in-page fullscreen to
  the control, i.e. to the cell.
- **Program cells reparent a real window** (`NativeAppHost`) with `SetParent` plus stripping of
  `WS_CAPTION`/`WS_THICKFRAME`/`WS_POPUP` and the frame ex-styles. Once the window is a child,
  the OS itself confines maximize/fullscreen to the parent's client area — that containment is
  a consequence of the reparenting, not separate enforcement code.

**Input is intercepted with low-level hooks, not overlay windows** (`Services/InputHook.cs`).
Cells host windows owned by other processes, which consume input before any managed control can
see it, so an overlay cannot reliably win. Two modes:

- *view mode* — `InputHook.MouseCaptureEnabled` is true. `MainForm.OnGlobalMouseUp` swallows
  clicks that land on a cell and turns them into zoom/context-menu actions. Hosted apps see no
  clicks at all.
- *input mode* — entered from a zoomed cell with `AppState.InputModeKey` (default F2). The mouse
  hook stands down and the keyboard hook forwards everything except the release key, so `Esc`
  and shortcuts reach the hosted app.

Guard rails that matter when touching the hooks:

- Hook callbacks run on the UI thread and add latency to *system-wide* input. They must return
  immediately; real work goes through `BeginInvoke`.
- Every hit test goes through `MainForm.CellIndexAtScreen`, which rejects points whose
  `GetAncestor(..., GA_ROOT)` is not the main form. That single check is what keeps dialogs,
  context menus and other applications clickable.
- The keyboard hook must only act while the main form is the foreground window, otherwise the
  app steals keys from the rest of the desktop.
- Modifiers are read with `GetAsyncKeyState`, not `Control.ModifierKeys` — inside a low-level
  hook the key has not been dispatched to any message queue yet.
- A swallowed key-down records its vkCode so the matching key-up is swallowed too; hosted apps
  must never see an unpaired key-up.

**Persistence.** `CellConfig` objects in `GridPreset.Cells` are the *same references* the running
`CellHost` mutates. A WebView2 `SourceChanged` writes straight into `CellConfig.Url` and raises
`StateChanged`, which the form turns into a debounced save. That aliasing is deliberate — do not
clone configs into the cells or live URLs stop being recorded. `StateStore` debounces ~1.5s and
writes atomically (temp file + `File.Move` overwrite) because navigation events are frequent.

**Layout.** `MainForm.Relayout` positions cells manually rather than through a
`TableLayoutPanel`, since zooming means "one cell takes the whole area and the rest go
`Visible = false`". Column/row edges are computed as `innerW * i / cols` so rounding never leaves
gaps. Hover/selection outlines are painted by the form into the inter-cell gap — the form cannot
paint over child windows, so `AppState.CellGap` must stay > 0 for those outlines to be visible.

**Responsiveness choices**, worth preserving:

- Cell content starts staggered (120ms apart) in `LoadPresetAsync`, after the window is already
  painted and clickable.
- All browser cells share one WebView2 user-data folder, so they share a single browser process;
  `CellConfig.ProfileName` separates sessions without separating processes.
- Zooming resizes cells but never reloads them.
- `AppState.SuspendHiddenCells` (off by default) trades switch latency for memory.
- `NativeAppHost` re-asserts geometry from a 1s watchdog rather than a tight timer or a hook.

## Layout of the source

`Models/` plain persisted state · `Services/` state store + input hooks · `Interop/` P/Invoke and
window lookup · `Controls/` the two cell hosting implementations · `Forms/` main window and
dialogs. Dialogs are built in code; there are no `.Designer.cs` files, so keep new UI code-built
for consistency.

## Conventions

- UI strings and user-facing messages are Korean; code, comments and identifiers are English.
- Cell failures are reported inside the cell (`CellHost.StatusText`), never as a message box or
  an exception that reaches the message loop — one broken cell must not take down the wall.
