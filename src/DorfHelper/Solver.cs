using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DorfHelper.Model;
using UnityEngine;

namespace DorfHelper
{
    internal enum Objective
    {
        Score,   // perfect edges, quests as a bonus
        Quests,  // quests first
        Compact, // fills holes, keeps a clean frontier to make the game last
        Clean,        // max score, but no mismatched edge (unless no clean placement exists)
        CompactClean, // last long, but no mismatched edge (unless no clean placement exists)
    }

    internal partial class Suggestion
    {
        public Vector2Int Pos;
        public int Rotation;
        public float Immediate;
        public float Future;
        public float Score => Immediate + Future;
        public PlacementEval Eval;
    }

    internal sealed class SearchResult
    {
        public List<Suggestion> Suggestions = new List<Suggestion>();
        public string Stats;
    }

    internal static partial class Solver
    {
        public const float Discount = 0.85f;
        private const int FirstMoveCandidates = 10;

        /// <summary>"No mismatched edge" objectives: a mismatched edge costs more than any placement can earn.</summary>
        public static bool IsStrict(Objective o) => o == Objective.Clean || o == Objective.CompactClean;

        /// <summary>Objectives that account for potential (future perfect placements).</summary>
        public static bool UsesPotential(Objective o) => o == Objective.Compact || o == Objective.CompactClean;

        /// <summary>Immediate score of a placement for the given objective.</summary>
        private const float TileValue = 30f;      // value of a gained tile, in rating points
        private const float PotentialWeight = 0.8f; // a "likely" perfect placement is worth slightly less than a secured one

        public static float ScoreFor(PlacementEval e, Objective objective, int questReward = 3, int perfectReward = 1)
        {
            // Base = weights of the devs' AutoPlacer: +10 per perfect edge, -30 per mismatched edge.
            float s = 10f * e.Perfect - 30f * e.Mismatched;
            if (e.PerfectPlacement) s += 20f;

            switch (objective)
            {
                case Objective.Score:
                    s += 40f * e.QuestsFulfilled - 80f * e.QuestsFailed + 20f * e.QuestProgress;
                    break;
                case Objective.Quests:
                    s += 120f * e.QuestsFulfilled - 150f * e.QuestsFailed + 60f * e.QuestProgress;
                    break;
                case Objective.Clean:
                    // Near-strict constraint: a mismatched edge costs more than any placement can earn.
                    s += 40f * e.QuestsFulfilled - 80f * e.QuestsFailed + 20f * e.QuestProgress;
                    break;
                case Objective.Compact:
                case Objective.CompactClean:
                    // Lasting = gaining tiles: perfect placements (the placed tile and completed neighbors)
                    // and fulfilled quests. A failed quest is a lost reward.
                    s += TileValue * e.TilesGained
                         - 0.7f * TileValue * questReward * e.QuestsFailed
                         + 0.8f * TileValue * questReward * e.QuestProgress
                         - 0.3f * TileValue * questReward * e.QuestsEndangered
                         + PotentialWeight * TileValue * perfectReward * e.Potential
                         + 8f * e.Neighbors;
                    if (e.Neighbors <= 1) s -= 20f; // avoid stretching the frontier
                    break;
            }
            if (IsStrict(objective)) s -= 1000f * e.Mismatched;
            if (e.GoalActive) s += GoalScore(e);
            if (e.ManyActive) s += e.GoalScale * ManyScore(e);
            if (e.AtOnceKind == 1)
            {
                if (e.PerfectTriggered >= e.AtOnceTarget) s += 5000f;           // challenge completed
                else if (e.FlowerRuined) s -= RuinPenalty(e);                    // flower ruined (always active)
                else s += e.GoalScale * FlowerWeight * e.FlowerDelta;           // flower progress
            }
            else if (e.AtOnceKind == 2)
            {
                // Complete flower "wasted" at the center without reaching the target: forbidden, unless the stack is nearly empty.
                if (e.FlowerCenterWasted && e.Points < e.AtOnceTarget && e.GoalScale > 0f) s -= CenterWastePenalty;
                s -= ReservedQuestPenalty * e.ReservedQuestsSpent;              // quest reserved for the center, wasted elsewhere
                s += ReservedProgressWeight * e.ReservedProgress;               // prepare reserved quests, wherever they are
                // A new quest touching the center (quest tile placed on a group connected to the center), without fulfilling it right away
                if (e.HandQuestNearCenter > 0 && e.QuestsFulfilled == 0) s += HandQuestNearCenterBonus * e.HandQuestNearCenter;
                if (e.Points >= e.AtOnceTarget) s += 5000f;                     // challenge completed
                else if (e.FlowerRuined) s -= RuinPenalty(e);                    // flower ruined (including center without reaching the target)
                else s += e.GoalScale * FlowerWeight * e.FlowerDelta;           // flower progress (if a flower is used)
            }
            return s;
        }

