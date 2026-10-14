using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DorfHelper.Model
{
    /// <summary>The game's hex grid (copy of GridCalculator, which is internal).</summary>
    internal static class Hex
    {
        private static readonly Vector2Int[] Even =
            { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(-1, 0), new Vector2Int(-1, 1) };

        private static readonly Vector2Int[] Odd =
            { new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(1, -1), new Vector2Int(0, -1), new Vector2Int(-1, -1), new Vector2Int(-1, 0) };

        public static Vector2Int Neighbor(Vector2Int p, int dir) => p + (p.x % 2 == 0 ? Even : Odd)[dir];
    }

    /// <summary>A tile edge: group types present (GroupTypeId ids).</summary>
    internal sealed class EdgeSpec
    {
        public static readonly EdgeSpec Empty = new EdgeSpec { Any = new byte[0], NonHybrid = new byte[0] };
        public byte[] Any;
        public byte[] NonHybrid;
        public bool Hybrid;
    }

    /// <summary>A tile segment (a forest area, village…), in the tile's local frame.</summary>
    internal sealed class SegSpec
    {
        public byte Type;
        public int[] Edges;    // directions (tile frame) covered by the segment
        public int[] Elements; // element count per tracked kind (index = Board.ElementKinds)
    }

    /// <summary>Pure description of a tile (no Unity object), in its own local frame.</summary>
    internal sealed class TileSpec
    {
        public string Name;
        public EdgeSpec[] Edges = new EdgeSpec[6];
        public SegSpec[] Segs;
    }

    internal enum QuestKind { CountElements, CountSegments, CloseGroup, Unknown }

    internal enum Comparison { MoreThan, Exactly, FewerThan }

    internal sealed class QuestModel
    {
        public string Label;
        public QuestKind Kind;
        public Comparison Comparison;
        public byte GroupType;
        public int ElementIndex = -1;
        public int Target;
        public int[] AnchorSegs; // watched segments (via their groups)

        /// <summary>
        /// Quest carried by the tile in hand: it only exists once the tile is placed. Its anchors are then
        /// the placed tile's segments (indices into the hand's TileSpec.Segs).
        /// </summary>
        public int[] HandSegs;

        public QuestModel AnchoredOn(int[] placedSegs) => new QuestModel
        {
            Label = Label, Kind = Kind, Comparison = Comparison, GroupType = GroupType, ElementIndex = ElementIndex,
            Target = Target, AnchorSegs = HandSegs.Select(i => placedSegs[i]).ToArray(),
        };
    }

    internal enum QuestStatus { InProgress, Fulfilled, Failed }

    internal struct QuestState
    {
        public int Value;
        public QuestStatus Status;
        public int Open; // edges still open on the watched groups
    }

    /// <summary>Result of evaluating a placement (without modifying the board).</summary>
    internal sealed class PlacementEval
    {
        public Vector2Int Pos;
        public int Rotation;
        public int Perfect, Mismatched, Empty;
        public int QuestsFulfilled, QuestsFailed;
        public float QuestProgress; // sum of relative progress (Δ / target) on counting quests
        public int QuestsTouched;
        public int QuestsEndangered; // in-progress counting quests whose area has only one open edge left
        public float Potential;      // change in the expected number of future perfect placements (see Board.PotentialDelta)
        public int PerfectTriggered; // perfect placements triggered (the placed tile + completed neighbors)
        public bool GoalActive;          // a biome goal is being tracked (see Board.Goal)
        public int GoalTarget;
        public float GoalScale = 1f;
        public bool GoalClose = true;
        public float GoalElementsPerSegment = 1f;
        public QuestState GoalBefore, GoalAfter;
        public int[] GoalMergedSegs = new int[0]; // placed tile's segments that join the target group
        public float GoalProspectDelta;           // change in the target group's growth prospects
        // "Several groups" goal (close N groups of at least S elements)
        public bool ManyActive;
        public int ManyClosedQualifying;  // groups closed by this placement with at least S elements
        public int ManyClosedSmall;       // closed groups that are too small
        public int ManyGrowth;            // progress toward S (capped at S per group; merging large groups is negative)
        public int ManyCloseProgress;     // edges closed on groups that already have S elements

        public int Points;                // points earned by the placement (matches, perfects, closed groups, quests)
        public int MaxClosedGroupSegs;    // largest group closed by the placement (in segments)
        public float FlowerDelta;         // change in flower "maturity" (challenge: 7 perfect placements at once)
        public int AtOnceKind, AtOnceTarget;
        public bool FlowerRuined;  // the placement ruins the flower under construction
        public bool FlowerCenterWasted; // tile placed in the center of a complete flower without reaching the target
        public int FlowerRuinedProgress; // progress of the ruined flower (proportional penalty)
        public int ReservedQuestsSpent; // quests reserved for the center tile, completed elsewhere by this placement
        public float ReservedProgress;  // progress (relative to target) on reserved quests, without completing them
        public int HandQuestNearCenter; // quest of the tile in hand, placed on a group that touches the flower's center
        public int TilesGained;      // tiles gained: perfect placements + fulfilled quests
        public int Neighbors => 6 - Empty;
        public bool PerfectPlacement => Empty == 0 && Mismatched == 0;
    }

    /// <summary>
    /// Pure board model: cells, segments and groups (union-find), quests.
    /// Mirrors the game rules: a segment edge is "open" as long as no tile touches it;
    /// two segments of the same type on facing edges merge their groups.
    /// </summary>
    internal sealed class Board
    {
        public const int TypeCount = 5; // GroupTypeId : Village, Forest, Agriculture, TrainTracks, Water

        private sealed class Cell
        {
            public TileSpec Spec;
            public int Rot;
            public int[] Segs; // global indices of the cell's segments
        }

        public int ElementKinds;
        public int PerfectReward = 2; // tiles gained per perfect placement (RewardSystemConfiguration)
        public int QuestReward = 0;   // tiles gained per fulfilled quest (depends on score)
        /// <summary>
        /// Biome goal (e.g. "close a field of 150 fields"): tracked like an "at least N elements" quest
        /// on the target group (anchors = its segments). Null if there is no goal.
        /// </summary>
        public QuestModel Goal;
        public float GoalScale = 1f; // 0..1: goal weight, reduced when the draw pile is low (survival first)
        public bool GoalClose = true; // true: the group must be closed; false: reaching the size is enough
        public float GoalElementsPerSegment = 1f; // average elements per segment of the target terrain (normalizes growth)

        /// <summary>Average elements (or 1 when counting segments) per segment of a type, over a sample.</summary>
        public static float ElementsPerSegment(IEnumerable<TileSpec> sample, byte type, int elementIndex)
        {
            if (elementIndex < 0) return 1f;
            int segs = 0, elems = 0;
            foreach (TileSpec t in sample)
                foreach (SegSpec sg in t.Segs)
                    if (sg.Type == type && elementIndex < sg.Elements.Length) { segs++; elems += sg.Elements[elementIndex]; }
            return segs == 0 ? 1f : System.Math.Max(1f, (float)elems / segs);
        }

        // Points (RewardSystemConfiguration), read in game; otherwise the code's default values
        public int FitScoreMethod;            // 0 linear, 1 2^x, 2 x²
        public int FitScoreModifier = 2;
        public bool RewardTileFit = true;
        public int PerfectScore = 60;
        public bool RewardGroups = true;
        public float GroupScorePow = 1.2f;
        public float GroupScoreMultiplier = 3f;
        public int QuestScore = 100;

        /// <summary>Points earned by closing a group of `segments` segments (ElementGroupManager.CloseGroup).</summary>
        public int GroupClosingPoints(int segments) =>
            RewardGroups && segments > 1 ? (int)System.Math.Round(System.Math.Pow(segments - 1, GroupScorePow) * GroupScoreMultiplier) : 0;

        /// <summary>Points for a tile in the center of a complete flower (7 perfect placements + 6 matches), excluding quests.</summary>
        public int FlowerJackpotPoints()
        {
            int fit = !RewardTileFit ? 0 : FitScoreMethod == 0 ? FitScoreModifier * 6
                : FitScoreMethod == 1 ? (int)System.Math.Round(System.Math.Pow(2, FitScoreModifier))
                : (int)System.Math.Round(System.Math.Pow(FitScoreModifier, 2));
            return 7 * PerfectScore + fit;
        }

        /// <summary>Smallest number of segments whose closing earns at least `points` (margin for matches).</summary>
        public int SegmentsForPoints(int points)
        {
            for (int n = 2; n < 2000; n++)
                if (GroupClosingPoints(n) + 12 >= points) return n;
            return 2000;
        }

        /// <summary>Is this segment's group still open?</summary>
        public bool IsSegOpen(int seg) => seg >= 0 && seg < parent.Count && open[Find(seg)] > 0;

        /// <summary>Type of a segment.</summary>
        public int SegType(int seg) => seg >= 0 && seg < segType.Count ? segType[seg] : -1;

        /// <summary>Type of the largest open group (in segments), or -1.</summary>
        public int LargestOpenGroupType()
        {
            int best = 0, type = -1;
            for (int i = 0; i < parent.Count; i++)
                if (parent[i] == i && open[i] > 0 && segCount[i] > best) { best = segCount[i]; type = segType[i]; }
            return type;
        }

        // "At once" challenges: 0 none, 1 perfect placements at once (target = count), 2 points in one placement (target = points)
        public int AtOnceKind;
        public int AtOnceTarget;

        // "Several groups" goal: close groups of type ManyType with at least ManySize elements
        public int ManyType = -1;
        public int ManyElement = -1;
        public int ManySize;
        public TileSpec Hand; // tile in hand (may carry a quest, see QuestModel.HandSegs)
        public bool[] Constraining = new bool[TypeCount];
        public List<QuestModel> Quests = new List<QuestModel>();

        private Dictionary<Vector2Int, Cell> cells = new Dictionary<Vector2Int, Cell>();

        // Quest tiles "pre-placed" on the map: they constrain connections (rivers, rails) of neighboring
        // cells, but are not on the board (they close no edge and merge nothing).
        private Dictionary<Vector2Int, Cell> ghosts = new Dictionary<Vector2Int, Cell>();

        public void AddGhost(TileSpec t, Vector2Int pos, int rot)
        {
            ghosts = new Dictionary<Vector2Int, Cell>(ghosts) { [pos] = new Cell { Spec = t, Rot = rot, Segs = new int[0] } };
            frontier.Remove(pos);
        }
        private HashSet<Vector2Int> frontier = new HashSet<Vector2Int>();

        // Segments (global index) and union-find
        private List<byte> segType = new List<byte>();
        private List<int> segWorldEdges = new List<int>(); // 6-bit mask
        private List<int> parent = new List<int>();
        // Aggregates, valid on roots
        private List<int> open = new List<int>();
        private List<int[]> elements = new List<int[]>();
        private List<int> segCount = new List<int>();

        public int TileCount => cells.Count;
        public IEnumerable<Vector2Int> Frontier => frontier;
        public bool IsOccupied(Vector2Int p) => cells.ContainsKey(p);

        public Board Clone()
        {
            var b = (Board)MemberwiseClone();
            b.cells = new Dictionary<Vector2Int, Cell>(cells); // Cells are never modified after creation
            b.frontier = new HashSet<Vector2Int>(frontier);
            b.segType = new List<byte>(segType);
            b.segWorldEdges = new List<int>(segWorldEdges);
            b.parent = new List<int>(parent);
            b.open = new List<int>(open);
            b.elements = elements.Select(e => (int[])e.Clone()).ToList();
            b.segCount = new List<int>(segCount);
            b.fitProbabilityCache = new Dictionary<Vector2Int, float>();
            b.tilePotentialCache = new Dictionary<Vector2Int, float>();
            b.goalSlotCache = null;
            b.ReservedQuests = new HashSet<QuestModel>(); // reserved quests only apply to the immediate placement
            b.CenterTouchRoots = new HashSet<int>();
            return b;
        }

        private int Find(int s)
        {
            while (parent[s] != s)
            {
                parent[s] = parent[parent[s]];
                s = parent[s];
            }
            return s;
        }

        private void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a == b) return;
            parent[b] = a;
            open[a] += open[b];
            segCount[a] += segCount[b];
            for (int i = 0; i < ElementKinds; i++) elements[a][i] += elements[b][i];
        }

        private static int WorldDir(int localDir, int rot) => (localDir + rot) % 6;
        private static int LocalDir(int worldDir, int rot) => (worldDir - rot + 6) % 6;

        private EdgeSpec EdgeAt(Cell c, int worldDir) => c.Spec.Edges[LocalDir(worldDir, c.Rot)] ?? EdgeSpec.Empty;

        /// <summary>Two facing edges "fit" (same rule as Tile.PlaceNeighbor / the AutoPlacer).</summary>
        private static bool EdgesFit(EdgeSpec mine, EdgeSpec theirs) =>
            (mine.Any.Length == 0 && theirs.Any.Length == 0)
            || (mine.Hybrid && theirs.Any.Length == 0)
            || (mine.Any.Length == 0 && theirs.Hybrid)
            || mine.Any.Any(theirs.Any.Contains);

        /// <summary>
        /// Does the neighbor at `np` become a perfect placement if we place at `pos` (whose edge toward it fits or not)?
        /// All 6 of its sides must be occupied and all its edges must fit.
        /// </summary>
        private bool CompletesPerfect(Vector2Int np, Vector2Int pos, bool fitTowardsPos)
        {
            if (!fitTowardsPos || !cells.TryGetValue(np, out Cell n)) return false;
            for (int k = 0; k < 6; k++)
            {
                Vector2Int q = Hex.Neighbor(np, k);
                if (q == pos) continue;
                if (!cells.TryGetValue(q, out Cell other)) return false;
                if (!EdgesFit(EdgeAt(n, k), EdgeAt(other, (k + 3) % 6))) return false;
            }
            return true;
        }

        // ---------- Rules ----------

        /// <summary>Same rule as TileFitter.TileFitsInSlot (rivers and rails must connect).</summary>
        public bool Fits(TileSpec t, Vector2Int pos, int rot)
        {
            for (int d = 0; d < 6; d++)
            {
                EdgeSpec mine = t.Edges[LocalDir(d, rot)] ?? EdgeSpec.Empty;
                Vector2Int np = Hex.Neighbor(pos, d);
                if (!cells.TryGetValue(np, out Cell nb)) ghosts.TryGetValue(np, out nb);
                EdgeSpec theirs = nb != null ? EdgeAt(nb, (d + 3) % 6) : EdgeSpec.Empty;

                foreach (byte g in theirs.NonHybrid)
                    if (Constraining[g] && !mine.Any.Contains(g)) return false;
                if (nb != null)
                    foreach (byte g in mine.NonHybrid)
                        if (Constraining[g] && !theirs.Any.Contains(g)) return false;
            }
            return true;
        }

        /// <summary>Evaluates a placement without modifying the board: edges, then the exact effect on each quest.</summary>
        public PlacementEval Evaluate(TileSpec t, Vector2Int pos, int rot, List<QuestState> questStatesAfter = null)
        {
            bool isHand = t == Hand;
            var ev = new PlacementEval { Pos = pos, Rotation = rot };
            int k = t.Segs.Length;

            // Local nodes: 0..k-1 = our segments, then the touched neighboring roots.
            var rootIndex = new Dictionary<int, int>();
            var roots = new List<int>();
            var closeCount = new List<int>();
            var localParent = new List<int>();
            for (int i = 0; i < k; i++) localParent.Add(i);
            var ownOpen = new int[k];

            int LocalOf(int root)
            {
                if (!rootIndex.TryGetValue(root, out int idx))
                {
                    idx = k + roots.Count;
                    rootIndex[root] = idx;
                    roots.Add(root);
                    closeCount.Add(0);
                    localParent.Add(idx);
                }
                return idx;
            }

            int LFind(int x)
            {
                while (localParent[x] != x) x = localParent[x] = localParent[localParent[x]];
                return x;
            }

            for (int d = 0; d < 6; d++)
            {
                int ld = LocalDir(d, rot);
                EdgeSpec mine = t.Edges[ld] ?? EdgeSpec.Empty;
                if (!cells.TryGetValue(Hex.Neighbor(pos, d), out Cell nb))
                {
                    ev.Empty++;
                    for (int i = 0; i < k; i++)
                        if (t.Segs[i].Edges.Contains(ld)) ownOpen[i]++;
                    continue;
                }

                int od = (d + 3) % 6;
                EdgeSpec theirs = EdgeAt(nb, od);
                bool perfect = EdgesFit(mine, theirs);
                if (perfect) ev.Perfect++;
                else ev.Mismatched++;
                if (CompletesPerfect(Hex.Neighbor(pos, d), pos, perfect)) ev.PerfectTriggered++;

                // Neighboring segments touching this edge lose an open edge, and merge with us if of the same type.
                foreach (int ns in nb.Segs)
                {
                    if ((segWorldEdges[ns] & (1 << od)) == 0) continue;
                    int li = LocalOf(Find(ns));
                    closeCount[li - k]++;
                    for (int i = 0; i < k; i++)
                    {
                        if (t.Segs[i].Type != segType[ns] || !t.Segs[i].Edges.Contains(ld)) continue;
                        int a = LFind(i), b = LFind(li);
                        if (a != b) localParent[b] = a;
                    }
                }
            }

            // Aggregates of the local components
            int n = localParent.Count;
            var cOpen = new int[n];
            var cSeg = new int[n];
            var cElem = new int[n][];
            for (int x = 0; x < n; x++)
            {
                int r = LFind(x);
                if (cElem[r] == null) cElem[r] = new int[ElementKinds];
                if (x < k)
                {
                    cOpen[r] += ownOpen[x];
                    cSeg[r] += 1;
                    for (int e = 0; e < ElementKinds; e++) cElem[r][e] += t.Segs[x].Elements[e];
                }
                else
                {
                    int root = roots[x - k];
                    cOpen[r] += open[root] - closeCount[x - k];
                    cSeg[r] += segCount[root];
                    for (int e = 0; e < ElementKinds; e++) cElem[r][e] += elements[root][e];
                }
            }

            if (ev.PerfectPlacement) ev.PerfectTriggered++;

            // Quests: state before / after
            foreach (QuestModel q in Quests)
            {
                if (q.HandSegs != null && !isHand) { questStatesAfter?.Add(default); continue; }
                if (q.HandSegs != null && CenterTouchRoots.Count > 0 && q.Kind != QuestKind.CloseGroup && q.Comparison == Comparison.MoreThan)
                {
                    // Do our quest segments merge with a group that touches the center?
                    var questComps = new HashSet<int>(q.HandSegs.Select(i => LFind(i)));
                    bool joins = CenterTouchRoots.Any(r => rootIndex.TryGetValue(r, out int li) && questComps.Contains(LFind(li)));
                    if (joins) ev.HandQuestNearCenter++;
                }
                QuestState before = q.HandSegs != null
                    ? new QuestState { Value = 0, Status = QuestStatus.InProgress } // the quest does not exist yet
                    : QuestStateOf(q, null, null, null, null, null);
                QuestState after = QuestStateOf(q, rootIndex, LFind, cOpen, cSeg, cElem);
                questStatesAfter?.Add(after);
                if (after.Value != before.Value || after.Status != before.Status) ev.QuestsTouched++;
                if (before.Status != QuestStatus.InProgress) continue;
                if (after.Status == QuestStatus.Fulfilled && ReservedQuests.Contains(q) && pos != FlowerSite) ev.ReservedQuestsSpent++;
                if (after.Status == QuestStatus.InProgress && ReservedQuests.Contains(q) && q.Target > 0 && after.Value > before.Value)
                    ev.ReservedProgress += (float)(after.Value - before.Value) / q.Target;
                if (after.Status == QuestStatus.Fulfilled) ev.QuestsFulfilled++;
                else if (after.Status == QuestStatus.Failed) ev.QuestsFailed++;
                else if (q.Kind != QuestKind.CloseGroup && q.Target > 0)
                {
                    ev.QuestProgress += Mathf.Clamp01((float)(after.Value - before.Value) / q.Target);
                    // Only one open edge left: the next tile placed there without extending the area will fail the quest.
                    if (after.Open == 1 && before.Open != 1) ev.QuestsEndangered++;
                }
            }
            if (ManyType >= 0) EvaluateMany(ev, t, k, roots, localParent.Count, LFind, cOpen, cSeg, cElem);

            if (Goal != null && Goal.AnchorSegs.Length > 0)
            {
                ev.GoalActive = true; // safeguards stay active even when the reserve is low
                ev.GoalTarget = Goal.Target;
                ev.GoalScale = GoalScale;
                ev.GoalClose = GoalClose;
                ev.GoalElementsPerSegment = GoalElementsPerSegment;
                ev.GoalBefore = QuestStateOf(Goal, null, null, null, null, null);
                ev.GoalAfter = QuestStateOf(Goal, rootIndex, LFind, cOpen, cSeg, cElem);
                var goalLocalRoots = new HashSet<int>();
                foreach (int a in Goal.AnchorSegs)
                    if (rootIndex.TryGetValue(Find(a), out int li)) goalLocalRoots.Add(LFind(li));
                ev.GoalMergedSegs = Enumerable.Range(0, k).Where(i => goalLocalRoots.Contains(LFind(i))).ToArray();
            }

            ev.TilesGained = ev.PerfectTriggered * PerfectReward + ev.QuestsFulfilled * QuestReward;

            // Placement points, like RewardSystem.AddTilePlacementScore + group closings + quests
            int fitPoints = 0;
            if (RewardTileFit && ev.Perfect > 0)
                fitPoints = FitScoreMethod == 0 ? FitScoreModifier * ev.Perfect
                    : FitScoreMethod == 1 ? (int)System.Math.Round(System.Math.Pow(2, FitScoreModifier))
                    : (int)System.Math.Round(System.Math.Pow(FitScoreModifier, 2));
            int groupPoints = 0;
            var counted = new HashSet<int>();
            for (int x = 0; x < n; x++)
            {
                int r = LFind(x);
                if (!counted.Add(r) || cOpen[r] != 0) continue;
                // group closed by the placement: it contains one of our segments or a neighboring root (all open before)
                groupPoints += GroupClosingPoints(cSeg[r]);
                if (cSeg[r] > ev.MaxClosedGroupSegs) ev.MaxClosedGroupSegs = cSeg[r];
            }
            ev.Points = fitPoints + ev.PerfectTriggered * PerfectScore + groupPoints + ev.QuestsFulfilled * QuestScore;
            ev.AtOnceKind = AtOnceKind;
            ev.AtOnceTarget = AtOnceTarget;
            if (AtOnceKind != 0) ev.GoalScale = GoalScale;
            return ev;
        }

        /// <summary>Effects of a placement on the "several groups" goal, computed from the local components.</summary>
        private void EvaluateMany(PlacementEval ev, TileSpec t, int k, List<int> roots, int n, System.Func<int, int> lfind,
                                  int[] cOpen, int[] cSeg, int[][] cElem)
        {
            ev.ManyActive = GoalScale > 0f;
            int Size(int value) => System.Math.Min(value, ManySize);
            // "Before" value of the touched roots, grouped by local component
            var beforeCapped = new Dictionary<int, int>();
            var beforeOpenBig = new Dictionary<int, int>();
            var hasType = new HashSet<int>();
            for (int x = 0; x < n; x++)
            {
                int r = lfind(x);
                bool ofType = x < k ? t.Segs[x].Type == ManyType : segType[roots[x - k]] == ManyType;
                if (!ofType) continue;
                hasType.Add(r);
                if (x >= k)
                {
                    int root = roots[x - k];
                    int v = ManyElement >= 0 ? elements[root][ManyElement] : segCount[root];
                    beforeCapped[r] = (beforeCapped.TryGetValue(r, out int b) ? b : 0) + Size(v);
                    if (v >= ManySize) beforeOpenBig[r] = (beforeOpenBig.TryGetValue(r, out int o) ? o : 0) + open[root];
                }
            }
            foreach (int r in hasType)
            {
                int after = ManyElement >= 0 ? cElem[r][ManyElement] : cSeg[r];
                int before = beforeCapped.TryGetValue(r, out int b) ? b : 0;
                ev.ManyGrowth += Size(after) - before;
                if (cOpen[r] == 0)
                {
                    if (after >= ManySize) ev.ManyClosedQualifying++;
                    else ev.ManyClosedSmall++;
                }
                else if (after >= ManySize && beforeOpenBig.TryGetValue(r, out int ob))
                    ev.ManyCloseProgress += ob - cOpen[r];
            }
        }

        /// <summary>
        /// Value and status of a quest, like GroupConditionWatcher. If the local arrays are provided,
        /// roots touched by the placement are replaced by their merged component.
        /// </summary>
        private QuestState QuestStateOf(QuestModel q, Dictionary<int, int> rootIndex, System.Func<int, int> lfind,
                                        int[] cOpen, int[] cSeg, int[][] cElem)
        {
            int value = 0;
            bool allClosed = true;
            var seen = new HashSet<long>();
            int openSum = 0;
            if (q.HandSegs != null)
            {
                if (lfind == null) return new QuestState { Value = 0, Status = QuestStatus.InProgress };
                foreach (int hs in q.HandSegs)
                {
                    int r = lfind(hs);
                    if (!seen.Add(-1 - r)) continue;
                    if (cOpen[r] > 0) allClosed = false;
                    openSum += cOpen[r];
                    switch (q.Kind)
                    {
                        case QuestKind.CountElements: value += q.ElementIndex >= 0 ? cElem[r][q.ElementIndex] : 0; break;
                        case QuestKind.CountSegments: value += cSeg[r]; break;
                        case QuestKind.CloseGroup: value += cOpen[r]; break;
                    }
                }
            }
            foreach (int a in q.AnchorSegs)
            {
                int root = Find(a);
                int o, sc, el;
                long key;
                if (rootIndex != null && rootIndex.TryGetValue(root, out int li))
                {
                    int r = lfind(li);
                    key = -1 - r;
                    o = cOpen[r];
                    sc = cSeg[r];
                    el = q.ElementIndex >= 0 ? cElem[r][q.ElementIndex] : 0;
                }
                else
                {
                    key = root;
                    o = open[root];
                    sc = segCount[root];
                    el = q.ElementIndex >= 0 ? elements[root][q.ElementIndex] : 0;
                }
                if (!seen.Add(key)) continue;
                if (o > 0) allClosed = false;
                openSum += o;
                switch (q.Kind)
                {
                    case QuestKind.CountElements: value += el; break;
                    case QuestKind.CountSegments: value += sc; break;
                    case QuestKind.CloseGroup: value += o; break;
                }
            }

            var st = new QuestState { Value = value, Status = QuestStatus.InProgress, Open = openSum };
            if (q.AnchorSegs.Length == 0 && q.HandSegs == null) return st;
            if (q.Kind == QuestKind.CloseGroup)
            {
                if (allClosed) st.Status = QuestStatus.Fulfilled;
                return st;
            }

            int remaining = q.Target - value;
            switch (q.Comparison)
            {
                case Comparison.MoreThan:
                    if (remaining <= 0) st.Status = QuestStatus.Fulfilled;
                    break;
                case Comparison.Exactly:
                    if (remaining == 0) st.Status = QuestStatus.Fulfilled;
                    else if (remaining < 0) st.Status = QuestStatus.Failed;
                    break;
                case Comparison.FewerThan:
                    st.Status = remaining >= 0 ? QuestStatus.Fulfilled : QuestStatus.Failed;
                    break;
            }
            if (st.Status == QuestStatus.InProgress && allClosed) st.Status = QuestStatus.Failed; // group closed too early
            return st;
        }

        /// <summary>Actually places the tile in the model. Returns the global indices of its segments.</summary>
        public int[] Apply(TileSpec t, Vector2Int pos, int rot)
        {
            var cell = new Cell { Spec = t, Rot = rot, Segs = new int[t.Segs.Length] };
            for (int i = 0; i < t.Segs.Length; i++)
            {
                SegSpec s = t.Segs[i];
                int idx = segType.Count;
                int mask = 0;
                int openEdges = 0;
                foreach (int le in s.Edges)
                {
                    int wd = WorldDir(le, rot);
                    mask |= 1 << wd;
                    if (!cells.ContainsKey(Hex.Neighbor(pos, wd))) openEdges++;
                }
                segType.Add(s.Type);
                segWorldEdges.Add(mask);
                parent.Add(idx);
                open.Add(openEdges);
                elements.Add((int[])s.Elements.Clone());
                segCount.Add(1);
                cell.Segs[i] = idx;
            }

            for (int d = 0; d < 6; d++)
            {
                if (!cells.TryGetValue(Hex.Neighbor(pos, d), out Cell nb)) continue;
                int od = (d + 3) % 6;
                foreach (int ns in nb.Segs)
                {
                    if ((segWorldEdges[ns] & (1 << od)) == 0) continue;
                    open[Find(ns)]--;
                    foreach (int ms in cell.Segs)
                        if (segType[ms] == segType[ns] && (segWorldEdges[ms] & (1 << d)) != 0)
                            Union(ms, ns);
                }
            }

            cells[pos] = cell;
            frontier.Remove(pos);
            for (int d = 0; d < 6; d++)
            {
                Vector2Int p = Hex.Neighbor(pos, d);
                if (!cells.ContainsKey(p) && !ghosts.ContainsKey(p)) frontier.Add(p);
            }
            return cell.Segs;
        }

        public QuestState QuestStateOf(QuestModel q) => QuestStateOf(q, null, null, null, null, null);

        // ---------- Potential: future perfect placements ----------
        //
        // A tile placed with no mismatched edge will become perfect (+1 tile) once its empty sides are filled with
        // fitting tiles. For each empty cell, we estimate the probability that a "random" tile (sample of tiles
        // seen so far) fits it perfectly in at least one rotation. A tile's potential is the product of these
        // probabilities over its empty sides; the board's potential is the sum over all tiles.

        private Dictionary<Vector2Int, float> fitProbabilityCache = new Dictionary<Vector2Int, float>();
        private Dictionary<Vector2Int, float> tilePotentialCache = new Dictionary<Vector2Int, float>();

        /// <summary>Occupied cell, accounting for a "virtual" tile placed at vPos (to evaluate a placement).</summary>
        private bool TryCell(Vector2Int p, Vector2Int vPos, Cell virt, out Cell c)
        {
            if (virt != null && p == vPos) { c = virt; return true; }
            return cells.TryGetValue(p, out c);
        }

        /// <summary>Probability that a sample tile fits perfectly at `slot` (1 if there are no neighbors).</summary>
        private float FitProbability(Vector2Int slot, List<TileSpec> sample, Vector2Int vPos, Cell virt)
        {
            var needs = new EdgeSpec[6];
            bool any = false;
            for (int d = 0; d < 6; d++)
            {
                if (TryCell(Hex.Neighbor(slot, d), vPos, virt, out Cell n))
                {
                    needs[d] = EdgeAt(n, (d + 3) % 6);
                    any = true;
                }
            }
            if (!any || sample.Count == 0) return 1f;

            int ok = 0;
            foreach (TileSpec t in sample)
            {
                for (int rot = 0; rot < 6; rot++)
                {
                    bool fits = true;
                    for (int d = 0; d < 6 && fits; d++)
                        if (needs[d] != null && !EdgesFit(t.Edges[LocalDir(d, rot)] ?? EdgeSpec.Empty, needs[d])) fits = false;
                    if (fits) { ok++; break; }
                }
            }
            return (float)ok / sample.Count;
        }

        /// <summary>Potential of a tile: 0 if an edge is mismatched or if it has no empty side left (already counted).</summary>
        private float TilePotential(Vector2Int p, List<TileSpec> sample, Vector2Int vPos, Cell virt,
                                    Dictionary<Vector2Int, float> slotProbabilities)
        {
            if (!TryCell(p, vPos, virt, out Cell c)) return 0f;
            float value = 1f;
            bool hasEmpty = false;
            for (int d = 0; d < 6; d++)
            {
                Vector2Int q = Hex.Neighbor(p, d);
                if (TryCell(q, vPos, virt, out Cell n))
                {
                    if (!EdgesFit(EdgeAt(c, d), EdgeAt(n, (d + 3) % 6))) return 0f; // mismatched edge: never perfect
                    continue;
                }
                if (ghosts.ContainsKey(q)) return 0f; // cell reserved for a quest tile: don't count on it
                hasEmpty = true;
                if (!slotProbabilities.TryGetValue(q, out float pr))
                {
                    pr = FitProbability(q, sample, vPos, virt);
                    slotProbabilities[q] = pr;
                }
                value *= pr;
            }
            return hasEmpty ? value : 0f;
        }

        /// <summary>
        /// Change in potential if `t` is placed at `pos` with rotation `rot`. Only the cells neighboring `pos`
        /// and the tiles bordering them are affected. Perfect placements achieved immediately are counted
        /// in TilesGained, not here (their potential drops to 0).
        /// </summary>
        public float PotentialDelta(TileSpec t, Vector2Int pos, int rot, List<TileSpec> sample)
        {
            if (sample == null || sample.Count == 0) return 0f;
            var virt = new Cell { Spec = t, Rot = rot, Segs = new int[0] };

            // Affected tiles: neighbors of pos, and neighbors of the empty cells around pos.
            var affected = new HashSet<Vector2Int>();
            for (int d = 0; d < 6; d++)
            {
                Vector2Int q = Hex.Neighbor(pos, d);
                if (cells.ContainsKey(q)) { affected.Add(q); continue; }
                for (int k = 0; k < 6; k++)
                {
                    Vector2Int r = Hex.Neighbor(q, k);
                    if (r != pos && cells.ContainsKey(r)) affected.Add(r);
                }
            }

            float before = 0f;
            foreach (Vector2Int a in affected)
            {
                if (!tilePotentialCache.TryGetValue(a, out float v))
                {
                    v = TilePotential(a, sample, default, null, fitProbabilityCache);
                    tilePotentialCache[a] = v;
                }
                before += v;
            }

            // After: probabilities recomputed for cells around pos, taken from the cache for the others.
            var afterProbabilities = new Dictionary<Vector2Int, float>();
            for (int d = 0; d < 6; d++)
            {
                Vector2Int q = Hex.Neighbor(pos, d);
                if (!cells.ContainsKey(q) && !ghosts.ContainsKey(q)) afterProbabilities[q] = FitProbability(q, sample, pos, virt);
            }
            foreach (var kv in fitProbabilityCache)
                if (!afterProbabilities.ContainsKey(kv.Key) && kv.Key != pos) afterProbabilities[kv.Key] = kv.Value;

            float after = TilePotential(pos, sample, pos, virt, afterProbabilities);
            foreach (Vector2Int a in affected) after += TilePotential(a, sample, pos, virt, afterProbabilities);
            return after - before;
        }

        /// <summary>
        /// Size of the largest still-open group of a type (in elements or segments): this is the game's reference
        /// for setting a new quest's target (QuestManager.ReferenceGroupCount).
        /// </summary>
        public int LargestOpenGroup(byte type, QuestKind kind, int elementIndex)
        {
            int best = 0;
            for (int i = 0; i < parent.Count; i++)
            {
                if (parent[i] != i || segType[i] != type || open[i] <= 0) continue;
                int v = kind == QuestKind.CountElements ? (elementIndex >= 0 ? elements[i][elementIndex] : 0) : segCount[i];
                if (v > best) best = v;
            }
            return best;
        }

        /// <summary>Segments of the largest open group of a type (in elements), or an empty array.</summary>
        public int[] LargestOpenGroupSegs(byte type, int elementIndex)
        {
            int bestRoot = -1, best = -1;
            for (int i = 0; i < parent.Count; i++)
            {
                if (parent[i] != i || segType[i] != type || open[i] <= 0) continue;
                int v = elementIndex >= 0 ? elements[i][elementIndex] : segCount[i];
                if (v > best) { best = v; bestRoot = i; }
            }
            if (bestRoot < 0) return new int[0];
            return Enumerable.Range(0, parent.Count).Where(i => Find(i) == bestRoot).ToArray();
        }

        /// <summary>Empty cells facing the open edges of the given segments (and the edge direction, as seen from the cell).</summary>
        public List<(Vector2Int slot, int dirFromSlot)> OpenEdgeSlots(IEnumerable<int> segs)
        {
            var result = new List<(Vector2Int, int)>();
            var roots = new HashSet<int>(segs.Select(Find));
            foreach (var kv in cells)
                foreach (int sIdx in kv.Value.Segs)
                {
                    if (!roots.Contains(Find(sIdx))) continue;
                    for (int d = 0; d < 6; d++)
                    {
                        if ((segWorldEdges[sIdx] & (1 << d)) == 0) continue;
                        Vector2Int q = Hex.Neighbor(kv.Key, d);
                        if (!cells.ContainsKey(q)) result.Add((q, (d + 3) % 6));
                    }
                }
            return result;
        }

        // ---------- Challenge: 7 perfect placements at once ("flower") ----------
        //
        // An empty cell H is a ready flower when its 6 neighbors are placed, each surrounded on its other 5 sides
        // with all its edges fitting: a tile that fits all 6 edges of H then triggers 7 perfect placements.
        // Maturity of H = (neighbors "complete except H")², weighted by the chance of finding the final tile.

        private int GoodFlowerNeighbors(Vector2Int h, Vector2Int vPos, Cell virt, out int present)
        {
            present = 0;
            int good = 0;
            for (int d = 0; d < 6; d++)
            {
                Vector2Int np = Hex.Neighbor(h, d);
                if (!TryCell(np, vPos, virt, out Cell n)) continue;
                present++;
                bool complete = true;
                for (int k = 0; k < 6 && complete; k++)
                {
                    Vector2Int q = Hex.Neighbor(np, k);
                    if (q == h) continue;
                    if (!TryCell(q, vPos, virt, out Cell o) || !EdgesFit(EdgeAt(n, k), EdgeAt(o, (k + 3) % 6))) complete = false;
                }
                if (complete) good++;
            }
            return good;
        }

        private float FlowerValue(Vector2Int h, List<TileSpec> sample, Vector2Int vPos, Cell virt)
        {
            if (TryCell(h, vPos, virt, out _) || ghosts.ContainsKey(h)) return 0f;
            int good = GoodFlowerNeighbors(h, vPos, virt, out int present);
            if (good == 0) return 0f;
            float chance = present == 6 ? FitProbability(h, sample, vPos, virt) : 0.5f;
            return good * good * (0.2f + chance);
        }

        /// <summary>Change in flower maturity around `pos` if `t` is placed there (rotation `rot`).</summary>
        public float FlowerDelta(TileSpec t, Vector2Int pos, int rot, List<TileSpec> sample)
        {
            if (sample == null || sample.Count == 0) return 0f;
            var virt = new Cell { Spec = t, Rot = rot, Segs = new int[0] };
            // Empty cells within distance 2 of pos (their neighbors may change state), and pos itself
            var holes = new HashSet<Vector2Int> { pos };
            for (int d = 0; d < 6; d++)
            {
                Vector2Int a = Hex.Neighbor(pos, d);
                holes.Add(a);
                for (int k = 0; k < 6; k++) holes.Add(Hex.Neighbor(a, k));
            }
            float before = 0f, after = 0f;
            foreach (Vector2Int h in holes)
            {
                if (cells.ContainsKey(h)) continue;
                before += FlowerValue(h, sample, default, null);
                after += FlowerValue(h, sample, pos, virt);
            }
            return after - before;
        }

        // ---------- Challenge: 7 perfect placements at once — planning a "flower" ----------
        //
        // Flower = center C (left empty until the end) + inner ring R1 (6) + outer ring R2 (12).
        // At the end, every R1 tile must have its 6 neighbors placed with all its edges fitting (except toward C),
        // then a tile that fits all 6 edges of C triggers 7 perfect placements.

        public Vector2Int? FlowerSite; // center of the flower under construction (chosen by SelectFlowerSite)
        public bool LastCenterWasted;  // last FlowerPlanDelta: placement in the center of a complete flower
        public int LastRuinedProgress; // last FlowerPlanDelta: progress (2×inner + outer) of the ruined flower
        public bool FlowerNeedsQuest;  // the flower alone is not enough: the center tile must also fulfill a quest
        public int FlowerQuestsNeeded = 1; // number of quests the center tile must fulfill to reach the target
        public HashSet<QuestModel> ReservedQuests = new HashSet<QuestModel>(); // quests to keep for the center tile
        public HashSet<int> CenterTouchRoots = new HashSet<int>(); // groups touching the flower's center (quest phase)
        public float QuestTileInRingBonus = 3f; // (in flower value points) quest tile in the ring, quest facing the center
        public int QuestPhaseRing1 = 5;     // quest preparation / reservation starts at 5 of 6 inner tiles
        public HashSet<Vector2Int> ExcludedFlowerSites = new HashSet<Vector2Int>(); // abandoned (blocked) sites
        public float FlowerQuestWeight = 80f; // weight of the "quest ready at the center" in a site's value

        public static Vector2Int[] Ring1(Vector2Int c) => Enumerable.Range(0, 6).Select(d => Hex.Neighbor(c, d)).ToArray();

        public static Vector2Int[] Ring2(Vector2Int c)
        {
            Vector2Int[] r1 = Ring1(c);
            var r2 = new HashSet<Vector2Int>();
            foreach (Vector2Int a in r1)
                for (int d = 0; d < 6; d++)
                {
                    Vector2Int b = Hex.Neighbor(a, d);
                    if (b != c && !r1.Contains(b)) r2.Add(b);
                }
            return r2.ToArray();
        }

        public struct FlowerState
        {
            public bool Ruined;      // a flower match is broken, or the center is occupied
            public int Ring1, Ring2; // occupied (and consistent) positions of each ring
            public float CenterChance; // share of tiles that would fit the edges facing the center
            public float Completion;   // product of the chances of filling each missing position
            public float QuestReadiness; // 0..1: a quest touching the center would be fulfilled by the center tile
            public float Value;
        }

        /// <summary>State of a flower centered at c (with an optional virtual tile at vPos).</summary>
        public FlowerState FlowerAt(Vector2Int c, List<TileSpec> sample) => FlowerAt(c, sample, default, null);

        private FlowerState FlowerAt(Vector2Int c, List<TileSpec> sample, Vector2Int vPos, Cell virt)
        {
            var st = new FlowerState();
            if (TryCell(c, vPos, virt, out _) || ghosts.ContainsKey(c)) { st.Ruined = true; return st; }
            Vector2Int[] r1 = Ring1(c);
            var needs = new EdgeSpec[6];
            foreach (Vector2Int a in r1)
            {
                if (ghosts.ContainsKey(a)) { st.Ruined = true; return st; }
                if (!TryCell(a, vPos, virt, out Cell ca)) continue;
                st.Ring1++;
                for (int d = 0; d < 6; d++)
                {
                    Vector2Int b = Hex.Neighbor(a, d);
                    if (b == c) { needs[(d + 3) % 6] = EdgeAt(ca, d); continue; } // edge facing the center
                    if (ghosts.ContainsKey(b)) { st.Ruined = true; return st; }
                    if (TryCell(b, vPos, virt, out Cell cb) && !EdgesFit(EdgeAt(ca, d), EdgeAt(cb, (d + 3) % 6)))
                    {
                        st.Ruined = true; // R1's neighbors (R1 and R2) must all fit
                        return st;
                    }
                }
            }
            Vector2Int[] r2 = Ring2(c);
            foreach (Vector2Int b in r2)
            {
                if (ghosts.ContainsKey(b)) { st.Ruined = true; return st; }
                if (TryCell(b, vPos, virt, out _)) st.Ring2++;
            }

            // Chance of filling each missing position (a tile that fits all its flower neighbors)
            st.Completion = 1f;
            if (sample != null && sample.Count > 0)
            {
                foreach (Vector2Int p in r1.Concat(r2))
                {
                    if (TryCell(p, vPos, virt, out _)) continue;
                    bool inner = r1.Contains(p);
                    var req = new EdgeSpec[6];
                    for (int d = 0; d < 6; d++)
                    {
                        Vector2Int q = Hex.Neighbor(p, d);
                        if (q == c) continue;
                        // R1: must fit all its placed neighbors; R2: only its R1 neighbors
                        if ((inner || r1.Contains(q)) && TryCell(q, vPos, virt, out Cell nq)) req[d] = EdgeAt(nq, (d + 3) % 6);
                    }
                    st.Completion *= System.Math.Max(0.02f, MatchProbability(req, sample));
                }
            }

            // Chance of finding the center tile (based on the edges already known)
            st.CenterChance = sample != null && sample.Count > 0 && st.Ring1 > 0 ? MatchProbability(needs, sample) : 1f;

            // Progress + chance of finishing: a blocked site (positions nearly impossible to fill) is worth little.
            float progress = 4f * st.Ring1 + 1.5f * st.Ring2;
            // Build first: quest preparation only counts once the inner ring is nearly finished.
            if (FlowerNeedsQuest && st.Ring1 >= QuestPhaseRing1) st.QuestReadiness = CenterQuestReadiness(c);

            // The center must stay achievable: otherwise the flower, even complete, is useless.
            float feasible = (float)System.Math.Sqrt(st.Completion * st.CenterChance);
            st.Value = progress * feasible + 15f * st.Ring1 / 6f * feasible
                       + (FlowerNeedsQuest ? FlowerQuestWeight * st.QuestReadiness * feasible : 0f); // useless if the center is impossible
            return st;
        }

        /// <summary>
        /// Would an "at least N" quest be fulfilled by a tile placed at c? The tile will merge the same-terrain groups
        /// that touch c (via the surrounding placed tiles): we sum those groups, plus the average contribution of one
        /// segment, and compare with the target. 1 = certainly fulfilled, decreasing with the shortfall.
        /// </summary>
        private float CenterQuestReadiness(Vector2Int c)
        {
            // Roots of the segments touching c, by type
            var touching = new Dictionary<byte, HashSet<int>>();
            for (int d = 0; d < 6; d++)
            {
                Vector2Int a = Hex.Neighbor(c, d);
                if (!cells.TryGetValue(a, out Cell ca)) continue;
                int towardCenter = (d + 3) % 6;
                foreach (int sIdx in ca.Segs)
                {
                    if ((segWorldEdges[sIdx] & (1 << towardCenter)) == 0) continue;
                    if (!touching.TryGetValue(segType[sIdx], out var set)) touching[segType[sIdx]] = set = new HashSet<int>();
                    set.Add(Find(sIdx));
                }
            }
            var readinessPerQuest = new List<float>();
            foreach (QuestModel q in Quests)
            {
                if (q.HandSegs != null || q.Kind == QuestKind.CloseGroup || q.Comparison != Comparison.MoreThan) continue;
                if (!touching.TryGetValue(q.GroupType, out var roots)) continue;
                var anchorRoots = new HashSet<int>(q.AnchorSegs.Select(Find));
                if (!roots.Overlaps(anchorRoots)) continue;
                QuestState now = QuestStateOf(q);
                if (now.Status != QuestStatus.InProgress) continue;
                // Value after merging by the center tile (not counting its own elements: safety margin)
                int merged = 0;
                foreach (int r in roots.Union(anchorRoots))
                    merged += q.Kind == QuestKind.CountElements ? (q.ElementIndex >= 0 ? elements[r][q.ElementIndex] : 0) : segCount[r];
                int afterCenter = merged + 1; // + at least one segment / element contributed by the tile
                int deficit = q.Target - afterCenter;
                float readiness = deficit <= 0 ? 1f : System.Math.Max(0f, 1f - (float)deficit / System.Math.Max(4, q.Target / 2));
                readinessPerQuest.Add(readiness);
            }
            // FlowerQuestsNeeded quests must be ready at the same time: average of the best ones (missing ones count as 0),
            // so that one ready quest and a second one well on its way already guide the preparation.
            int k = System.Math.Max(1, FlowerQuestsNeeded);
            return readinessPerQuest.OrderByDescending(r => r).Take(k).Sum() / k;
        }

        /// <summary>
        /// In-progress "at least N" quests whose area touches the flower's center c: they are reserved for the center
        /// tile (completing them elsewhere would lose the quest points of the final placement).
        /// </summary>
        public void ReserveQuestsAround(Vector2Int? center)
        {
            ReservedQuests = new HashSet<QuestModel>();
            CenterTouchRoots = new HashSet<int>();
            if (!center.HasValue || !FlowerNeedsQuest) return;
            Vector2Int c = center.Value;
            // No reservation until the inner ring is nearly finished: quests keep us alive.
            if (Ring1(c).Count(p => cells.ContainsKey(p)) < QuestPhaseRing1) return;
            var touching = new HashSet<int>();
            for (int d = 0; d < 6; d++)
            {
                if (!cells.TryGetValue(Hex.Neighbor(c, d), out Cell ca)) continue;
                int towardCenter = (d + 3) % 6;
                foreach (int sIdx in ca.Segs)
                    if ((segWorldEdges[sIdx] & (1 << towardCenter)) != 0) touching.Add(Find(sIdx));
            }
            CenterTouchRoots = touching;
            foreach (QuestModel q in Quests)
            {
                if (q.HandSegs != null || q.Kind == QuestKind.CloseGroup || q.Comparison != Comparison.MoreThan) continue;
                if (q.AnchorSegs.Any(a => touching.Contains(Find(a))) && QuestStateOf(q).Status == QuestStatus.InProgress)
                    ReservedQuests.Add(q);
            }
        }

        /// <summary>Share of sample tiles that fit (in some rotation) all the required edges.</summary>
        private static float MatchProbability(EdgeSpec[] req, List<TileSpec> sample)
        {
            int ok = 0;
            foreach (TileSpec t in sample)
                for (int rot = 0; rot < 6; rot++)
                {
                    bool fits = true;
                    for (int d = 0; d < 6 && fits; d++)
                        if (req[d] != null && !EdgesFit(t.Edges[LocalDir(d, rot)] ?? EdgeSpec.Empty, req[d])) fits = false;
                    if (fits) { ok++; break; }
                }
            return (float)ok / sample.Count;
        }

        /// <summary>
        /// Picks the most advanced flower center among the empty cells near the board. Keeps the current site
        /// as long as it is not ruined and no other one is clearly more advanced.
        /// </summary>
        public Vector2Int? SelectFlowerSite(List<TileSpec> sample, Vector2Int? current)
        {
            var candidates = new HashSet<Vector2Int>();
            foreach (Vector2Int f in frontier)
            {
                candidates.Add(f);
                for (int d = 0; d < 6; d++) candidates.Add(Hex.Neighbor(f, d));
            }
            Vector2Int? best = null;
            float bestValue = float.MinValue;
            foreach (Vector2Int c in candidates)
            {
                if (cells.ContainsKey(c) || ExcludedFlowerSites.Contains(c)) continue;
                FlowerState st = FlowerAt(c, sample, default, null);
                if (st.Ruined) continue;
                if (st.Value > bestValue) { bestValue = st.Value; best = c; }
            }
            if (current.HasValue && !ExcludedFlowerSites.Contains(current.Value))
            {
                FlowerState cur = FlowerAt(current.Value, sample, default, null);
                if (!cur.Ruined && cur.Ring1 == 6 && cur.Ring2 == 12) return current; // complete flower: wait for the right tile
                // Persistence: only switch to a clearly better site (otherwise two close sites alternate).
                if (!cur.Ruined && bestValue <= 1.5f * cur.Value + 2f) return current;
            }
            return best;
        }

        /// <summary>
        /// Effect of a placement on the flower under construction: change in its value, or ruin (broken match, center
        /// occupied without completing the challenge).
        /// </summary>
        public (float delta, bool ruins) FlowerPlanDelta(TileSpec t, Vector2Int pos, int rot, List<TileSpec> sample)
        {
            if (!FlowerSite.HasValue) return (0f, false);
            Vector2Int c = FlowerSite.Value;
            if (pos != c && !Ring1(c).Contains(pos) && !Ring2(c).Contains(pos)) return (0f, false);
            var virt = new Cell { Spec = t, Rot = rot, Segs = new int[0] };
            FlowerState before = FlowerAt(c, sample, default, null);
            FlowerState after = FlowerAt(c, sample, pos, virt);
            if (before.Ruined) return (0f, false);
            if (after.Ruined)
            {
                LastCenterWasted = pos == c && before.Ring1 == 6 && before.Ring2 == 12;
                LastRuinedProgress = 2 * before.Ring1 + before.Ring2;
                return (0f, true);
            }
            LastCenterWasted = false;
            float delta = after.Value - before.Value;

            // Player trick: a quest tile placed in the inner ring, with its quest area facing the center,
            // puts its quest in contact with the final tile (which will fulfill it by merging the groups).
            if (FlowerNeedsQuest && t == Hand && Ring1(c).Contains(pos))
            {
                int towardCenter = Enumerable.Range(0, 6).First(d => Hex.Neighbor(pos, d) == c);
                int local = LocalDir(towardCenter, rot);
                foreach (QuestModel q in Quests)
                    if (q.HandSegs != null && q.Kind != QuestKind.CloseGroup && q.Comparison == Comparison.MoreThan &&
                        q.HandSegs.Any(i => t.Segs[i].Edges.Contains(local)))
                        delta += QuestTileInRingBonus;
            }
            return (delta, false);
        }

        // ---------- Biome goal: growth prospects ----------

        private Dictionary<Vector2Int, (List<int> dirs, float prob)> goalSlotCache;

        /// <summary>Like Fits, with an optional "virtual" tile at vPos.</summary>
        private bool FitsWith(TileSpec t, Vector2Int pos, int rot, Vector2Int vPos, Cell virt)
        {
            for (int d = 0; d < 6; d++)
            {
                EdgeSpec mine = t.Edges[LocalDir(d, rot)] ?? EdgeSpec.Empty;
                Vector2Int np = Hex.Neighbor(pos, d);
                if (!TryCell(np, vPos, virt, out Cell nb)) ghosts.TryGetValue(np, out nb);
                EdgeSpec theirs = nb != null ? EdgeAt(nb, (d + 3) % 6) : EdgeSpec.Empty;
                foreach (byte g in theirs.NonHybrid)
                    if (Constraining[g] && !mine.Any.Contains(g)) return false;
                if (nb != null)
                    foreach (byte g in mine.NonHybrid)
                        if (Constraining[g] && !theirs.Any.Contains(g)) return false;
            }
            return true;
        }

        /// <summary>Share of sample tiles placeable at `slot` with type `type` on one of the `dirs` edges.</summary>
        private float ExtendProbability(Vector2Int slot, List<int> dirs, byte type, List<TileSpec> sample, Vector2Int vPos, Cell virt)
        {
            if (sample.Count == 0) return 0f;
            int ok = 0;
            foreach (TileSpec t in sample)
            {
                for (int rot = 0; rot < 6; rot++)
                {
                    bool extends = dirs.Any(d => (t.Edges[LocalDir(d, rot)] ?? EdgeSpec.Empty).Any.Contains(type));
                    if (extends && FitsWith(t, slot, rot, vPos, virt)) { ok++; break; }
                }
            }
            return (float)ok / sample.Count;
        }

        /// <summary>Empty cells facing a group's open edges, with the directions (as seen from the cell).</summary>
        private Dictionary<Vector2Int, List<int>> GroupOpenSlots(IEnumerable<int> segs)
        {
            var slots = new Dictionary<Vector2Int, List<int>>();
            foreach (var (slot, dir) in OpenEdgeSlots(segs))
            {
                if (ghosts.ContainsKey(slot)) continue;
                if (!slots.TryGetValue(slot, out List<int> dirs)) slots[slot] = dirs = new List<int>();
                if (!dirs.Contains(dir)) dirs.Add(dir);
            }
            return slots;
        }

        /// <summary>Prospects of a group: sum, over its open cells, of the chances of extending it.</summary>
        public float GroupProspects(IEnumerable<int> segs, byte type, List<TileSpec> sample) =>
            GroupOpenSlots(segs).Sum(kv => ExtendProbability(kv.Key, kv.Value, type, sample, default, null));

        /// <summary>
        /// Group to target for a goal: the best size × prospects trade-off. A "trapped" group
        /// (open cells where almost no tile can extend it) is heavily penalized.
        /// </summary>
        public int[] SelectGoalGroup(byte type, int elementIndex, List<TileSpec> sample, int currentAnchor = -1)
        {
            int bestRoot = -1;
            float best = float.MinValue;
            int currentRoot = currentAnchor >= 0 && currentAnchor < parent.Count ? Find(currentAnchor) : -1;
            if (currentRoot >= 0 && (segType[currentRoot] != type || open[currentRoot] <= 0)) currentRoot = -1;
            float currentScore = 0f, currentProspects = 0f;

            for (int i = 0; i < parent.Count; i++)
            {
                if (parent[i] != i || segType[i] != type || open[i] <= 0) continue;
                int size = elementIndex >= 0 ? elements[i][elementIndex] : segCount[i];
                int root = i;
                float prospects = GroupProspects(Enumerable.Range(0, parent.Count).Where(x => Find(x) == root), type, sample);
                float score = size * System.Math.Min(1f, prospects / 0.5f) + 0.01f * prospects;
                if (root == currentRoot) { currentScore = score; currentProspects = prospects; }
                if (score > best) { best = score; bestRoot = i; }
            }
            // Persistence: keep the current target while it is open, unless it is trapped
            // and another group is clearly more promising.
            if (currentRoot >= 0 && !(currentProspects < 0.2f && best > 1.5f * currentScore)) bestRoot = currentRoot;
            if (bestRoot < 0) return new int[0];
            return Enumerable.Range(0, parent.Count).Where(i => Find(i) == bestRoot).ToArray();
        }

        /// <summary>
        /// Change in the target group's prospects if `t` is placed at `pos` (rotation `rot`): open cells
        /// around pos whose constraints change, the pos cell disappearing, and new open edges contributed
        /// by the tile if it joins the group (ev.GoalMergedSegs).
        /// </summary>
        public float GoalProspectDelta(TileSpec t, Vector2Int pos, int rot, PlacementEval ev, List<TileSpec> sample)
        {
            if (Goal == null || Goal.AnchorSegs.Length == 0 || sample == null || sample.Count == 0) return 0f;
            if (goalSlotCache == null)
                goalSlotCache = GroupOpenSlots(Goal.AnchorSegs).ToDictionary(
                    kv => kv.Key, kv => (kv.Value, ExtendProbability(kv.Key, kv.Value, Goal.GroupType, sample, default, null)));

            var virt = new Cell { Spec = t, Rot = rot, Segs = new int[0] };
            float before = 0f, after = 0f;

            // The group's open cells affected by the placement: pos itself and its neighbors.
            var afterDirs = new Dictionary<Vector2Int, List<int>>();
            foreach (var kv in goalSlotCache)
            {
                if (kv.Key == pos) { before += kv.Value.prob; continue; }
                bool adjacent = false;
                for (int d = 0; d < 6 && !adjacent; d++) adjacent = Hex.Neighbor(pos, d) == kv.Key;
                if (!adjacent) continue;
                before += kv.Value.prob;
                afterDirs[kv.Key] = new List<int>(kv.Value.dirs);
            }

            // New open edges contributed by our segments that join the group.
            foreach (int i in ev.GoalMergedSegs)
                foreach (int le in t.Segs[i].Edges)
                {
                    int wd = WorldDir(le, rot);
                    Vector2Int q = Hex.Neighbor(pos, wd);
                    if (cells.ContainsKey(q) || ghosts.ContainsKey(q)) continue;
                    if (!afterDirs.TryGetValue(q, out List<int> dirs)) afterDirs[q] = dirs = new List<int>();
                    int fromSlot = (wd + 3) % 6;
                    if (!dirs.Contains(fromSlot)) dirs.Add(fromSlot);
                }

            foreach (var kv in afterDirs) after += ExtendProbability(kv.Key, kv.Value, Goal.GroupType, sample, pos, virt);
            return after - before;
        }

        /// <summary>Largest group of a type, open or closed ("form a large group" goals).</summary>
        public int LargestGroup(byte type, int elementIndex)
        {
            int best = 0;
            for (int i = 0; i < parent.Count; i++)
            {
                if (parent[i] != i || segType[i] != type) continue;
                int v = elementIndex >= 0 ? elements[i][elementIndex] : segCount[i];
                if (v > best) best = v;
            }
            return best;
        }

        /// <summary>Largest closed group of a type (in elements): used to detect that a goal is reached.</summary>
        public int LargestClosedGroup(byte type, int elementIndex)
        {
            int best = 0;
            for (int i = 0; i < parent.Count; i++)
            {
                if (parent[i] != i || segType[i] != type || open[i] > 0) continue;
                int v = elementIndex >= 0 ? elements[i][elementIndex] : segCount[i];
                if (v > best) best = v;
            }
            return best;
        }

        /// <summary>Finds a segment by (cell, type, world edge mask). -1 if not found.</summary>
        public int FindSeg(Vector2Int pos, byte type, int worldEdgeMask)
        {
            if (!cells.TryGetValue(pos, out Cell c)) return -1;
            foreach (int s in c.Segs)
                if (segType[s] == type && segWorldEdges[s] == worldEdgeMask) return s;
            foreach (int s in c.Segs) // fallback: same type, overlapping edges
                if (segType[s] == type && (segWorldEdges[s] & worldEdgeMask) != 0) return s;
            return -1;
        }

        /// <summary>Diagnostic: for each distinct anchor group, its type, open edges and segment count.</summary>
        public string DescribeAnchors(int[] anchors) =>
            string.Join(", ", anchors.Select(Find).Distinct().Select(r => $"[type {segType[r]} open {open[r]} segs {segCount[r]}]"));

        /// <summary>Cell and world edge mask of a segment (to find it again in another snapshot).</summary>
        public (Vector2Int pos, int mask) DescribeSeg(int seg)
        {
            foreach (var kv in cells)
                if (kv.Value.Segs.Contains(seg)) return (kv.Key, segWorldEdges[seg]);
            return (default, 0);
        }

        /// <summary>Diagnostic: edge as seen from the cell neighboring `pos` in direction `dir`.</summary>
        public string DescribeNeighborEdge(Vector2Int pos, int dir)
        {
            Vector2Int np = Hex.Neighbor(pos, dir);
            if (!cells.TryGetValue(np, out Cell nb)) return $"{np}: empty";
            EdgeSpec e = EdgeAt(nb, (dir + 3) % 6);
            return $"{np} {nb.Spec.Name} rot{nb.Rot}: [{string.Join(",", e.Any)}]";
        }
    }
}
