using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DorfHelper;
using DorfHelper.Model;
using UnityEngine;

namespace DorfSim
{
    /// <summary>
    /// Rejoue des parties complètes avec le vrai solveur du mod et des tuiles tirées parmi celles enregistrées en jeu.
    ///
    ///   dotnet run -c Release -- --games 50 --objective CompactClean,Clean --depth 2
    /// </summary>
    internal static class Program
    {
        private sealed class Options
        {
            public string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library/Application Support/Steam/steamapps/common/Dorfromantik/BepInEx/DorfHelper");
            public int Games = 30;
            public List<Objective> Objectives = new List<Objective> { Objective.CompactClean };
            public int Depth = 2;
            public int InitialStack = 40;
            public int PerfectReward = 1;
            public int QuestReward = 5;
            public int Seed = 1;
            public int MaxTiles = 600;  // garde-fou
            public bool Verbose;
            public double QuestScale = 1.0; // multiplie l'incrément d'objectif des quêtes (calibrage)
            public int FitModifier = 10;      // règles du mode Classique (log "Config des points")
            public bool GroupPoints = false;
            public string AtOnce; // "perfect:7" (placements parfaits d'un coup) ou "points:660" (points en une pose)
            public string Many; // ex. "Agriculture:Field:10" : fermer le plus de groupes d'au moins 10 Field
            public string Goal; // ex. "Agriculture:Field:150" : fermer un groupe d'au moins 150 éléments Field
        }

        private sealed class GameResult
        {
            public int Placed, Perfects, QuestsDone, QuestsFailed, Discarded, Mismatched;
            public int GoalReachedAt = -1;   // tuiles posées quand l'objectif est atteint
            public int GoalBestSize;         // plus grand groupe ciblé observé
            public int GoalClosedEarly;      // fois où le groupe ciblé a été fermé avant la cible
            public int ManyDone;             // groupes fermés assez grands (objectif "plusieurs groupes")
            public int MaxPerfectAtOnce, MaxPoints, AtOnceReachedAt = -1;
        }

        public static int Main(string[] args)
        {
            Options o = ParseArgs(args);
            Dataset data = Dataset.Load(o.DataDir);
 if (o.Many != null && o.Many.Split(':')[1] != "" && !data.ElementNames.Contains(o.Many.Split(':')[1])) data.ElementNames.Add(o.Many.Split(':')[1]);
            if (o.Goal != null && o.Goal.Split(':')[1] != "" && !data.ElementNames.Contains(o.Goal.Split(':')[1])) data.ElementNames.Add(o.Goal.Split(':')[1]);
            Console.WriteLine($"Données : {data.Tiles.Count} tuiles ({data.Tiles.Count(t => t.Quest)} de quête), " +
                              $"{data.Quests.Count} quêtes — source {data.Source}");
            Console.WriteLine($"Parties : {o.Games} par objectif, pioche initiale {o.InitialStack}, anticipation {o.Depth}, " +
                              $"récompenses parfait +{o.PerfectReward} / quête +{o.QuestReward}\n");

            foreach (Objective objective in o.Objectives)
            {
                var sw = Stopwatch.StartNew();
                var results = new GameResult[o.Games];
                Parallel.For(0, o.Games, g => results[g] = PlayGame(data, o, objective, o.Seed + g));
                Report(objective, results, sw.Elapsed);
            }
            return 0;
        }