        // Biome goal: grow up to the target without closing the group, then close it.
        public static float GoalGrowWeight = 60f;    // per element added to the group
        public static float GoalCloseWeight = 25f;  // per open edge closed, once the target is reached
        public static int GoalSafeOpenEdges = 4;    // below this, the group risks being closed too early
        public static float GoalEdgeLossWeight = 40f; // per open edge of the group closed without extending it
        public static float GoalProspectWeight = 150f; // per "safe slot" for growth gained or lost

        // Goal weighting by remaining tiles: 0 below GoalMinStack tiles in the stack, 1 above GoalFullStack.
        public static int GoalMinStack = 15;
        public static int GoalFullStack = 40;

        // Flowers ("at once" challenges): building costs little survival, so we cut off much later.
        public static int FlowerMinStack = 5;
        public static int FlowerFullStack = 20;

        public static float FlowerScaleFor(int stackHeight) =>
            Mathf.Clamp01((float)(stackHeight - FlowerMinStack) / (FlowerFullStack - FlowerMinStack));

        public static float GoalScaleFor(int stackHeight) =>
            GoalFullStack <= GoalMinStack ? 1f : Mathf.Clamp01((float)(stackHeight - GoalMinStack) / (GoalFullStack - GoalMinStack));

        public static float FlowerWeight = 100f;        // per progress point of the planned flower
        public static float FlowerRuinPenalty = 400f;  // ruining the flower under construction
        public static float ReservedQuestPenalty = 400f; // completing elsewhere a quest reserved for the center tile
        public static float FlowerRuinPerProgress = 120f; // per progress point (2 × inner + outer) of the ruined flower

        /// <summary>Ruining a flower costs more the further along it was.</summary>
        private static float RuinPenalty(PlacementEval e) => FlowerRuinPenalty + FlowerRuinPerProgress * e.FlowerRuinedProgress;

        public static float CenterWastePenalty = 3000f;  // placing at the center of a complete flower without reaching the target
        public static float ReservedProgressWeight = 400f;   // per reserved quest brought from 0 to 100% of its target (without completing it)
        public static float HandQuestNearCenterBonus = 600f; // new quest connected to the flower center

        // "Many groups" goal
        public static float ManyClosedWeight = 600f;  // per closed group that is large enough
        public static float ManySmallPenalty = 40f;   // per closed group that is too small
        public static float ManyGrowthWeight = 25f;   // per element toward the target size (capped per group)
        public static float ManyCloseWeight = 40f;    // per edge closed on a group that is already large enough

        public static float ManyScore(PlacementEval e) =>
            ManyClosedWeight * e.ManyClosedQualifying
            - ManySmallPenalty * e.ManyClosedSmall
            + ManyGrowthWeight * e.ManyGrowth
            + ManyCloseWeight * e.ManyCloseProgress;

