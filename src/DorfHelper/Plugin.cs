using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using DorfHelper.Model;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DorfHelper
{
    [BepInPlugin("fr.lphilippe.dorfhelper", "Dorf Helper", "0.25.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private readonly GameState state = new GameState();
        private DataRecorder recorder;
        private TilePlacer subscribedPlacer;
        private string snapshotPath;
        private float nextResolveTime;
        private string lastMissing;
        private bool updateLogged;

        // Solver
        // Lookahead: visible tiles (only the next 2 are already drawn by the game) + simulated likely tiles.
        private static readonly (int known, int simulated)[] DepthChoices = { (0, 0), (2, 0), (2, 5), (2, 10) };
        private int depthChoice = 1; // 2 visible tiles
        private BepInEx.Configuration.ConfigEntry<Objective> objectiveConfig;
        private BepInEx.Configuration.ConfigEntry<int> depthConfig;

        // Biome goal (F3)
        private BepInEx.Configuration.ConfigEntry<string> goalChoiceConfig;

        // Auto restart (F2): in auto mode, start a new game when the previous one ends without completing the challenge
        private BepInEx.Configuration.ConfigEntry<bool> autoRetryConfig;
        private const float RetryDelay = 8f; // seconds after game over (score saving, progression)
        private float gameOverSince = -1f;
        private int retryCount;
        private bool restarting;
        private float lastRestartTime = -999f; // safeguard: no two restarts in a row while loading

        // Fail early ("in one go" challenges): restart without waiting for the end if the game is not promising
        private BepInEx.Configuration.ConfigEntry<bool> failEarlyConfig;
        private int failEarlyChecked; // last checkpoint evaluated in this game
        private const int FlowerStallLimit = 60; // tiles without flower progress before switching sites
        private int flowerBestProgress, flowerProgressAt;
        private Vector2Int? flowerTrackedSite;
        private int gameBestFlower, gameBestRing1;
        private int abandonedGames;

        internal enum PanelPosition { BottomCenter, BottomRight, BottomLeft, TopCenter, TopRight, TopLeft }
        private BepInEx.Configuration.ConfigEntry<PanelPosition> panelPosition;
        private List<BiomeGoal> gameGoals = new List<BiomeGoal>();
        private BepInEx.Configuration.ConfigEntry<GroupTypeId> goalGroupConfig;
        private BepInEx.Configuration.ConfigEntry<string> goalElementConfig;
        private BepInEx.Configuration.ConfigEntry<int> goalTargetConfig;
        private BiomeGoal goal;
        private Objective objective = Objective.Score;
        private List<Suggestion> suggestions = new List<Suggestion>();
        private int selectedIndex = -1;
        private int recomputeInFrames = -1;
        private bool overlayVisible = true;
        private GameSnapshot snapshot;
        private Task<SearchResult> searchTask;
        private CancellationTokenSource searchCancel;
        private string lastStats = "";
        private string goalAchievedMessage;

        // Ctrl+Cmd held: suggestion locked, click = place
        private bool chordHeld;
        private Tile clickedTile;
        private int clickFrame;
        private bool gamePlacedSinceClick;

        // Auto mode (F10): places the best suggestion for every tile
        private const float AutoDelay = 0.6f; // seconds between two placements, to let animations play
        private bool autoPlay;
        private float autoNextTime;
        private int autoPlaced;
        private int autoErrors;
        private Tile suggestionsHand; // tile in hand for which the suggestions were computed
        private Tile searchHand;

        // Model self-check (once per hovered slot/rotation pair)
        private string lastCheckedPreview;
        private int checksOk, checksKo;

        private void Awake()
        {
            Log = Logger;
            string dir = Path.Combine(Paths.BepInExRootPath, "DorfHelper");
            Directory.CreateDirectory(dir);
            snapshotPath = Path.Combine(dir, "snapshots.jsonl");
            recorder = new DataRecorder(dir);
            // Settings remembered between launches (BepInEx/config/fr.lphilippe.dorfhelper.cfg)
            objectiveConfig = Config.Bind("Solver", "Objective", Objective.Score, "Solver objective (F8)");
            depthConfig = Config.Bind("Solver", "Lookahead", 1, "Lookahead choice index (F4): 0 none, 1 two visible, 2 +5 likely, 3 +10 likely");
            bestEverPoints = Config.Bind("Statistics", "Best points in one placement", 0, "Updated automatically");
            failEarlyConfig = Config.Bind("Auto mode", "Fail early", true,
                "\"In one go\" challenges in auto mode with restart: abandons a game where no flower got past 3 of 6 inner tiles by 250 tiles");
            autoRetryConfig = Config.Bind("Auto mode", "Auto restart", true,
                "In auto mode, starts a new game when the previous one ends without completing the chosen challenge (F2)");
            panelPosition = Config.Bind("Display", "Panel position", PanelPosition.BottomCenter,
                "Where to show the panel (BottomCenter hides neither the stack nor the undo button)");
            goalChoiceConfig = Config.Bind("Biome goal", "Choice", "", "Goal chosen with F3 (challenge title, \"custom\" or empty)");
            goalGroupConfig = Config.Bind("Biome goal", "Terrain", GroupTypeId.Agriculture, "Type of group to build then close");
            goalElementConfig = Config.Bind("Biome goal", "Element", "Field", "Counted element (Field, House, Tree…)");
            goalTargetConfig = Config.Bind("Biome goal", "Target", 150, "Number of elements to reach before closing the group (custom goal)");
            objective = objectiveConfig.Value;
            depthChoice = Mathf.Clamp(depthConfig.Value, 0, DepthChoices.Length - 1);

            var harmony = new Harmony("fr.lphilippe.dorfhelper");
            harmony.PatchAll(typeof(PreviewLock));
            harmony.PatchAll(typeof(PlacementHook));
            PlacementHook.BeforePlace = VerifyAtPlacement;
            Log.LogInfo("Dorf Helper loaded. Snapshots: " + snapshotPath);
        }

        private void OnDestroy()
        {
            searchCancel?.Cancel();
            Log.LogWarning("Plugin destroyed (the BepInEx GameObject was removed by the game).");
        }

        private void Update()
        {
            if (!updateLogged)
            {
                updateLogged = true;
                Log.LogInfo("Update loop active.");
            }

            if (!subscribedPlacer)
            {
                TryAttachToGame();
                return;
            }

            PreviewLock.Tick();
            try { HandleHotkeys(); }
            catch (Exception e) { Log.LogError("Keyboard shortcuts failed: " + e); }

            // Safety net: the tile in hand or the number of placed tiles changed since the last snapshot.
            if (recomputeInFrames < 0 && searchTask == null && Time.frameCount % 10 == 0)
            {
                bool handChanged = state.TilePlacer.CurrentTile != lastHandTile;
                bool boardChanged = snapshot != null && state.World.GetAllPlacedTiles().Count != snapshot.PlacedCount;
                if (handChanged || boardChanged) recomputeInFrames = 2;
            }

            VerifyPreview();
            CollectSearchResult();
            if (autoPlay) AutoStep();
            CheckAutoRetry();
            CheckFailEarly();

            // Wait a few frames after a new tile: the game must first recompute the valid slots.
            if (recomputeInFrames > 0) recomputeInFrames--;
            else if (recomputeInFrames == 0)
            {
                recomputeInFrames = -1;
                StartSearch();
            }
        }

        private void TryAttachToGame()
        {
            // Game objects change on every game load: look them up (again) periodically.
            if (Time.unscaledTime < nextResolveTime) return;
            nextResolveTime = Time.unscaledTime + 1f;
            suggestions.Clear();
            snapshot = null;

            if (!state.TryResolve())
            {
                // Game in progress (TilePlacer present) but a component is missing: report it once.
                string missing = state.MissingComponents();
                if (state.TilePlacer && missing != lastMissing) Log.LogWarning("Components not found: " + missing);
                lastMissing = missing;
                return;
            }

            subscribedPlacer = state.TilePlacer;
            subscribedPlacer.OnNewPreviewTileSet -= OnNewTileInHand;
            subscribedPlacer.OnNewPreviewTileSet += OnNewTileInHand;
            // These game objects survive from one game to the next: unsubscribe before subscribing again.
            state.InputRouter.OnPlaceTile -= OnGamePlacedTile;
            state.InputRouter.OnPlaceTile += OnGamePlacedTile;
            if (state.RewardSystem)
            {
                state.RewardSystem.OnTilesGained -= OnTilesGained;
                state.RewardSystem.OnTilesGained += OnTilesGained;
                state.RewardSystem.OnScoreChanged -= OnScoreChanged;
                state.RewardSystem.OnScoreChanged += OnScoreChanged;
                state.RewardSystem.OnPerfectPlacement -= OnPerfectPlacement;
                state.RewardSystem.OnPerfectPlacement += OnPerfectPlacement;
            }
            Log.LogInfo("Game detected, subscribed to new tiles." + (restarting ? $" (attempt #{retryCount + 1})" : ""));
            restarting = false;
            gameOverSince = -1f;
            failEarlyChecked = 0;
            flowerBestProgress = 0;
            flowerProgressAt = 0;
            flowerTrackedSite = null;
            gameBestFlower = gameBestRing1 = 0;
            if (goal != null) goal.ExcludedFlowerSites.Clear();
            ResetGameStats();
            RewardSystemConfiguration rc = state.RewardSystem ? state.RewardSystem.Configuration : null;
            if (rc)
                Log.LogInfo($"Scoring config: edge matches {(rc.rewardTileFit ? $"{rc.tileFitScoreCalculationMethod} ×{rc.tileFitScoreModifier}" : "no")}, " +
                            $"perfect placement {(rc.rewardTileClosed ? rc.perfectPlacementScore.ToString() : "no")}, " +
                            $"closed groups {(rc.rewardCompletedGroups ? $"{rc.groupScoreMultiplier} × (segments-1)^{rc.groupCompletionScorePow}" : "no")}, " +
                            $"quest {(rc.rewardQuestsWithScore ? rc.questScoreReward.ToString() : "no")}, flag {rc.closingQuestScoreReward}");
            LoadGameGoals();
            Log.LogInfo(GameState.DescribeAutoPlacer());
            recomputeInFrames = 2;
        }

        private void OnNewTileInHand(Tile tile)
        {
            PreviewLock.Release();
            recomputeInFrames = 2;
            try
            {
                string json = JsonConvert.SerializeObject(state.Snapshot());
                File.AppendAllText(snapshotPath, json + Environment.NewLine);
            }
            catch (Exception e)
            {
                Log.LogError("Snapshot failed: " + e);
            }
        }

        // ---------- Search ----------

        private Tile lastHandTile;

        private void StartSearch()
        {
            // Right after a placement, the tile in hand is still the placed tile: wait for the actual new tile.
            Tile hand = state.TilePlacer.CurrentTile;
            if (hand && hand.State == TileState.placed)
            {
                recomputeInFrames = 3;
                return;
            }
            lastHandTile = hand;
            searchHand = hand;

            searchCancel?.Cancel();
            suggestions.Clear();
            selectedIndex = -1;
            try
            {
                snapshot = Snapshotter.Take(state, DepthChoices[depthChoice].known, goal);
                if (goal != null && goal.Achieved)
                {
                    Log.LogInfo($"Biome goal achieved: {goal}!");
                    string snapshotGoalTitle = goal.ToString();
                    SelectGoal(null);
                    goalAchievedMessage = "✓ Goal achieved: " + snapshotGoalTitle;
                    LoadGameGoals();
                }
            }
            catch (Exception e)
            {
                snapshot = null;
                Log.LogError("Could not build the board model: " + e);
                return;
            }

            GameSnapshot snap = snapshot;
            if (goal != null)
            {
                if (goal.HasTarget && goal.AtOnceKind == 0 && !goal.Many)
                {
                    if (goal.History.Count == 0 || goal.History[goal.History.Count - 1].placed != snap.PlacedCount)
                        goal.History.Add((snap.PlacedCount, goal.Current.Value));
                    if (goal.History.Count > 60) goal.History.RemoveAt(0);
                    goal.MaxValue = Math.Max(goal.MaxValue, goal.Current.Value);
                }
                if (goal.StartProgress < 0 && goal.Source) goal.StartProgress = goal.GameProgress;
            }
            recorder.RecordDrawnTile(hand);
            recorder.RecordQuests(state.QuestManager.AllQuestWatchers ?? new List<QuestWatcher>(), state.QuestManager);
            if (predictedTiles >= 0 && Time.unscaledTime - lastPlacementTime > 1.5f) FinishTilesCheck();
            Log.LogInfo($"Snapshot: {snap.Board.TileCount} slots (game: {snap.PlacedCount} placed), hand = {(hand ? hand.name + " " + hand.State : "none")}, " +
                        $"{snap.ValidSlots.Count} valid slots, {snap.Board.Quests.Count} quests, readable stack {snap.Stack.Count}, " +
                        $"rewards: perfect +{snap.Board.PerfectReward}, quest +{snap.Board.QuestReward}" +
                        (goal != null && goal.Many ? $", goal {goal.GameProgress}/{goal.Target} closed groups weight {goal.Scale:0%}" : "") +
                        (goal != null && (goal.AtOnceKind == 1 || goal.UsesFlower) && goal.FlowerSite.HasValue
                            ? $", flower {goal.FlowerSite.Value} rings {goal.Flower.Ring1}/6 {goal.Flower.Ring2}/12 center {goal.Flower.CenterChance:0%} quest {goal.Flower.QuestReadiness:0%} reserved {goal.ReservedQuests}" : "") +
                        (goal != null && !goal.Many ? $", goal {(goal.HasTarget ? $"{goal.Current.Value}/{goal.Target} ({goal.Current.Open} open edges)" : "no target")} weight {goal.Scale:0%}" : ""));
            Objective obj = objective;
            (int depth, int simulated) = DepthChoices[depthChoice];
            var cts = new CancellationTokenSource();
            searchCancel = cts;
            searchTask = Task.Run(() => Solver.Run(snap, obj, depth, simulated, cts.Token), cts.Token);
        }

        private void CollectSearchResult()
        {
            if (searchTask == null || !searchTask.IsCompleted) return;
            Task<SearchResult> task = searchTask;
            searchTask = null;
            if (task.IsCanceled) return;
            if (task.IsFaulted)
            {
                Log.LogError("Solver failed: " + task.Exception);
                return;
            }

            suggestions = task.Result.Suggestions;
            suggestionsHand = searchHand;
            foreach (Suggestion s in suggestions) s.Slot = state.SlotPreviewer.GetTileSlot(s.Pos);
            lastStats = task.Result.Stats;
            if (chordHeld) PreviewNextSuggestion(); // Ctrl+Cmd still held: move on to the next one
            if (suggestions.Count > 0)
            {
                Suggestion s = suggestions[0];
                PlacementEval e = s.Eval;
                Log.LogInfo($"[{objective}] best slot {s.Pos} rot {s.Rotation}: {s.Score:0} (immediate {s.Immediate:0} + future {s.Future:0}) — " +
                            $"{e.Perfect} perfect, {e.Mismatched} mismatched, {e.Neighbors} neighbors, quests +{e.QuestsFulfilled} -{e.QuestsFailed} " +
                            $"progress {e.QuestProgress:0.00}, +{e.TilesGained} tiles, potential {e.Potential:+0.00;-0.00} | {lastStats}");
            }
        }

        /// <summary>
        /// Checks the model on every placement: the tile is then on its final slot, with its final rotation,
        /// and the game has already recomputed the quests. We compare with the model's prediction for this placement.
        /// </summary>
        private int placementOk, placementKo;

        // Tiles-gained check: prediction made right before placement, compared to the tiles actually added
        private int predictedTiles = -1, actualTiles, tilesOk, tilesKo;
        private int predictedPoints = -1, actualPoints, pointsOk, pointsKo;
        private float lastPlacementTime;

        private void OnTilesGained(int count) => actualTiles += count;

        private void OnScoreChanged(int added)
        {
            actualPoints += added;
            placementPoints += added;
        }

        // Points-per-placement statistics (current game), delimited by the "right before placement" hook
        private int placementPoints, gamePlacements, gamePointsSum, gameBestPoints;
        private int placementPerfects, gameBestPerfects;

        private void OnPerfectPlacement() => placementPerfects++;
        private bool placementOpen;
        private BepInEx.Configuration.ConfigEntry<int> bestEverPoints;

        private void ClosePlacementStats()
        {
            if (!placementOpen) return;
            placementOpen = false;
            gamePlacements++;
            gamePointsSum += placementPoints;
            if (placementPoints > gameBestPoints) gameBestPoints = placementPoints;
            if (placementPerfects > gameBestPerfects) gameBestPerfects = placementPerfects;
            if (bestEverPoints != null && placementPoints > bestEverPoints.Value) bestEverPoints.Value = placementPoints;
        }

        private void ResetGameStats()
        {
            placementOpen = false;
            placementPoints = gamePlacements = gamePointsSum = gameBestPoints = 0;
            placementPerfects = gameBestPerfects = 0;
            if (goal != null) { goal.History.Clear(); goal.MaxValue = 0; goal.StartProgress = -1; }
        }

        private BiomeGoal CustomGoal() => new BiomeGoal
        {
            GroupType = goalGroupConfig.Value,
            Element = goalElementConfig.Value,
            Target = goalTargetConfig.Value,
            Title = "custom",
        };

        /// <summary>F3 list: the game's uncompleted "big group" challenges, then the custom goal from the config.</summary>
        private List<BiomeGoal> GoalChoices() => gameGoals.Concat(new[] { CustomGoal() }).ToList();

        private void SelectGoal(BiomeGoal g)
        {
            goal = g;
            goalChoiceConfig.Value = g?.Title ?? "";
            goalAchievedMessage = null;
            Log.LogInfo(g != null ? "Biome goal: " + g : "Biome goal disabled");
        }

        private void LoadGameGoals()
        {
            try
            {
                gameGoals = GoalCatalog.FromGame();
                Log.LogInfo($"Uncompleted \"big group\" challenges: {gameGoals.Count}");
                foreach (BiomeGoal g in gameGoals) Log.LogInfo("  - " + g);
            }
            catch (Exception e)
            {
                gameGoals = new List<BiomeGoal>();
                Log.LogWarning("Could not read the game's challenges: " + e.Message);
            }
            // Restore the goal chosen at the last launch
            if (goal == null && !string.IsNullOrEmpty(goalChoiceConfig.Value))
                goal = GoalChoices().FirstOrDefault(g => g.Title == goalChoiceConfig.Value);
        }

        private void OnGamePlacedTile(TileSlot _) => gamePlacedSinceClick = true;

        private void FinishTilesCheck()
        {
            if (predictedTiles < 0) return;
            if (predictedTiles == actualTiles) tilesOk++;
            else
            {
                tilesKo++;
                Log.LogWarning($"Tiles-gained check: predicted {predictedTiles}, actual {actualTiles}");
            }
            if (predictedTiles > 0 || actualTiles > 0) Log.LogInfo($"Tiles-gained check: {tilesOk} OK, {tilesKo} mismatches (last placement +{actualTiles})");
            predictedTiles = -1;

            if (predictedPoints >= 0)
            {
                if (predictedPoints == actualPoints) pointsOk++;
                else { pointsKo++; Log.LogWarning($"Points check: predicted {predictedPoints}, actual {actualPoints}"); }
                if (actualPoints >= 100 || predictedPoints >= 100) Log.LogInfo($"Points check: {pointsOk} OK, {pointsKo} mismatches (last placement {actualPoints} pts)");
                predictedPoints = -1;
            }
        }

        private void VerifyAtPlacement(TileSlot targetSlot)
        {
            // The previous placement's points are complete: record them, then open the next one.
            ClosePlacementStats();
            placementPoints = 0;
            placementPerfects = 0;
            placementOpen = true;

            FinishTilesCheck();
            if (snapshot != null && snapshot.Hand != null && targetSlot && state.TilePlacer.CurrentTile && !SnapshotStale())
            {
                PlacementEval ev = snapshot.Board.Evaluate(snapshot.Hand, targetSlot.GridPos, state.TilePlacer.CurrentTile.RotationIndex);
                predictedTiles = ev.TilesGained;
                actualTiles = 0;
                predictedPoints = ev.Points;
                actualPoints = 0;
                lastPlacementTime = Time.unscaledTime;
            }

            if (snapshot == null || !subscribedPlacer || state.TilePlacer.CurrentTileSlot != targetSlot || SnapshotStale()) return;
            try
            {
                string diff = Solver.CheckAgainstPreview(state, snapshot);
                if (diff == null) placementOk++;
                else { placementKo++; Log.LogWarning("Placement check: " + diff); }
                Log.LogInfo($"Placement check: {placementOk} OK, {placementKo} mismatches");
            }
            catch (Exception e)
            {
                Log.LogError("Placement check failed: " + e);
            }
        }

        /// <summary>The snapshot no longer matches the board (a placement was made before it was refreshed).</summary>
        private bool SnapshotStale() => state.World.GetAllPlacedTiles().Count != snapshot.PlacedCount;

        private void VerifyPreview()
        {
            TileSlot slot = state.TilePlacer.CurrentTileSlot;
            Tile tile = state.TilePlacer.CurrentTile;
            if (!slot || !tile || snapshot == null || SnapshotStale()) return;
            string key = slot.GridPos + "/" + tile.RotationIndex + "/" + tile.GetInstanceID();
            if (key == lastCheckedPreview) return;
            lastCheckedPreview = key;
            try
            {
                string diff = Solver.CheckAgainstPreview(state, snapshot);
                if (diff == null) checksOk++;
                else { checksKo++; Log.LogWarning("Model check: " + diff); }
                if ((checksOk + checksKo) % 5 == 0) Log.LogInfo($"Model check: {checksOk} OK, {checksKo} mismatches");
            }
            catch (Exception e)
            {
                Log.LogError("Model check failed: " + e);
            }
        }

        // ---------- Shortcuts ----------

        private void HandleHotkeys()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return;

            if (kb.f5Key.wasPressedThisFrame) PreviewNextSuggestion();
            if (kb.f6Key.wasPressedThisFrame) PlaceSelectedSuggestion();
            if (kb.f4Key.wasPressedThisFrame)
            {
                depthChoice = (depthChoice + 1) % DepthChoices.Length;
                depthConfig.Value = depthChoice;
                Log.LogInfo("Lookahead: " + DepthLabel());
                StartSearch();
            }
            if (kb.f8Key.wasPressedThisFrame)
            {
                objective = (Objective)(((int)objective + 1) % Enum.GetValues(typeof(Objective)).Length);
                objectiveConfig.Value = objective;
                Log.LogInfo("Objective: " + objective);
                StartSearch();
            }
            if (kb.f9Key.wasPressedThisFrame) overlayVisible = !overlayVisible;
            if (kb.f2Key.wasPressedThisFrame)
            {
                autoRetryConfig.Value = !autoRetryConfig.Value;
                Log.LogInfo("Auto restart: " + (autoRetryConfig.Value ? "ON" : "OFF"));
            }
            if (kb.f3Key.wasPressedThisFrame)
            {
                // None → challenge 1 → challenge 2 → … → custom → none
                List<BiomeGoal> choices = GoalChoices();
                int index = goal == null ? -1 : choices.FindIndex(g => g.Title == goal.Title);
                SelectGoal(index + 1 < choices.Count ? choices[index + 1] : null);
                StartSearch();
            }
            if (kb.f10Key.wasPressedThisFrame)
            {
                autoPlay = !autoPlay;
                autoPlaced = 0;
                autoNextTime = Time.unscaledTime + 0.2f;
                Log.LogInfo(autoPlay ? "Auto mode: ON" : "Auto mode: OFF");
            }

            HandleChord(kb);
        }

        /// <summary>
        /// Ctrl+Cmd held = F5 locked for as long as they are held (pressing again = next suggestion).
        /// A click meanwhile places the tile: normally the game handles it (the current slot is
        /// the suggestion); if it hasn't done so a few frames later, we place it ourselves.
        /// </summary>
        private void HandleChord(Keyboard kb)
        {
            bool chord = kb.ctrlKey.isPressed && (kb.leftCommandKey.isPressed || kb.rightCommandKey.isPressed);
            if (chord && !chordHeld)
            {
                chordHeld = true;
                PreviewLock.Held = true;
                PreviewNextSuggestion();
            }
            else if (!chord && chordHeld)
            {
                chordHeld = false;
                PreviewLock.Held = false;
                PreviewLock.Release();
            }

            Mouse mouse = Mouse.current;
            if (chordHeld && mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
            {
                clickedTile = state.TilePlacer.CurrentTile;
                clickFrame = Time.frameCount;
                gamePlacedSinceClick = false;
            }

            if (clickedTile && Time.frameCount >= clickFrame + 3)
            {
                if (!gamePlacedSinceClick && state.TilePlacer.CurrentTile == clickedTile) PlaceSelectedSuggestion();
                clickedTile = null;
            }
        }

        /// <summary>
        /// Fail early ("in one go" challenges): a flower that has not progressed for FlowerStallLimit tiles is replaced by
        /// a new site; at 250 placed tiles, a game where no flower ever got past 3 of 6 inner tiles is abandoned for a new
        /// attempt.
        /// </summary>
        private void CheckFailEarly()
        {
            if (!autoPlay || !autoRetryConfig.Value || !failEarlyConfig.Value || restarting) return;
            if (goal == null || (goal.AtOnceKind != 1 && !goal.UsesFlower) || snapshot == null) return;
            int placed = snapshot.PlacedCount;

            // Stall detector: the flower must progress; a new site resets the counter.
            int progress = goal.FlowerSite.HasValue ? 2 * goal.Flower.Ring1 + goal.Flower.Ring2 : 0;
            if (goal.FlowerSite != flowerTrackedSite) { flowerTrackedSite = goal.FlowerSite; flowerBestProgress = progress; flowerProgressAt = placed; }
            else if (progress > flowerBestProgress) { flowerBestProgress = progress; flowerProgressAt = placed; }
            bool complete = goal.FlowerSite.HasValue && goal.Flower.Ring1 == 6 && goal.Flower.Ring2 == 12;
            if (progress > gameBestFlower) gameBestFlower = progress;
            if (goal.FlowerSite.HasValue && goal.Flower.Ring1 > gameBestRing1) gameBestRing1 = goal.Flower.Ring1;
            // Stalled flower: set it aside and aim for another one (without abandoning the game).
            if (goal.FlowerSite.HasValue && !complete && placed - flowerProgressAt >= FlowerStallLimit)
            {
                Log.LogInfo($"Flower {goal.FlowerSite.Value} stalled for {placed - flowerProgressAt} tiles ({goal.Flower.Ring1}/6, {goal.Flower.Ring2}/12): switching sites.");
                goal.ExcludedFlowerSites.Add(goal.FlowerSite.Value);
                goal.FlowerSite = null;
                flowerTrackedSite = null;
                flowerProgressAt = placed;
                return;
            }

            // Abandon the game only if no flower has really taken shape.
            int[] checkpoints = { 250 };
            int cp = checkpoints.LastOrDefault(c => placed >= c);
            if (cp == 0 || cp <= failEarlyChecked) return;
            failEarlyChecked = cp;

            Board.FlowerState f = goal.Flower;
            bool hasSite = goal.FlowerSite.HasValue;
            bool needQuests = goal.UsesFlower && goal.QuestsNeeded > 0;
            string reason = gameBestRing1 < 4 ? $"no flower got past {gameBestRing1}/6 inner tiles" : null;
            if (reason == null)
            {
                Log.LogInfo($"Checkpoint {cp} tiles: promising game (flower {f.Ring1}/6 {f.Ring2}/12, center {f.CenterChance:0%}, {goal.ReservedQuests} reserved quest(s)), continuing.");
                return;
            }
            AbandonGame(placed, reason);
        }

        private void AbandonGame(int placed, string reason)
        {
            abandonedGames++;
            Log.LogInfo($"Failing early at {placed} tiles ({reason}): new attempt.");
            ClosePlacementStats();
            Log.LogInfo($"Game over (attempt #{retryCount + 1}, abandoned): {placed} tiles, score {state.RewardSystem.Score}, " +
                        $"best in one go {gameBestPoints} pts / {gameBestPerfects} perfect");
            RestartGame();
        }

        /// <summary>New game in the same game mode (like the game's reset after inactivity).</summary>
        private void RestartGame()
        {
            try
            {
                SaveGameLoadingInitiator loader = Resources.FindObjectsOfTypeAll<SaveGameLoadingInitiator>().FirstOrDefault();
                GameMode mode = OverwritingSingleton<GameSession>.Instance ? OverwritingSingleton<GameSession>.Instance.GameMode : null;
                if (!loader || !mode)
                {
                    Log.LogWarning("Auto restart impossible: game loader or game mode not found.");
                    autoRetryConfig.Value = false;
                    return;
                }
                restarting = true;
                lastRestartTime = Time.unscaledTime;
                retryCount++;
                Log.LogInfo($"Auto restart: new game ({mode.name}), attempt #{retryCount + 1}");
                subscribedPlacer = null; // we will attach to the new game
                suggestions.Clear();
                loader.SetSelectedGameMode(mode);
                loader.NewGameInSelectedGameMode();
            }
            catch (Exception e)
            {
                restarting = false;
                Log.LogError("Auto restart failed: " + e);
                autoRetryConfig.Value = false;
            }
        }

        /// <summary>
        /// Auto restart: in auto mode, with a chosen challenge not yet completed, a finished game is
        /// summarized in the log then replaced by a new game in the same game mode.
        /// </summary>
        private void CheckAutoRetry()
        {
            bool over = state.RewardSystem && state.RewardSystem.IsGameOver;
            if (!over) { gameOverSince = -1f; return; }
            if (gameOverSince < 0f)
            {
                gameOverSince = Time.unscaledTime;
                ClosePlacementStats();
                Log.LogInfo($"Game over (attempt #{retryCount + 1}): {state.World.GetAllPlacedTiles().Count} tiles, score {state.RewardSystem.Score}, " +
                            $"best in one go {gameBestPoints} pts / {gameBestPerfects} perfect" +
                            (goal != null && goal.FlowerSite.HasValue ? $", last flower {goal.Flower.Ring1}/6 {goal.Flower.Ring2}/12 center {goal.Flower.CenterChance:0%}" : "") +
                            (goal != null && goal.HasTarget && goal.AtOnceKind == 0 && !goal.Many ? $", goal max {goal.MaxValue}/{goal.Target}" : ""));
            }
            if (restarting || !autoPlay || !autoRetryConfig.Value || goal == null) return;
            if (Time.unscaledTime - gameOverSince < RetryDelay || Time.unscaledTime - lastRestartTime < 30f) return;

            RestartGame();
        }

        /// <summary>
        /// Auto mode: as soon as the suggestions for the tile in hand are ready (and up to date), places the best one.
        /// Waits while the game is not in the "Playing" state (menus, reward screens…).
        /// </summary>
        private void AutoStep()
        {
            if (Time.unscaledTime < autoNextTime || searchTask != null || recomputeInFrames >= 0) return;
            Tile hand = state.TilePlacer.CurrentTile;
            if (!hand || hand.State == TileState.placed) return;
            if (suggestionsHand != hand || snapshot == null || SnapshotStale()) return;
            if (state.InputRouter.GameState != global::GameState.Playing || state.InputRouter.IsLoading) return;

            if (suggestions.Count == 0)
            {
                autoPlay = false;
                Log.LogInfo($"Auto mode: no more suggestions, stopping after {autoPlaced} tiles.");
                return;
            }

            Suggestion s = suggestions[0];
            if (!s.Slot || !s.Slot.IsValid) return;
            try
            {
                selectedIndex = 0;
                PreviewLock.MoveAndLock(state.InputRouter, s.Slot);
                if (state.TilePlacer.CurrentTileSlot != s.Slot) { autoNextTime = Time.unscaledTime + 0.3f; return; }
                state.TilePlacer.RotatePreviewTileTo(s.Rotation, animate: false);
                // Camera recentering: optional (it can fail if the game keeps a destroyed slot selected)
                try { state.InputRouter.MoveCameraToSelection(); } catch (Exception) { }
                // Direct placement, like the game's AutoPlacer (no "slot visible on screen" check).
                state.TilePlacer.PlaceCurrentTile(s.Slot);
                autoPlaced++;
                autoErrors = 0;
                autoNextTime = Time.unscaledTime + AutoDelay;
            }
            catch (Exception e)
            {
                // Never loop every frame: one message, a pause, then retry.
                autoErrors++;
                autoNextTime = Time.unscaledTime + 1f;
                if (autoErrors == 1 || autoErrors % 30 == 0) Log.LogError($"Auto mode: placement failed ({autoErrors}×): {e.Message}");
            }
            finally
            {
                PreviewLock.Release();
            }
        }

        /// <summary>F5: moves the preview tile to the next suggestion (1 → 2 → 3 → 1…), without placing it.</summary>
        private void PreviewNextSuggestion()
        {
            if (suggestions.Count == 0) return;
            selectedIndex = (selectedIndex + 1) % suggestions.Count;
            Suggestion s = suggestions[selectedIndex];
            if (!s.Slot) return;
            PreviewLock.MoveAndLock(state.InputRouter, s.Slot);
            if (state.TilePlacer.CurrentTileSlot == s.Slot)
            {
                state.TilePlacer.RotatePreviewTileTo(s.Rotation, animate: true);
                try { state.InputRouter.MoveCameraToSelection(); } catch (Exception) { }
            }
        }

        /// <summary>F6: places the tile on the displayed suggestion (or the best one if none is displayed).</summary>
        private void PlaceSelectedSuggestion()
        {
            if (suggestions.Count == 0) return;
            if (selectedIndex < 0) PreviewNextSuggestion();
            Suggestion s = suggestions[selectedIndex];
            if (!s.Slot) return;
            if (state.TilePlacer.CurrentTileSlot != s.Slot || state.TilePlacer.CurrentTile.RotationIndex != s.Rotation)
            {
                // The mouse moved the preview in the meantime: put it back before placing.
                PreviewLock.MoveAndLock(state.InputRouter, s.Slot);
                state.TilePlacer.RotatePreviewTileTo(s.Rotation, animate: false);
            }
            state.InputRouter.PlaceTileOnCurrentSlot();
        }

        // ---------- Display ----------

        private GUIStyle labelStyle, panelStyle;

        private void OnGUI()
        {
            if (!overlayVisible || !subscribedPlacer) return;
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                panelStyle = new GUIStyle(GUI.skin.box) { fontSize = 14, alignment = TextAnchor.UpperLeft, richText = true, padding = new RectOffset(10, 10, 8, 8) };
            }

            Camera cam = state.Camera;
            Color[] colors = { new Color(1f, 0.85f, 0.1f), new Color(0.85f, 0.85f, 0.85f), new Color(0.85f, 0.55f, 0.3f) };
            for (int i = 0; i < suggestions.Count; i++)
            {
                Suggestion s = suggestions[i];
                if (!cam || !s.Slot) continue;
                Vector3 p = cam.WorldToScreenPoint(s.Slot.transform.position);
                if (p.z < 0) continue;
                var rect = new Rect(p.x - 40, Screen.height - p.y - 25, 80, 50);
                var bg = new Rect(p.x - 26, Screen.height - p.y - 26, 52, 52);
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(bg, Texture2D.whiteTexture);
                GUI.color = Color.white;
                DrawOutlined(rect, (i + 1).ToString(), i == selectedIndex ? Color.green : colors[Math.Min(i, colors.Length - 1)]);
            }

            if (goal != null && (goal.AtOnceKind == 1 || goal.UsesFlower) && goal.FlowerSite.HasValue && cam)
            {
                TileSlot centre = state.SlotPreviewer.GetTileSlot(goal.FlowerSite.Value);
                Vector3 c = centre ? cam.WorldToScreenPoint(centre.transform.position)
                                   : cam.WorldToScreenPoint(GridToWorld(goal.FlowerSite.Value));
                if (c.z > 0) DrawOutlined(new Rect(c.x - 40, Screen.height - c.y - 25, 80, 50), "✿", new Color(1f, 0.5f, 0.8f));
            }

            DrawPanel();
        }

        // ---------- Panel (fixed height, aligned columns, no line wrapping) ----------

        private GUIStyle textStyle, smallStyle, titleStyle, rankStyle;
        private float uiScale = -1f;

        private void EnsurePanelStyles()
        {
            float scale = Mathf.Clamp(Screen.height / 1080f, 1f, 2.5f);
            if (textStyle != null && Mathf.Approximately(scale, uiScale)) return;
            uiScale = scale;
            GUIStyle Make(int size, FontStyle fs = FontStyle.Normal, TextAnchor anchor = TextAnchor.MiddleLeft) => new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(size * scale), fontStyle = fs, alignment = anchor, richText = true,
                wordWrap = false, clipping = TextClipping.Clip, padding = new RectOffset(0, 0, 0, 0),
                normal = { textColor = new Color(0.92f, 0.92f, 0.92f) },
            };
            textStyle = Make(15);
            smallStyle = Make(12);
            smallStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            titleStyle = Make(17, FontStyle.Bold);
            rankStyle = Make(18, FontStyle.Bold, TextAnchor.MiddleCenter);
        }

        private static void Fill(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        /// <summary>Progress bar with centered text.</summary>
        private void ProgressBar(Rect r, float ratio, Color color, string label)
        {
            Fill(r, new Color(1f, 1f, 1f, 0.12f));
            Fill(new Rect(r.x, r.y, r.width * Mathf.Clamp01(ratio), r.height), color);
            var centered = new GUIStyle(smallStyle) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            GUI.Label(r, label, centered);
        }

        private void DrawPanel()
        {
            EnsurePanelStyles();
            float u = uiScale;
            float pad = 12 * u, row = 24 * u, width = 640 * u;
            const int SuggestionRows = 3;
            float height = pad * 2 + row * (4 + SuggestionRows) + 18 * u;
            float margin = 16 * u;
            float px = panelPosition.Value switch
            {
                PanelPosition.BottomRight or PanelPosition.TopRight => Screen.width - width - margin,
                PanelPosition.BottomLeft or PanelPosition.TopLeft => margin,
                _ => (Screen.width - width) / 2f,
            };
            float py = panelPosition.Value is PanelPosition.TopCenter or PanelPosition.TopRight or PanelPosition.TopLeft
                ? margin : Screen.height - height - margin;
            var panel = new Rect(px, py, width, height);
            Fill(panel, new Color(0.08f, 0.09f, 0.11f, 0.88f));
            Fill(new Rect(panel.x, panel.y, 4 * u, panel.height), new Color(1f, 0.82f, 0.35f, 0.9f));

            if (goal != null)
            {
                float cardHeight = pad * 2 + row * 4;
                bool above = panelPosition.Value is PanelPosition.BottomCenter or PanelPosition.BottomRight or PanelPosition.BottomLeft;
                var card = new Rect(panel.x, above ? panel.y - cardHeight - 8 * u : panel.yMax + 8 * u, width, cardHeight);
                if (goal.AtOnceKind == 2) DrawAnalystCard(card, pad, row);
                else if (goal.AtOnceKind == 1) DrawFlowerCard(card, pad, row);
                else if (goal.Many) DrawManyCard(card, pad, row);
                else DrawBigGroupCard(card, pad, row);
            }

            float x = panel.x + pad, w = width - pad * 2, y = panel.y + pad;

            // Line 1: title + status
            GUI.Label(new Rect(x, y, w, row), "Dorf Helper", titleStyle);
            string status = (autoPlay ? $"<color=#7cf><b>AUTO</b> · {autoPlaced} placed</color>   " : "")
                            + (autoPlay && autoRetryConfig.Value && goal != null ? $"<color=#7cf>restart · attempt {retryCount + 1}" +
                                  (abandonedGames > 0 ? $" ({abandonedGames} abandoned)" : "") + "</color>   " : "")
                            + (gameOverSince >= 0f && autoPlay && autoRetryConfig.Value && goal != null
                                ? $"<color=#fd6>new game in {Mathf.CeilToInt(RetryDelay - (Time.unscaledTime - gameOverSince))} s</color>   " : "")
                            + (searchTask != null ? "<color=#aaa><i>computing…</i></color>" : "");
            GUI.Label(new Rect(x, y, w, row), status, new GUIStyle(textStyle) { alignment = TextAnchor.MiddleRight });
            y += row;

            // Line 2: mode and lookahead
            GUI.Label(new Rect(x, y, w, row), $"Mode <b>{ObjectiveLabel(objective)}</b>   ·   Lookahead <b>{DepthLabel()}</b>", textStyle);
            y += row;

            // Lines 3-4: challenge / goal, with progress bar
            DrawGoalRows(new Rect(x, y, w, row * 2));
            y += row * 2;

            // Separator
            Fill(new Rect(x, y + 2 * u, w, 1), new Color(1f, 1f, 1f, 0.15f));
            y += 4 * u;

            // Suggestions: rank | score | edges | details
            float rankW = 28 * u, scoreW = 110 * u, edgesW = 120 * u;
            Color[] rankColors = { new Color(1f, 0.85f, 0.1f), new Color(0.85f, 0.85f, 0.85f), new Color(0.85f, 0.55f, 0.3f) };
            for (int i = 0; i < SuggestionRows; i++)
            {
                var r = new Rect(x, y, w, row);
                if (i < suggestions.Count)
                {
                    Suggestion s = suggestions[i];
                    PlacementEval e = s.Eval;
                    if (i == selectedIndex) Fill(r, new Color(0.3f, 0.8f, 0.4f, 0.18f));
                    rankStyle.normal.textColor = i == selectedIndex ? Color.green : rankColors[i];
                    GUI.Label(new Rect(x, y, rankW, row), (i + 1).ToString(), rankStyle);
                    bool withFuture = DepthChoices[depthChoice].known + DepthChoices[depthChoice].simulated > 0;
                    GUI.Label(new Rect(x + rankW, y, scoreW, row),
                              withFuture ? $"<b>{s.Score:0}</b> <color=#999>({s.Future:+0;-0})</color>" : $"<b>{s.Score:0}</b>", textStyle);
                    string edges = e.Mismatched > 0
                        ? $"{e.Perfect}/{e.Neighbors} edges <color=#f77>✗{e.Mismatched}</color>"
                        : $"{e.Perfect}/{e.Neighbors} edges" + (e.PerfectPlacement ? " <color=#fd6>★</color>" : "");
                    GUI.Label(new Rect(x + rankW + scoreW, y, edgesW, row), edges, textStyle);
                    GUI.Label(new Rect(x + rankW + scoreW + edgesW, y, w - rankW - scoreW - edgesW, row), SuggestionTags(e), textStyle);
                }
                else
                {
                    GUI.Label(r, i == 0 ? (searchTask != null ? "<color=#888>computing…</color>" : "<color=#888>no suggestion</color>") : "", textStyle);
                }
                y += row;
            }

            // Help (small, one line)
            string help = PreviewLock.IsLocked
                ? (PreviewLock.Held ? "Locked: click = place · release Ctrl+Cmd to take back control"
                                    : "Locked: click or F6 = place · move the mouse to take back control")
                : "Ctrl+Cmd / F5 view · click / F6 place · F3 challenge · F4 lookahead · F8 mode · F9 hide · F10 auto · F2 restart";
            GUI.Label(new Rect(x, panel.yMax - pad - 16 * u, w, 16 * u), help, smallStyle);
        }

        // ---------- Challenge cards ----------

        /// <summary>Common card frame: background, colored stripe, title. Returns (x, width, y of the 1st line).</summary>
        private (float x, float w, float y) CardFrame(Rect card, float pad, Color accent)
        {
            Fill(card, new Color(0.08f, 0.09f, 0.11f, 0.88f));
            Fill(new Rect(card.x, card.y, 4 * uiScale, card.height), accent);
            return (card.x + pad, card.width - pad * 2, card.y + pad);
        }

        /// <summary>"Confidence" line: colored bar (red / orange / green) with its breakdown.</summary>
        private void ConfidenceRow(float x, float y, float w, float row, float confidence, string detail)
        {
            float labelW = 110 * uiScale;
            Color bar = confidence >= 0.66f ? new Color(0.3f, 0.75f, 0.4f, 0.85f)
                      : confidence >= 0.33f ? new Color(0.95f, 0.7f, 0.2f, 0.85f) : new Color(0.85f, 0.35f, 0.3f, 0.85f);
            GUI.Label(new Rect(x, y, labelW, row), "<b>Confidence</b>", textStyle);
            ProgressBar(new Rect(x + labelW, y + 3 * uiScale, w - labelW, row - 6 * uiScale), confidence, bar, detail);
        }

        /// <summary>Growth of the target group (elements per placed tile) over the latest snapshots.</summary>
        private float GrowthRate()
        {
            if (goal.History.Count < 2) return 0f;
            var first = goal.History[Math.Max(0, goal.History.Count - 30)];
            var last = goal.History[goal.History.Count - 1];
            int tiles = last.placed - first.placed;
            return tiles > 0 ? Math.Max(0f, (float)(last.value - first.value) / tiles) : 0f;
        }

        /// <summary>Big group (build / close): size, pace, open edges, confidence.</summary>
        private void DrawBigGroupCard(Rect card, float pad, float row)
        {
            var (x, w, y) = CardFrame(card, pad, new Color(0.95f, 0.7f, 0.2f, 0.9f));
            int target = Math.Max(1, goal.Target), value = goal.HasTarget ? goal.Current.Value : 0;
            bool closingPhase = goal.Close && value >= target;

            GUI.Label(new Rect(x, y, w, row),
                      $"<b>{goal.GroupType}</b>: <color=#fd6><b>{value}</b></color> / {target} {goal.Unit}" +
                      $"   <color=#999>· game max {goal.MaxValue}</color>", textStyle);
            y += row;

            float rate = GrowthRate();
            int remaining = Math.Max(0, target - value);
            string pace = closingPhase ? "<color=#7f7>size reached</color>"
                : rate > 0.01f ? $"{rate:0.0} {goal.Unit} per tile · ~{Mathf.CeilToInt(remaining / rate)} more tiles at this pace"
                : "<color=#888>no growth measured yet</color>";
            GUI.Label(new Rect(x, y, w, row), $"<b>Pace</b>: {pace}   <color=#999>· stack {state.TileStack.Height}</color>", textStyle);
            y += row;

            int open = goal.HasTarget ? goal.Current.Open : 0;
            string edges = closingPhase ? $"<b>Closing</b>: {open} edge(s) to close"
                : $"<b>Open edges</b>: {open}" + (open <= Solver.GoalSafeOpenEdges ? "  <color=#f77>⚠ risk of closing</color>" : "  <color=#7f7>enough margin</color>");
            GUI.Label(new Rect(x, y, w, row), edges, textStyle);
            y += row;

            float size = Mathf.Clamp01((float)value / target);
            float pacing = closingPhase || remaining == 0 ? 1f
                : rate > 0.01f ? Mathf.Clamp01((state.TileStack.Height + 30f) / (remaining / rate)) : 0.2f;
            float safety = closingPhase ? 1f / (1f + 0.15f * open) : Mathf.Clamp01(open / 5f);
            float confidence = goal.HasTarget ? Mathf.Clamp01(size * 0.5f + 0.5f) * pacing * safety : 0f;
            ConfidenceRow(x, y, w, row, confidence,
                          $"{confidence:0%}  (size {size:0%} · pace/stack {pacing:0%} · {(closingPhase ? "closing" : "edges")} {safety:0%})");
        }

        /// <summary>Several groups: cumulative progress, pace, ready groups, remaining games.</summary>
        private void DrawManyCard(Rect card, float pad, float row)
        {
            var (x, w, y) = CardFrame(card, pad, new Color(0.4f, 0.8f, 0.45f, 0.9f));
            int progress = goal.GameProgress, target = Math.Max(1, goal.Target);
            int thisGame = goal.StartProgress >= 0 ? Math.Max(0, progress - goal.StartProgress) : 0;

            GUI.Label(new Rect(x, y, w, row),
                      $"<b>Progress</b>: <color=#7f7><b>{progress}</b></color> / {target} {goal.GroupType} groups ≥ {goal.GroupSize} {goal.Unit}" +
                      $"   <color=#999>· {thisGame} in this game</color>", textStyle);
            y += row;

            float per100 = gamePlacements > 0 ? thisGame * 100f / gamePlacements : 0f;
            int left = Math.Max(0, target - progress);
            string pace = per100 > 0.01f ? $"{per100:0.0} groups per 100 tiles · ~{Mathf.CeilToInt(left / per100 * 100)} more tiles"
                                        : "<color=#888>no group completed in this game yet</color>";
            GUI.Label(new Rect(x, y, w, row), $"<b>Pace</b>: {pace}", textStyle);
            y += row;

            GUI.Label(new Rect(x, y, w, row),
                      $"<b>Remaining</b>: {left} group(s)   <color=#999>· progress carries over from one game to the next</color>", textStyle);
            y += row;

            float needTiles = per100 > 0.01f ? left / per100 * 100f : float.MaxValue;
            float confidence = left == 0 ? 1f : per100 > 0.01f ? Mathf.Clamp01((state.TileStack.Height + 60f) / needTiles) : 0.1f;
            ConfidenceRow(x, y, w, row, confidence,
                          left == 0 ? "challenge complete!" : $"{confidence:0%} to finish in this game");
        }

        /// <summary>Perfect placements in one go: best, flower (rings), center, confidence.</summary>
        private void DrawFlowerCard(Rect card, float pad, float row)
        {
            var (x, w, y) = CardFrame(card, pad, new Color(0.95f, 0.45f, 0.7f, 0.9f));
            int target = Math.Max(1, goal.Target);

            GUI.Label(new Rect(x, y, w, row),
                      $"<b>Game best</b>: <color=#f9c><b>{gameBestPerfects}</b></color> / {target} perfect placements in one go", textStyle);
            y += row;

            if (!goal.FlowerSite.HasValue)
            {
                GUI.Label(new Rect(x, y, w, row), "<color=#888>looking for a flower site…</color>", textStyle);
                ConfidenceRow(x, y + row * 2, w, row, 0f, "no site");
                return;
            }
            Board.FlowerState f = goal.Flower;
            FlowerStateBars(new Rect(x, y + 3 * uiScale, w, row - 6 * uiScale));
            y += row;

            GUI.Label(new Rect(x, y, w, row),
                      $"<b>Site</b> {goal.FlowerSite.Value} ✿   <color=#999>· chance to complete the rings {f.Completion:0%} · center tile {f.CenterChance:0%}</color>", textStyle);
            y += row;

            float progress = (4f * f.Ring1 + 1.5f * f.Ring2) / 42f;
            float reserve = Mathf.Clamp01(state.TileStack.Height / 30f);
            float confidence = gameBestPerfects >= target ? 1f
                : progress * Mathf.Sqrt(f.Completion) * Mathf.Clamp01(f.CenterChance * 3f) * (0.4f + 0.6f * reserve);
            ConfidenceRow(x, y, w, row, confidence, gameBestPerfects >= target ? "challenge complete!"
                : $"{confidence:0%}  (rings {progress:0%} · completion {Mathf.Sqrt(f.Completion):0%} · center {Mathf.Clamp01(f.CenterChance * 3f):0%} · stack {reserve:0%})");
        }

        /// <summary>
        /// "Points in one placement" challenge card: best, average, projection of the target group and confidence index
        /// (size × closing × stack).
        /// </summary>
        private void DrawAnalystCard(Rect card, float pad, float row)
        {
            var (x, w, y) = CardFrame(card, pad, new Color(0.5f, 0.7f, 1f, 0.9f));
            int target = Math.Max(1, goal.Target);

            // 1. Best
            int bestEver = bestEverPoints?.Value ?? 0;
            GUI.Label(new Rect(x, y, w, row),
                      $"<b>Game best</b>: <color=#fd6><b>{gameBestPoints} pts</b></color> / {target}" +
                      $"   <color=#999>· all-time best {bestEver} pts</color>", textStyle);
            y += row;

            // 2. Average
            float avg = gamePlacements > 0 ? (float)gamePointsSum / gamePlacements : 0f;
            GUI.Label(new Rect(x, y, w, row),
                      $"<b>Average</b>: {avg:0} pts per placement   <color=#999>· {gamePlacements} placements · {gamePointsSum} pts total</color>", textStyle);
            y += row;

            if (goal.UsesFlower)
            {
                // Closings earn nothing: flower (7 perfect) + at least one quest at the center
                int missing = Math.Max(0, target - goal.ProjectedPoints);
                string need = missing == 0 ? "<color=#7f7>enough on its own</color>"
                    : $"+ {goal.QuestsNeeded} quest(s) completed by the center tile";
                GUI.Label(new Rect(x, y, w, row),
                          $"<b>Full flower</b> ≈ <color=#f9c><b>{goal.ProjectedPoints} pts</b></color> / {target}   <color=#999>· {need}</color>" +
                          (missing > 0 ? $"   <color=#fd6>· {goal.ReservedQuests} reserved quest(s)</color>" : ""), textStyle);
                y += row;
                if (goal.FlowerSite.HasValue) FlowerStateBars(new Rect(x, y + 3 * uiScale, w, row - 6 * uiScale));
                else GUI.Label(new Rect(x, y, w, row), "<color=#888>looking for a flower site…</color>", textStyle);
                y += row;
                Board.FlowerState f = goal.Flower;
                float ringProgress = (4f * f.Ring1 + 1.5f * f.Ring2) / 42f;
                float questFactor = missing > 0 ? 0.2f + 0.8f * f.QuestReadiness : 1f;
                float flowerConfidence = gameBestPoints >= target ? 1f : !goal.FlowerSite.HasValue ? 0f
                    : ringProgress * Mathf.Sqrt(f.Completion) * Mathf.Clamp01(f.CenterChance * 3f) * questFactor;
                ConfidenceRow(x, y, w, row, flowerConfidence, gameBestPoints >= target ? "challenge complete!"
                    : $"{flowerConfidence:0%}  (rings {ringProgress:0%} · completion {Mathf.Sqrt(f.Completion):0%} · center {Mathf.Clamp01(f.CenterChance * 3f):0%}" +
                      (missing > 0 ? $" · {goal.QuestsNeeded} quest(s) at center {f.QuestReadiness:0%})" : ")"));
                return;
            }

            // 3. Projection
            int needed = Math.Max(1, goal.SegmentsNeeded);
            string projection = goal.HasTarget && goal.Current.Value > 0
                ? $"<b>If closed now</b>: <color={(goal.ProjectedPoints >= target ? "#7f7" : "#fd6")}><b>{goal.ProjectedPoints} pts</b></color> / {target}" +
                  $"   <color=#999>· {goal.GroupType} group {goal.Current.Value}/{needed} segments, {goal.Current.Open} open edge(s)</color>"
                : "<b>If closed now</b>: <color=#888>no target group yet</color>";
            GUI.Label(new Rect(x, y, w, row), projection, textStyle);
            y += row;

            // 4. Confidence = size × closing × stack
            float size = goal.HasTarget ? Mathf.Clamp01((float)goal.Current.Value / needed) : 0f;
            float sizeFactor = size * size;                                         // the last segments are the hardest
            float closeFactor = size >= 1f ? 1f / (1f + 0.15f * goal.Current.Open)  // every edge to close is a risk
                                           : 0.5f;                                  // closing still far off
            float reserve = Mathf.Clamp01(goal.StackHeight / 30f);
            float confidence = sizeFactor * closeFactor * (0.4f + 0.6f * reserve);
            if (gameBestPoints >= target) confidence = 1f;
            ConfidenceRow(x, y, w, row, confidence, gameBestPoints >= target ? "challenge complete!"
                : $"{confidence:0%}  (size {sizeFactor:0%} · closing {closeFactor:0%} · stack {reserve:0%})");
        }

        /// <summary>Details of a suggestion, by order of importance (the least important are cut if too long).</summary>
        private static string SuggestionTags(PlacementEval e)
        {
            var tags = new List<string>();
            if (e.ReservedQuestsSpent > 0) tags.Add("<color=#f77>wastes a reserved quest</color>");
            if (e.AtOnceKind == 2 && e.HandQuestNearCenter > 0) tags.Add("<color=#f9c>quest linked to center ✿</color>");
            if (e.FlowerCenterWasted && e.Points < e.AtOnceTarget) tags.Add($"<color=#f77>burns the flower ({e.Points} pts)</color>");
            if (e.AtOnceKind == 1 && e.PerfectTriggered >= e.AtOnceTarget) tags.Add($"<color=#7f7><b>✿ {e.PerfectTriggered} perfect!</b></color>");
            if (e.AtOnceKind == 2 && e.Points >= e.AtOnceTarget) tags.Add($"<color=#7f7><b>{e.Points} pts!</b></color>");
            if (e.QuestsFailed > 0) tags.Add($"<color=#f77>✗{e.QuestsFailed} quest</color>");
            if (e.QuestsFulfilled > 0) tags.Add($"<color=#7f7>✓{e.QuestsFulfilled} quest</color>");
            if (e.ManyActive && e.ManyClosedQualifying > 0) tags.Add($"<color=#7f7>✓{e.ManyClosedQualifying} group</color>");
            if (e.TilesGained > 0) tags.Add($"<color=#7cf>+{e.TilesGained} tile{(e.TilesGained > 1 ? "s" : "")}</color>");
            if (e.GoalActive && e.GoalAfter.Value > e.GoalBefore.Value) tags.Add($"<color=#fd6>goal +{e.GoalAfter.Value - e.GoalBefore.Value}</color>");
            if (e.ManyActive && e.ManyGrowth > 0) tags.Add($"<color=#fd6>groups +{e.ManyGrowth}</color>");
            if (e.AtOnceKind == 1 && e.FlowerRuined) tags.Add("<color=#f77>ruins the flower</color>");
            else if (e.AtOnceKind == 1 && e.FlowerDelta >= 0.5f) tags.Add($"<color=#f9c>flower +{e.FlowerDelta:0}</color>");
            if (e.QuestsEndangered > 0) tags.Add($"<color=#fb5>⚠ quest</color>");
            if (e.QuestProgress > 0.001f) tags.Add($"quests +{e.QuestProgress * 100:0}%");
            if (e.Points >= 100 && !(e.AtOnceKind == 2 && e.Points >= e.AtOnceTarget)) tags.Add($"<color=#fd6>{e.Points} pts</color>");
            if (e.PerfectTriggered >= 2 && e.AtOnceKind != 1) tags.Add($"{e.PerfectTriggered} perfect");
            return string.Join("  ", tags.Take(4));
        }

        /// <summary>Two lines: challenge / goal title, then progress (bar) or status.</summary>
        private void DrawGoalRows(Rect area)
        {
            float row = area.height / 2;
            var title = new Rect(area.x, area.y, area.width, row);
            var bar = new Rect(area.x, area.y + row + 3 * uiScale, area.width, row - 6 * uiScale);
            Color gold = new Color(0.95f, 0.7f, 0.2f, 0.85f);

            if (goal == null)
            {
                GUI.Label(title, goalAchievedMessage != null ? $"<color=#7f7>{goalAchievedMessage}</color>" : "<color=#888>No challenge — F3 to pick one</color>", textStyle);
                return;
            }
            string scale = goal.Scale < 0.999f ? $"  <color=#999>(weight {goal.Scale:0%})</color>" : "";
            GUI.Label(title, $"<color=#fd6><b>{goal.Title}</b></color> — {GoalSummary()}{scale}", textStyle);

            if (goal.AtOnceKind == 1)
            {
                if (!goal.FlowerSite.HasValue) { GUI.Label(bar, "<color=#888>looking for a flower site…</color>", smallStyle); return; }
                FlowerStateBars(bar);
            }
            else if (goal.AtOnceKind == 2 && goal.UsesFlower)
            {
                if (!goal.FlowerSite.HasValue) { GUI.Label(bar, "<color=#888>looking for a flower site…</color>", smallStyle); return; }
                FlowerStateBars(bar);
            }
            else if (goal.AtOnceKind == 2)
            {
                float ratio = goal.SegmentsNeeded > 0 ? (float)goal.Current.Value / goal.SegmentsNeeded : 0f;
                ProgressBar(bar, ratio, gold, goal.Current.Value >= goal.SegmentsNeeded
                    ? $"{goal.GroupType} group ready: close its {goal.Current.Open} open edge(s)"
                    : $"{goal.GroupType} group: {goal.Current.Value}/{goal.SegmentsNeeded} segments · {goal.Current.Open} open edges");
            }
            else if (goal.Many)
            {
                ProgressBar(bar, (float)goal.GameProgress / Math.Max(1, goal.Target), gold,
                            $"{goal.GameProgress}/{goal.Target} closed groups of at least {goal.GroupSize} {goal.Unit}");
            }
            else if (!goal.HasTarget)
            {
                GUI.Label(bar, "<color=#888>no open group yet</color>", smallStyle);
            }
            else
            {
                bool closing = goal.Current.Value >= goal.Target;
                ProgressBar(bar, closing ? 1f : (float)goal.Current.Value / goal.Target, closing ? new Color(0.3f, 0.75f, 0.4f, 0.85f) : gold,
                            closing ? $"{goal.Current.Value} {goal.Unit} — closing: {goal.Current.Open} open edge(s)"
                                    : $"{goal.Current.Value}/{goal.Target} {goal.Unit} · {goal.Current.Open} open edges");
            }
        }

        private string GoalSummary()
        {
            if (goal.AtOnceKind == 1) return $"{goal.Target} perfect placements in one go";
            if (goal.AtOnceKind == 2) return $"{goal.Target} points in one placement";
            if (goal.Many) return $"close {goal.Target} {goal.GroupType} groups ≥ {goal.GroupSize} {goal.Unit}";
            return $"{(goal.Close ? "close" : "build")} a {goal.GroupType} group of {goal.Target} {goal.Unit}";
        }

        /// <summary>Flower: three mini-bars (inner ring, outer ring, center chance).</summary>
        private void FlowerStateBars(Rect r)
        {
            float gap = 8 * uiScale, third = (r.width - 2 * gap) / 3;
            Color pink = new Color(0.95f, 0.45f, 0.7f, 0.85f);
            ProgressBar(new Rect(r.x, r.y, third, r.height), goal.Flower.Ring1 / 6f, pink, $"inner {goal.Flower.Ring1}/6");
            ProgressBar(new Rect(r.x + third + gap, r.y, third, r.height), goal.Flower.Ring2 / 12f, pink, $"outer {goal.Flower.Ring2}/12");
            ProgressBar(new Rect(r.x + 2 * (third + gap), r.y, third, r.height), goal.Flower.CenterChance, new Color(0.5f, 0.7f, 1f, 0.85f),
                        $"center {goal.Flower.CenterChance:0%}");
        }

        private void DrawOutlined(Rect rect, string text, Color color)
        {
            labelStyle.normal.textColor = Color.black;
            foreach (var o in new[] { new Vector2(-2, 0), new Vector2(2, 0), new Vector2(0, -2), new Vector2(0, 2) })
                GUI.Label(new Rect(rect.x + o.x, rect.y + o.y, rect.width, rect.height), text, labelStyle);
            labelStyle.normal.textColor = color;
            GUI.Label(rect, text, labelStyle);
        }

        /// <summary>World position of a slot (copy of GridCalculator.GridToWorldPos).</summary>
        private static Vector3 GridToWorld(Vector2Int p) =>
            new Vector3(p.x * 0.75f, 0f, (p.y - Mathf.Abs(p.x % 2) / 2f) * 0.8660254f);

        private string DepthLabel()
        {
            (int known, int simulated) = DepthChoices[depthChoice];
            if (known + simulated == 0) return "none";
            return simulated == 0 ? $"{known} visible" : $"{known} visible + {simulated} likely";
        }

        private static string ObjectiveLabel(Objective o)
        {
            switch (o)
            {
                case Objective.Score: return "score";
                case Objective.Quests: return "quests";
                case Objective.Compact: return "last long (compact)";
                case Objective.Clean: return "score with no mismatched edge";
                case Objective.CompactClean: return "last long with no mismatched edge";
                default: return o.ToString();
            }
        }
    }
}
