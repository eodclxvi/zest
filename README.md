# Zest

One game library for **Eden** (Switch), **Azahar** (3DS) and **PCSX2** (PS2), styled after
[Lemon](https://github.com/Ghael-V/Lemon-Project)'s redesigned Android interface. Built with C# and Avalonia.

Zest is a launcher: it finds your games, shows them with art, and starts the right emulator.
Each game still runs in its own emulator window.

## Features

- Reads game folders straight from Eden's, Azahar's and PCSX2's own settings on first run
- Switch titles and icons read from the NSP/XCI files with your `prod.keys` (LibHac, all offline)
- 3DS and PS2 boxart from thumbnails.libretro.com, cached in `~/.cache/zest`
- 3DS titles installed inside Azahar are listed too, with an "Installed" badge
- Grid and carousel views, a details page per game, and Lemon's ten color themes
- Controller support (SDL3); Zest lets go of the controller while a game is running
- Hide any game (e.g. a duplicate); hidden games live under the "Hidden" chip
- Search, per-system filter chips, four sort orders, "Continue playing", play-time tracking

## Keys

| Key | Action |
|---|---|
| Ctrl+F | Search |
| ↓ from search | Jump into the games |
| Enter / double-click | Play |
| Space or I | Details |
| V | Switch grid / carousel |
| Esc | Close details, or clear search |
| F5 | Rescan folders |
| Right-click a game | Play, details, show file, fetch art again, hide |

## Controller

| Button | Action |
|---|---|
| D-pad / left stick | Move |
| A (bottom) | Play |
| Y (top) | Details |
| X (left) | Switch grid / carousel; on the details page: hide or show |
| B (right) | Back, or clear search |
| LB / RB | Previous / next system |
| Select | Change sort |

## Files

- `~/.config/zest/config.json`: emulator commands, game folders, key paths (also editable in Settings)
- `~/.local/share/zest/playtime.json`: play time per game
- `~/.cache/zest/`: art and libretro indexes (safe to delete)

## Build

```bash
cd src/Zest
dotnet run                     # debug
dotnet run -- --probe          # print the library scan without opening a window
dotnet publish -c Release -o ~/.local/share/zest/app   # what the app-menu entry runs
```

The .NET 10 SDK comes from mise (`mise.toml`); the published app runs on the system .NET runtime.

## Credits

- Look and palette: Lemon (GPL-3.0-or-later). Zest is GPL-3.0-or-later as well.
- Fonts: Bricolage Grotesque and DM Sans (SIL Open Font License)
- Icons: Material Design icons (Apache 2.0)
- Switch metadata: [LibHac](https://github.com/Thealexbarney/LibHac) (BSD-3-Clause)
- Boxart: [libretro-thumbnails](https://github.com/libretro-thumbnails/libretro-thumbnails)