        /// <summary>
        /// Biome goal score. Bonuses (growth, prospects, closing) are scaled by the remaining tiles
        /// (e.GoalScale); group protections (don't close it too early, don't remove its open edges)
        /// always stay active, otherwise the bot destroys its own group when tiles run low.
        /// </summary>
        public static float GoalScore(PlacementEval e)
        {
            QuestState before = e.GoalBefore, after = e.GoalAfter;
            int target = Math.Max(1, e.GoalTarget);
            float bonus = 0f, protection = 0f;
            if (after.Value < target)
            {
                bonus += GoalGrowWeight / e.GoalElementsPerSegment * (after.Value - before.Value); // per "segment" added
                bonus += GoalProspectWeight * e.GoalProspectDelta;
                bonus += 4f * (Math.Min(after.Open, 8) - Math.Min(before.Open, 8)); // keep room to grow

                if (after.Open == 0) protection -= 3000f;                           // closed before the target
                else if (after.Open <= GoalSafeOpenEdges && after.Open < before.Open)
                    protection -= 80f * (GoalSafeOpenEdges + 1 - after.Open);       // increasingly at risk
                // Each open edge lost without growth is one less chance to grow.
                if (after.Value == before.Value && after.Open < before.Open) protection -= GoalEdgeLossWeight * (before.Open - after.Open);
            }
            else if (!e.GoalClose)
            {
                if (before.Value < target) bonus += 3000f;                          // "form": goal reached
            }
            else
            {
                if (before.Value < target) bonus += 300f;                           // target reached
                bonus += GoalCloseWeight * (before.Open - after.Open);
                if (after.Open == 0) bonus += 3000f;                                // group closed: goal completed
            }
            return e.GoalScale * bonus + protection;
        }

