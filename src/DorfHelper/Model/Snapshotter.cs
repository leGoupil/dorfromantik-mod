using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DorfHelper.Model
{
    /// <summary>Unity side of the snapshot: links to the game's quests (for self-checks).</summary>
    internal sealed partial class GameSnapshot
    {
        public Dictionary<QuestModel, QuestWatcher> QuestSources = new Dictionary<QuestModel, QuestWatcher>();
    }

    /// <summary>Biome goal chosen by the player (e.g. close a field group of 150 fields) and its current state.</summary>
    internal sealed class BiomeGoal
    {
        public HashSet<Vector2Int> ExcludedFlowerSites = new HashSet<Vector2Int>(); // blocked flowers, no longer to be targeted
        public GroupTypeId GroupType = GroupTypeId.Agriculture;
        public string Element = "Field"; // null or empty: count segments
        public int Target = 150;
        public bool Close = true;        // close the group (CloseBigGroup) or just reach the size (FormBigGroup)
        public string Title;             // title of the game challenge, if it comes from there

        // "Many groups" mode (CloseManyGroups / FormManyGroups): close Target groups of at least GroupSize elements
        public bool Many;
        public int GroupSize;
        public SessionQuest Source;      // game challenge (to read its progress)
        public int Level = -1;           // targeted level ("at once" challenges), -1: current level

        /// <summary>Has the targeted level been completed in the game?</summary>
        public bool SourceDone => Source && (Source.CurrentState == RewardState.Completed || (Level >= 0 && Source.CurrentLevelIndex > Level));
        public int GameProgress => Source ? Source.currentProgress : 0;

        // "At once" challenges: 1 = perfect placements at once, 2 = points in a single placement (Target = count / points)
        public int AtOnceKind;
        public int SegmentsNeeded; // points challenge: group size (segments) whose closing yields the target
        public int ProjectedPoints;      // points challenge: points if the targeted group were closed now (or the flower completed)
        public bool UsesFlower;          // points challenge played with a flower (closing groups yields nothing)
        public int QuestsNeeded;         // quests the center tile must fulfill in addition to the flower
        public int ReservedQuests;       // quests touching the center, kept for the final tile

        // Tracking for the challenge cards (reset every game)
        public List<(int placed, int value)> History = new List<(int, int)>();
        public int MaxValue;             // highest value reached by the targeted group this game
        public int StartProgress = -1;   // game challenge progress at the start of the game (cumulative challenges)
        public int StackHeight;          // tiles in the stack at snapshot time
        public Vector2Int? FlowerSite;   // perfect placements challenge: center of the flower under construction
        public Board.FlowerState Flower;

        public bool CountsSegments => string.IsNullOrEmpty(Element);
        public string Unit => CountsSegments ? "segments" : Element;

        // Current target, remembered across snapshots by (slot, edge mask) of one of its segments
        public (Vector2Int pos, int mask)? Anchor;
        public bool HasTarget, Achieved;
        public QuestState Current;
        public float Scale;

        public override string ToString() =>
            (Title != null ? Title + ": " : "") + (AtOnceKind == 1 ? $"{Target} perfect placements at once"
                : AtOnceKind == 2 ? $"{Target} points in a single placement"
                : Many
                ? $"close {Target} {GroupType} groups of at least {GroupSize} {Unit}"
                : $"{(Close ? "close" : "form")} a {GroupType} group of {Target} {Unit}");
    }

    internal static class Snapshotter
    {
        public static GameSnapshot Take(GameState state, int maxStack, BiomeGoal goal = null)
        {
            // The "hand" tile is only excluded if it really is a preview (right after a placement,
            // CurrentTile still points to the placed tile for a few frames).
            Tile current = state.TilePlacer.CurrentTile;
            if (current && current.State == TileState.placed) current = null;
            // Active quests on the board + the quest carried by the hand tile (whatever its state).
            var watchers = (state.QuestManager.AllQuestWatchers ?? new List<QuestWatcher>())
                .Where(q => q && ((q.Watching && q.State != TileState.topStackPreview)
                                  || (current && q.GetComponentInParent<QuestTile>() == current)))
                .ToList();

            // Tracked element types = those requested by counting quests
            var elementIndex = new Dictionary<ElementType, int>();
            foreach (QuestWatcher q in watchers)
            {
                ElementType et = null;
                try { et = q.GetConditionWatcher(0)?.Condition?.elementType; } catch (System.Exception) { }
                if (et && !elementIndex.ContainsKey(et)) elementIndex[et] = elementIndex.Count;
            }

            // Element counted by the biome goal (e.g. "Field"), looked up on placed tiles
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
            // Tile rewards, read from the game config
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

            // Placed tiles ("pre-placed" quest tiles are added further down as ghosts).
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

            // Tiles are listed roughly in placement order: keep the most recent ones.
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
                catch (System.Exception e) { Plugin.Log.LogWarning("Quest skipped: " + e.Message); continue; }
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
                    // Perfect placements at once: planned flower, kept across snapshots
                    List<TileSpec> sample = snap.Pool.Count <= 40 ? snap.Pool
                        : Enumerable.Range(0, 40).Select(i => snap.Pool[i * snap.Pool.Count / 40]).ToList();
                    board.ExcludedFlowerSites = goal.ExcludedFlowerSites;
                    board.FlowerSite = board.SelectFlowerSite(sample, goal.FlowerSite);
                    goal.FlowerSite = board.FlowerSite;
                    goal.Flower = board.FlowerSite.HasValue ? board.FlowerAt(board.FlowerSite.Value, sample) : default;
                }
                // Points in a single placement, when closing groups yields nothing (rewardCompletedGroups disabled):
                // only a flower (7 perfect placements) + a quest at the center can reach the target.
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
                    // Points in a single placement: grow the largest open group, then close it in one move.
                    // Terrain is locked as long as the targeted group stays open.
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
            // stack[0] is the hand tile; the next ones start at 1
            for (int i = 1; i <= maxStack && i < state.TileStack.Height; i++)
            {
                // Tiles at the bottom of the stack are not built yet (null edges): stop here.
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
            // Quest that watches no group (anymore) (completed and pending, or carried by a stack tile):
            // no placement can change it, so skip it.
            if (cw.WatchedGroups == null || cw.WatchedGroups.Count == 0)
            {
                QuestTile qt = q.GetComponentInParent<QuestTile>();
                if (!(qt && qt == state.TilePlacer.CurrentTile)) return null;
            }
            QuestCondition c = cw.Condition;

            // Anchors = segments of the watched groups lying on the quest tile (or failing that, on its neighbors).
            // Ignore the preview tile's segments, which may have merged groups.
            Tile currentTile = state.TilePlacer.CurrentTile;
            if (currentTile && currentTile.State == TileState.placed) currentTile = null;
            List<ElementGroupSegment> allWatched = (cw.WatchedGroups ?? new List<ElementGroup>())
                .Where(g => g).SelectMany(g => g.Segments).Where(s => s && s.Tile).ToList();
            List<ElementGroupSegment> watched = allWatched.Where(s => s.Tile != currentTile).ToList();
            // The placed tile is a duplicate: take the QuestTile that actually carries this watcher.
            QuestTile questTile = q.GetComponentInParent<QuestTile>() ?? q.QuestTile;
            bool onHand = currentTile && (questTile == currentTile || q.QuestTile == currentTile);
            var anchors = watched.Where(s => s.Tile == questTile || s.Tile == q.QuestTile).ToList();
            if (anchors.Count == 0 && questTile && questTile.NeighborTiles != null)
                anchors = watched.Where(s => s.Tile && questTile.NeighborTiles.Contains(s.Tile)).ToList();
            if (anchors.Count == 0 && !onHand) anchors = watched; // last resort: all watched segments
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
                // Hand tile quest: anchored on its own segments (those watched in the preview).
                List<ElementGroupSegment> handSegs = currentTile.AllElementGroupSegments ?? new List<ElementGroupSegment>();
                model.HandSegs = allWatched.Where(s => s.Tile == currentTile).Select(s => handSegs.IndexOf(s)).Where(i => i >= 0).Distinct().ToArray();
                if (model.HandSegs.Length == 0) // preview not on a slot yet: take the segments of the right type
                    model.HandSegs = Enumerable.Range(0, handSegs.Count).Where(i => handSegs[i] && handSegs[i].GroupType == c.groupType).ToArray();
                return model.Kind == QuestKind.Unknown ? null : model;
            }
            if (model.AnchorSegs.Length == 0)
                Plugin.Log.LogWarning($"Quest without anchor [{model.Label}]: {cw.WatchedGroups?.Count ?? 0} groups, " +
                                      $"{watched.Count} watched segments, quest tile {(questTile ? questTile.GridPos.ToString() : "?")}");
            return model.Kind == QuestKind.Unknown ? null : model;
        }
    }
}