        private static GameResult PlayGame(Dataset data, Options o, Objective objective, int seed)
        {
            var rng = new System.Random(seed);
            var r = new GameResult();
            var board = new Board
            {
                ElementKinds = data.ElementNames.Count,
                Constraining = (bool[])data.Constraining.Clone(),
                PerfectReward = o.PerfectReward,
                QuestReward = o.QuestReward,
            };
            var pool = new List<TileSpec>();

            // Tuile de départ
            RecordedTile start = data.Tiles.Where(t => !t.Quest).OrderBy(_ => rng.Next()).First();
            TileSpec startSpec = data.ToSpec(start);
            board.Apply(startSpec, new Vector2Int(0, 0), 0);

            var upcoming = new Queue<(TileSpec spec, RecordedTile tile)>();
            (TileSpec, RecordedTile) Draw()
            {
                RecordedTile t = data.Tiles[rng.Next(data.Tiles.Count)];
                return (data.ToSpec(t), t);
            }

            int goalType = -1, goalElement = -1, goalTarget = 0, goalAnchor = -1;
            bool goalClose = true;
            if (o.Goal != null)
            {
                string[] g = o.Goal.Split(':');
                goalType = Array.IndexOf(Dataset.GroupNames, g[0]);
                goalElement = g[1] == "" ? -1 : data.ElementNames.IndexOf(g[1]); // vide : on compte les segments
                goalTarget = int.Parse(g[2]);
                goalClose = g.Length < 4 || g[3] != "form";
            }

            if (o.Many != null)
            {
                string[] m = o.Many.Split(':');
                board.ManyType = Array.IndexOf(Dataset.GroupNames, m[0]);
                board.ManyElement = m[1] == "" ? -1 : data.ElementNames.IndexOf(m[1]);
                board.ManySize = int.Parse(m[2]);
            }

            board.FitScoreModifier = o.FitModifier;
            if (questWeightOverride >= 0) board.FlowerQuestWeight = questWeightOverride;
            board.RewardGroups = o.GroupPoints;
            if (o.AtOnce != null)
            {
                string[] a = o.AtOnce.Split(':');
                board.AtOnceKind = a[0] == "perfect" ? 1 : 2;
                board.AtOnceTarget = int.Parse(a[1]);
            }

            int stack = o.InitialStack;
            while (stack > 0 && r.Placed < o.MaxTiles)
            {
                if (o.Verbose && r.Placed % 25 == 0)
                {
                    string goalInfo = "";
                    if (board.Goal != null && board.Goal.AnchorSegs.Length > 0)
                    {
                        QuestState gs = board.QuestStateOf(board.Goal);
                        goalInfo = $", objectif {gs.Value} champs / {gs.Open} bords ouverts ({board.Goal.AnchorSegs.Length} segments)";
                        // Pour chaque bord ouvert : part des tuiles connues qui peuvent s'y poser en le prolongeant
                        foreach (var (slot, dir) in board.OpenEdgeSlots(board.Goal.AnchorSegs))
                        {
                            int fit = 0, extend = 0;
                            foreach (RecordedTile t in data.Tiles.Take(300))
                            {
                                TileSpec sp = data.ToSpec(t);
                                bool anyFit = false, anyExtend = false;
                                for (int rot = 0; rot < 6; rot++)
                                {
                                    if (!board.Fits(sp, slot, rot)) continue;
                                    anyFit = true;
                                    if ((sp.Edges[(dir - rot + 6) % 6]?.Any ?? new byte[0]).Contains((byte)goalType)) anyExtend = true;
                                }
                                if (anyFit) fit++;
                                if (anyExtend) extend++;
                            }
                            goalInfo += $"\n        case {slot} : {fit * 100 / 300}% des tuiles posables, {extend * 100 / 300}% prolongent le champ";
                        }
                    }
                    if (board.FlowerSite.HasValue)
                    {
                        Board.FlowerState fs = board.FlowerAt(board.FlowerSite.Value, GoalSample(pool, data));
                        goalInfo += $", fleur {board.FlowerSite.Value} : anneau 1 {fs.Ring1}/6, anneau 2 {fs.Ring2}/12, centre {fs.CenterChance:0%}, terminable {fs.Completion:0.000}, quête {fs.QuestReadiness:0%}";
                    }
                    Console.WriteLine($"   [{objective} #{seed}] {r.Placed} posées, pioche {stack}, {board.Quests.Count} quêtes, frontière {board.Frontier.Count()}{goalInfo}");
                }
                while (upcoming.Count < 3) upcoming.Enqueue(Draw());
                (TileSpec hand, RecordedTile handTile) = upcoming.Dequeue();
                QuestModel handQuest = handTile.Quest ? MakeQuest(data, board, handTile, hand, rng, o.QuestScale) : null;
                stack--;

                bool flowerForPoints = board.AtOnceKind == 2 && board.GroupClosingPoints(1000) == 0;
                if (flowerForPoints)
                {
                    board.FlowerNeedsQuest = board.FlowerJackpotPoints() < board.AtOnceTarget;
                    board.FlowerQuestsNeeded = (int)Math.Ceiling((board.AtOnceTarget - board.FlowerJackpotPoints()) / (double)board.QuestScore);
                    board.FlowerSite = board.SelectFlowerSite(GoalSample(pool, data), board.FlowerSite);
                    board.ReserveQuestsAround(board.FlowerSite);
                }
                else if (board.AtOnceKind == 2)
                {
                    // Terrain verrouillé tant que le groupe visé reste ouvert
                    int type = goalType >= 0 && goalAnchor >= 0 && board.IsSegOpen(goalAnchor) ? goalType : board.LargestOpenGroupType();
                    if (type != goalType) goalAnchor = -1;
                    if (type >= 0)
                    {
                        goalType = type; goalElement = -1; goalClose = true;
                        goalTarget = board.SegmentsForPoints(board.AtOnceTarget);
                    }
                }
                if (board.AtOnceKind != 0)
                    board.GoalScale = board.AtOnceKind == 1 || board.GroupClosingPoints(1000) == 0 ? Solver.FlowerScaleFor(stack) : Solver.GoalScaleFor(stack);
                if (board.AtOnceKind == 1) board.FlowerSite = board.SelectFlowerSite(GoalSample(pool, data), board.FlowerSite);

                if (goalType >= 0)
                {
                    board.Goal = new QuestModel
                    {
                        Label = "objectif", Comparison = Comparison.MoreThan,
                        Kind = goalElement >= 0 ? QuestKind.CountElements : QuestKind.CountSegments,
                        GroupType = (byte)goalType, ElementIndex = goalElement, Target = goalTarget,
                        AnchorSegs = board.SelectGoalGroup((byte)goalType, goalElement, GoalSample(pool, data), goalAnchor),
                    };
                    goalAnchor = board.Goal.AnchorSegs.Length > 0 ? board.Goal.AnchorSegs[0] : -1;
                    board.GoalScale = Solver.GoalScaleFor(stack);
                    board.GoalClose = goalClose;
                    board.GoalElementsPerSegment = Board.ElementsPerSegment(data.Tiles.Take(300).Select(data.ToSpec), (byte)goalType, goalElement);
                    if (board.Goal.AnchorSegs.Length > 0)
                        r.GoalBestSize = Math.Max(r.GoalBestSize, board.QuestStateOf(board.Goal).Value);
                }

                var quests = new List<QuestModel>(board.Quests);
                if (handQuest != null) quests.Add(handQuest);
                board.Quests = quests;
                board.Hand = hand;

                var snap = new GameSnapshot
                {
                    Board = board,
                    Hand = hand,
                    Stack = upcoming.Take(o.Depth).Select(u => u.spec).ToList(),
                    ValidSlots = board.Frontier.Where(p => Enumerable.Range(0, 6).Any(rot => board.Fits(hand, p, rot))).ToList(),
                    PlacedCount = r.Placed,
                    Pool = pool.Skip(Math.Max(0, pool.Count - 80)).ToList(),
                };
                SearchResult res = Solver.Run(snap, objective, o.Depth, 0, CancellationToken.None);
                if (res.Suggestions.Count == 0)
                {
                    r.Discarded++;
                    board.Quests = quests.Where(q => q.HandSegs == null).ToList();
                    continue;
                }

                Suggestion best = res.Suggestions[0];
                PlacementEval ev = board.Evaluate(hand, best.Pos, best.Rotation);
                r.ManyDone += ev.ManyClosedQualifying;
                r.MaxPerfectAtOnce = Math.Max(r.MaxPerfectAtOnce, ev.PerfectTriggered);
                r.MaxPoints = Math.Max(r.MaxPoints, ev.Points);
                if (board.AtOnceKind != 0 && r.AtOnceReachedAt < 0 &&
                    (board.AtOnceKind == 1 ? ev.PerfectTriggered : ev.Points) >= board.AtOnceTarget) r.AtOnceReachedAt = r.Placed + 1;
                if (o.Many != null) board.GoalScale = Solver.GoalScaleFor(stack);
                if (ev.GoalActive && ev.GoalAfter.Open == 0 && ev.GoalAfter.Value < goalTarget) r.GoalClosedEarly++;
                int[] placed = board.Apply(hand, best.Pos, best.Rotation);
                board.Hand = null;
                pool.Add(hand);
                r.Placed++;
                r.Perfects += ev.PerfectTriggered;
                r.Mismatched += ev.Mismatched;
                stack += ev.TilesGained;

                // Quêtes : la quête de la tuile posée s'ancre sur ses segments ; on retire les quêtes terminées.
                var next = new List<QuestModel>();
                foreach (QuestModel q in board.Quests)
                {
                    QuestModel model = q.HandSegs != null ? q.AnchoredOn(placed) : q;
                    QuestStatus status = board.QuestStateOf(model).Status;
                    if (status == QuestStatus.Fulfilled) r.QuestsDone++;
                    else if (status == QuestStatus.Failed) r.QuestsFailed++;
                    else next.Add(model);
                }
                board.Quests = next;

                if (board.AtOnceKind == 0 && goalType >= 0 && (goalClose ? board.LargestClosedGroup((byte)goalType, goalElement) : board.LargestGroup((byte)goalType, goalElement)) >= goalTarget)
                {
                    r.GoalReachedAt = r.Placed;
                    break;
                }
            }
            return r;
        }

