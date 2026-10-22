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
- *input mode* — entered by zooming a cell (`ActivateCell`); there is no separate key for it.
  The mouse hook stands down and the keyboard hook forwards everything except `Esc`, which
  leaves input mode and the zoom together (`LeaveZoom`). The cost is that a hosted app never
  receives `Esc`.

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

The user's own zoom (Ctrl+wheel) *is* stored, as `CellConfig.ZoomScale` — a multiplier of the
fit, not a factor, so it survives zoom/unzoom and restarts without reflowing the page.
`CellHost.OnZoomFactorChanged` tells it from our own assignments by comparing against
`_appliedZoom`, and ignores changes mid-navigation: WebView2 scopes a user zoom to the current
page and reverts it on the next load, and that revert must not be recorded as the user zooming
out. `NavigationCompleted` re-asserts the stored zoom.

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

**Surviving a restart's login screen is two more per-cell options.** `CellConfig.KeepSignedIn`
(off by default) has `CellHost.PersistSessionCookiesAsync` rewrite the profile's *session* cookies
with an expiry, so they reach the disk — intranet logins are almost always session cookies, which
is why every restart used to land on a login page. It is profile-wide, since cookies are.
`ReturnAfterLogin` (on) has `CheckLoginAsync` treat a page with a password field as a login page;
only one the cell was *sent* to (not `IsUserInitiated`) arms a single return to the last
non-login page, so a failing login costs a login screen, never a navigation loop. A two-step
login (ID page, then password page) is not recognised.

The drag itself goes through `InputHook`, not WinForms drag-and-drop — hosted windows belong to
other processes and eat the input first, the same reason the hooks exist. `OnGlobalMouseMove`
promotes a left press past `DragThreshold` into a drag; `OnGlobalMouseUp` swallows the release so
it never turns into a zoom. Feedback is limited to dashed-source / solid-target cell frames,
because the gap between cells is the only surface the form can paint on.

**Input mode shows nothing over the cell.** There used to be an amber strip across the top; it was
removed at the user's request, and `ShowBanner` returns early while `_interactive`, so no notice
lands on a cell being worked in either. A zoomed cell in input mode fills the whole grid area.

**Grid cells are still pictures unless asked to be live.** `MainForm.ApplyPresentation` (end of
every `Relayout`) calls `CellHost.SetLive`: the zoomed cell is live, grid cells are live only with
`CellConfig.LiveInGrid`. A still cell captures its page with `CapturePreviewAsync` into the
`_still` PictureBox, hides the WebView and `TrySuspendAsync`s it — that sleep is the whole point,
it is where a wall of dashboards spends its CPU, GPU and memory. Every
`AppState.StillRefreshMinutes` (5) the page wakes *behind* the old picture, reloads, settles,
is captured again and sleeps. Deliberate details: the page must be visible to paint, so capture
happens with the WebView shown under the picture; a hidden cell (another one zoomed) is never
captured, or the picture would be blank — instead `SetHidden` puts *every* browser cell behind a
zoom to sleep, live-in-grid ones included, and `RefreshStillAsync`/`Reload` refuse to wake a
hidden cell; a failed capture leaves the page running; a
`PreventLogout` cell is hidden but not suspended, because its keep-alive timer must keep firing;
`SetSuspended` stands aside for a still cell; `_stillWanted` survives `Release`, since it is the
wall's wish rather than the content's state. Program cells are always live — another process's
window cannot be put to sleep from here.

**The cell menu is the only chrome, and it must close like a menu.** The toolbar (and F1) is gone;
set switching, grid size, shortcuts and exit are in `_menu`. Because the hook swallows clicks on
cells, the menu would never see the outside click that normally dismisses it: `_menuOpen` (set by
`Opened`/`Closed`) makes `OnGlobalMouseDown` close the menu and swallow that press and its release
(`_swallowRelease`), and `Esc` closes it from the keyboard hook.

**A zoomed cell has a mouse way out.** `_closeZoom`, a round ✕ at the top centre, appears while
the pointer is within ~90px of the top edge (polled by `_closeZoomTimer` while zoomed, because the
mouse hook stands down in input mode) and calls `LeaveZoom`, the same as Esc. It is a sibling
control brought to front, which sits above the hosted window where painting could not.

**Painted chrome takes height, never width.** Fit zoom is cell width over grid-area width, so
insetting the sides would knock a zoomed cell off exactly 1.0 and reflow the hosted page; height
is the only free dimension. Chrome is painted by the form rather than hosted in a `Label`
because a sibling control can be covered by the hosted window, a painted band cannot.
`AppState.ShowCellLabels` (on) has `Relayout` take
`_labelStrip` px off the *bottom* of every grid cell, and `DrawCellLabel` paints number, title and
setting badges there. `CellSlot(i)` — cell plus strip — is what frames, `InvalidateCellFrame` and
`PublishHitTestMap` use, so a click on the strip is a click on the cell. The strip is 0 while a
cell is zoomed, so a zoomed cell still fills the area.

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

**Updating takes whichever road the copy was installed by, and `AppVersion.IsPackaged` is the
fork.** A packaged build hands the job to Windows (`PackageManager`, or the Store); an unpackaged
one — the Inno Setup installer under `installer\` — cannot, because MSIX and a plain folder are
updated in completely different ways. So `UpdateService.ApplyAsync` splits: a feed pointing at a
`.zip` with a `sha256` beside it is the unpackaged shape, and `UpdateInstaller` finishes that one
itself.

That second path exists because Windows will not let a running executable overwrite itself:
`UpdateInstaller.Launch` starts the *staged* `GridDisplay.exe` with `--apply-update <pid>
<installDir>` and quits; the second process waits for the first to exit, copies staging over the
install folder and starts it again. `Program.Main` routes the switch before any form exists. The
applier is the app rather than a `.bat` or PowerShell script, so there is nothing extra for
antivirus to quarantine, and a failure can still be reported — the app is a WinExe with no
console, hence the log under `UpdateInstaller.UpdatesRoot`. Details that are deliberate:

- `UpdatesRoot` is owned by `UpdateInstaller`, not `UpdateService`. The applier's whole job is to
  report what went wrong, so its log path must not depend on the static initialisation of the
  type that may be what failed — that exact coupling once silenced the log completely.
- `StageZipAsync` refuses a package whose SHA-256 does not match the feed, and one without
  `GridDisplay.exe` at its root. MSIX is signed and Windows checks it; a zip that overwrites the
  install folder has nothing between it and the running app but that comparison.
- `BlockedReason` refuses to update while another copy runs from the same install folder. The
  applier waits only for the process that launched it, so the others would still hold
  `GridDisplay.dll` open and the copy would fail halfway.
- The staged copy cannot delete the folder it runs from, so `CleanupStaging` defers that to the
  next startup and ignores whatever it cannot remove.
- The Inno installer puts the app in `{localappdata}\Programs` with `PrivilegesRequired=lowest`,
  and that is a consequence of the above rather than a preference: under Program Files the copy
  step needs admin, so every update would stall on a prompt an unattended wall cannot answer.
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
  hard-coded pixel bounds do not. `MainForm` sizes its painted chrome with `MainForm.Scaled(...)`
  (there is no toolbar: everything it held lives in the cell menu), and `CellSettingsForm` authors
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
