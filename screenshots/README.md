# Screenshots

Screens for the app's store listing, one folder per UI language: `en\` (English) and `ko\`
(Korean). 2560×1440 PNG, 16:9. Both folders hold the same six shots:

| File | Shows |
|---|---|
| `01-grid.png` | Six dashboards in a 3×2 grid |
| `02-cell-menu.png` | The cell's right-click menu |
| `03-zoomed-cell.png` | One cell zoomed to full screen |
| `04-cell-settings.png` | The Cell settings dialog |
| `05-set-manager.png` | The Manage sets dialog (with the reorder buttons) |
| `06-another-set.png` | Switched to another set (2×2) |

The pages shown in the cells are the fictional dashboards in `demo\en\` and `demo\ko\` (sharing
`demo\style.css` and `demo\demo.js`); they have nothing to do with any real company or data.

## Re-shooting

```powershell
.\tools\capture-screenshots.ps1 -PauseProcesses VirtualDesktopIndicator              # both languages
.\tools\capture-screenshots.ps1 -PauseProcesses VirtualDesktopIndicator -Language ko  # one language
```

The script backs up `%APPDATA%\GridDisplay`, swaps in the demo sets with the UI language set to
match, takes the shots and then puts the original folder back. It takes over the primary monitor
for about a minute per language, so don't touch the mouse or keyboard meanwhile. Programs named
in `-PauseProcesses` are closed for the capture only and started again afterwards.

The script finds menu items and dialogs by their captions, so if a caption changes in the app,
update the `$texts` table at the top of the script to match.
