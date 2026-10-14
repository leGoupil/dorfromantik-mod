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
    [BepInPlugin("fr.lphilippe.dorfhelper", "Dorf Helper", "0.24.0")]
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

        // Solveur
        // Anticipation : tuiles visibles (seules les 2 suivantes sont déjà tirées par le jeu) + tuiles probables simulées.
        private static readonly (int known, int simulated)[] DepthChoices = { (0, 0), (2, 0), (2, 5), (2, 10) };
        private int depthChoice = 1; // 2 tuiles visibles
        private BepInEx.Configuration.ConfigEntry<Objective> objectiveConfig;
        private BepInEx.Configuration.ConfigEntry<int> depthConfig;

        // Objectif de biome (F3)
        private BepInEx.Configuration.ConfigEntry<string> goalChoiceConfig;

        // Relance automatique (F2) : en mode auto, nouvelle partie quand la précédente se termine sans avoir réussi le défi
        private BepInEx.Configuration.ConfigEntry<bool> autoRetryConfig;
        private const float RetryDelay = 8f; // secondes après la fin de partie (enregistrement du score, progression)
        private float gameOverSince = -1f;
        private int retryCount;
        private bool restarting;
        private float lastRestartTime = -999f; // garde-fou : pas deux relances de suite pendant le chargement

        // Abandon précoce (défis "d'un coup") : relancer sans attendre la fin si la partie n'est pas prometteuse
        private BepInEx.Configuration.ConfigEntry<bool> failEarlyConfig;
        private int failEarlyChecked; // dernier point de contrôle évalué dans la partie
        private const int FlowerStallLimit = 60; // tuiles sans progrès d'une fleur avant de changer de site
        private int flowerBestProgress, flowerProgressAt;
        private Vector2Int? flowerTrackedSite;
        private int gameBestFlower, gameBestRing1;
        private int abandonedGames;

        internal enum PanelPosition { BasCentre, BasDroite, BasGauche, HautCentre, HautDroite, HautGauche }
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

        // Ctrl+Cmd maintenus : suggestion verrouillée, clic = poser
        private bool chordHeld;
        private Tile clickedTile;
        private int clickFrame;
        private bool gamePlacedSinceClick;

        // Mode auto (F10) : pose la meilleure suggestion à chaque tuile
        private const float AutoDelay = 0.6f; // secondes entre deux poses, pour laisser jouer les animations
        private bool autoPlay;
        private float autoNextTime;
        private int autoPlaced;
        private int autoErrors;
        private Tile suggestionsHand; // tuile en main pour laquelle les suggestions ont été calculées
        private Tile searchHand;

        // Auto-vérification du modèle (une fois par couple case/rotation survolé)
        private string lastCheckedPreview;
        private int checksOk, checksKo;

        private void Awake()
        {
            Log = Logger;
            string dir = Path.Combine(Paths.BepInExRootPath, "DorfHelper");
            Directory.CreateDirectory(dir);
            snapshotPath = Path.Combine(dir, "snapshots.jsonl");
            recorder = new DataRecorder(dir);
            // Réglages mémorisés d'un lancement à l'autre (BepInEx/config/fr.lphilippe.dorfhelper.cfg)
            objectiveConfig = Config.Bind("Solveur", "Objectif", Objective.Score, "Objectif du solveur (F8)");
            depthConfig = Config.Bind("Solveur", "Anticipation", 1, "Index du choix d'anticipation (F4) : 0 aucune, 1 deux visibles, 2 +5 probables, 3 +10 probables");
            bestEverPoints = Config.Bind("Statistiques", "Record de points en une pose", 0, "Mis à jour automatiquement");
            failEarlyConfig = Config.Bind("Mode auto", "Abandon précoce", true,
                "Défis \"d'un coup\" en mode auto avec relance : abandonne une partie peu prometteuse aux points de contrôle (150 et 300 tuiles)");
            autoRetryConfig = Config.Bind("Mode auto", "Relance automatique", true,
                "En mode auto, relance une partie quand la précédente se termine sans avoir réussi le défi choisi (F2)");
            panelPosition = Config.Bind("Affichage", "Position du panneau", PanelPosition.BasCentre,
                "Où afficher le panneau (BasCentre ne masque ni la pile ni le bouton retour)");
            goalChoiceConfig = Config.Bind("Objectif de biome", "Choix", "", "Objectif choisi avec F3 (titre du défi, \"perso\" ou vide)");
            goalGroupConfig = Config.Bind("Objectif de biome", "Terrain", GroupTypeId.Agriculture, "Type de groupe à construire puis fermer");
            goalElementConfig = Config.Bind("Objectif de biome", "Element", "Field", "Élément compté (Field, House, Tree…)");
            goalTargetConfig = Config.Bind("Objectif de biome", "Cible", 150, "Nombre d'éléments à atteindre avant de fermer le groupe (objectif perso)");
            objective = objectiveConfig.Value;
            depthChoice = Mathf.Clamp(depthConfig.Value, 0, DepthChoices.Length - 1);

            var harmony = new Harmony("fr.lphilippe.dorfhelper");
            harmony.PatchAll(typeof(PreviewLock));
            harmony.PatchAll(typeof(PlacementHook));
            PlacementHook.BeforePlace = VerifyAtPlacement;
            Log.LogInfo("Dorf Helper chargé. Snapshots : " + snapshotPath);
        }

        private void OnDestroy()
        {
            searchCancel?.Cancel();
            Log.LogWarning("Plugin détruit (le GameObject de BepInEx a été supprimé par le jeu).");
        }

        private void Update()
        {
            if (!updateLogged)
            {
                updateLogged = true;
                Log.LogInfo("Boucle Update active.");
            }

            if (!subscribedPlacer)
            {
                TryAttachToGame();
                return;
            }

            PreviewLock.Tick();
            try { HandleHotkeys(); }
            catch (Exception e) { Log.LogError("Raccourcis clavier en erreur : " + e); }

            // Filet de sécurité : la tuile en main ou le nombre de tuiles posées a changé depuis la dernière photo.
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

            // On attend quelques frames après une nouvelle tuile : le jeu doit d'abord recalculer les cases valides.
            if (recomputeInFrames > 0) recomputeInFrames--;
            else if (recomputeInFrames == 0)
            {
                recomputeInFrames = -1;
                StartSearch();
            }
        }

        private void TryAttachToGame()
        {
            // Les objets du jeu changent à chaque chargement de partie : on (re)cherche périodiquement.
            if (Time.unscaledTime < nextResolveTime) return;
            nextResolveTime = Time.unscaledTime + 1f;
            suggestions.Clear();
            snapshot = null;

            if (!state.TryResolve())
            {
                // Partie en cours (TilePlacer présent) mais composant manquant : on le signale une fois.
                string missing = state.MissingComponents();
                if (state.TilePlacer && missing != lastMissing) Log.LogWarning("Composants introuvables : " + missing);
                lastMissing = missing;
                return;
            }

            subscribedPlacer = state.TilePlacer;
            subscribedPlacer.OnNewPreviewTileSet -= OnNewTileInHand;
            subscribedPlacer.OnNewPreviewTileSet += OnNewTileInHand;
            // Ces objets du jeu survivent d'une partie à l'autre : on se désabonne avant de se réabonner.
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
            Log.LogInfo("Partie détectée, abonné aux nouvelles tuiles." + (restarting ? $" (essai n°{retryCount + 1})" : ""));
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
                Log.LogInfo($"Config des points : raccords {(rc.rewardTileFit ? $"{rc.tileFitScoreCalculationMethod} ×{rc.tileFitScoreModifier}" : "non")}, " +
                            $"placement parfait {(rc.rewardTileClosed ? rc.perfectPlacementScore.ToString() : "non")}, " +
                            $"groupes fermés {(rc.rewardCompletedGroups ? $"{rc.groupScoreMultiplier} × (segments-1)^{rc.groupCompletionScorePow}" : "non")}, " +
                            $"quête {(rc.rewardQuestsWithScore ? rc.questScoreReward.ToString() : "non")}, drapeau {rc.closingQuestScoreReward}");
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
                Log.LogError("Snapshot impossible : " + e);
            }
        }

        // ---------- Recherche ----------

        private Tile lastHandTile;

        private void StartSearch()
        {
            // Juste après une pose, la tuile en main est encore la tuile posée : on attend la vraie nouvelle tuile.
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
                    Log.LogInfo($"Objectif de biome atteint : {goal} !");
                    string snapshotGoalTitle = goal.ToString();
                    SelectGoal(null);
                    goalAchievedMessage = "✓ Objectif atteint : " + snapshotGoalTitle;
                    LoadGameGoals();
                }
            }
            catch (Exception e)
            {
                snapshot = null;
                Log.LogError("Modèle du plateau impossible à construire : " + e);
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
            Log.LogInfo($"Photo : {snap.Board.TileCount} cases (jeu : {snap.PlacedCount} posées), main = {(hand ? hand.name + " " + hand.State : "aucune")}, " +
                        $"{snap.ValidSlots.Count} cases valides, {snap.Board.Quests.Count} quêtes, pioche lisible {snap.Stack.Count}, " +
                        $"récompenses : parfait +{snap.Board.PerfectReward}, quête +{snap.Board.QuestReward}" +
                        (goal != null && goal.Many ? $", objectif {goal.GameProgress}/{goal.Target} groupes fermés poids {goal.Scale:0%}" : "") +
                        (goal != null && (goal.AtOnceKind == 1 || goal.UsesFlower) && goal.FlowerSite.HasValue
                            ? $", fleur {goal.FlowerSite.Value} anneaux {goal.Flower.Ring1}/6 {goal.Flower.Ring2}/12 centre {goal.Flower.CenterChance:0%} quête {goal.Flower.QuestReadiness:0%} réservées {goal.ReservedQuests}" : "") +
                        (goal != null && !goal.Many ? $", objectif {(goal.HasTarget ? $"{goal.Current.Value}/{goal.Target} ({goal.Current.Open} bords ouverts)" : "sans cible")} poids {goal.Scale:0%}" : ""));
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
                Log.LogError("Solveur en erreur : " + task.Exception);
                return;
            }

            suggestions = task.Result.Suggestions;
            suggestionsHand = searchHand;
            foreach (Suggestion s in suggestions) s.Slot = state.SlotPreviewer.GetTileSlot(s.Pos);
            lastStats = task.Result.Stats;
            if (chordHeld) PreviewNextSuggestion(); // Ctrl+Cmd toujours maintenus : on enchaîne
            if (suggestions.Count > 0)
            {
                Suggestion s = suggestions[0];
                PlacementEval e = s.Eval;
                Log.LogInfo($"[{objective}] meilleure case {s.Pos} rot {s.Rotation} : {s.Score:0} (immédiat {s.Immediate:0} + suite {s.Future:0}) — " +
                            $"{e.Perfect} parfaits, {e.Mismatched} ratés, {e.Neighbors} voisins, quêtes +{e.QuestsFulfilled} -{e.QuestsFailed} " +
                            $"progrès {e.QuestProgress:0.00}, +{e.TilesGained} tuiles, potentiel {e.Potential:+0.00;-0.00} | {lastStats}");
            }
        }

        /// <summary>
        /// Vérifie le modèle à chaque pose : la tuile est alors sur sa case finale, avec sa rotation finale,
        /// et le jeu a déjà recalculé les quêtes. On compare avec la prédiction du modèle pour cette pose.
        /// </summary>
        private int placementOk, placementKo;

        // Vérif des tuiles gagnées : prédiction faite juste avant la pose, comparée aux tuiles réellement ajoutées
        private int predictedTiles = -1, actualTiles, tilesOk, tilesKo;
        private int predictedPoints = -1, actualPoints, pointsOk, pointsKo;
        private float lastPlacementTime;

        private void OnTilesGained(int count) => actualTiles += count;

        private void OnScoreChanged(int added)
        {
            actualPoints += added;
            placementPoints += added;
        }

        // Statistiques de points par pose (partie en cours), délimitées par le crochet "juste avant la pose"
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
            Title = "perso",
        };

        /// <summary>Liste F3 : défis "grand groupe" du jeu non accomplis, puis l'objectif perso de la config.</summary>
        private List<BiomeGoal> GoalChoices() => gameGoals.Concat(new[] { CustomGoal() }).ToList();

        private void SelectGoal(BiomeGoal g)
        {
            goal = g;
            goalChoiceConfig.Value = g?.Title ?? "";
            goalAchievedMessage = null;
            Log.LogInfo(g != null ? "Objectif de biome : " + g : "Objectif de biome désactivé");
        }

        private void LoadGameGoals()
        {
            try
            {
                gameGoals = GoalCatalog.FromGame();
                Log.LogInfo($"Défis \"grand groupe\" non accomplis : {gameGoals.Count}");
                foreach (BiomeGoal g in gameGoals) Log.LogInfo("  - " + g);
            }
            catch (Exception e)
            {
                gameGoals = new List<BiomeGoal>();
                Log.LogWarning("Lecture des défis du jeu impossible : " + e.Message);
            }
            // Reprend l'objectif choisi au dernier lancement
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
                Log.LogWarning($"Vérif tuiles gagnées : prédit {predictedTiles}, réel {actualTiles}");
            }
            if (predictedTiles > 0 || actualTiles > 0) Log.LogInfo($"Vérif tuiles gagnées : {tilesOk} OK, {tilesKo} écarts (dernière pose +{actualTiles})");
            predictedTiles = -1;

            if (predictedPoints >= 0)
            {
                if (predictedPoints == actualPoints) pointsOk++;
                else { pointsKo++; Log.LogWarning($"Vérif points : prédit {predictedPoints}, réel {actualPoints}"); }
                if (actualPoints >= 100 || predictedPoints >= 100) Log.LogInfo($"Vérif points : {pointsOk} OK, {pointsKo} écarts (dernière pose {actualPoints} pts)");
                predictedPoints = -1;
            }
        }

        private void VerifyAtPlacement(TileSlot targetSlot)
        {
            // Les points de la pose précédente sont complets : on les comptabilise, puis on ouvre la suivante.
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
                else { placementKo++; Log.LogWarning("Vérif pose : " + diff); }
                Log.LogInfo($"Vérif pose : {placementOk} OK, {placementKo} écarts");
            }
            catch (Exception e)
            {
                Log.LogError("Vérif pose en erreur : " + e);
            }
        }

        /// <summary>La photo ne correspond plus au plateau (pose faite avant qu'elle soit rafraîchie).</summary>
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
                else { checksKo++; Log.LogWarning("Vérif modèle : " + diff); }
                if ((checksOk + checksKo) % 5 == 0) Log.LogInfo($"Vérif modèle : {checksOk} OK, {checksKo} écarts");
            }
            catch (Exception e)
            {
                Log.LogError("Vérif modèle en erreur : " + e);
            }
        }

        // ---------- Raccourcis ----------

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
                Log.LogInfo("Anticipation : " + DepthLabel());
                StartSearch();
            }
            if (kb.f8Key.wasPressedThisFrame)
            {
                objective = (Objective)(((int)objective + 1) % Enum.GetValues(typeof(Objective)).Length);
                objectiveConfig.Value = objective;
                Log.LogInfo("Objectif : " + objective);
                StartSearch();
            }
            if (kb.f9Key.wasPressedThisFrame) overlayVisible = !overlayVisible;
            if (kb.f2Key.wasPressedThisFrame)
            {
                autoRetryConfig.Value = !autoRetryConfig.Value;
                Log.LogInfo("Relance automatique : " + (autoRetryConfig.Value ? "ON" : "OFF"));
            }
            if (kb.f3Key.wasPressedThisFrame)
            {
                // Aucun → défi 1 → défi 2 → … → perso → aucun
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
                Log.LogInfo(autoPlay ? "Mode auto : ON" : "Mode auto : OFF");
            }

            HandleChord(kb);
        }

        /// <summary>
        /// Ctrl+Cmd maintenus = F5 verrouillé tant qu'on tient (rappui = suggestion suivante).
        /// Un clic pendant ce temps pose la tuile : normalement le jeu s'en charge (la case courante est
        /// la suggestion) ; s'il ne l'a pas fait quelques frames plus tard, on pose nous-mêmes.
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
        /// Relance automatique : en mode auto, avec un défi choisi et pas encore réussi, une partie terminée est
        /// résumée dans le log puis remplacée par une nouvelle partie dans le même mode de jeu.
        /// </summary>
        /// <summary>
        /// Abandon précoce : à 150 puis 300 tuiles posées, une partie de défi "d'un coup" sans fleur assez avancée
        /// (ou au centre impossible, ou sans quête réservée quand il en faut) est abandonnée pour un nouvel essai.
        /// </summary>
        private void CheckFailEarly()
        {
            if (!autoPlay || !autoRetryConfig.Value || !failEarlyConfig.Value || restarting) return;
            if (goal == null || (goal.AtOnceKind != 1 && !goal.UsesFlower) || snapshot == null) return;
            int placed = snapshot.PlacedCount;

            // Détecteur de blocage : la fleur doit progresser ; un nouveau site remet le compteur à zéro.
            int progress = goal.FlowerSite.HasValue ? 2 * goal.Flower.Ring1 + goal.Flower.Ring2 : 0;
            if (goal.FlowerSite != flowerTrackedSite) { flowerTrackedSite = goal.FlowerSite; flowerBestProgress = progress; flowerProgressAt = placed; }
            else if (progress > flowerBestProgress) { flowerBestProgress = progress; flowerProgressAt = placed; }
            bool complete = goal.FlowerSite.HasValue && goal.Flower.Ring1 == 6 && goal.Flower.Ring2 == 12;
            if (progress > gameBestFlower) gameBestFlower = progress;
            if (goal.FlowerSite.HasValue && goal.Flower.Ring1 > gameBestRing1) gameBestRing1 = goal.Flower.Ring1;
            // Fleur bloquée : on la met de côté et on en vise une autre (sans abandonner la partie).
            if (goal.FlowerSite.HasValue && !complete && placed - flowerProgressAt >= FlowerStallLimit)
            {
                Log.LogInfo($"Fleur {goal.FlowerSite.Value} bloquée depuis {placed - flowerProgressAt} tuiles ({goal.Flower.Ring1}/6, {goal.Flower.Ring2}/12) : on change de site.");
                goal.ExcludedFlowerSites.Add(goal.FlowerSite.Value);
                goal.FlowerSite = null;
                flowerTrackedSite = null;
                flowerProgressAt = placed;
                return;
            }

            // Abandon de la partie seulement si aucune fleur n'a vraiment pris forme.
            int[] checkpoints = { 250 };
            int cp = checkpoints.LastOrDefault(c => placed >= c);
            if (cp == 0 || cp <= failEarlyChecked) return;
            failEarlyChecked = cp;

            Board.FlowerState f = goal.Flower;
            bool hasSite = goal.FlowerSite.HasValue;
            bool needQuests = goal.UsesFlower && goal.QuestsNeeded > 0;
            string reason = gameBestRing1 < 4 ? $"aucune fleur n'a dépassé {gameBestRing1}/6 tuiles intérieures" : null;
            if (reason == null)
            {
                Log.LogInfo($"Point de contrôle {cp} tuiles : partie prometteuse (fleur {f.Ring1}/6 {f.Ring2}/12, centre {f.CenterChance:0%}, {goal.ReservedQuests} quête(s) réservée(s)), on continue.");
                return;
            }
            AbandonGame(placed, reason);
        }

        private void AbandonGame(int placed, string reason)
        {
            abandonedGames++;
            Log.LogInfo($"Abandon précoce à {placed} tuiles ({reason}) : nouvel essai.");
            ClosePlacementStats();
            Log.LogInfo($"Fin de partie (essai n°{retryCount + 1}, abandonnée) : {placed} tuiles, score {state.RewardSystem.Score}, " +
                        $"record d'un coup {gameBestPoints} pts / {gameBestPerfects} parfaits");
            RestartGame();
        }

        /// <summary>Nouvelle partie dans le même mode de jeu (comme la réinitialisation du jeu après inactivité).</summary>
        private void RestartGame()
        {
            try
            {
                SaveGameLoadingInitiator loader = Resources.FindObjectsOfTypeAll<SaveGameLoadingInitiator>().FirstOrDefault();
                GameMode mode = OverwritingSingleton<GameSession>.Instance ? OverwritingSingleton<GameSession>.Instance.GameMode : null;
                if (!loader || !mode)
                {
                    Log.LogWarning("Relance automatique impossible : chargeur de partie ou mode de jeu introuvable.");
                    autoRetryConfig.Value = false;
                    return;
                }
                restarting = true;
                lastRestartTime = Time.unscaledTime;
                retryCount++;
                Log.LogInfo($"Relance automatique : nouvelle partie ({mode.name}), essai n°{retryCount + 1}");
                subscribedPlacer = null; // on se rattachera à la nouvelle partie
                suggestions.Clear();
                loader.SetSelectedGameMode(mode);
                loader.NewGameInSelectedGameMode();
            }
            catch (Exception e)
            {
                restarting = false;
                Log.LogError("Relance automatique en erreur : " + e);
                autoRetryConfig.Value = false;
            }
        }

        private void CheckAutoRetry()
        {
            bool over = state.RewardSystem && state.RewardSystem.IsGameOver;
            if (!over) { gameOverSince = -1f; return; }
            if (gameOverSince < 0f)
            {
                gameOverSince = Time.unscaledTime;
                ClosePlacementStats();
                Log.LogInfo($"Fin de partie (essai n°{retryCount + 1}) : {state.World.GetAllPlacedTiles().Count} tuiles, score {state.RewardSystem.Score}, " +
                            $"record d'un coup {gameBestPoints} pts / {gameBestPerfects} parfaits" +
                            (goal != null && goal.FlowerSite.HasValue ? $", dernière fleur {goal.Flower.Ring1}/6 {goal.Flower.Ring2}/12 centre {goal.Flower.CenterChance:0%}" : "") +
                            (goal != null && goal.HasTarget && goal.AtOnceKind == 0 && !goal.Many ? $", objectif max {goal.MaxValue}/{goal.Target}" : ""));
            }
            if (restarting || !autoPlay || !autoRetryConfig.Value || goal == null) return;
            if (Time.unscaledTime - gameOverSince < RetryDelay || Time.unscaledTime - lastRestartTime < 30f) return;

            RestartGame();
        }

        /// <summary>
        /// Mode auto : dès que les suggestions de la tuile en main sont prêtes (et à jour), pose la meilleure.
        /// Attend tant que le jeu n'est pas en état "Playing" (menus, écrans de récompense…).
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
                Log.LogInfo($"Mode auto : plus de suggestion, arrêt après {autoPlaced} tuiles.");
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
                // Recentrage de la caméra : facultatif (il peut échouer si le jeu garde une case détruite en sélection)
                try { state.InputRouter.MoveCameraToSelection(); } catch (Exception) { }
                // Pose directe, comme l'AutoPlacer du jeu (pas de contrôle "case visible à l'écran").
                state.TilePlacer.PlaceCurrentTile(s.Slot);
                autoPlaced++;
                autoErrors = 0;
                autoNextTime = Time.unscaledTime + AutoDelay;
            }
            catch (Exception e)
            {
                // Jamais en boucle à chaque frame : un message, une pause, puis on réessaie.
                autoErrors++;
                autoNextTime = Time.unscaledTime + 1f;
                if (autoErrors == 1 || autoErrors % 30 == 0) Log.LogError($"Mode auto : pose impossible ({autoErrors}×) : {e.Message}");
            }
            finally
            {
                PreviewLock.Release();
            }
        }

        /// <summary>F5 : place la tuile d'aperçu sur la suggestion suivante (1 → 2 → 3 → 1…), sans la poser.</summary>
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

        /// <summary>F6 : pose la tuile sur la suggestion affichée (ou la meilleure si aucune n'est affichée).</summary>
        private void PlaceSelectedSuggestion()
        {
            if (suggestions.Count == 0) return;
            if (selectedIndex < 0) PreviewNextSuggestion();
            Suggestion s = suggestions[selectedIndex];
            if (!s.Slot) return;
            if (state.TilePlacer.CurrentTileSlot != s.Slot || state.TilePlacer.CurrentTile.RotationIndex != s.Rotation)
            {
                // La souris a déplacé l'aperçu entre-temps : on le remet en place avant de poser.
                PreviewLock.MoveAndLock(state.InputRouter, s.Slot);
                state.TilePlacer.RotatePreviewTileTo(s.Rotation, animate: false);
            }
            state.InputRouter.PlaceTileOnCurrentSlot();
        }

        // ---------- Affichage ----------

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

        // ---------- Panneau (hauteur fixe, colonnes alignées, pas de retour à la ligne) ----------

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

        /// <summary>Barre de progression avec texte centré.</summary>
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
                PanelPosition.BasDroite or PanelPosition.HautDroite => Screen.width - width - margin,
                PanelPosition.BasGauche or PanelPosition.HautGauche => margin,
                _ => (Screen.width - width) / 2f,
            };
            float py = panelPosition.Value is PanelPosition.HautCentre or PanelPosition.HautDroite or PanelPosition.HautGauche
                ? margin : Screen.height - height - margin;
            var panel = new Rect(px, py, width, height);
            Fill(panel, new Color(0.08f, 0.09f, 0.11f, 0.88f));
            Fill(new Rect(panel.x, panel.y, 4 * u, panel.height), new Color(1f, 0.82f, 0.35f, 0.9f));

            if (goal != null)
            {
                float cardHeight = pad * 2 + row * 4;
                bool above = panelPosition.Value is PanelPosition.BasCentre or PanelPosition.BasDroite or PanelPosition.BasGauche;
                var card = new Rect(panel.x, above ? panel.y - cardHeight - 8 * u : panel.yMax + 8 * u, width, cardHeight);
                if (goal.AtOnceKind == 2) DrawAnalystCard(card, pad, row);
                else if (goal.AtOnceKind == 1) DrawFlowerCard(card, pad, row);
                else if (goal.Many) DrawManyCard(card, pad, row);
                else DrawBigGroupCard(card, pad, row);
            }

            float x = panel.x + pad, w = width - pad * 2, y = panel.y + pad;

            // Ligne 1 : titre + état
            GUI.Label(new Rect(x, y, w, row), "Dorf Helper", titleStyle);
            string status = (autoPlay ? $"<color=#7cf><b>AUTO</b> · {autoPlaced} posées</color>   " : "")
                            + (autoPlay && autoRetryConfig.Value && goal != null ? $"<color=#7cf>relance · essai {retryCount + 1}" +
                                  (abandonedGames > 0 ? $" ({abandonedGames} abandonnés)" : "") + "</color>   " : "")
                            + (gameOverSince >= 0f && autoPlay && autoRetryConfig.Value && goal != null
                                ? $"<color=#fd6>nouvelle partie dans {Mathf.CeilToInt(RetryDelay - (Time.unscaledTime - gameOverSince))} s</color>   " : "")
                            + (searchTask != null ? "<color=#aaa><i>calcul…</i></color>" : "");
            GUI.Label(new Rect(x, y, w, row), status, new GUIStyle(textStyle) { alignment = TextAnchor.MiddleRight });
            y += row;

            // Ligne 2 : mode et anticipation
            GUI.Label(new Rect(x, y, w, row), $"Mode <b>{ObjectiveLabel(objective)}</b>   ·   Anticipation <b>{DepthLabel()}</b>", textStyle);
            y += row;

            // Lignes 3-4 : défi / objectif, avec barre de progression
            DrawGoalRows(new Rect(x, y, w, row * 2));
            y += row * 2;

            // Séparateur
            Fill(new Rect(x, y + 2 * u, w, 1), new Color(1f, 1f, 1f, 0.15f));
            y += 4 * u;

            // Suggestions : rang | note | bords | détails
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
                        ? $"{e.Perfect}/{e.Neighbors} bords <color=#f77>✗{e.Mismatched}</color>"
                        : $"{e.Perfect}/{e.Neighbors} bords" + (e.PerfectPlacement ? " <color=#fd6>★</color>" : "");
                    GUI.Label(new Rect(x + rankW + scoreW, y, edgesW, row), edges, textStyle);
                    GUI.Label(new Rect(x + rankW + scoreW + edgesW, y, w - rankW - scoreW - edgesW, row), SuggestionTags(e), textStyle);
                }
                else
                {
                    GUI.Label(r, i == 0 ? (searchTask != null ? "<color=#888>calcul en cours…</color>" : "<color=#888>aucune suggestion</color>") : "", textStyle);
                }
                y += row;
            }

            // Aide (petite, une ligne)
            string help = PreviewLock.IsLocked
                ? (PreviewLock.Held ? "Verrouillé : clic = poser · relâche Ctrl+Cmd pour reprendre la main"
                                    : "Verrouillé : clic ou F6 = poser · bouge la souris pour reprendre la main")
                : "Ctrl+Cmd / F5 voir · clic / F6 poser · F3 défi · F4 anticipation · F8 mode · F9 masquer · F10 auto · F2 relance";
            GUI.Label(new Rect(x, panel.yMax - pad - 16 * u, w, 16 * u), help, smallStyle);
        }

        // ---------- Cartes de défi ----------

        /// <summary>Cadre commun d'une carte : fond, liseré de couleur, titre. Renvoie (x, largeur, y de la 1re ligne).</summary>
        private (float x, float w, float y) CardFrame(Rect card, float pad, Color accent)
        {
            Fill(card, new Color(0.08f, 0.09f, 0.11f, 0.88f));
            Fill(new Rect(card.x, card.y, 4 * uiScale, card.height), accent);
            return (card.x + pad, card.width - pad * 2, card.y + pad);
        }

        /// <summary>Ligne "Confiance" : barre colorée (rouge / orange / vert) avec son détail.</summary>
        private void ConfidenceRow(float x, float y, float w, float row, float confidence, string detail)
        {
            float labelW = 110 * uiScale;
            Color bar = confidence >= 0.66f ? new Color(0.3f, 0.75f, 0.4f, 0.85f)
                      : confidence >= 0.33f ? new Color(0.95f, 0.7f, 0.2f, 0.85f) : new Color(0.85f, 0.35f, 0.3f, 0.85f);
            GUI.Label(new Rect(x, y, labelW, row), "<b>Confiance</b>", textStyle);
            ProgressBar(new Rect(x + labelW, y + 3 * uiScale, w - labelW, row - 6 * uiScale), confidence, bar, detail);
        }

        /// <summary>Croissance du groupe visé (éléments par tuile posée) sur les dernières photos.</summary>
        private float GrowthRate()
        {
            if (goal.History.Count < 2) return 0f;
            var first = goal.History[Math.Max(0, goal.History.Count - 30)];
            var last = goal.History[goal.History.Count - 1];
            int tiles = last.placed - first.placed;
            return tiles > 0 ? Math.Max(0f, (float)(last.value - first.value) / tiles) : 0f;
        }

        /// <summary>Grand groupe (former / fermer) : taille, rythme, bords ouverts, confiance.</summary>
        private void DrawBigGroupCard(Rect card, float pad, float row)
        {
            var (x, w, y) = CardFrame(card, pad, new Color(0.95f, 0.7f, 0.2f, 0.9f));
            int target = Math.Max(1, goal.Target), value = goal.HasTarget ? goal.Current.Value : 0;
            bool closingPhase = goal.Close && value >= target;

            GUI.Label(new Rect(x, y, w, row),
                      $"<b>{goal.GroupType}</b> : <color=#fd6><b>{value}</b></color> / {target} {goal.Unit}" +
                      $"   <color=#999>· maximum de la partie {goal.MaxValue}</color>", textStyle);
            y += row;

            float rate = GrowthRate();
            int remaining = Math.Max(0, target - value);
            string pace = closingPhase ? "<color=#7f7>taille atteinte</color>"
                : rate > 0.01f ? $"{rate:0.0} {goal.Unit} par tuile · encore ~{Mathf.CeilToInt(remaining / rate)} tuiles à ce rythme"
                : "<color=#888>pas encore de croissance mesurée</color>";
            GUI.Label(new Rect(x, y, w, row), $"<b>Rythme</b> : {pace}   <color=#999>· pioche {state.TileStack.Height}</color>", textStyle);
            y += row;

            int open = goal.HasTarget ? goal.Current.Open : 0;
            string edges = closingPhase ? $"<b>Fermeture</b> : {open} bord(s) à refermer"
                : $"<b>Bords ouverts</b> : {open}" + (open <= Solver.GoalSafeOpenEdges ? "  <color=#f77>⚠ risque de fermeture</color>" : "  <color=#7f7>marge suffisante</color>");
            GUI.Label(new Rect(x, y, w, row), edges, textStyle);
            y += row;

            float size = Mathf.Clamp01((float)value / target);
            float pacing = closingPhase || remaining == 0 ? 1f
                : rate > 0.01f ? Mathf.Clamp01((state.TileStack.Height + 30f) / (remaining / rate)) : 0.2f;
            float safety = closingPhase ? 1f / (1f + 0.15f * open) : Mathf.Clamp01(open / 5f);
            float confidence = goal.HasTarget ? Mathf.Clamp01(size * 0.5f + 0.5f) * pacing * safety : 0f;
            ConfidenceRow(x, y, w, row, confidence,
                          $"{confidence:0%}  (taille {size:0%} · rythme/réserve {pacing:0%} · {(closingPhase ? "fermeture" : "bords")} {safety:0%})");
        }

        /// <summary>Plusieurs groupes : progression cumulée, rythme, groupes prêts, parties restantes.</summary>
        private void DrawManyCard(Rect card, float pad, float row)
        {
            var (x, w, y) = CardFrame(card, pad, new Color(0.4f, 0.8f, 0.45f, 0.9f));
            int progress = goal.GameProgress, target = Math.Max(1, goal.Target);
            int thisGame = goal.StartProgress >= 0 ? Math.Max(0, progress - goal.StartProgress) : 0;

            GUI.Label(new Rect(x, y, w, row),
                      $"<b>Progression</b> : <color=#7f7><b>{progress}</b></color> / {target} groupes {goal.GroupType} ≥ {goal.GroupSize} {goal.Unit}" +
                      $"   <color=#999>· {thisGame} dans cette partie</color>", textStyle);
            y += row;

            float per100 = gamePlacements > 0 ? thisGame * 100f / gamePlacements : 0f;
            int left = Math.Max(0, target - progress);
            string pace = per100 > 0.01f ? $"{per100:0.0} groupes pour 100 tuiles · encore ~{Mathf.CeilToInt(left / per100 * 100)} tuiles"
                                        : "<color=#888>pas encore de groupe validé dans cette partie</color>";
            GUI.Label(new Rect(x, y, w, row), $"<b>Rythme</b> : {pace}", textStyle);
            y += row;

            GUI.Label(new Rect(x, y, w, row),
                      $"<b>Reste</b> : {left} groupe(s)   <color=#999>· la progression est conservée d'une partie à l'autre</color>", textStyle);
            y += row;

            float needTiles = per100 > 0.01f ? left / per100 * 100f : float.MaxValue;
            float confidence = left == 0 ? 1f : per100 > 0.01f ? Mathf.Clamp01((state.TileStack.Height + 60f) / needTiles) : 0.1f;
            ConfidenceRow(x, y, w, row, confidence,
                          left == 0 ? "défi réussi !" : $"{confidence:0%} de finir dans cette partie");
        }

        /// <summary>Placements parfaits d'un coup : record, fleur (anneaux), centre, confiance.</summary>
        private void DrawFlowerCard(Rect card, float pad, float row)
        {
            var (x, w, y) = CardFrame(card, pad, new Color(0.95f, 0.45f, 0.7f, 0.9f));
            int target = Math.Max(1, goal.Target);

            GUI.Label(new Rect(x, y, w, row),
                      $"<b>Record de la partie</b> : <color=#f9c><b>{gameBestPerfects}</b></color> / {target} placements parfaits d'un coup", textStyle);
            y += row;

            if (!goal.FlowerSite.HasValue)
            {
                GUI.Label(new Rect(x, y, w, row), "<color=#888>recherche d'un site de fleur…</color>", textStyle);
                ConfidenceRow(x, y + row * 2, w, row, 0f, "pas de site");
                return;
            }
            Board.FlowerState f = goal.Flower;
            FlowerStateBars(new Rect(x, y + 3 * uiScale, w, row - 6 * uiScale));
            y += row;

            GUI.Label(new Rect(x, y, w, row),
                      $"<b>Site</b> {goal.FlowerSite.Value} ✿   <color=#999>· chance de terminer les anneaux {f.Completion:0%} · tuile du centre {f.CenterChance:0%}</color>", textStyle);
            y += row;

            float progress = (4f * f.Ring1 + 1.5f * f.Ring2) / 42f;
            float reserve = Mathf.Clamp01(state.TileStack.Height / 30f);
            float confidence = gameBestPerfects >= target ? 1f
                : progress * Mathf.Sqrt(f.Completion) * Mathf.Clamp01(f.CenterChance * 3f) * (0.4f + 0.6f * reserve);
            ConfidenceRow(x, y, w, row, confidence, gameBestPerfects >= target ? "défi réussi !"
                : $"{confidence:0%}  (anneaux {progress:0%} · finition {Mathf.Sqrt(f.Completion):0%} · centre {Mathf.Clamp01(f.CenterChance * 3f):0%} · réserve {reserve:0%})");
        }

        /// <summary>
        /// Carte du défi "points en une pose" : record, moyenne, projection du groupe visé et indice de confiance
        /// (taille × fermeture × réserve).
        /// </summary>
        private void DrawAnalystCard(Rect card, float pad, float row)
        {
            var (x, w, y) = CardFrame(card, pad, new Color(0.5f, 0.7f, 1f, 0.9f));
            int target = Math.Max(1, goal.Target);

            // 1. Record
            int bestEver = bestEverPoints?.Value ?? 0;
            GUI.Label(new Rect(x, y, w, row),
                      $"<b>Record de la partie</b> : <color=#fd6><b>{gameBestPoints} pts</b></color> / {target}" +
                      $"   <color=#999>· record absolu {bestEver} pts</color>", textStyle);
            y += row;

            // 2. Moyenne
            float avg = gamePlacements > 0 ? (float)gamePointsSum / gamePlacements : 0f;
            GUI.Label(new Rect(x, y, w, row),
                      $"<b>Moyenne</b> : {avg:0} pts par pose   <color=#999>· {gamePlacements} poses · {gamePointsSum} pts au total</color>", textStyle);
            y += row;

            if (goal.UsesFlower)
            {
                // Les fermetures ne rapportent rien : fleur (7 parfaits) + au moins une quête au centre
                int missing = Math.Max(0, target - goal.ProjectedPoints);
                string need = missing == 0 ? "<color=#7f7>suffit à elle seule</color>"
                    : $"+ {goal.QuestsNeeded} quête(s) réussie(s) par la tuile du centre";
                GUI.Label(new Rect(x, y, w, row),
                          $"<b>Fleur complète</b> ≈ <color=#f9c><b>{goal.ProjectedPoints} pts</b></color> / {target}   <color=#999>· {need}</color>" +
                          (missing > 0 ? $"   <color=#fd6>· {goal.ReservedQuests} quête(s) réservée(s)</color>" : ""), textStyle);
                y += row;
                if (goal.FlowerSite.HasValue) FlowerStateBars(new Rect(x, y + 3 * uiScale, w, row - 6 * uiScale));
                else GUI.Label(new Rect(x, y, w, row), "<color=#888>recherche d'un site de fleur…</color>", textStyle);
                y += row;
                Board.FlowerState f = goal.Flower;
                float ringProgress = (4f * f.Ring1 + 1.5f * f.Ring2) / 42f;
                float questFactor = missing > 0 ? 0.2f + 0.8f * f.QuestReadiness : 1f;
                float flowerConfidence = gameBestPoints >= target ? 1f : !goal.FlowerSite.HasValue ? 0f
                    : ringProgress * Mathf.Sqrt(f.Completion) * Mathf.Clamp01(f.CenterChance * 3f) * questFactor;
                ConfidenceRow(x, y, w, row, flowerConfidence, gameBestPoints >= target ? "défi réussi !"
                    : $"{flowerConfidence:0%}  (anneaux {ringProgress:0%} · finition {Mathf.Sqrt(f.Completion):0%} · centre {Mathf.Clamp01(f.CenterChance * 3f):0%}" +
                      (missing > 0 ? $" · {goal.QuestsNeeded} quête(s) au centre {f.QuestReadiness:0%})" : ")"));
                return;
            }

            // 3. Projection
            int needed = Math.Max(1, goal.SegmentsNeeded);
            string projection = goal.HasTarget && goal.Current.Value > 0
                ? $"<b>Si fermé maintenant</b> : <color={(goal.ProjectedPoints >= target ? "#7f7" : "#fd6")}><b>{goal.ProjectedPoints} pts</b></color> / {target}" +
                  $"   <color=#999>· groupe {goal.GroupType} {goal.Current.Value}/{needed} segments, {goal.Current.Open} bord(s) ouvert(s)</color>"
                : "<b>Si fermé maintenant</b> : <color=#888>pas encore de groupe visé</color>";
            GUI.Label(new Rect(x, y, w, row), projection, textStyle);
            y += row;

            // 4. Confiance = taille × fermeture × réserve
            float size = goal.HasTarget ? Mathf.Clamp01((float)goal.Current.Value / needed) : 0f;
            float sizeFactor = size * size;                                         // les derniers segments sont les plus durs
            float closeFactor = size >= 1f ? 1f / (1f + 0.15f * goal.Current.Open)  // chaque bord à refermer est un risque
                                           : 0.5f;                                  // fermeture encore lointaine
            float reserve = Mathf.Clamp01(goal.StackHeight / 30f);
            float confidence = sizeFactor * closeFactor * (0.4f + 0.6f * reserve);
            if (gameBestPoints >= target) confidence = 1f;
            ConfidenceRow(x, y, w, row, confidence, gameBestPoints >= target ? "défi réussi !"
                : $"{confidence:0%}  (taille {sizeFactor:0%} · fermeture {closeFactor:0%} · réserve {reserve:0%})");
        }

        /// <summary>Détails d'une suggestion, par ordre d'importance (les moins importants sont coupés si trop long).</summary>
        private static string SuggestionTags(PlacementEval e)
        {
            var tags = new List<string>();
            if (e.ReservedQuestsSpent > 0) tags.Add("<color=#f77>gâche une quête réservée</color>");
            if (e.AtOnceKind == 2 && e.HandQuestNearCenter > 0) tags.Add("<color=#f9c>quête reliée au centre ✿</color>");
            if (e.FlowerCenterWasted && e.Points < e.AtOnceTarget) tags.Add($"<color=#f77>grille la fleur ({e.Points} pts)</color>");
            if (e.AtOnceKind == 1 && e.PerfectTriggered >= e.AtOnceTarget) tags.Add($"<color=#7f7><b>✿ {e.PerfectTriggered} parfaits !</b></color>");
            if (e.AtOnceKind == 2 && e.Points >= e.AtOnceTarget) tags.Add($"<color=#7f7><b>{e.Points} pts !</b></color>");
            if (e.QuestsFailed > 0) tags.Add($"<color=#f77>✗{e.QuestsFailed} quête</color>");
            if (e.QuestsFulfilled > 0) tags.Add($"<color=#7f7>✓{e.QuestsFulfilled} quête</color>");
            if (e.ManyActive && e.ManyClosedQualifying > 0) tags.Add($"<color=#7f7>✓{e.ManyClosedQualifying} groupe</color>");
            if (e.TilesGained > 0) tags.Add($"<color=#7cf>+{e.TilesGained} tuile{(e.TilesGained > 1 ? "s" : "")}</color>");
            if (e.GoalActive && e.GoalAfter.Value > e.GoalBefore.Value) tags.Add($"<color=#fd6>objectif +{e.GoalAfter.Value - e.GoalBefore.Value}</color>");
            if (e.ManyActive && e.ManyGrowth > 0) tags.Add($"<color=#fd6>groupes +{e.ManyGrowth}</color>");
            if (e.AtOnceKind == 1 && e.FlowerRuined) tags.Add("<color=#f77>gâche la fleur</color>");
            else if (e.AtOnceKind == 1 && e.FlowerDelta >= 0.5f) tags.Add($"<color=#f9c>fleur +{e.FlowerDelta:0}</color>");
            if (e.QuestsEndangered > 0) tags.Add($"<color=#fb5>⚠ quête</color>");
            if (e.QuestProgress > 0.001f) tags.Add($"quêtes +{e.QuestProgress * 100:0}%");
            if (e.Points >= 100 && !(e.AtOnceKind == 2 && e.Points >= e.AtOnceTarget)) tags.Add($"<color=#fd6>{e.Points} pts</color>");
            if (e.PerfectTriggered >= 2 && e.AtOnceKind != 1) tags.Add($"{e.PerfectTriggered} parfaits");
            return string.Join("  ", tags.Take(4));
        }

        /// <summary>Deux lignes : titre du défi / objectif, puis progression (barre) ou état.</summary>
        private void DrawGoalRows(Rect area)
        {
            float row = area.height / 2;
            var title = new Rect(area.x, area.y, area.width, row);
            var bar = new Rect(area.x, area.y + row + 3 * uiScale, area.width, row - 6 * uiScale);
            Color gold = new Color(0.95f, 0.7f, 0.2f, 0.85f);

            if (goal == null)
            {
                GUI.Label(title, goalAchievedMessage != null ? $"<color=#7f7>{goalAchievedMessage}</color>" : "<color=#888>Aucun défi — F3 pour en choisir un</color>", textStyle);
                return;
            }
            string scale = goal.Scale < 0.999f ? $"  <color=#999>(poids {goal.Scale:0%})</color>" : "";
            GUI.Label(title, $"<color=#fd6><b>{goal.Title}</b></color> — {GoalSummary()}{scale}", textStyle);

            if (goal.AtOnceKind == 1)
            {
                if (!goal.FlowerSite.HasValue) { GUI.Label(bar, "<color=#888>recherche d'un site de fleur…</color>", smallStyle); return; }
                FlowerStateBars(bar);
            }
            else if (goal.AtOnceKind == 2 && goal.UsesFlower)
            {
                if (!goal.FlowerSite.HasValue) { GUI.Label(bar, "<color=#888>recherche d'un site de fleur…</color>", smallStyle); return; }
                FlowerStateBars(bar);
            }
            else if (goal.AtOnceKind == 2)
            {
                float ratio = goal.SegmentsNeeded > 0 ? (float)goal.Current.Value / goal.SegmentsNeeded : 0f;
                ProgressBar(bar, ratio, gold, goal.Current.Value >= goal.SegmentsNeeded
                    ? $"groupe {goal.GroupType} prêt : fermer ses {goal.Current.Open} bord(s) ouvert(s)"
                    : $"groupe {goal.GroupType} : {goal.Current.Value}/{goal.SegmentsNeeded} segments · {goal.Current.Open} bords ouverts");
            }
            else if (goal.Many)
            {
                ProgressBar(bar, (float)goal.GameProgress / Math.Max(1, goal.Target), gold,
                            $"{goal.GameProgress}/{goal.Target} groupes fermés d'au moins {goal.GroupSize} {goal.Unit}");
            }
            else if (!goal.HasTarget)
            {
                GUI.Label(bar, "<color=#888>aucun groupe ouvert pour l'instant</color>", smallStyle);
            }
            else
            {
                bool closing = goal.Current.Value >= goal.Target;
                ProgressBar(bar, closing ? 1f : (float)goal.Current.Value / goal.Target, closing ? new Color(0.3f, 0.75f, 0.4f, 0.85f) : gold,
                            closing ? $"{goal.Current.Value} {goal.Unit} — fermeture : {goal.Current.Open} bord(s) ouvert(s)"
                                    : $"{goal.Current.Value}/{goal.Target} {goal.Unit} · {goal.Current.Open} bords ouverts");
            }
        }

        private string GoalSummary()
        {
            if (goal.AtOnceKind == 1) return $"{goal.Target} placements parfaits d'un coup";
            if (goal.AtOnceKind == 2) return $"{goal.Target} points en une pose";
            if (goal.Many) return $"fermer {goal.Target} groupes {goal.GroupType} ≥ {goal.GroupSize} {goal.Unit}";
            return $"{(goal.Close ? "fermer" : "former")} un groupe {goal.GroupType} de {goal.Target} {goal.Unit}";
        }

        /// <summary>Fleur : trois mini-barres (anneau intérieur, extérieur, chance du centre).</summary>
        private void FlowerStateBars(Rect r)
        {
            float gap = 8 * uiScale, third = (r.width - 2 * gap) / 3;
            Color pink = new Color(0.95f, 0.45f, 0.7f, 0.85f);
            ProgressBar(new Rect(r.x, r.y, third, r.height), goal.Flower.Ring1 / 6f, pink, $"intérieur {goal.Flower.Ring1}/6");
            ProgressBar(new Rect(r.x + third + gap, r.y, third, r.height), goal.Flower.Ring2 / 12f, pink, $"extérieur {goal.Flower.Ring2}/12");
            ProgressBar(new Rect(r.x + 2 * (third + gap), r.y, third, r.height), goal.Flower.CenterChance, new Color(0.5f, 0.7f, 1f, 0.85f),
                        $"centre {goal.Flower.CenterChance:0%}");
        }

        private void DrawOutlined(Rect rect, string text, Color color)
        {
            labelStyle.normal.textColor = Color.black;
            foreach (var o in new[] { new Vector2(-2, 0), new Vector2(2, 0), new Vector2(0, -2), new Vector2(0, 2) })
                GUI.Label(new Rect(rect.x + o.x, rect.y + o.y, rect.width, rect.height), text, labelStyle);
            labelStyle.normal.textColor = color;
            GUI.Label(rect, text, labelStyle);
        }

        /// <summary>Position monde d'une case (copie de GridCalculator.GridToWorldPos).</summary>
        private static Vector3 GridToWorld(Vector2Int p) =>
            new Vector3(p.x * 0.75f, 0f, (p.y - Mathf.Abs(p.x % 2) / 2f) * 0.8660254f);

        private string DepthLabel()
        {
            (int known, int simulated) = DepthChoices[depthChoice];
            if (known + simulated == 0) return "aucune";
            return simulated == 0 ? $"{known} visibles" : $"{known} visibles + {simulated} probables";
        }

        private static string ObjectiveLabel(Objective o)
        {
            switch (o)
            {
                case Objective.Score: return "score";
                case Objective.Quests: return "quêtes";
                case Objective.Compact: return "durer (compact)";
                case Objective.Clean: return "score sans bord raté";
                case Objective.CompactClean: return "durer sans bord raté";
                default: return o.ToString();
            }
        }
    }
}