        /// <summary>
        /// Quête d'une tuile de quête, comme le jeu : objectif = plus grand groupe ouvert du même terrain
        /// + incrément (tiré parmi les incréments observés en jeu, ou valeur par défaut).
        /// </summary>
        private static QuestModel MakeQuest(Dataset data, Board board, RecordedTile t, TileSpec spec, System.Random rng, double scale)
        {
            int type = t.MainType;
            int[] handSegs = Enumerable.Range(0, spec.Segs.Length).Where(i => spec.Segs[i].Type == type).ToArray();
            if (handSegs.Length == 0) return null;

            List<RecordedQuest> observed = data.Quests.Where(q => q.Group == type && q.Increment > 0).ToList();
            RecordedQuest rq = observed.Count > 0
                ? observed[rng.Next(observed.Count)]
                : new RecordedQuest { Kind = QuestKind.CountSegments, Comparison = Comparison.MoreThan, Group = type, Reference = 0, Target = 4 + rng.Next(5) };
            int elementIndex = rq.Element != null ? data.ElementNames.IndexOf(rq.Element) : -1;
            int target = board.LargestOpenGroup((byte)type, rq.Kind, elementIndex) + Math.Max(1, (int)Math.Round(rq.Increment * scale));

            return new QuestModel
            {
                Label = $"{rq.Kind} {Dataset.GroupNames[type]} {rq.Comparison} {target}",
                Kind = rq.Kind,
                Comparison = rq.Comparison,
                GroupType = (byte)type,
                ElementIndex = elementIndex,
                Target = target,
                AnchorSegs = new int[0],
                HandSegs = handSegs,
            };
        }

