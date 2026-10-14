# Changelog

Versions follow the plugin version (`BepInPlugin` in `src/DorfHelper/Plugin.cs`). Releases before 0.25.0 predate
the public repository and are summarized from the development history.

## 0.25.0

- Project translated to English (code comments, logs, overlay text, config names). Config sections and keys were
  renamed: settings from earlier versions are not carried over.
- `tools/install.sh`: local install (BepInEx 5, full Mono/Unity libraries, launcher and BepInEx config, plugin build
  or prebuilt download).
- `tools/uninstall.sh`: removes BepInEx and the mod from the game folder.
- MIT license.

## 0.24.0

- Quest tiles placed so that their quest joins a group touching the flower's center are rewarded.
  **Analyst VI (660 points in a single placement) completed with 750 points.**

## 0.23.x

- "Build first": the flower site is chosen on feasibility alone; quest preparation and reservation start once the
  inner ring has 5 of 6 tiles.
- Stalled flowers (no progress for 60 tiles) are replaced by a new site instead of abandoning the game.
- A complete, intact flower is never abandoned; ruining a flower costs more the further it was built; placing the
  center of a complete flower below the target is strongly penalized.
- Progress on reserved quests is rewarded anywhere on the board.

## 0.22.x

- Fail early for "in one go" challenges in auto mode.
- Bonus for placing a quest tile in the flower's inner ring with its quest facing the center.

## 0.21.0

- Quest reservation: quests touching the flower's center must not be completed elsewhere.

## 0.20.x

- F3 lists every remaining level of "in one go" challenges (aiming at a higher level also completes the lower ones).
- Auto mode hardened: optional camera move, error back-off, preview lock released after each placement, preview moves
  to destroyed slots ignored (fixes a slowdown caused by an error loop).

## 0.19.x

- Center-quest readiness for the points challenge (how many quests the center tile would complete).

## 0.18.0

- Auto restart (F2): a new game in the same game mode when one ends without completing the chosen challenge.

## 0.17.x

- One card per challenge type above the panel (progress, pace, confidence).
- Points challenge played with a flower plus quests when closing groups scores no points (Classic mode); the game's
  scoring configuration is logged at the start of each game.

## 0.16.0

- Analyst card: best and average points per placement, projection, confidence.

## 0.15.x

- New overlay panel: fixed height, aligned columns, progress bars, configurable position (bottom center by default).

## 0.14.x

- Flower planning for "perfect placements at once" (site selection, ring completion chances, center feasibility).

## 0.13.x

- Points of a placement modeled (matches, perfect placements, closed groups, quests) and checked against the game.
- "In one go" challenges: perfect placements at once, points in a single placement.

## 0.12.0

- "Many groups" challenges (close N groups of at least S elements, cumulative across games).

## 0.11.0

- Challenges read from the game's session quests (form / close a big group), segment-counted goals.

## 0.10.0

- Biome goals (grow a group of a given terrain to a target size, then close it).

## 0.9.0

- Tile and quest recording for the offline simulator (`tools/sim`).

## 0.8.x

- Perfect-placement potential (expected future perfect placements).

## 0.7.x

- Tiles gained modeled (perfect placements, quest rewards) and used by the "Compact" objectives.

## 0.6.x

- Auto mode (F10), "compact without mismatches" objective, settings saved in the config.

## 0.5.x

- Probable tiles in the lookahead, "score without mismatches" objective.

## 0.4.x

- Pure board model (segments, groups, quests) with exact quest effects; lookahead on the visible tiles;
  hold Ctrl+Cmd to preview and click to place; model self-checks before every placement.

## 0.3.0

- First solver and in-game overlay (suggestions 1-2-3, F5/F6/F8/F9).

## 0.2.0

- Game state snapshots (tile in hand, valid slots, quests).

## 0.1.0

- BepInEx plugin skeleton on macOS (Mono, Rosetta, full `mscorlib`).
