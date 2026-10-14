using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DorfHelper.Model
{
    /// <summary>Grille hexagonale du jeu (copie de GridCalculator, qui est internal).</summary>
    internal static class Hex
    {
        private static readonly Vector2Int[] Even =
            { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(-1, 0), new Vector2Int(-1, 1) };

        private static readonly Vector2Int[] Odd =
            { new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(1, -1), new Vector2Int(0, -1), new Vector2Int(-1, -1), new Vector2Int(-1, 0) };

        public static Vector2Int Neighbor(Vector2Int p, int dir) => p + (p.x % 2 == 0 ? Even : Odd)[dir];
    }

    /// <summary>Un bord de tuile : types de groupe présents (ids de GroupTypeId).</summary>
    internal sealed class EdgeSpec
    {
        public static readonly EdgeSpec Empty = new EdgeSpec { Any = new byte[0], NonHybrid = new byte[0] };
        public byte[] Any;
        public byte[] NonHybrid;
        public bool Hybrid;
    }

    /// <summary>Un segment de tuile (une zone forêt, village…), dans le repère de la tuile.</summary>
    internal sealed class SegSpec
    {
        public byte Type;
        public int[] Edges;    // directions (repère tuile) couvertes par le segment
        public int[] Elements; // nombre d'éléments par type suivi (index = Board.ElementKinds)
    }

    /// <summary>Description pure d'une tuile (sans objet Unity), dans son repère propre.</summary>
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
        public int[] AnchorSegs; // segments surveillés (via leurs groupes)

        /// <summary>
        /// Quête portée par la tuile en main : elle n'existe qu'une fois la tuile posée. Ses ancres sont alors
        /// les segments de la tuile posée (indices dans TileSpec.Segs de la main).
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
        public int Open; // bords encore ouverts sur les groupes surveillés
    }

    /// <summary>Résultat de l'évaluation d'une pose (sans modifier le plateau).</summary>
    internal sealed class PlacementEval
    {
        public Vector2Int Pos;
        public int Rotation;
        public int Perfect, Mismatched, Empty;
        public int QuestsFulfilled, QuestsFailed;
        public float QuestProgress; // somme des progrès relatifs (Δ / cible) sur les quêtes de comptage
        public int QuestsTouched;
        public int QuestsEndangered; // quêtes de comptage en cours dont la zone n'a plus qu'un bord ouvert
        public float Potential;      // variation du nombre attendu de futurs placements parfaits (voir Board.PotentialDelta)
        public int PerfectTriggered; // placements parfaits déclenchés (la tuile posée + voisines complétées)
        public bool GoalActive;          // un objectif de biome est suivi (voir Board.Goal)
        public int GoalTarget;
        public float GoalScale = 1f;
        public bool GoalClose = true;
        public float GoalElementsPerSegment = 1f;
        public QuestState GoalBefore, GoalAfter;
        public int[] GoalMergedSegs = new int[0]; // segments de la tuile posée qui rejoignent le groupe ciblé
        public float GoalProspectDelta;           // variation des perspectives de croissance du groupe ciblé
        // Objectif "plusieurs groupes" (fermer N groupes d'au moins S éléments)
        public bool ManyActive;
        public int ManyClosedQualifying;  // groupes fermés par cette pose avec au moins S éléments
        public int ManyClosedSmall;       // groupes fermés trop petits
        public int ManyGrowth;            // progression vers S (plafonnée à S par groupe ; une fusion de gros groupes est négative)
        public int ManyCloseProgress;     // bords refermés sur les groupes qui ont déjà S éléments

        public int Points;                // points rapportés par la pose (raccords, parfaits, groupes fermés, quêtes)
        public int MaxClosedGroupSegs;    // plus grand groupe fermé par la pose (en segments)
        public float FlowerDelta;         // variation de la "maturité" des fleurs (défi : 7 placements parfaits d'un coup)
        public int AtOnceKind, AtOnceTarget;
        public bool FlowerRuined;  // la pose gâche la fleur en construction
        public bool FlowerCenterWasted; // tuile posée au centre d'une fleur complète sans atteindre la cible
        public int FlowerRuinedProgress; // avancement de la fleur gâchée (pénalité proportionnelle)
        public int ReservedQuestsSpent; // quêtes réservées pour la tuile du centre, terminées ailleurs par cette pose
        public float ReservedProgress;  // progression (relative à la cible) des quêtes réservées, sans les terminer
        public int HandQuestNearCenter; // quête de la tuile en main, posée sur un groupe qui touche le centre de la fleur
        public int TilesGained;      // tuiles gagnées : placements parfaits + quêtes réussies
        public int Neighbors => 6 - Empty;
        public bool PerfectPlacement => Empty == 0 && Mismatched == 0;
    }

    /// <summary>
    /// Modèle pur du plateau : cases, segments et groupes (union-find), quêtes.
    /// Reproduit les règles du jeu : un bord de segment est "ouvert" tant qu'aucune tuile ne le touche,
    /// deux segments du même type sur des bords en vis-à-vis fusionnent leurs groupes.
    /// </summary>
    internal sealed class Board
    {
        public const int TypeCount = 5; // GroupTypeId : Village, Forest, Agriculture, TrainTracks, Water

        private sealed class Cell
        {
            public TileSpec Spec;
            public int Rot;
            public int[] Segs; // indices globaux des segments de la case
        }

        public int ElementKinds;
        public int PerfectReward = 2; // tuiles gagnées par placement parfait (RewardSystemConfiguration)
        public int QuestReward = 0;   // tuiles gagnées par quête réussie (dépend du score)
        /// <summary>
        /// Objectif de biome (ex. "fermer un champ de 150 champs") : suivi comme une quête "au moins N éléments"
        /// sur le groupe ciblé (ancres = ses segments). Null si aucun objectif.
        /// </summary>
        public QuestModel Goal;
        public float GoalScale = 1f; // 0..1 : poids de l'objectif, réduit quand la pioche est basse (survie d'abord)
        public bool GoalClose = true; // true : il faut fermer le groupe ; false : il suffit de l'atteindre
        public float GoalElementsPerSegment = 1f; // moyenne d'éléments par segment du terrain visé (normalise la croissance)

        /// <summary>Moyenne d'éléments (ou 1 si on compte les segments) par segment d'un type, sur un échantillon.</summary>
        public static float ElementsPerSegment(IEnumerable<TileSpec> sample, byte type, int elementIndex)
        {
            if (elementIndex < 0) return 1f;
            int segs = 0, elems = 0;
            foreach (TileSpec t in sample)
                foreach (SegSpec sg in t.Segs)
                    if (sg.Type == type && elementIndex < sg.Elements.Length) { segs++; elems += sg.Elements[elementIndex]; }
            return segs == 0 ? 1f : System.Math.Max(1f, (float)elems / segs);
        }

        // Points (RewardSystemConfiguration), lus en jeu ; valeurs par défaut du code sinon
        public int FitScoreMethod;            // 0 linéaire, 1 2^x, 2 x²
        public int FitScoreModifier = 2;
        public bool RewardTileFit = true;
        public int PerfectScore = 60;
        public bool RewardGroups = true;
        public float GroupScorePow = 1.2f;
        public float GroupScoreMultiplier = 3f;
        public int QuestScore = 100;

        /// <summary>Points rapportés par la fermeture d'un groupe de `segments` segments (ElementGroupManager.CloseGroup).</summary>
        public int GroupClosingPoints(int segments) =>
            RewardGroups && segments > 1 ? (int)System.Math.Round(System.Math.Pow(segments - 1, GroupScorePow) * GroupScoreMultiplier) : 0;

        /// <summary>Points d'une tuile au centre d'une fleur complète (7 placements parfaits + 6 raccords), hors quêtes.</summary>
        public int FlowerJackpotPoints()
        {
            int fit = !RewardTileFit ? 0 : FitScoreMethod == 0 ? FitScoreModifier * 6
                : FitScoreMethod == 1 ? (int)System.Math.Round(System.Math.Pow(2, FitScoreModifier))
                : (int)System.Math.Round(System.Math.Pow(FitScoreModifier, 2));
            return 7 * PerfectScore + fit;
        }

        /// <summary>Plus petit nombre de segments dont la fermeture rapporte au moins `points` (marge pour les raccords).</summary>
        public int SegmentsForPoints(int points)
        {
            for (int n = 2; n < 2000; n++)
                if (GroupClosingPoints(n) + 12 >= points) return n;
            return 2000;
        }

        /// <summary>Le groupe de ce segment est-il encore ouvert ?</summary>
        public bool IsSegOpen(int seg) => seg >= 0 && seg < parent.Count && open[Find(seg)] > 0;

        /// <summary>Type d'un segment.</summary>
        public int SegType(int seg) => seg >= 0 && seg < segType.Count ? segType[seg] : -1;

        /// <summary>Type du plus grand groupe ouvert (en segments), ou -1.</summary>
        public int LargestOpenGroupType()
        {
            int best = 0, type = -1;
            for (int i = 0; i < parent.Count; i++)
                if (parent[i] == i && open[i] > 0 && segCount[i] > best) { best = segCount[i]; type = segType[i]; }
            return type;
        }

        // Défis "d'un coup" : 0 aucun, 1 placements parfaits d'un coup (cible = nombre), 2 points en une pose (cible = points)
        public int AtOnceKind;
        public int AtOnceTarget;

        // Objectif "plusieurs groupes" : fermer des groupes de type ManyType d'au moins ManySize éléments
        public int ManyType = -1;
        public int ManyElement = -1;
        public int ManySize;
        public TileSpec Hand; // tuile en main (porte éventuellement une quête, voir QuestModel.HandSegs)
        public bool[] Constraining = new bool[TypeCount];
        public List<QuestModel> Quests = new List<QuestModel>();

        private Dictionary<Vector2Int, Cell> cells = new Dictionary<Vector2Int, Cell>();

        // Tuiles de quête "pré-placées" sur la carte : elles contraignent les raccordements (rivières, rails)
        // des cases voisines, mais ne sont pas sur le plateau (ne ferment aucun bord, ne fusionnent rien).
        private Dictionary<Vector2Int, Cell> ghosts = new Dictionary<Vector2Int, Cell>();

        public void AddGhost(TileSpec t, Vector2Int pos, int rot)
        {
            ghosts = new Dictionary<Vector2Int, Cell>(ghosts) { [pos] = new Cell { Spec = t, Rot = rot, Segs = new int[0] } };
            frontier.Remove(pos);
        }
        private HashSet<Vector2Int> frontier = new HashSet<Vector2Int>();

        // Segments (index global) et union-find
        private List<byte> segType = new List<byte>();
        private List<int> segWorldEdges = new List<int>(); // masque 6 bits
        private List<int> parent = new List<int>();
        // Agrégats, valides sur les racines
        private List<int> open = new List<int>();
        private List<int[]> elements = new List<int[]>();
        private List<int> segCount = new List<int>();

        public int TileCount => cells.Count;
        public IEnumerable<Vector2Int> Frontier => frontier;
        public bool IsOccupied(Vector2Int p) => cells.ContainsKey(p);

        public Board Clone()
        {
            var b = (Board)MemberwiseClone();
            b.cells = new Dictionary<Vector2Int, Cell>(cells); // les Cell ne sont jamais modifiées après création
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
            b.ReservedQuests = new HashSet<QuestModel>(); // les quêtes réservées ne concernent que la pose immédiate
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

        /// <summary>Deux bords face à face "collent" (même règle que Tile.PlaceNeighbor / l'AutoPlacer).</summary>
        private static bool EdgesFit(EdgeSpec mine, EdgeSpec theirs) =>
            (mine.Any.Length == 0 && theirs.Any.Length == 0)
            || (mine.Hybrid && theirs.Any.Length == 0)
            || (mine.Any.Length == 0 && theirs.Hybrid)
            || mine.Any.Any(theirs.Any.Contains);

        /// <summary>
        /// La voisine en `np` devient-elle un placement parfait si on pose en `pos` (dont le bord vers elle colle ou non) ?
        /// Il faut ses 6 côtés occupés et tous ses bords qui collent.
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

        // ---------- Règles ----------

        /// <summary>Même règle que TileFitter.TileFitsInSlot (rivières et rails doivent se raccorder).</summary>
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

        /// <summary>Évalue une pose sans modifier le plateau : bords, puis effet exact sur chaque quête.</summary>
        public PlacementEval Evaluate(TileSpec t, Vector2Int pos, int rot, List<QuestState> questStatesAfter = null)
        {
            bool isHand = t == Hand;
            var ev = new PlacementEval { Pos = pos, Rotation = rot };
            int k = t.Segs.Length;

            // Nœuds locaux : 0..k-1 = nos segments, puis les racines voisines touchées.
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

                // Les segments voisins qui touchent ce bord perdent un bord ouvert, et fusionnent avec nous si même type.
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

            // Agrégats des composantes locales
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

            // Quêtes : état avant / après
            foreach (QuestModel q in Quests)
            {
                if (q.HandSegs != null && !isHand) { questStatesAfter?.Add(default); continue; }
                if (q.HandSegs != null && CenterTouchRoots.Count > 0 && q.Kind != QuestKind.CloseGroup && q.Comparison == Comparison.MoreThan)
                {
                    // Nos segments de quête fusionnent-ils avec un groupe qui touche le centre ?
                    var questComps = new HashSet<int>(q.HandSegs.Select(i => LFind(i)));
                    bool joins = CenterTouchRoots.Any(r => rootIndex.TryGetValue(r, out int li) && questComps.Contains(LFind(li)));
                    if (joins) ev.HandQuestNearCenter++;
                }
                QuestState before = q.HandSegs != null
                    ? new QuestState { Value = 0, Status = QuestStatus.InProgress } // la quête n'existe pas encore
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
                    // Plus qu'un bord ouvert : la prochaine tuile posée là sans prolonger la zone fera échouer la quête.
                    if (after.Open == 1 && before.Open != 1) ev.QuestsEndangered++;
                }
            }
            if (ManyType >= 0) EvaluateMany(ev, t, k, roots, localParent.Count, LFind, cOpen, cSeg, cElem);

            if (Goal != null && Goal.AnchorSegs.Length > 0)
            {
                ev.GoalActive = true; // les protections restent actives même quand la réserve est basse
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

            // Points de la pose, comme RewardSystem.AddTilePlacementScore + fermetures de groupes + quêtes
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
                // groupe fermé par la pose : il contient un de nos segments ou une racine voisine (toutes ouvertes avant)
                groupPoints += GroupClosingPoints(cSeg[r]);
                if (cSeg[r] > ev.MaxClosedGroupSegs) ev.MaxClosedGroupSegs = cSeg[r];
            }
            ev.Points = fitPoints + ev.PerfectTriggered * PerfectScore + groupPoints + ev.QuestsFulfilled * QuestScore;
            ev.AtOnceKind = AtOnceKind;
            ev.AtOnceTarget = AtOnceTarget;
            if (AtOnceKind != 0) ev.GoalScale = GoalScale;
            return ev;
        }

        /// <summary>Effets d'une pose sur l'objectif "plusieurs groupes", à partir des composantes locales.</summary>
        private void EvaluateMany(PlacementEval ev, TileSpec t, int k, List<int> roots, int n, System.Func<int, int> lfind,
                                  int[] cOpen, int[] cSeg, int[][] cElem)
        {
            ev.ManyActive = GoalScale > 0f;
            int Size(int value) => System.Math.Min(value, ManySize);
            // Valeur "avant" des racines touchées, regroupées par composante locale
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
        /// Valeur et statut d'une quête, comme GroupConditionWatcher. Si les tableaux locaux sont fournis,
        /// les racines touchées par la pose sont remplacées par leur composante fusionnée.
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
            if (st.Status == QuestStatus.InProgress && allClosed) st.Status = QuestStatus.Failed; // groupe fermé trop tôt
            return st;
        }

        /// <summary>Pose réellement la tuile dans le modèle. Renvoie les indices globaux de ses segments.</summary>
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

        // ---------- Potentiel : futurs placements parfaits ----------
        //
        // Une tuile posée sans bord raté deviendra parfaite (+1 tuile) quand ses côtés vides seront remplis par des
        // tuiles qui collent. Pour chaque case vide, on estime la probabilité qu'une tuile "au hasard" (échantillon
        // des tuiles déjà vues) y colle parfaitement dans au moins une rotation. Le potentiel d'une tuile est le
        // produit de ces probabilités sur ses côtés vides ; le potentiel du plateau est la somme sur les tuiles.

        private Dictionary<Vector2Int, float> fitProbabilityCache = new Dictionary<Vector2Int, float>();
        private Dictionary<Vector2Int, float> tilePotentialCache = new Dictionary<Vector2Int, float>();

        /// <summary>Case occupée, en tenant compte d'une tuile "virtuelle" posée en vPos (pour évaluer une pose).</summary>
        private bool TryCell(Vector2Int p, Vector2Int vPos, Cell virt, out Cell c)
        {
            if (virt != null && p == vPos) { c = virt; return true; }
            return cells.TryGetValue(p, out c);
        }

        /// <summary>Probabilité qu'une tuile de l'échantillon colle parfaitement en `slot` (1 si aucun voisin).</summary>
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

        /// <summary>Potentiel d'une tuile : 0 si un bord est raté ou si elle n'a plus de côté vide (déjà comptée).</summary>
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
                    if (!EdgesFit(EdgeAt(c, d), EdgeAt(n, (d + 3) % 6))) return 0f; // bord raté : jamais parfaite
                    continue;
                }
                if (ghosts.ContainsKey(q)) return 0f; // case réservée à une tuile de quête : on ne compte pas dessus
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
        /// Variation du potentiel si on pose `t` en `pos` avec la rotation `rot`. Seules les cases voisines de `pos`
        /// et les tuiles qui les bordent sont touchées. Les placements parfaits réalisés tout de suite sont comptés
        /// dans TilesGained, pas ici (leur potentiel tombe à 0).
        /// </summary>
        public float PotentialDelta(TileSpec t, Vector2Int pos, int rot, List<TileSpec> sample)
        {
            if (sample == null || sample.Count == 0) return 0f;
            var virt = new Cell { Spec = t, Rot = rot, Segs = new int[0] };

            // Tuiles touchées : voisines de pos, et voisines des cases vides autour de pos.
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

            // Après : probabilités recalculées pour les cases autour de pos, reprises du cache pour les autres.
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
        /// Taille du plus grand groupe encore ouvert d'un type (en éléments ou en segments) : c'est la référence
        /// du jeu pour fixer l'objectif d'une nouvelle quête (QuestManager.ReferenceGroupCount).
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

        /// <summary>Segments du plus grand groupe ouvert d'un type (en éléments), ou tableau vide.</summary>
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

        /// <summary>Cases vides face aux bords ouverts des segments donnés (et la direction du bord, vue depuis la case).</summary>
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

        // ---------- Défi : 7 placements parfaits d'un coup ("fleur") ----------
        //
        // Une case vide H est une fleur prête quand ses 6 voisines sont posées, chacune entourée sur ses 5 autres
        // côtés avec tous ses bords qui collent : une tuile qui colle sur les 6 bords de H déclenche alors 7 placements
        // parfaits. Maturité de H = (voisines "complètes sauf H")², pondérée par la chance de trouver la tuile finale.

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

        /// <summary>Variation de la maturité des fleurs autour de `pos` si on y pose `t` (rotation `rot`).</summary>
        public float FlowerDelta(TileSpec t, Vector2Int pos, int rot, List<TileSpec> sample)
        {
            if (sample == null || sample.Count == 0) return 0f;
            var virt = new Cell { Spec = t, Rot = rot, Segs = new int[0] };
            // Cases vides à distance ≤ 2 de pos (leurs voisines peuvent changer d'état), et pos elle-même
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

        // ---------- Défi : 7 placements parfaits d'un coup — planification d'une "fleur" ----------
        //
        // Fleur = centre C (laissé vide jusqu'au bout) + anneau intérieur R1 (6) + anneau extérieur R2 (12).
        // À la fin, chaque tuile de R1 doit avoir ses 6 voisines posées avec tous ses bords qui collent (sauf vers C),
        // puis une tuile qui colle sur les 6 bords de C déclenche 7 placements parfaits.

        public Vector2Int? FlowerSite; // centre de la fleur en construction (choisi par SelectFlowerSite)
        public bool LastCenterWasted;  // dernier FlowerPlanDelta : pose au centre d'une fleur complète
        public int LastRuinedProgress; // dernier FlowerPlanDelta : avancement (2×intérieur + extérieur) de la fleur gâchée
        public bool FlowerNeedsQuest;  // la fleur seule ne suffit pas : la tuile du centre doit aussi réussir une quête
        public int FlowerQuestsNeeded = 1; // nombre de quêtes que la tuile du centre doit réussir pour atteindre la cible
        public HashSet<QuestModel> ReservedQuests = new HashSet<QuestModel>(); // quêtes à garder pour la tuile du centre
        public HashSet<int> CenterTouchRoots = new HashSet<int>(); // groupes qui touchent le centre de la fleur (phase quêtes)
        public float QuestTileInRingBonus = 3f;
        public int QuestPhaseRing1 = 5;     // la préparation / réservation de quêtes commence à 5 tuiles intérieures sur 6
        public HashSet<Vector2Int> ExcludedFlowerSites = new HashSet<Vector2Int>(); // sites abandonnés (bloqués) // (en points de valeur de fleur) tuile de quête dans l'anneau, quête vers le centre
        public float FlowerQuestWeight = 80f; // poids de la "quête prête au centre" dans la valeur d'un site

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
            public bool Ruined;      // un raccord de la fleur est raté, ou le centre est occupé
            public int Ring1, Ring2; // positions occupées (et cohérentes) de chaque anneau
            public float CenterChance; // part des tuiles qui colleraient aux bords tournés vers le centre
            public float Completion;   // produit des chances de remplir chaque position manquante
            public float QuestReadiness; // 0..1 : une quête touchant le centre serait réussie par la tuile du centre
            public float Value;
        }

        /// <summary>État d'une fleur centrée en c (avec une éventuelle tuile virtuelle en vPos).</summary>
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
                    if (b == c) { needs[(d + 3) % 6] = EdgeAt(ca, d); continue; } // bord tourné vers le centre
                    if (ghosts.ContainsKey(b)) { st.Ruined = true; return st; }
                    if (TryCell(b, vPos, virt, out Cell cb) && !EdgesFit(EdgeAt(ca, d), EdgeAt(cb, (d + 3) % 6)))
                    {
                        st.Ruined = true; // les voisines de R1 (R1 et R2) doivent toutes coller
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

            // Chance de pouvoir remplir chaque position manquante (une tuile qui colle à toutes ses voisines de la fleur)
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
                        // R1 : doit coller à toutes ses voisines posées ; R2 : seulement à ses voisines de R1
                        if ((inner || r1.Contains(q)) && TryCell(q, vPos, virt, out Cell nq)) req[d] = EdgeAt(nq, (d + 3) % 6);
                    }
                    st.Completion *= System.Math.Max(0.02f, MatchProbability(req, sample));
                }
            }

            // Chance de trouver la tuile du centre (sur les bords déjà connus)
            st.CenterChance = sample != null && sample.Count > 0 && st.Ring1 > 0 ? MatchProbability(needs, sample) : 1f;

            // Avancement + chance de terminer : un site bloqué (positions presque impossibles à remplir) vaut peu.
            float progress = 4f * st.Ring1 + 1.5f * st.Ring2;
            // Construire d'abord : la préparation de quête ne compte qu'avec un anneau intérieur presque terminé.
            if (FlowerNeedsQuest && st.Ring1 >= QuestPhaseRing1) st.QuestReadiness = CenterQuestReadiness(c);

            // Le centre doit rester réalisable : sinon la fleur, même complète, ne servira à rien.
            float feasible = (float)System.Math.Sqrt(st.Completion * st.CenterChance);
            st.Value = progress * feasible + 15f * st.Ring1 / 6f * feasible
                       + (FlowerNeedsQuest ? FlowerQuestWeight * st.QuestReadiness * feasible : 0f); // inutile si le centre est impossible
            return st;
        }

        /// <summary>
        /// Une quête "au moins N" serait-elle réussie par une tuile posée en c ? La tuile fusionnera les groupes du même
        /// terrain qui touchent c (par les tuiles posées autour) : on additionne ces groupes, plus l'apport moyen d'un
        /// segment, et on compare à la cible. 1 = réussie à coup sûr, décroît avec le manque.
        /// </summary>
        private float CenterQuestReadiness(Vector2Int c)
        {
            // Racines des segments qui touchent c, par type
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
                // Valeur après fusion par la tuile du centre (sans compter ses propres éléments : marge de sécurité)
                int merged = 0;
                foreach (int r in roots.Union(anchorRoots))
                    merged += q.Kind == QuestKind.CountElements ? (q.ElementIndex >= 0 ? elements[r][q.ElementIndex] : 0) : segCount[r];
                int afterCenter = merged + 1; // + au moins un segment / élément apporté par la tuile
                int deficit = q.Target - afterCenter;
                float readiness = deficit <= 0 ? 1f : System.Math.Max(0f, 1f - (float)deficit / System.Math.Max(4, q.Target / 2));
                readinessPerQuest.Add(readiness);
            }
            // Il faut FlowerQuestsNeeded quêtes prêtes à la fois : moyenne des meilleures (les manquantes comptent 0),
            // pour qu'une quête prête et une seconde en bonne voie guident déjà la préparation.
            int k = System.Math.Max(1, FlowerQuestsNeeded);
            return readinessPerQuest.OrderByDescending(r => r).Take(k).Sum() / k;
        }

        /// <summary>
        /// Quêtes "au moins N" en cours dont la zone touche le centre c de la fleur : elles sont réservées à la tuile du
        /// centre (la terminer ailleurs ferait perdre les points de quête de la pose finale).
        /// </summary>
        public void ReserveQuestsAround(Vector2Int? center)
        {
            ReservedQuests = new HashSet<QuestModel>();
            CenterTouchRoots = new HashSet<int>();
            if (!center.HasValue || !FlowerNeedsQuest) return;
            Vector2Int c = center.Value;
            // Pas de réservation tant que l'anneau intérieur n'est pas presque fini : les quêtes font survivre.
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

        /// <summary>Part des tuiles de l'échantillon qui collent (dans une rotation) à tous les bords demandés.</summary>
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
        /// Choisit le centre de fleur le plus avancé parmi les cases vides proches du plateau. Garde le site actuel
        /// tant qu'il n'est pas gâché et qu'aucun autre n'est nettement plus avancé.
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
                if (!cur.Ruined && cur.Ring1 == 6 && cur.Ring2 == 12) return current; // fleur complète : on attend la bonne tuile
                // Persévérance : on ne change que pour un site nettement meilleur (sinon deux sites proches alternent).
                if (!cur.Ruined && bestValue <= 1.5f * cur.Value + 2f) return current;
            }
            return best;
        }

        /// <summary>
        /// Effet d'une pose sur la fleur en construction : variation de sa valeur, ou gâchis (raccord raté, centre
        /// occupé sans réussir le défi).
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

            // Astuce de joueur : une tuile de quête posée dans l'anneau intérieur, sa zone de quête tournée vers le centre,
            // met sa quête au contact de la tuile finale (qui la réussira en fusionnant les groupes).
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

        // ---------- Objectif de biome : perspectives de croissance ----------

        private Dictionary<Vector2Int, (List<int> dirs, float prob)> goalSlotCache;

        /// <summary>Comme Fits, avec une tuile "virtuelle" éventuelle en vPos.</summary>
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

        /// <summary>Part des tuiles de l'échantillon posables en `slot` avec le type `type` sur l'un des bords `dirs`.</summary>
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

        /// <summary>Cases vides face aux bords ouverts d'un groupe, avec les directions (vues depuis la case).</summary>
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

        /// <summary>Perspectives d'un groupe : somme, sur ses cases ouvertes, des chances de pouvoir le prolonger.</summary>
        public float GroupProspects(IEnumerable<int> segs, byte type, List<TileSpec> sample) =>
            GroupOpenSlots(segs).Sum(kv => ExtendProbability(kv.Key, kv.Value, type, sample, default, null));

        /// <summary>
        /// Groupe à viser pour un objectif : le meilleur compromis taille × perspectives. Un groupe "piégé"
        /// (cases ouvertes où presque aucune tuile ne peut le prolonger) est fortement déprécié.
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
            // Persévérance : on garde la cible actuelle tant qu'elle est ouverte, sauf si elle est piégée
            // et qu'un autre groupe est nettement plus prometteur.
            if (currentRoot >= 0 && !(currentProspects < 0.2f && best > 1.5f * currentScore)) bestRoot = currentRoot;
            if (bestRoot < 0) return new int[0];
            return Enumerable.Range(0, parent.Count).Where(i => Find(i) == bestRoot).ToArray();
        }

        /// <summary>
        /// Variation des perspectives du groupe ciblé si on pose `t` en `pos` (rotation `rot`) : cases ouvertes
        /// autour de pos dont les contraintes changent, case pos qui disparaît, et nouveaux bords ouverts apportés
        /// par la tuile si elle rejoint le groupe (ev.GoalMergedSegs).
        /// </summary>
        public float GoalProspectDelta(TileSpec t, Vector2Int pos, int rot, PlacementEval ev, List<TileSpec> sample)
        {
            if (Goal == null || Goal.AnchorSegs.Length == 0 || sample == null || sample.Count == 0) return 0f;
            if (goalSlotCache == null)
                goalSlotCache = GroupOpenSlots(Goal.AnchorSegs).ToDictionary(
                    kv => kv.Key, kv => (kv.Value, ExtendProbability(kv.Key, kv.Value, Goal.GroupType, sample, default, null)));

            var virt = new Cell { Spec = t, Rot = rot, Segs = new int[0] };
            float before = 0f, after = 0f;

            // Cases ouvertes du groupe touchées par la pose : pos elle-même et ses voisines.
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

            // Nouveaux bords ouverts apportés par nos segments qui rejoignent le groupe.
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

        /// <summary>Plus grand groupe d'un type, ouvert ou fermé (objectifs "former un grand groupe").</summary>
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

        /// <summary>Plus grand groupe fermé d'un type (en éléments) : sert à détecter qu'un objectif est atteint.</summary>
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

        /// <summary>Retrouve un segment par (case, type, masque de bords monde). -1 si absent.</summary>
        public int FindSeg(Vector2Int pos, byte type, int worldEdgeMask)
        {
            if (!cells.TryGetValue(pos, out Cell c)) return -1;
            foreach (int s in c.Segs)
                if (segType[s] == type && segWorldEdges[s] == worldEdgeMask) return s;
            foreach (int s in c.Segs) // à défaut : même type, bords qui se recoupent
                if (segType[s] == type && (segWorldEdges[s] & worldEdgeMask) != 0) return s;
            return -1;
        }

        /// <summary>Diagnostic : pour chaque groupe distinct des ancres, type, bords ouverts et nombre de segments.</summary>
        public string DescribeAnchors(int[] anchors) =>
            string.Join(", ", anchors.Select(Find).Distinct().Select(r => $"[type {segType[r]} ouverts {open[r]} segs {segCount[r]}]"));

        /// <summary>Case et masque de bords monde d'un segment (pour le retrouver dans une autre photo).</summary>
        public (Vector2Int pos, int mask) DescribeSeg(int seg)
        {
            foreach (var kv in cells)
                if (kv.Value.Segs.Contains(seg)) return (kv.Key, segWorldEdges[seg]);
            return (default, 0);
        }

        /// <summary>Diagnostic : bord vu depuis la case voisine de `pos` dans la direction `dir`.</summary>
        public string DescribeNeighborEdge(Vector2Int pos, int dir)
        {
            Vector2Int np = Hex.Neighbor(pos, dir);
            if (!cells.TryGetValue(np, out Cell nb)) return $"{np}: vide";
            EdgeSpec e = EdgeAt(nb, (dir + 3) % 6);
            return $"{np} {nb.Spec.Name} rot{nb.Rot}: [{string.Join(",", e.Any)}]";
        }
    }
}
