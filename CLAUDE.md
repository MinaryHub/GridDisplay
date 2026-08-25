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

# release — stamps <Version>, publishes, zips, hashes, writes latest.json, compiles the
# installer, and creates the GitHub release with all three assets
.\tools\release.ps1 -Version 1.1.0 -Notes "..." -Publish

# icon — regenerate src\GridDisplay\app.ico after a palette change
.\tools\make-icon.ps1
```

The icon is drawn by `tools\make-icon.ps1` rather than committed as an opaque binary, so the
palette lives in one place. Its entries are 32-bit DIBs up to 128px and PNG only at 256: .NET's
own `Icon` class cannot decode PNG entries, so a PNG-only file loads in Explorer and fails in
code. Sizes at or below 24px drop to a 2×2 grid — a 3×2 wall is mush at 16px.

`tools\release.ps1` and `installer\GridDisplay.iss` must both stay UTF-8 **with BOM**: Windows
PowerShell 5.1 and Inno Setup each read a BOM-less file as ANSI and mangle every Korean string
in it. `release.ps1` writes the stamped `.csproj` back through `File.WriteAllText` with a
BOM-less encoding for the same reason in reverse — `Set-Content -Encoding utf8` would add one
and rewrite the whole file in every diff.

Two packages come out of one publish, and they are not interchangeable: the **zip** is what the
in-app updater downloads and unpacks, so `GridDisplay.exe` must sit at its root; the
**installer** is only for a machine that has nothing yet.

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

**The hooks run on a thread of their own**, and that is load-bearing. A low-level hook callback
is dispatched by the message pump of the thread that installed it, and Windows holds *every
input event on the machine* until each installed hook returns or `LowLevelHooksTimeout` (300ms)
expires. On the UI thread, any pause — WebView2 start-up, a relayout, a modal dialog, a GC —
therefore froze the whole desktop, not just this app, and three walls on one machine paid that
cost three times per event. `InputHook` now owns a dedicated thread whose only job is to pump
messages, so a busy UI thread can no longer stall input for anything.

That has a consequence which must be preserved: **the callback cannot touch a Control.** It hit
tests against `HitTestMap`, an immutable snapshot of cell rectangles in screen coordinates that
`MainForm.PublishHitTestMap` republishes on every relayout and mode change. Reaching for
`_cells[i].Bounds` or `PointToClient` from the callback would put the UI thread back in the path
and undo the whole arrangement.

Guard rails that matter when touching the hooks:

- Callbacks must return immediately; real work goes through `MainForm.Post`, which is
  `BeginInvoke` plus the guards a cross-thread caller needs during shutdown.
- Order the callback cheapest-test-first. The rectangle comparison against `HitTestMap` rules out
  every event that happens while the user works elsewhere on the desktop — nearly all of them —
  before any call into the window manager. The mouse-move throttle sits in the hook for the same
  reason: a discarded event should cost nothing.
- `HitTestMap` still ends with the `GetAncestor(..., GA_ROOT)` check against the form handle.
  That single test is what keeps dialogs, context menus and other applications clickable.
- Fields shared with the hook thread (`_zoomed`, `_hover`, `_dragFrom`, `_interactive`, …) are
  `volatile`; the press/drag fields are touched only by the hook thread, in event order.
- `HookDiagnostics` times every callback and writes a line when one exceeds 15ms. Two timestamp
  reads per event is a fair price for being able to tell "the machine froze" apart from "our
  callback was slow" — the two are indistinguishable by eye.
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

**Input mode announces itself by taking height, never width.** `MainForm.InputStripHeight`
carves an amber strip off the *top* of the zoomed cell and `DrawInputModeStrip` paints into it.
Height is the only dimension that is free: fit zoom is cell width over grid-area width, so
insetting the sides would knock a zoomed cell off exactly 1.0 and reflow the hosted page every
time the user toggles the mode. It is painted by the form rather than hosted in a `Label`
because the strip is form-owned canvas — a sibling control can be covered by the hosted window,
a painted band cannot. `SetInteractive` therefore has to `Relayout` as well as `Invalidate`.

**Several copies on one machine are a supported arrangement, and every fixed path was a bug for
it.** Three walls used to share one `state.json`: each loaded it at startup and each debounced
save then wrote its own grid over the other two. `AppPaths` resolves the roaming and local roots
once in `Program.Main`, before `StateStore` or `UpdateInstaller` capture theirs, and
`--profile <name>` moves a copy into `profiles\<name>`. Separate roots also give each copy its
own WebView2 folder, so one wall's browser process can no longer take the other two down with
it. `UpdateInstaller.BlockedReason` additionally refuses to update while a sibling runs from the
same install folder — the applier waits only for the copy that launched it, so the others would
still hold `GridDisplay.dll` open and the copy would fail halfway.

**Long runs are a first-class case; a wall is up for weeks.** `MainForm.StartMaintenance` runs
one slow timer for everything that only matters after days: `HealthMonitor` writes a resource
line every 30 minutes (working set, handles, GC heap, WebView2 totals, free physical memory),
and `AppState.CellRecycleHours` reloads browser cells daily. The reload is not about leaks in
this app — a dashboard's own scripts and DOM grow whether or not we are at fault, and reloading
is the one cure that does not involve restarting the wall. It is skipped while a cell is zoomed
or taking input, and staggered for the same reason cells start staggered.

**Updating is the app replacing itself.** Windows will not let a running executable overwrite
itself, so `UpdateInstaller.Launch` starts the *staged* `GridDisplay.exe` with
`--apply-update <pid> <installDir>` and closes the form; that second process waits for the first
to exit, copies staging over the install folder and starts it again. `Program.Main` routes the
switch before any form exists. The applier is the app itself rather than a `.bat` or PowerShell
script, so there is nothing extra for antivirus to quarantine and a failure can still be
reported — the app is a WinExe with no console, hence the log under `UpdateInstaller.UpdatesRoot`.
Details that are deliberate, not incidental:

- `UpdatesRoot` is owned by `UpdateInstaller`, not `UpdateService`. The applier's whole job is to
  report what went wrong, so its log path must not depend on the static initialisation of the
  type that may be what failed — that exact coupling once silenced the log completely.
- `UpdateService`'s `HttpClient` is built through a `Lazy<T>`. Static field initialisers run in
  declaration order and `CreateClient` reads `CurrentVersion`, so an eager field observes a
  half-initialised class and throws on the very first check.
- Versions are padded to four components before comparing. `Version` treats an absent component
  as -1, so an unpadded "1.2.0" sorts *below* the equivalent "1.2.0.0" and the same update would
  offer itself forever.
- `StageAsync` refuses a package whose SHA-256 does not match the manifest, and one without
  `GridDisplay.exe` at its root. An install folder must never be left unbootable.
- The staged copy cannot delete the folder it runs from, so `CleanupStaging` defers that to the
  next startup and ignores whatever it cannot remove.
- A failed check is silent unless the user asked for it. An unattended wall must not sit on a
  modal dialog because the network blipped.
- The toolbar's update button is gated on the `_pendingUpdate` field, never on `Control.Visible`
  — a child of the hidden toolbar reports `Visible == false` whatever was set, so reading it back
  in `LayoutBar` would drop every item from the layout.
- `installer\GridDisplay.iss` installs per-user into `{localappdata}\Programs\GridDisplay` with
  `PrivilegesRequired=lowest`, and that is a consequence of the above rather than a preference:
  under Program Files the copy step needs admin, so every update would stall on a prompt an
  unattended wall cannot answer. Moving the install location breaks self-update.
- Inside the installer's `[Code]` section, braces are written **singly**. The `{{` doubling used
  in `[Setup]`/`[Files]` is constant-expansion escaping, which does not apply there — and a `{`
  inside a `{ ... }` comment ends it early, which is its own way to break the compile.

**Responsiveness choices**, worth preserving:

- Cell content starts staggered (120ms apart) in `LoadPresetAsync`, after the window is already
  painted and clickable.
- All browser cells share one WebView2 user-data folder, so they share a single browser process;
  `CellConfig.ProfileName` separates sessions without separating processes.
- Zooming resizes cells but never reloads them.
- `AppState.SuspendHiddenCells` (off by default) trades switch latency for memory.
- `NativeAppHost` re-asserts geometry from a 1s watchdog rather than a tight timer or a hook.

## Layout of the source

`Models/` plain persisted state · `Services/` state store, input hooks, updater · `Interop/` P/Invoke and
window lookup · `Controls/` the two cell hosting implementations · `Forms/` main window and
dialogs. Dialogs are built in code; there are no `.Designer.cs` files, so keep new UI code-built
for consistency.

## Conventions

- UI strings and user-facing messages are Korean; code, comments and identifiers are English.
- The app runs `ApplicationHighDpiMode=PerMonitorV2`, so fonts scale with the monitor but
  hard-coded pixel bounds do not. `MainForm.LayoutBar` therefore positions the toolbar from
  measured text times `MainForm.Scaled(...)` on every relayout, and `CellSettingsForm` authors
  every coordinate at 96 DPI and wraps it in its own `S(...)`, as do `PresetManagerForm` and
  `UpdateForm`. Copy one of those rather than nudging magic numbers.
  Rows of side-by-side controls (the kind radios) additionally advance by
  `PreferredSize.Width` so a long caption cannot cover the next control's glyph.
- Cell failures are reported inside the cell (`CellHost.StatusText`), never as a message box or
  an exception that reaches the message loop — one broken cell must not take down the wall.


# Communication & Output Guidelines

1. **설명 수준**: 기술 용어나 시스템 내부 로그를 직접 언급하지 말고, '비전공자/기획자도 이해할 수 있는 쉬운 비유'와 '핵심 동작' 위주로 설명해주세요.
2. **중간 출력 제한**: 코드를 수정하는 구체적인 CLI 명령어/툴 실행 로그는 가급적 요약하고, "어떤 파일의 무엇을 수정 중인지" 한 눈에 알 수 있는 한 줄 요약만 출력하세요.
3. **결과 보고**: 
   - [무엇을 했는지]
   - [왜 이렇게 했는지]
   - [다음 추천 행동] 
   3가지 항목으로 구조화하여 답변하세요.
