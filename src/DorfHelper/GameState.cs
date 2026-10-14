using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DorfHelper
{
    /// <summary>
    /// Accès aux objets du jeu (trouvés dans la scène) et capture de l'état courant.
    /// </summary>
    internal class GameState
    {
        public TilePlacer TilePlacer { get; private set; }
        public TileSlotPreviewer SlotPreviewer { get; private set; }
        public TileStack TileStack { get; private set; }
        public QuestManager QuestManager { get; private set; }
        public World World { get; private set; }
        public InputRouter InputRouter { get; private set; }
        public RewardSystem RewardSystem { get; private set; }

        public Camera Camera => OverwritingSingleton<IngameUi>.Instance ? OverwritingSingleton<IngameUi>.Instance.mainCamera : Camera.main;

        public bool IsReady => TilePlacer && SlotPreviewer && TileStack && QuestManager && World && InputRouter;

        /// <summary>Cherche les composants du jeu dans la scène. Renvoie true s'ils sont tous trouvés.</summary>
        public bool TryResolve()
        {
            if (!TilePlacer) TilePlacer = UnityEngine.Object.FindObjectOfType<TilePlacer>();
            if (!SlotPreviewer) SlotPreviewer = UnityEngine.Object.FindObjectOfType<TileSlotPreviewer>();
            if (!TileStack) TileStack = UnityEngine.Object.FindObjectOfType<TileStack>();
            // QuestManager est un ScriptableObject (asset), pas un composant de la scène :
            // on le récupère via le champ privé de TilePlacer.
            if (!QuestManager && TilePlacer) QuestManager = Traverse.Create(TilePlacer).Field("questManager").GetValue<QuestManager>();
            if (!World) World = UnityEngine.Object.FindObjectOfType<World>();
            if (!InputRouter && TilePlacer) InputRouter = Traverse.Create(TilePlacer).Field("inputRouter").GetValue<InputRouter>();
            if (!RewardSystem && TilePlacer) RewardSystem = Traverse.Create(TilePlacer).Field("rewardSystem").GetValue<RewardSystem>();
            return IsReady;
        }

        public string MissingComponents()
        {
            var missing = new List<string>();
            if (!TilePlacer) missing.Add(nameof(TilePlacer));
            if (!SlotPreviewer) missing.Add(nameof(TileSlotPreviewer));
            if (!TileStack) missing.Add(nameof(TileStack));
            if (!QuestManager) missing.Add(nameof(QuestManager));
            if (!World) missing.Add(nameof(World));
            if (!InputRouter) missing.Add(nameof(InputRouter));
            return string.Join(", ", missing);
        }

        public object Snapshot()
        {
            Tile current = TilePlacer.CurrentTile;
            return new
            {
                time = DateTime.Now.ToString("o"),
                placedTiles = World.TotalTileCount,
                stackHeight = TileStack.Height,
                currentTile = current ? DescribeTile(current) : null,
                nextTile = SafeStackedTile(1) is Tile next ? DescribeTile(next) : null,
                validSlots = SlotPreviewer.AllValidTileSlots.Select(s => new[] { s.GridPos.x, s.GridPos.y }).ToList(),
                quests = (QuestManager.AllQuestWatchers ?? new List<QuestWatcher>())
                    .Where(q => q && q.State != TileState.topStackPreview)
                    .Select(DescribeQuest)
                    .ToList(),
            };
        }

        private Tile SafeStackedTile(int index)
        {
            try { return TileStack.Height > index ? TileStack.GetStackedTile(index) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Bords de la tuile dans sa rotation de base (Space.Self) : 6 listes de types de groupe.</summary>
        private static object DescribeTile(Tile tile)
        {
            var edges = new List<List<string>>();
            for (int i = 0; i < 6; i++)
            {
                edges.Add(tile.GetEdgeTypes(i, Space.Self).Select(g => g ? g.id.ToString() : "?").ToList());
            }
            return new
            {
                name = tile.name,
                quest = tile is QuestTile,
                rotation = tile.RotationIndex,
                edges,
            };
        }

        private static object DescribeQuest(QuestWatcher q)
        {
            QuestConditionWatcher cw = null;
            try { cw = q.GetConditionWatcher(0); } catch (Exception) { }
            QuestCondition cond = cw?.Condition;
            return new
            {
                pos = q.QuestTile ? new[] { q.QuestTile.GridPos.x, q.QuestTile.GridPos.y } : null,
                type = cond?.conditionType.ToString(),
                group = cond?.groupType ? cond.groupType.id.ToString() : null,
                comparison = cond?.equalityComparer.ToString(),
                target = cw?.TargetValue,
                current = cw?.CurrentValue,
                remaining = cw?.RemainingValue,
                status = q.CurrentFulfillmentStatus.ToString(),
            };
        }

        /// <summary>L'AutoPlacer de debug laissé par les devs : existe-t-il dans la scène, et avec quels poids ?</summary>
        public static string DescribeAutoPlacer()
        {
            var ap = UnityEngine.Object.FindObjectOfType<AutoPlacer>(true);
            if (!ap) return "AutoPlacer absent de la scène";
            var t = Traverse.Create(ap);
            string[] fields =
            {
                "scorePerPerfectEdge", "penaltyPerUnfittingEdge", "scorePerQuestValue", "penaltyPerQuestFailed",
                "scorePerQuestFulfilled", "tileStackCountToPrioritizeQuests",
            };
            return "AutoPlacer trouvé (actif=" + ap.isActiveAndEnabled + ") : "
                + string.Join(", ", fields.Select(f => f + "=" + t.Field(f).GetValue()));
        }
    }
}
