using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DorfHelper.Model
{
    /// <summary>Partie Unity de la photo : lien vers les quêtes du jeu (pour l'auto-vérification).</summary>
    internal sealed partial class GameSnapshot
    {
        public Dictionary<QuestModel, QuestWatcher> QuestSources = new Dictionary<QuestModel, QuestWatcher>();
    }

    /// <summary>Objectif de biome choisi par le joueur (ex. fermer un champ de 150 champs) et son état courant.</summary>
    internal sealed class BiomeGoal
    {
        public HashSet<Vector2Int> ExcludedFlowerSites = new HashSet<Vector2Int>(); // fleurs bloquées, à ne plus viser
        public GroupTypeId GroupType = GroupTypeId.Agriculture;
        public string Element = "Field"; // null ou vide : on compte les segments
        public int Target = 150;
        public bool Close = true;        // fermer le groupe (CloseBigGroup) ou seulement l'atteindre (FormBigGroup)
        public string Title;             // titre du défi du jeu, s'il vient de là

        // Mode "plusieurs groupes" (CloseManyGroups / FormManyGroups) : fermer Target groupes d'au moins GroupSize éléments
        public bool Many;
        public int GroupSize;
        public SessionQuest Source;      // défi du jeu (pour lire la progression)
        public int Level = -1;           // palier visé (défis "d'un coup"), -1 : palier courant

        /// <summary>Le palier visé est-il validé dans le jeu ?</summary>
        public bool SourceDone => Source && (Source.CurrentState == RewardState.Completed || (Level >= 0 && Source.CurrentLevelIndex > Level));
        public int GameProgress => Source ? Source.currentProgress : 0;

        // Défis "d'un coup" : 1 = placements parfaits d'un coup, 2 = points en une pose (Target = nombre / points)
        public int AtOnceKind;
        public int SegmentsNeeded; // défi points : taille de groupe (segments) dont la fermeture rapporte la cible
        public int ProjectedPoints;      // défi points : points si le groupe visé était fermé maintenant (ou fleur complète)
        public bool UsesFlower;          // défi points joué avec une fleur (les fermetures ne rapportent rien)
        public int QuestsNeeded;         // quêtes que la tuile du centre doit réussir en plus de la fleur
        public int ReservedQuests;       // quêtes touchant le centre, gardées pour la tuile finale

        // Suivi pour les cartes de défi (réinitialisé à chaque partie)
        public List<(int placed, int value)> History = new List<(int, int)>();
        public int MaxValue;             // plus grande valeur atteinte par le groupe visé dans la partie
        public int StartProgress = -1;   // progression du défi du jeu au début de la partie (défis cumulés)
        public int StackHeight;          // tuiles en pioche au moment de la photo
        public Vector2Int? FlowerSite;   // défi placements parfaits : centre de la fleur en construction
        public Board.FlowerState Flower;

        public bool CountsSegments => string.IsNullOrEmpty(Element);
        public string Unit => CountsSegments ? "segments" : Element;

        // Cible actuelle, mémorisée d'une photo à l'autre par (case, masque de bords) d'un de ses segments
        public (Vector2Int pos, int mask)? Anchor;
        public bool HasTarget, Achieved;
        public QuestState Current;
        public float Scale;

        public override string ToString() =>
            (Title != null ? Title + " : " : "") + (AtOnceKind == 1 ? $"{Target} placements parfaits d'un coup"
                : AtOnceKind == 2 ? $"{Target} points en une seule pose"
                : Many
                ? $"fermer {Target} groupes {GroupType} d'au moins {GroupSize} {Unit}"
                : $"{(Close ? "fermer" : "former")} un groupe {GroupType} de {Target} {Unit}");
    }

    internal static class Snapshotter
    {
        public static GameSnapshot Take(GameState state, int maxStack, BiomeGoal goal = null)
        {
            // La tuile "en main" n'est exclue que si c'est bien un aperçu (juste après une pose,
            // CurrentTile désigne encore la tuile posée pendant quelques frames).
            Tile current = state.TilePlacer.CurrentTile;
            if (current && current.State == TileState.placed) current = null;
            // Quêtes actives sur le plateau + quête portée par la tuile en main (quel que soit son état).
            var watchers = (state.QuestManager.AllQuestWatchers ?? new List<QuestWatcher>())
                .Where(q => q && ((q.Watching && q.State != TileState.topStackPreview)
                                  || (current && q.GetComponentInParent<QuestTile>() == current)))
                .ToList();

            // Types d'éléments suivis = ceux demandés par les quêtes de comptage
            var elementIndex = new Dictionary<ElementType, int>();
            foreach (QuestWatcher q in watchers)
            {
                ElementType et = null;
                try { et = q.GetConditionWatcher(0)?.Condition?.elementType; } catch (System.Exception) { }
                if (et && !elementIndex.ContainsKey(et)) elementIndex[et] = elementIndex.Count;
            }

            // Élément compté par l'objectif de biome (ex. "Field"), retrouvé sur les tuiles posées
            ElementType goalElement = null;
            if (goal != null && !goal.CountsSegments)
            {
                goalElement = state.World.GetAllPlacedTiles()
                    .SelectMany(t => t.AllElementGroupSegments ?? new List<ElementGroupSegment>())
                    .Where(sg => sg && sg.Elements != null)
                    .SelectMany(sg => sg.Elements.Keys)
                    .FirstOrDefault(k => k && k.name == goal.Element);
                if (goalElement && !elementIndex.ContainsKey(goalElement)) elementIndex[goalElement] = elementIndex.Count;
            }

            var board = new Board { ElementKinds = elementIndex.Count };
            // Récompenses en tuiles, lues dans la config du jeu
            RewardSystemConfiguration rc = state.RewardSystem ? state.RewardSystem.Configuration : null;
            if (rc)
            {
                board.PerfectReward = rc.rewardTileClosed ? rc.perfectPlacementTileReward : 0;
                board.QuestReward = rc.gainQuestRewardTiles && rc.questRewardByScore != null
                    ? Mathf.RoundToInt(rc.questRewardByScore.Evaluate(state.RewardSystem.Score)) : 0;
                board.RewardTileFit = rc.rewardTileFit;
                board.FitScoreMethod = (int)rc.tileFitScoreCalculationMethod;
                board.FitScoreModifier = rc.tileFitScoreModifier;
                board.PerfectScore = rc.rewardTileClosed ? rc.perfectPlacementScore : 0;
                board.RewardGroups = rc.rewardCompletedGroups;
                board.GroupScorePow = rc.groupCompletionScorePow;
                board.GroupScoreMultiplier = rc.groupScoreMultiplier;
                board.QuestScore = rc.rewardQuestsWithScore ? rc.questScoreReward : 0;
            }
            var pool = new List<TileSpec>();
            var segIndex = new Dictionary<ElementGroupSegment, int>();

            // Tuiles posées (les tuiles de quête "pré-placées" sont ajoutées plus bas comme fantômes).
            var preplaced = HarmonyLib.Traverse.Create(state.SlotPreviewer).Field("preplacedTiles").GetValue<Dictionary<Vector2Int, Tile>>();
            var allTiles = state.World.GetAllPlacedTiles().ToList();

            foreach (Tile tile in allTiles)
            {
                if (!tile || tile == current) continue;
                TileSpec spec = SpecOf(tile, elementIndex, board.Constraining);
                if (tile.State == TileState.placed) pool.Add(spec);
                int[] segs = board.Apply(spec, tile.GridPos, tile.RotationIndex);
                List<ElementGroupSegment> real = tile.AllElementGroupSegments ?? new List<ElementGroupSegment>();
                for (int i = 0; i < segs.Length && i < real.Count; i++) segIndex[real[i]] = segs[i];
            }

            if (preplaced != null)
                foreach (Tile t in preplaced.Values.Where(t => t && state.World.GetTile(t.GridPos) == null))
                    board.AddGhost(SpecOf(t, elementIndex, board.Constraining), t.GridPos, t.RotationIndex);

            // Les tuiles sont listées à peu près dans l'ordre de pose : on garde les plus récentes.
            const int PoolSize = 80;
            var snap = new GameSnapshot
            {
                Board = board,
                PlacedCount = state.World.GetAllPlacedTiles().Count,
                Pool = pool.Skip(Math.Max(0, pool.Count - PoolSize)).ToList(),
            };
            foreach (QuestWatcher q in watchers)
            {
                QuestModel model;
                try { model = QuestOf(state, board, q, elementIndex); }
                catch (System.Exception e) { Plugin.Log.LogWarning("Quête ignorée : " + e.Message); continue; }
                if (model == null) continue;
                board.Quests.Add(model);
                snap.QuestSources[model] = q;
            }

            snap.Hand = current ? SpecOf(current, elementIndex, board.Constraining) : null;
            board.Hand = snap.Hand;

            if (goal != null && goal.AtOnceKind != 0)
            {
                board.AtOnceKind = goal.AtOnceKind;
                goal.StackHeight = state.TileStack.Height;
                board.AtOnceTarget = goal.Target;
                bool flowerChallenge = goal.AtOnceKind == 1 || board.GroupClosingPoints(1000) == 0;
                board.GoalScale = flowerChallenge ? Solver.FlowerScaleFor(state.TileStack.Height) : Solver.GoalScaleFor(state.TileStack.Height);
                goal.Scale = board.GoalScale;
                goal.HasTarget = true;
                goal.Achieved = goal.SourceDone;
                if (goal.AtOnceKind == 1)
                {
                    // Placements parfaits d'un coup : fleur planifiée, conservée d'une photo à l'autre
                    List<TileSpec> sample = snap.Pool.Count <= 40 ? snap.Pool
                        : Enumerable.Range(0, 40).Select(i => snap.Pool[i * snap.Pool.Count / 40]).ToList();
                    board.ExcludedFlowerSites = goal.ExcludedFlowerSites;
                    board.FlowerSite = board.SelectFlowerSite(sample, goal.FlowerSite);
                    goal.FlowerSite = board.FlowerSite;
                    goal.Flower = board.FlowerSite.HasValue ? board.FlowerAt(board.FlowerSite.Value, sample) : default;
                }
                // Points en une pose, quand les fermetures de groupes ne rapportent rien (rewardCompletedGroups désactivé) :
                // seule une fleur (7 placements parfaits) + une quête au centre peut atteindre la cible.
                goal.UsesFlower = goal.AtOnceKind == 2 && board.GroupClosingPoints(1000) == 0;
                if (goal.UsesFlower)
                {
                    List<TileSpec> sample = snap.Pool.Count <= 40 ? snap.Pool
                        : Enumerable.Range(0, 40).Select(i => snap.Pool[i * snap.Pool.Count / 40]).ToList();
                    board.ExcludedFlowerSites = goal.ExcludedFlowerSites;
                    board.FlowerNeedsQuest = board.FlowerJackpotPoints() < goal.Target;
                    board.FlowerQuestsNeeded = goal.QuestsNeeded = board.QuestScore > 0
                        ? Mathf.CeilToInt((goal.Target - board.FlowerJackpotPoints()) / (float)board.QuestScore) : 99;
                    board.FlowerSite = board.SelectFlowerSite(sample, goal.FlowerSite);
                    board.ReserveQuestsAround(board.FlowerSite);
                    goal.ReservedQuests = board.ReservedQuests.Count;
                    goal.FlowerSite = board.FlowerSite;
                    goal.Flower = board.FlowerSite.HasValue ? board.FlowerAt(board.FlowerSite.Value, sample) : default;
                    goal.ProjectedPoints = board.FlowerJackpotPoints();
                }
                else if (goal.AtOnceKind == 2)
                {
                    // Points en une pose : faire grossir le plus grand groupe ouvert puis le fermer d'un coup.
                    // Terrain verrouillé tant que le groupe visé reste ouvert.
                    int previous = goal.Anchor.HasValue ? board.FindSeg(goal.Anchor.Value.pos, (byte)goal.GroupType, goal.Anchor.Value.mask) : -1;
                    int type = previous >= 0 && board.IsSegOpen(previous) ? (int)goal.GroupType : board.LargestOpenGroupType();
                    if (type != (int)goal.GroupType) goal.Anchor = null;
                    goal.SegmentsNeeded = board.SegmentsForPoints(goal.Target);
                    if (type >= 0)
                    {
                        List<TileSpec> sample = snap.Pool.Count <= 40 ? snap.Pool
                            : Enumerable.Range(0, 40).Select(i => snap.Pool[i * snap.Pool.Count / 40]).ToList();
                        int currentAnchor = goal.Anchor.HasValue ? board.FindSeg(goal.Anchor.Value.pos, (byte)type, goal.Anchor.Value.mask) : -1;
                        board.Goal = new QuestModel
                        {
                            Label = goal.ToString(), Kind = QuestKind.CountSegments, Comparison = Comparison.MoreThan,
                            GroupType = (byte)type, ElementIndex = -1, Target = goal.SegmentsNeeded,
                            AnchorSegs = board.SelectGoalGroup((byte)type, -1, sample, currentAnchor),
                        };
                        board.GoalClose = true;
                        goal.Anchor = board.Goal.AnchorSegs.Length > 0 ? board.DescribeSeg(board.Goal.AnchorSegs[0]) : ((Vector2Int, int)?)null;
                        goal.Current = board.Goal.AnchorSegs.Length > 0 ? board.QuestStateOf(board.Goal) : default;
                        goal.GroupType = (GroupTypeId)type;
                        goal.ProjectedPoints = board.GroupClosingPoints(goal.Current.Value);
                    }
                }
            }
            else if (goal != null && goal.Many && (goal.CountsSegments || goalElement))
            {
                board.ManyType = (int)goal.GroupType;
                board.ManyElement = goal.CountsSegments ? -1 : elementIndex[goalElement];
                board.ManySize = goal.GroupSize;
                board.GoalScale = Solver.GoalScaleFor(state.TileStack.Height);
                goal.Scale = board.GoalScale;
                goal.HasTarget = true;
                goal.Achieved = goal.Source && goal.Source.CurrentState == RewardState.Completed;
            }
            else if (goal != null && (goal.CountsSegments || goalElement))
            {
                int elemIdx = goal.CountsSegments ? -1 : elementIndex[goalElement];
                byte type = (byte)goal.GroupType;
                List<TileSpec> sample = snap.Pool.Count <= 40 ? snap.Pool
                    : Enumerable.Range(0, 40).Select(i => snap.Pool[i * snap.Pool.Count / 40]).ToList();
                int currentAnchor = goal.Anchor.HasValue ? board.FindSeg(goal.Anchor.Value.pos, type, goal.Anchor.Value.mask) : -1;
                board.Goal = new QuestModel
                {
                    Label = goal.ToString(), Comparison = Comparison.MoreThan,
                    Kind = goal.CountsSegments ? QuestKind.CountSegments : QuestKind.CountElements,
                    GroupType = type, ElementIndex = elemIdx, Target = goal.Target,
                    AnchorSegs = board.SelectGoalGroup(type, elemIdx, sample, currentAnchor),
                };
                board.GoalScale = Solver.GoalScaleFor(state.TileStack.Height);
                board.GoalClose = goal.Close;
                board.GoalElementsPerSegment = Board.ElementsPerSegment(snap.Pool, type, elemIdx);
                goal.Anchor = board.Goal.AnchorSegs.Length > 0 ? board.DescribeSeg(board.Goal.AnchorSegs[0]) : ((Vector2Int, int)?)null;
                goal.Current = board.Goal.AnchorSegs.Length > 0 ? board.QuestStateOf(board.Goal) : default;
                goal.HasTarget = board.Goal.AnchorSegs.Length > 0;
                goal.Achieved = (goal.Close ? board.LargestClosedGroup(type, elemIdx) : board.LargestGroup(type, elemIdx)) >= goal.Target;
                goal.Scale = board.GoalScale;
            }
            // stack[0] est la tuile en main ; les suivantes commencent à 1
            for (int i = 1; i <= maxStack && i < state.TileStack.Height; i++)
            {
                // Les tuiles du fond de la pioche ne sont pas encore construites (bords nuls) : on s'arrête là.
                Tile t = state.TileStack.GetStackedTile(i);
                if (!t) break;
                try { snap.Stack.Add(SpecOf(t, elementIndex, board.Constraining)); }
                catch (System.NullReferenceException) { break; }
            }
            snap.ValidSlots = state.SlotPreviewer.AllValidTileSlots.Select(s => s.GridPos).ToList();
            return snap;
        }

        public static TileSpec SpecOf(Tile tile, Dictionary<ElementType, int> elementIndex, bool[] constraining)
        {
            var spec = new TileSpec { Name = tile.name };
            for (int i = 0; i < 6; i++)
            {
                List<GroupType> any = tile.GetEdgeTypes(i, Space.Self);
                foreach (GroupType g in any)
                    if (g && g.constraining) constraining[(int)g.id] = true;
                spec.Edges[i] = new EdgeSpec
                {
                    Any = any.Where(g => g).Select(g => (byte)g.id).ToArray(),
                    NonHybrid = tile.GetEdgeTypes(i, Space.Self, TileEdgeType.NonHybrid).Where(g => g).Select(g => (byte)g.id).ToArray(),
                    Hybrid = tile.GetHybridEdges(i, Space.Self).Count > 0,
                };
            }

            var segs = new List<SegSpec>();
            foreach (ElementGroupSegment s in tile.AllElementGroupSegments ?? new List<ElementGroupSegment>())
            {
                var elems = new int[elementIndex.Count];
                if (s && s.Elements != null)
                    foreach (var kv in s.Elements)
                        if (kv.Key && elementIndex.TryGetValue(kv.Key, out int idx)) elems[idx] += kv.Value;
                segs.Add(new SegSpec
                {
                    Type = s && s.GroupType ? (byte)s.GroupType.id : (byte)0,
                    Edges = s ? s.Edges.ToArray() : new int[0],
                    Elements = elems,
                });
            }
            spec.Segs = segs.ToArray();
            return spec;
        }

        private static int SegIndexOf(Board board, ElementGroupSegment s)
        {
            int mask = 0;
            foreach (int e in s.GetEdges(Space.World)) mask |= 1 << e;
            return board.FindSeg(s.Tile.GridPos, s.GroupType ? (byte)s.GroupType.id : (byte)0, mask);
        }

        private static QuestModel QuestOf(GameState state, Board board, QuestWatcher q, Dictionary<ElementType, int> elementIndex)
        {
            if (!(q.GetConditionWatcher(0) is GroupConditionWatcher cw) || cw.Condition == null) return null;
            // Quête qui ne surveille (plus) aucun groupe (terminée en attente, ou portée par une tuile de la pioche) :
            // aucune pose ne peut la faire bouger, on l'ignore.
            if (cw.WatchedGroups == null || cw.WatchedGroups.Count == 0)
            {
                QuestTile qt = q.GetComponentInParent<QuestTile>();
                if (!(qt && qt == state.TilePlacer.CurrentTile)) return null;
            }
            QuestCondition c = cw.Condition;

            // Ancres = segments des groupes surveillés, posés sur la tuile de quête (ou à défaut sur ses voisines).
            // On ignore les segments de la tuile d'aperçu, qui peut avoir fusionné des groupes.
            Tile currentTile = state.TilePlacer.CurrentTile;
            if (currentTile && currentTile.State == TileState.placed) currentTile = null;
            List<ElementGroupSegment> allWatched = (cw.WatchedGroups ?? new List<ElementGroup>())
                .Where(g => g).SelectMany(g => g.Segments).Where(s => s && s.Tile).ToList();
            List<ElementGroupSegment> watched = allWatched.Where(s => s.Tile != currentTile).ToList();
            // La tuile posée est un duplicata : on prend la QuestTile qui porte réellement ce watcher.
            QuestTile questTile = q.GetComponentInParent<QuestTile>() ?? q.QuestTile;
            bool onHand = currentTile && (questTile == currentTile || q.QuestTile == currentTile);
            var anchors = watched.Where(s => s.Tile == questTile || s.Tile == q.QuestTile).ToList();
            if (anchors.Count == 0 && questTile && questTile.NeighborTiles != null)
                anchors = watched.Where(s => s.Tile && questTile.NeighborTiles.Contains(s.Tile)).ToList();
            if (anchors.Count == 0 && !onHand) anchors = watched; // dernier recours : tous les segments surveillés
            if (onHand) anchors.Clear();

            var model = new QuestModel
            {
                Label = $"{c.conditionType} {(c.groupType ? c.groupType.id.ToString() : "?")} {c.equalityComparer} {cw.TargetValue}",
                Kind = c.conditionType == QuestConditionType.CountElements ? QuestKind.CountElements
                    : c.conditionType == QuestConditionType.CountSegments ? QuestKind.CountSegments
                    : c.conditionType == QuestConditionType.CloseGroup ? QuestKind.CloseGroup
                    : QuestKind.Unknown,
                Comparison = c.equalityComparer == EqualityComparison.Exactly ? Comparison.Exactly
                    : c.equalityComparer == EqualityComparison.FewerThan ? Comparison.FewerThan
                    : Comparison.MoreThan,
                GroupType = c.groupType ? (byte)c.groupType.id : (byte)0,
                ElementIndex = c.elementType && elementIndex.TryGetValue(c.elementType, out int ei) ? ei : -1,
                Target = cw.TargetValue,
                AnchorSegs = anchors.Select(s => SegIndexOf(board, s)).Where(i => i >= 0).Distinct().ToArray(),
            };
            if (onHand)
            {
                // Quête de la tuile en main : ancrée sur ses propres segments (ceux surveillés dans l'aperçu).
                List<ElementGroupSegment> handSegs = currentTile.AllElementGroupSegments ?? new List<ElementGroupSegment>();
                model.HandSegs = allWatched.Where(s => s.Tile == currentTile).Select(s => handSegs.IndexOf(s)).Where(i => i >= 0).Distinct().ToArray();
                if (model.HandSegs.Length == 0) // aperçu pas encore posé sur une case : on prend les segments du bon type
                    model.HandSegs = Enumerable.Range(0, handSegs.Count).Where(i => handSegs[i] && handSegs[i].GroupType == c.groupType).ToArray();
                return model.Kind == QuestKind.Unknown ? null : model;
            }
            if (model.AnchorSegs.Length == 0)
                Plugin.Log.LogWarning($"Quête sans ancre [{model.Label}] : {cw.WatchedGroups?.Count ?? 0} groupes, " +
                                      $"{watched.Count} segments surveillés, tuile de quête {(questTile ? questTile.GridPos.ToString() : "?")}");
            return model.Kind == QuestKind.Unknown ? null : model;
        }
    }
}