        /// <summary>
        /// Full search (background thread): every placement of the tile in hand, then for the best ones,
        /// a greedy simulation of what follows: the `depth` visible tiles of the stack, then `simulated` likely
        /// tiles drawn from the tiles seen so far. Several draws (the same for all candidates) are averaged;
        /// their number adapts to the time budget.
        /// </summary>
        public static SearchResult Run(GameSnapshot snap, Objective objective, int depth, int simulated, CancellationToken ct)
        {
            const int MaxSamples = 16;
            const long BudgetMs = 1500;

            var sw = Stopwatch.StartNew();
            var result = new SearchResult();
            Board board = snap.Board;
            if (snap.Hand == null) return result;

            // Sample of "typical" tiles to estimate the potential of empty slots.
            const int SampleSize = 40;
            List<TileSpec> sample = snap.Pool.Count <= SampleSize
                ? snap.Pool
                : Enumerable.Range(0, SampleSize).Select(i => snap.Pool[i * snap.Pool.Count / SampleSize]).ToList();
            bool usePotential = UsesPotential(objective);

            var first = new List<Suggestion>();
            foreach (Vector2Int pos in snap.ValidSlots)
            {
                for (int rot = 0; rot < 6; rot++)
                {
                    if (!board.Fits(snap.Hand, pos, rot)) continue;
                    PlacementEval ev = board.Evaluate(snap.Hand, pos, rot);
                    if (usePotential) ev.Potential = board.PotentialDelta(snap.Hand, pos, rot, sample);
                    if (ev.GoalActive) ev.GoalProspectDelta = board.GoalProspectDelta(snap.Hand, pos, rot, ev, sample);
                    if (board.AtOnceKind == 1 || (board.AtOnceKind == 2 && board.FlowerSite.HasValue))
                    {
                        (float delta, bool ruins) = board.FlowerPlanDelta(snap.Hand, pos, rot, sample);
                        ev.FlowerDelta = delta;
                        ev.FlowerRuined = ruins;
                        ev.FlowerCenterWasted = ruins && board.LastCenterWasted;
                        ev.FlowerRuinedProgress = ruins ? board.LastRuinedProgress : 0;
                    }
                    first.Add(new Suggestion { Pos = pos, Rotation = rot, Eval = ev, Immediate = ScoreFor(ev, objective, board.QuestReward, board.PerfectReward) });
                }
            }

            int known = Mathf.Min(depth, snap.Stack.Count);
            int extra = snap.Pool.Count > 0 ? simulated : 0;
            bool lookahead = known + extra > 0;
            List<Suggestion> candidates = first.OrderByDescending(s => s.Immediate).Take(lookahead ? FirstMoveCandidates : first.Count).ToList();

            int samplesDone = 0;
            if (lookahead)
            {
                // Draws shared by all candidates (fair comparison).
                var rng = new System.Random(snap.PlacedCount);
                var sequences = new List<List<TileSpec>>();
                int maxSamples = extra > 0 ? MaxSamples : 1;
                for (int i = 0; i < maxSamples; i++)
                {
                    var seq = new List<TileSpec>(snap.Stack.Take(known));
                    for (int k = 0; k < extra; k++) seq.Add(snap.Pool[rng.Next(snap.Pool.Count)]);
                    sequences.Add(seq);
                }

                var totals = new float[candidates.Count];
                var options = new ParallelOptions { CancellationToken = ct };

                void RunSample(int sample)
                {
                    Parallel.For(0, candidates.Count, options, i =>
                    {
                        Suggestion c = candidates[i];
                        Board b = board.Clone();
                        int[] placed = b.Apply(snap.Hand, c.Pos, c.Rotation);
                        // The hand tile's quest becomes a regular quest, anchored on its placed segments.
                        b.Quests = b.Quests.Select(q => q.HandSegs != null ? q.AnchoredOn(placed) : q).ToList();
                        b.Hand = null;
                        totals[i] += Rollout(b, sequences[sample], sequences[sample].Count, objective, ct);
                    });
                }

                // First draw to measure the cost, then as many as the budget allows.
                long t0 = sw.ElapsedMilliseconds;
                RunSample(0);
                samplesDone = 1;
                long perSample = Math.Max(1, sw.ElapsedMilliseconds - t0);
                int target = Mathf.Clamp((int)((BudgetMs - sw.ElapsedMilliseconds) / perSample) + 1, 1, sequences.Count);
                while (samplesDone < target)
                {
                    RunSample(samplesDone);
                    samplesDone++;
                }
                for (int i = 0; i < candidates.Count; i++) candidates[i].Future = totals[i] / samplesDone;
            }

            result.Suggestions = candidates
                .GroupBy(s => s.Pos)
                .Select(g => g.OrderByDescending(s => s.Score).First())
                .OrderByDescending(s => s.Score)
                .ThenByDescending(s => s.Eval.Neighbors)
                .Take(3)
                .ToList();
            result.Stats = $"{first.Count} placements, lookahead = {known} visible + {extra} likely × {samplesDone} draws " +
                           $"× {candidates.Count} candidates (pool {snap.Pool.Count} tiles) in {sw.ElapsedMilliseconds} ms";
            return result;
        }

        /// <summary>Plays the next tiles greedily (best immediate score) and returns the discounted sum of scores.</summary>
        private static float Rollout(Board b, List<TileSpec> stack, int steps, Objective objective, CancellationToken ct)
        {
            float total = 0f, weight = 1f;
            for (int k = 0; k < steps; k++)
            {
                ct.ThrowIfCancellationRequested();
                weight *= Discount;
                TileSpec t = stack[k];
                PlacementEval best = null;
                float bestScore = float.MinValue;
                foreach (Vector2Int pos in b.Frontier.ToList())
                {
                    for (int rot = 0; rot < 6; rot++)
                    {
                        if (!b.Fits(t, pos, rot)) continue;
                        PlacementEval ev = b.Evaluate(t, pos, rot);
                        float sc = ScoreFor(ev, objective, b.QuestReward, b.PerfectReward);
                        if (sc > bestScore)
                        {
                            bestScore = sc;
                            best = ev;
                        }
                    }
                }
                if (best == null)
                {
                    total -= 50f * weight; // tile cannot be placed
                    continue;
                }
                total += weight * bestScore;
                b.Apply(t, best.Pos, best.Rotation);
            }
            return total;
        }
    }
}
