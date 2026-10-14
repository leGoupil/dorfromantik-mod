# Dorf Helper

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![Platform: macOS](https://img.shields.io/badge/platform-macOS-lightgrey?logo=apple)
![Game: Dorfromantik](https://img.shields.io/badge/game-Dorfromantik-6a9f4c)
![BepInEx 5](https://img.shields.io/badge/BepInEx-5.4.23-orange)
![Unity 2021.3 (Mono)](https://img.shields.io/badge/Unity-2021.3%20Mono-black?logo=unity)
![.NET Framework 4.7.2](https://img.shields.io/badge/.NET-net472-512BD4?logo=dotnet)
![Challenges completed: 8](https://img.shields.io/badge/challenges%20completed-8-success)

A BepInEx mod for **Dorfromantik** (Steam, macOS) that suggests where to place each tile, and can play on its own
(auto mode) to chase the game's challenges.

It reads the live game state, rebuilds it as a pure model (tiles, segments, groups, quests, scoring), evaluates every
possible placement (with lookahead on the visible tiles) and shows the best ones on the board. The model is checked
against the game before every placement.

> This repository contains only the mod's own code. The game's decompiled code and binaries are never committed.

## Results

All obtained in Classic mode with the game's normal rules (no config, speed or save manipulation), the bot playing
in auto mode with automatic restarts.

### Challenges completed

| Challenge (session quest) | Requirement | How |
|---|---|---|
| Ocean I | form a water group of 100 segments | biome goal, "form" |
| Engineering (50 and 75) | form a railway of 50, then 75 segments | biome goal, "form" |
| Village I | form a village of 250 houses | biome goal, "form" |
| Green thumb (1000 then 1500) | form a forest of 1000, then 1500 trees | biome goal, growth normalized per segment |
| Self-sufficiency I | close 50 field groups of at least 10 fields (cumulative) | "many groups" mode |
| Planning I | 7 perfect placements at once | planned "flower" (center + 6 + 12 cells) |
| Analyst V | 550 points in a single placement | flower + a quest at the center |
| **Analyst VI** | **660 points in a single placement** | **750 points**: flower + quest reservation |

### Analyst VI: the winning move

- 15th attempt of the last series (v0.24.0), 145th tile of the game; about 30 attempts in total since v0.20.
- Placed at the flower's center: 5 perfect placements (300) + 5 matching edges (50) + **4 quests completed at once**
  (400) = **750 points**, and +25 tiles. No tile could match all 6 edges (7 perfects impossible), but 5 reserved quests
  touched the center. The model had predicted those 750 points exactly.
- Classic mode scoring (read from the game): 10 per matching edge, 60 per perfect placement, 100 per quest,
  50 per flag, nothing for closing a group.

### Endurance ("Compact" objective)

- Best game: **1033 tiles placed, 67,080 points** (initial goal: 250 tiles; previous manual best: 17,630 points).

### Model accuracy (last series)

| Check | Result |
|---|---|
| Placements (edges and quest values, right before each placement) | 4,422 / 4,422 |
| Tiles gained | 4,419 / 4,421 |
| Points of the placement | 4,392 / 4,420 (99.4%) |

## Installation

### Requirements

- Dorfromantik from **Steam on macOS** (tested with Unity 2021.3.45f2, Mono build, Apple Silicon and Intel).
- The **.NET SDK** (8 or later) to build the plugin: <https://dotnet.microsoft.com/download>.
- `curl` and `unzip` (built into macOS).

### Quick install

```bash
git clone https://github.com/leGoupil/dorfromantik-mod.git
cd dorfromantik-mod
tools/install.sh
```

If the game is not in the default Steam location, pass it explicitly:

```bash
tools/install.sh --game-dir "/path/to/steamapps/common/Dorfromantik"
```

No .NET SDK? Use the prebuilt plugin from the [latest release](https://github.com/leGoupil/dorfromantik-mod/releases/latest)
(this is also what the script does automatically when `dotnet` is not installed):

```bash
tools/install.sh --prebuilt
```

Without cloning the repo: download `DorfHelper-<version>.zip` from the
[latest release](https://github.com/leGoupil/dorfromantik-mod/releases/latest), unzip it and run `./install.sh` from
that folder (it installs the bundled `DorfHelper.dll`; `./uninstall.sh` removes everything).

The script:
1. downloads BepInEx 5.4.23.5 (macOS) and the full Mono/Unity 2021.3.45 libraries (the game ships stripped ones),
   cached in `vendor/`;
2. installs BepInEx next to `Dorfromantik.app` and the full libraries in `BepInEx/unstripped_corlib/`;
3. configures `run_bepinex.sh` (game executable, full libraries first, Rosetta) and `BepInEx.cfg`
   (`HideManagerGameObject = true`);
4. builds the plugin (or downloads the prebuilt one) into `BepInEx/plugins/`;
5. prints the Steam launch option to use.

It never modifies the game's own files and can be run again safely.

**Last step, in Steam**: Dorfromantik > Properties > General > Launch options:

```
"<game folder>/run_bepinex.sh" %command%
```

### Manual install (what the script does)

1. Extract [`BepInEx_macos_universal_5.4.23.5.zip`](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5)
   into the game folder (next to `Dorfromantik.app`).
2. Extract <https://unity.bepinex.dev/corlibs/2021.3.45.zip> and <https://unity.bepinex.dev/libraries/2021.3.45.zip>
   into `BepInEx/unstripped_corlib/`.
3. In `run_bepinex.sh`, set:
   - `executable_name="Dorfromantik.app"`
   - `dll_search_path_override="BepInEx/unstripped_corlib"` — the game's `mscorlib`/`UnityEngine` are stripped;
   - `export ARCHPREFERENCE="x86_64,arm64"` — BepInEx 5's Harmony/MonoMod has no arm64 support, so the game runs
     under Rosetta.
4. In `BepInEx/config/BepInEx.cfg`, section `[Chainloader]`: `HideManagerGameObject = true` (otherwise the game
   destroys the plugin when a game scene loads).
5. Build: `dotnet build -c Release src/DorfHelper/DorfHelper.csproj` (copies `DorfHelper.dll` to `BepInEx/plugins/`;
   override the game location with `-p:GameDir=...`).
6. Set the Steam launch option above.

### Uninstall

To play the unmodded game again, just clear the Steam launch option. To remove everything:

```bash
tools/uninstall.sh                # asks for confirmation; --yes to skip it
tools/uninstall.sh --keep-data    # keep the mod's config and recorded tiles/quests
```

It removes only what the installer added (`BepInEx/`, `libdoorstop.dylib`, `.doorstop_version`, `run_bepinex.sh`,
BepInEx's `changelog.txt`). Then clear the Steam launch option.

### Optional: UnityExplorer

An in-game scene inspector ([maintained fork](https://github.com/yukieiji/UnityExplorer), BepInEx5.Mono build),
toggled with **F7**. It needs a non-stripped `UnityEngine.UI.dll`, which is not available for download: build it
from the uGUI sources of a local Unity 2021.3.45f1 editor with `tools/ugui-build` (`dotnet build -c Release`), then
copy it, together with the editor's `PlaybackEngines/MacStandaloneSupport/Variations/mono/Managed/UnityEngine*.dll`,
into `BepInEx/unstripped_corlib/`.

## Usage

On Mac, F keys may need **fn**.

| Key | Action |
|---|---|
| Ctrl+Cmd (held) | Preview the best suggestion and lock it while held; click to place; press again for the next one |
| F5 | Preview the next suggestion (1 → 2 → 3) with the right rotation and center the camera; click or F6 to place |
| F6 | Place the tile on the shown suggestion |
| F4 | Lookahead: none / 2 visible tiles (default) / + 5 probable / + 10 probable |
| F8 | Objective: score / quests / compact (endurance) / score without mismatches / compact without mismatches |
| F3 | Challenge: none → the game's unfinished challenges (one entry per remaining level) → custom goal → none |
| F10 | Auto mode: places the best suggestion for every tile (pauses during menus and reward screens) |
| F2 | Auto restart: in auto mode, starts a new game (same game mode) when one ends without completing the challenge |
| F9 | Hide/show the overlay |

The overlay (bottom center by default) shows the objective, the suggestions (score, edges, gains) and, when a
challenge is selected, a dedicated card with its progress and a confidence estimate.

Settings are saved in `BepInEx/config/fr.lphilippe.dorfhelper.cfg` (objective, lookahead, chosen challenge,
custom goal, auto restart, fail early, panel position). Logs: `BepInEx/LogOutput.log`.

## How it works

- **`Model/Board.cs`** — pure board model: hex grid, tile segments merged into groups (union-find), open edges,
  quests, perfect placements, tiles gained, points. Reproduces the game's rules (a segment edge is open until any
  tile touches it; facing segments of the same type merge; a count quest fails if its group closes too early or an
  "exactly" quest is exceeded).
- **`Model/Snapshotter.cs`** — builds the model from the running game (excluding the preview tile, which merges
  groups) and reads the game's scoring configuration.
- **`Solver.cs`** — scores every placement of the tile in hand, then plays the visible next tiles greedily
  (optionally probable tiles drawn from those already seen) and keeps the best sequence. Runs in the background.
- **Self-checks** — right before every placement (Harmony hook on `TilePlacer.PlaceCurrentTile`), the model's
  prediction (edges, quest values, tiles gained, points) is compared with the game.
- **Challenges** (`GoalCatalog.cs`) — read from the game's session quests:
  - *form / close a big group* (`FormBigGroup`, `CloseBigGroup`): grow the most promising open group while protecting
    its open edges, then close it if required;
  - *many groups* (`CloseManyGroups`, `FormManyGroups`): many medium groups, growth capped at the required size;
  - *perfect placements at once* (`Challenge_PerfectPlacementsAtOnce`): plan a "flower" — a center cell left empty,
    an inner ring of 6 and an outer ring of 12 matching tiles — then place a tile matching all 6 center edges;
  - *points in one placement* (`Challenge_ScorePerPlacement`): when closing groups scores points, grow a group and close
    it at once; otherwise (Classic mode) build a flower, reserve the quests touching its center and only place the
    center tile when it reaches the target.
- **Auto mode** — places the best suggestion, restarts finished games, and can abandon unpromising games early
  (flower challenges); stalled flowers are replaced by a new site.

Known limitation: adaptive tiles (that change shape depending on their neighbors) are modeled with the shape they
have when the snapshot is taken.

## Development

```bash
dotnet build -c Release src/DorfHelper/DorfHelper.csproj   # builds and installs the plugin
```

Restart the game to load a new build.

### Simulator (`tools/sim`)

Replays full games offline with the mod's model and solver, using tiles and quests recorded in-game
(`BepInEx/DorfHelper/tiles.jsonl`, `quests.jsonl`).

```bash
cd tools/sim
dotnet run -c Release -- --games 30 --objective Compact,CompactClean --quest-scale 0.4
dotnet run -c Release -- --games 20 --objective Compact --goal Agriculture:Field:150
dotnet run -c Release -- --games 20 --objective Compact --goal TrainTracks::80        # segments
dotnet run -c Release -- --games 20 --objective Compact --goal Water::100:form       # form without closing
dotnet run -c Release -- --games 12 --objective Compact --atonce perfect:7
dotnet run -c Release -- --games 12 --objective Compact --atonce points:660
```

Options: `--depth`, `--stack`, `--max`, `--seed`, `--quest-scale` (quest difficulty calibration), `--fit-mod`,
`--group-points`, `--goal-grow/-loss/-prospect/-close/-min-stack/-full-stack`, `--many-*`, `--flower`,
`--flower-quest`, `--reserve`, `--verbose 1`.

### Decompiling the game (for development only)

```bash
dotnet tool restore
DOTNET_ROLL_FORWARD=Major dotnet ilspycmd -p -o decompiled/Assembly-CSharp \
  -r "<Managed>" "<Managed>/Assembly-CSharp.dll"
```

`<Managed>` is `Dorfromantik.app/Contents/Resources/Data/Managed`. `decompiled/` is git-ignored: it is the game's
code and must not be redistributed.

Key game classes: `TilePlacer` (tile in hand, rotation, placement), `TileSlotPreviewer` (valid slots), `TileStack`
(the stack), `World` (placed tiles), `Tile.GetEdgeTypes` (edge types), `QuestManager` / `QuestConditionWatcher`
(quests), `RewardSystem` / `RewardSystemConfiguration` (scoring), `SessionQuest*` / `Challenge_*` (challenges),
`AutoPlacer` (a developer debug tool that evaluates every slot × rotation).

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

## License

The mod's code is released under the [MIT License](LICENSE).

Dorfromantik is a game by Toukana Interactive. This is an unofficial fan project, not affiliated with or endorsed by
Toukana Interactive; it contains none of the game's code or assets, and the game is required to use it.