        /// <summary>Échantillon de tuiles pour estimer les perspectives (mêmes 40 tuiles que le solveur).</summary>
        private static List<TileSpec> GoalSample(List<TileSpec> pool, Dataset data)
        {
            List<TileSpec> recent = pool.Skip(Math.Max(0, pool.Count - 80)).ToList();
            if (recent.Count < 20) recent = data.Tiles.Take(80).Select(data.ToSpec).ToList();
            return recent.Count <= 40 ? recent : Enumerable.Range(0, 40).Select(i => recent[i * recent.Count / 40]).ToList();
        }

        private static void Report(Objective objective, GameResult[] results, TimeSpan elapsed)
        {
            double Avg(Func<GameResult, int> f) => results.Average(f);
            int[] placed = results.Select(x => x.Placed).OrderBy(x => x).ToArray();
            double sd = Math.Sqrt(results.Average(x => Math.Pow(x.Placed - Avg(y => y.Placed), 2)));
            Console.WriteLine($"== {objective} ({results.Length} parties, {elapsed.TotalSeconds:0.0} s)");
            Console.WriteLine($"   tuiles posées : moyenne {Avg(x => x.Placed):0.0} ± {sd / Math.Sqrt(results.Length):0.0} " +
                              $"(médiane {placed[placed.Length / 2]}, min {placed[0]}, max {placed[^1]}, ≥250 : {placed.Count(x => x >= 250)})");
            Console.WriteLine($"   par partie : {Avg(x => x.Perfects):0.0} placements parfaits, {Avg(x => x.QuestsDone):0.0} quêtes réussies, " +
                              $"{Avg(x => x.QuestsFailed):0.0} ratées, {Avg(x => x.Mismatched):0.0} bords ratés, {Avg(x => x.Discarded):0.0} tuiles défaussées");
            if (results.Any(x => x.ManyDone > 0))
                Console.WriteLine($"   plusieurs groupes : {Avg(x => x.ManyDone):0.0} groupes fermés assez grands par partie " +
                                  $"({Avg(x => x.ManyDone) / Math.Max(1, Avg(x => x.Placed)) * 100:0.0} pour 100 tuiles)");
            Console.WriteLine($"   record d'un coup : {Avg(x => x.MaxPerfectAtOnce):0.0} placements parfaits (max {results.Max(x => x.MaxPerfectAtOnce)}), " +
                              $"{Avg(x => x.MaxPoints):0} points en une pose (max {results.Max(x => x.MaxPoints)})" +
                              (results.Any(x => x.AtOnceReachedAt >= 0) || results.Any(x => x.MaxPoints > 0) ? $" ; défi réussi dans {results.Count(x => x.AtOnceReachedAt >= 0)}/{results.Length} parties" : ""));
            int[] reached = results.Where(x => x.GoalReachedAt >= 0).Select(x => x.GoalReachedAt).OrderBy(x => x).ToArray();
            if (results.Any(x => x.GoalBestSize > 0 || x.GoalReachedAt >= 0))
                Console.WriteLine($"   objectif : atteint dans {reached.Length}/{results.Length} parties" +
                                  (reached.Length > 0 ? $", en {reached[reached.Length / 2]} tuiles (médiane)" : "") +
                                  $" ; fermé trop tôt {results.Average(x => x.GoalClosedEarly):0.0} fois/partie ; plus grand groupe ciblé (échecs) : médiane {results.Where(x => x.GoalReachedAt < 0).Select(x => x.GoalBestSize).DefaultIfEmpty(0).OrderBy(x => x).ElementAt(results.Count(x => x.GoalReachedAt < 0) / 2)}");
            Console.WriteLine();
        }

