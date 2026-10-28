# Screenshots

Screens for the app's store listing. 2560×1440 PNG, 16:9.

| File | Shows |
|---|---|
| `01-grid.png` | Six dashboards in a 3×2 grid |
| `02-cell-menu.png` | The cell's right-click menu |
| `03-zoomed-cell.png` | One cell zoomed to full screen |
| `04-cell-settings.png` | The Cell settings dialog |
| `05-set-manager.png` | The Manage sets dialog (with the reorder buttons) |
| `06-another-set.png` | Switched to another set (2×2) |

The pages shown in the cells are the fictional dashboards in `demo\`; they have nothing to do
with any real company or data.

## Re-shooting

```powershell
.\tools\capture-screenshots.ps1 -PauseProcesses VirtualDesktopIndicator
```

The script backs up `%APPDATA%\GridDisplay`, swaps in the demo sets (with the UI set to English),
takes the shots and then puts the original folder back. It takes over the primary monitor for
about a minute, so don't touch the mouse or keyboard meanwhile. Programs named in
`-PauseProcesses` are closed for the capture only and started again afterwards.
