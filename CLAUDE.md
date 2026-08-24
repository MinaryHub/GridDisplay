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

**Browser zoom is derived, not stored.** `CellConfig.FitToCell` (on by default) makes `CellHost`
compute `ZoomFactor` as its own width over the *grid area* width, so a cell shows the page laid
out for the full monitor, only smaller — at 100% a cell would show just the page's top-left
corner. Measuring against the grid area rather than a fixed width is what makes a zoomed cell
land on exactly 1.0, so zoom/unzoom rescales without reflowing the page. `Relayout` pushes the
reference width into every cell and `CellHost.OnResize` reapplies; the stored `ZoomFactor` is
only consulted when `FitToCell` is off.

**Rearranging is a host swap, not a content move.** Dragging one cell onto another runs
`MainForm.SwapCells`, which exchanges entries in `_cells` *and* `GridPreset.Cells` together and
reassigns `CellHost.Index`. The hosts keep their WebView2 / reparented window and only get new
bounds from `Relayout`, so a rearrange never reloads a cell — same trade as zooming. Moving the
*configs* between hosts instead would restart both cells and break the aliasing that records
live URLs. Two consequences worth remembering: `_cells[i].Config` must stay reference-equal to
`_preset.Cells[i]`, and `CellHost.Index` cannot be readonly because an empty cell paints its own
position number.

**Keep-alive answers two independent logout mechanisms.** `CellConfig.PreventLogout` (off by
default) starts a per-cell timer in `CellHost` that runs `KeepAliveScript`: synthetic activity
events reset a page's *client-side* idle timer, and a credentialed `cache: 'no-store'` request
stops the *server-side* session from expiring. Dropping either half leaves one of the two causes
unhandled. Details that are deliberate, not incidental: events are dispatched on `body` so they
bubble on to `document` and `window` (idle detectors listen on all three); there is no
`mousedown`/`click` in the list, because those would fire the page's own buttons; and
`SetSuspended` refuses to suspend a keep-alive cell, since that idleness is the very thing being
prevented. It runs in view mode only — `MainForm.SetInteractive` calls `SetKeepAliveActive`, as
input mode already carries real activity and synthetic keys would land in the user's typing.
Known limits: the events are `isTrusted: false`, so a site that checks that is not fooled, and
`ExecuteScriptAsync` reaches the main frame only, so a dashboard inside an iframe is not covered.

The drag itself goes through `InputHook`, not WinForms drag-and-drop — hosted windows belong to
other processes and eat the input first, the same reason the hooks exist. `OnGlobalMouseMove`
promotes a left press past `DragThreshold` into a drag; `OnGlobalMouseUp` swallows the release so
it never turns into a zoom. Feedback is limited to dashed-source / solid-target cell frames,
because the gap between cells is the only surface the form can paint on.

**Responsiveness choices**, worth preserving:

- Cell content starts staggered (120ms apart) in `LoadPresetAsync`, after the window is already
  painted and clickable.
- All browser cells share one WebView2 user-data folder, so they share a single browser process;
  `CellConfig.ProfileName` separates sessions without separating processes.
- Zooming resizes cells but never reloads them.
- `AppState.SuspendHiddenCells` (off by default) trades switch latency for memory.
- `NativeAppHost` re-asserts geometry from a 1s watchdog rather than a tight timer or a hook.

**Packaging has two targets and they are not interchangeable.** `packaging/build-msix.ps1` builds
either a signed `.msix` plus an `.appinstaller` feed for sideloading, or — with `-Store` — a
`.msixupload` for Microsoft Store. The Store variant is *self-contained*, because the Store cannot
install the .NET Desktop Runtime on a customer's machine, and it is *unsigned*, because Partner
Center strips any signature and re-signs. Identity (`Name`, `Publisher`, `PublisherDisplayName`,
`DisplayName`) is stamped from parameters rather than checked in: the Store dictates all four and
rejects a package that differs by a character. See `packaging/README.md`.

The corresponding code decision is that **a Store install disables the app's own updater rather
than hiding it**. `AppVersion.IsStoreInstall` reads `Package.Current.SignatureKind`; when it is
true `UpdateService` neither checks nor installs, and `UpdateForm` collapses to one button that
opens the Store. Store policy reserves updating a Store app for the Store, so a submission with a
reachable `PackageManager` path is a certification failure, not merely redundant work.

Two traps in the packaging script, both already handled: XML is read with `XmlDocument.Load`, never
`[xml](Get-Content -Raw)`, because Windows PowerShell decodes a BOM-less file with the system ANSI
codepage and turns the manifest's Korean description into mojibake that no longer parses; and the
SDK tools come from the `Microsoft.Windows.SDK.BuildTools` NuGet package (`packaging/SdkTools.proj`)
when no Windows SDK is installed, which is the normal case on a machine that only has the .NET SDK.

## Layout of the source

`Models/` plain persisted state · `Services/` state store + input hooks · `Interop/` P/Invoke and
window lookup · `Controls/` the two cell hosting implementations · `Forms/` main window and
dialogs. Dialogs are built in code; there are no `.Designer.cs` files, so keep new UI code-built
for consistency.

## Conventions

- UI strings and user-facing messages are Korean; code, comments and identifiers are English.
- The app runs `ApplicationHighDpiMode=PerMonitorV2`, so fonts scale with the monitor but
  hard-coded pixel bounds do not. `MainForm.LayoutBar` therefore positions the toolbar from
  measured text times `MainForm.Scaled(...)` on every relayout, and `CellSettingsForm` authors
  every coordinate at 96 DPI and wraps it in its own `S(...)`. Copy one of those. `PresetManagerForm`
  is still raw pixels and clips at 150% — convert it the same way when touching it, rather than
  nudging magic numbers. Rows of side-by-side controls (the kind radios) additionally advance by
  `PreferredSize.Width` so a long caption cannot cover the next control's glyph.
- Cell failures are reported inside the cell (`CellHost.StatusText`), never as a message box or
  an exception that reaches the message loop — one broken cell must not take down the wall.