        private static float questWeightOverride = -1f;

        private static Options ParseArgs(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length - 1; i++)
            {
                string v = args[i + 1];
                switch (args[i])
                {
                    case "--data": o.DataDir = v; break;
                    case "--games": o.Games = int.Parse(v); break;
                    case "--objective": o.Objectives = v.Split(',').Select(x => Enum.Parse<Objective>(x, true)).ToList(); break;
                    case "--depth": o.Depth = int.Parse(v); break;
                    case "--stack": o.InitialStack = int.Parse(v); break;
                    case "--perfect": o.PerfectReward = int.Parse(v); break;
                    case "--quest": o.QuestReward = int.Parse(v); break;
                    case "--seed": o.Seed = int.Parse(v); break;
                    case "--max": o.MaxTiles = int.Parse(v); break;
                    case "--verbose": o.Verbose = v == "1"; break;
                    case "--goal": o.Goal = v; break;
                    case "--many": o.Many = v; break;
                    case "--atonce": o.AtOnce = v; break;
                    case "--fit-mod": o.FitModifier = int.Parse(v); break;
                    case "--group-points": o.GroupPoints = v == "1"; break;
                    case "--flower-quest": questWeightOverride = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--reserve": Solver.ReservedQuestPenalty = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--flower-ruin": Solver.FlowerRuinPenalty = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--flower": Solver.FlowerWeight = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--many-closed": Solver.ManyClosedWeight = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--many-growth": Solver.ManyGrowthWeight = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--many-close": Solver.ManyCloseWeight = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--goal-grow": Solver.GoalGrowWeight = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--goal-close": Solver.GoalCloseWeight = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--goal-safe": Solver.GoalSafeOpenEdges = int.Parse(v); break;
                    case "--goal-prospect": Solver.GoalProspectWeight = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--goal-min-stack": Solver.GoalMinStack = int.Parse(v); break;
                    case "--goal-full-stack": Solver.GoalFullStack = int.Parse(v); break;
                    case "--goal-loss": Solver.GoalEdgeLossWeight = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--quest-scale": o.QuestScale = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                }
            }
            return o;
        }
    }
}
