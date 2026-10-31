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
- **Forge**: a Pokémon save editor and Bank, ported from [PKForge](https://github.com/sofianeelhor/PKForge)'s
  engine (PKHeX.Core + Auto Legality Mod). Pokémon games show **Edit save** on their details page, which opens
  the save straight from Eden's NAND or Azahar's SD card. Browse the party and boxes, edit a Pokémon with a live
  legality check, Legalize in one click, move Pokémon between games through the Bank, and roll back with
  restore points. Every write is validated, limited to the slots it changes, and preceded by a restore point;
  writes are refused while a game is running or when the save changed on disk since it was opened.
  Also in Forge: a **Trainer** tab (name, IDs, money, play time, Battle Points, Pokédex completion, plus Poké Beans,
  Day Care and fashion where the game has them), a **Bag** editor, the offline **Mystery Gift** database (receive any
  card into the save), and the **Living Dex Autopilot**, which plans and applies moves from every save and the Bank
  into one living dex, all-or-nothing. The controller works throughout: D-pad moves between controls, A presses,
  Left/Right nudge numbers, X/Y switch tabs, LB/RB switch boxes.
- **Updates and DLC** are nested under their game instead of filling the library with extra cards. Zest reads
  each Switch file's signed content meta to tell games, updates and DLC apart and to find the game they belong to
  (compressed NSZ/XCZ files fall back to the title ID in their name). Cards show a badge such as
  `UPDATE 1.3.2 · 2 DLC`, and the details page lists each file. An update or DLC whose game isn't in the library
  stays visible on its own, marked UPDATE or DLC. Eden still needs them installed (File → Install Files to NAND).
- **NSZ → NSP and XCZ → XCI**: compressed dumps get a **Convert** button on their details page. Zest decompresses each
  NCZ, encrypts its sections again (AES-128-CTR) and checks every rebuilt NCA against its own SHA-256 name, so the
  result is exact or not kept. Uncompressed files are copied as they are. XCI output gets a fresh partition layout
  with every hash recomputed and the header's layout fields updated; the header's signature can't be redone, which
  emulators don't check. The new file goes next to the original; **Delete the original** appears only once it exists.
- **Native ports**: some Switch games have a community static recompilation, a native PC build of the game that
  runs without Eden. Zest recognises the dump by the title ID and version in its own signed metadata, and the
  details page then offers **Set up native port**. That downloads the project's official Linux AppImage from GitHub
  (refused unless it matches GitHub's SHA-256), extracts the base game's `exefs/` and `romfs/` with your keys, and
  adds **Play native**. No game files are downloaded: each port builds the game code on this PC from your dump.
  On the first run, pick the extracted folder in the port's own setup screen (Play native puts its path on the
  clipboard). After that, **Remove extracted files** frees Zest's copy. Supported today:
  [LAS Recomp](https://github.com/peiikai/LASRecomp) (Link's Awakening v1.0.0/1.0.1/1.1.0, beta) and
  [Sky Recomp](https://github.com/Apolinario-coder/SkyRecomp) (Skyward Sword HD v1.0.0, experimental).

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
- `~/.local/share/zest/forge/`: the Bank and save restore points (**not** safe to delete: the Bank lives here)
- `~/.local/share/zest/native/<port>/`: installed port AppImages and extracted game files (the game files can be
  removed from the details page once the port has imported them; saves live in the port's own folder)

## Build

PKForge (and the PKHeX it pins) is a git submodule, so clone with `--recurse-submodules`, or run
`git submodule update --init --recursive` once.

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
- Forge engine: [PKForge](https://github.com/sofianeelhor/PKForge) v3.1.1 by sofianeelhor (GPL-3.0-or-later),
  built on [PKHeX.Core](https://github.com/kwsch/PKHeX) and the Auto Legality Mod from
  [PKHeX-Plugins](https://github.com/santacrab2/PKHeX-Plugins). Not affiliated with Nintendo, Game Freak, or The Pokémon Company.
- Zstandard decompression: [ZstdSharp](https://github.com/oleg-st/ZstdSharp) (MIT). The NCZ format is nsz's
  (nicoboss/nsz); Zest's decoder is its own implementation.
- Native ports are separate projects, installed from their official releases and credited on each card:
  LAS Recomp by peiikai (MIT) and Sky Recomp by Apolinario-coder. Zest bundles none of their code.
