[한국어](README.ko.md)

# Grid Display

A display-wall app for Windows that tiles several browsers and programs into a grid on a single
monitor.

- **Grid layout** — 3 columns × 2 rows by default; anything from 1×1 up to 12×12.
- **Different content per cell** — a browser (embedded WebView2) or any Windows program.
- **Windows stay inside their cell** — a program's window becomes a child window, so maximizing it
  or going full screen never takes it outside the cell.
- **Click to zoom and type straight away** — clicking a cell fills the whole monitor with it, and
  from then on the keyboard and mouse go to that app. Press `Esc` to return to the grid.
- **Remembers and restores** — each cell's program and browser address are tracked continuously,
  and the next launch brings the same arrangement back.
- **Sets** — save several arrangements under names and switch between them instantly with
  `Ctrl+1`–`Ctrl+9`.

The UI follows the Windows display language (Korean on Korean Windows, English otherwise). You can
change it from the cell menu under **언어 / Language**.

## Requirements

- Windows 10 1809 or later (x64)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) — the Microsoft Store
  version bundles the runtime, so it needs no separate install.
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) —
  needed for browser cells. Windows 11 ships with it.

## Installation

- **Microsoft Store** — the Store handles installation and updates. The runtime is inside the
  package, so there is nothing to prepare.
- **MSIX sideloading** — for distribution from an internal server or a shared folder. Run the
  `.appinstaller` once, and the app fetches new versions by itself from then on.
- **Installer (.exe)** — run `GridDisplay-Setup-x.y.z.exe` from
  [Releases](https://github.com/MinaryHub/GridDisplay/releases). No administrator rights are
  needed, and later versions are downloaded and swapped in by the app itself. This route is for
  environments where MSIX is hard to use.

The installer puts the app in `%LOCALAPPDATA%\Programs\GridDisplay`. It avoids `Program Files`
because of automatic updates: on this route the app updates by overwriting its own install folder,
so installing somewhere a standard user cannot write would mean an administrator prompt on every
update.

How to build the two MSIX packages and submit to the Store is described in
[`packaging/README.md`](packaging/README.md); how to build the installer is in `tools\release.ps1`.

## Build and run

```powershell
dotnet build src\GridDisplay\GridDisplay.csproj -c Release
dotnet run   --project src\GridDisplay\GridDisplay.csproj
```

Single-folder output for distribution:

```powershell
dotnet publish src\GridDisplay\GridDisplay.csproj -c Release -r win-x64 --self-contained false -o publish
```

## Usage

| Action | Result |
| --- | --- |
| Click a cell | Zoom it to the full monitor and switch straight to input mode |
| Click an empty cell | Open that cell's settings |
| Right-click a cell | Cell menu (Cell settings… · Refresh · Live in grid · Switch set · Grid size · Manage sets… · Shortcuts · Version and updates… · Exit) |
| Click outside the menu / `Esc` | Close the right-click menu |
| ✕ at the top edge of the screen | Leave the zoom and return to the grid (appears when the mouse nears the top) |
| `Esc` | Leave input mode and the zoom together, back to the grid |
| Arrow keys / `Enter` | Move the cell selection / zoom |
| `Ctrl+1`–`Ctrl+9` | Switch to a saved set (same as right-click › Switch set) |
| `Ctrl+R` | Refresh the selected cell |
| `F11` | Toggle full screen for the window |

In input mode every key except `Esc` goes to the running app. `Esc` is reserved for returning to
the grid, so the app never receives it. Nothing is overlaid on the screen while you are typing.

In the grid, browser cells are shown as **still pictures** while the site sleeps (the picture is
refreshed every 5 minutes). A zoomed cell is live, and while it is, every other cell sleeps. For a
cell that must keep moving in the grid too, turn on right-click › *Live in grid*.

On first launch the app opens as a normal window with a title bar. Switch with `F11` or
right-click › *Full screen (F11)*; the choice carries over to the next launch. Full screen has no
title bar, so quit with right-click › **Exit**.

Right-click › *Show cell labels* toggles the strip under each cell with its number and title.

## Running several copies (multiple monitors)

You can run several copies at once, one per monitor. When you do, **always give each one a name
with `--profile`.**

```powershell
GridDisplay.exe --profile left
GridDisplay.exe --profile center
GridDisplay.exe --profile right
```

Without names, all three share one settings file, and changing a cell in one overwrites the
arrangement of the others. With names, settings, browser sign-ins and update files are each kept
separately.

If you installed with the installer, only one copy can update at a time. When several copies are
running from the same folder the update is blocked with a notice, so close the others first.

## Running for a long time

The app is meant to stay up for days or weeks, and includes the following for that.

**Automatic refresh of browser cells** — every cell is reloaded every 24 hours by default. A web
page left open for a long time keeps holding on to more memory by itself, and a reload is the only
way to reclaim it without restarting the program. The address is already remembered, so the screen
comes back as it was; a cell that is zoomed or in input mode is skipped. Adjust it with
`CellRecycleHours` in `state.json`; `0` turns it off.

**Log files** — two files are kept so you can see what grew when something goes wrong.

| File | Contents |
| --- | --- |
| `health.log` | Memory, handles, WebView2 and free system memory every 30 minutes |
| `input-hook.log` | Written only when input handling was slow (nothing is written normally) |

Both live in `%LOCALAPPDATA%\GridDisplay` (under the profile folder when using a profile), and are
rotated once when they pass 512KB.

**Short on memory?** Set `SuspendHiddenCells` to `true` in `state.json`. Cells that are not on
screen then release their memory, at the cost of a short delay when you switch to them.

## Settings files

- Sets and cell layout: `%APPDATA%\GridDisplay\state.json`
- Browser profiles: `%LOCALAPPDATA%\GridDisplay\WebView2`
- Update staging files and logs: `%LOCALAPPDATA%\GridDisplay\Updates`

Running with `--profile name` moves each of these under `profiles\name`.

As you browse within a cell, its address is updated and saved automatically, so the same screen
comes back on the next launch without any explicit save.

## Cell settings

**Browser** — just enter an address. Putting different names in *Profile* lets you be signed in to
the same site with different accounts at the same time.

**Program** — give the path to the executable. For an app that opens several windows, put an
identifying string in *Window title contains* so the right window is picked. To take over a window
that is already open, turn on *Attach to a running window*.

## Known limitations

- Program cells work only with ordinary desktop apps that have a window. UWP/Store apps, and apps
  running as administrator (while this program runs with normal rights), cannot be embedded.
- An external browser shown in a program cell cannot have its address remembered. Use a browser
  cell when you need the address restored.
- When a cell is released or the app exits with *Close program when the cell is released* turned
  off, the app goes back to being a standalone window at its original size.
