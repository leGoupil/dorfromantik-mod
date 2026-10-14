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
        Score,   // bords parfaits, quêtes en bonus
        Quests,  // priorité aux quêtes
        Compact, // remplit les trous, garde une frontière propre pour faire durer la partie
        Clean,        // score max, mais aucun bord incompatible (sauf s'il n'existe aucune pose propre)
        CompactClean, // durer, mais aucun bord incompatible (sauf s'il n'existe aucune pose propre)
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

        /// <summary>Objectifs "sans bord raté" : un bord raté coûte plus que tout ce qu'une pose peut rapporter.</summary>
        public static bool IsStrict(Objective o) => o == Objective.Clean || o == Objective.CompactClean;

        /// <summary>Objectifs qui tiennent compte du potentiel (futurs placements parfaits).</summary>
        public static bool UsesPotential(Objective o) => o == Objective.Compact || o == Objective.CompactClean;

        /// <summary>Note immédiate d'une pose selon l'objectif.</summary>
        private const float TileValue = 30f;      // valeur d'une tuile gagnée, en points de note
        private const float PotentialWeight = 0.8f; // un placement parfait "probable" vaut un peu moins qu'un acquis

        public static float ScoreFor(PlacementEval e, Objective objective, int questReward = 3, int perfectReward = 1)
        {
            // Base = poids de l'AutoPlacer des devs : +10 bord parfait, -30 bord qui ne colle pas.
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
                    // Contrainte quasi stricte : un bord raté coûte plus que tout ce qu'une pose peut rapporter.
                    s += 40f * e.QuestsFulfilled - 80f * e.QuestsFailed + 20f * e.QuestProgress;
                    break;
                case Objective.Compact:
                case Objective.CompactClean:
                    // Durer = gagner des tuiles : placements parfaits (la tuile posée et les voisines complétées)
                    // et quêtes réussies. Une quête ratée est une récompense perdue.
                    s += TileValue * e.TilesGained
                         - 0.7f * TileValue * questReward * e.QuestsFailed
                         + 0.8f * TileValue * questReward * e.QuestProgress
                         - 0.3f * TileValue * questReward * e.QuestsEndangered
                         + PotentialWeight * TileValue * perfectReward * e.Potential
                         + 8f * e.Neighbors;
                    if (e.Neighbors <= 1) s -= 20f; // évite d'étirer la frontière
                    break;
            }
            if (IsStrict(objective)) s -= 1000f * e.Mismatched;
            if (e.GoalActive) s += GoalScore(e);
            if (e.ManyActive) s += e.GoalScale * ManyScore(e);
            if (e.AtOnceKind == 1)
            {
                if (e.PerfectTriggered >= e.AtOnceTarget) s += 5000f;           // défi réussi
                else if (e.FlowerRuined) s -= RuinPenalty(e);                    // fleur gâchée (toujours actif)
                else s += e.GoalScale * FlowerWeight * e.FlowerDelta;           // progression de la fleur
            }
            else if (e.AtOnceKind == 2)
            {
                // Fleur complète "grillée" au centre sans atteindre la cible : interdit, sauf si la pioche est presque vide.
                if (e.FlowerCenterWasted && e.Points < e.AtOnceTarget && e.GoalScale > 0f) s -= CenterWastePenalty;
                s -= ReservedQuestPenalty * e.ReservedQuestsSpent;              // quête réservée au centre, gâchée ailleurs
                s += ReservedProgressWeight * e.ReservedProgress;               // préparer les quêtes réservées, où qu'elles soient
                // Une nouvelle quête qui touche le centre (tuile de quête posée sur un groupe relié au centre), sans la réussir tout de suite
                if (e.HandQuestNearCenter > 0 && e.QuestsFulfilled == 0) s += HandQuestNearCenterBonus * e.HandQuestNearCenter;
                if (e.Points >= e.AtOnceTarget) s += 5000f;                     // défi réussi
                else if (e.FlowerRuined) s -= RuinPenalty(e);                    // fleur gâchée (dont centre sans atteindre la cible)
                else s += e.GoalScale * FlowerWeight * e.FlowerDelta;           // progression de la fleur (si elle est utilisée)
            }
            return s;
        }

        // Objectif de biome : croissance jusqu'à la cible sans fermer le groupe, puis fermeture.
        public static float GoalGrowWeight = 60f;    // par élément ajouté au groupe
        public static float GoalCloseWeight = 25f;  // par bord ouvert refermé, une fois la cible atteinte
        public static int GoalSafeOpenEdges = 4;    // en dessous, le groupe risque d'être fermé trop tôt
        public static float GoalEdgeLossWeight = 40f; // par bord ouvert du groupe refermé sans le prolonger
        public static float GoalProspectWeight = 150f; // par "case sûre" de croissance gagnée ou perdue

        // Dosage de l'objectif selon la réserve : 0 sous GoalMinStack tuiles en pioche, 1 au-delà de GoalFullStack.
        public static int GoalMinStack = 15;
        public static int GoalFullStack = 40;

        // Fleurs (défis "d'un coup") : construire coûte peu en survie, on coupe beaucoup plus tard.
        public static int FlowerMinStack = 5;
        public static int FlowerFullStack = 20;

        public static float FlowerScaleFor(int stackHeight) =>
            Mathf.Clamp01((float)(stackHeight - FlowerMinStack) / (FlowerFullStack - FlowerMinStack));

        public static float GoalScaleFor(int stackHeight) =>
            GoalFullStack <= GoalMinStack ? 1f : Mathf.Clamp01((float)(stackHeight - GoalMinStack) / (GoalFullStack - GoalMinStack));

        public static float FlowerWeight = 100f;        // par point de progression de la fleur planifiée
        public static float FlowerRuinPenalty = 400f;  // gâcher la fleur en construction
        public static float ReservedQuestPenalty = 400f; // terminer ailleurs une quête réservée pour la tuile du centre
        public static float FlowerRuinPerProgress = 120f; // par point d'avancement (2 × intérieur + extérieur) de la fleur gâchée

        /// <summary>Gâcher une fleur coûte d'autant plus qu'elle était avancée.</summary>
        private static float RuinPenalty(PlacementEval e) => FlowerRuinPenalty + FlowerRuinPerProgress * e.FlowerRuinedProgress;

        public static float CenterWastePenalty = 3000f;  // poser au centre d'une fleur complète sans atteindre la cible
        public static float ReservedProgressWeight = 400f;
        public static float HandQuestNearCenterBonus = 600f; // nouvelle quête reliée au centre de la fleur // par quête réservée amenée de 0 à 100 % de sa cible (sans la terminer)

        // Objectif "plusieurs groupes"
        public static float ManyClosedWeight = 600f;  // par groupe fermé assez grand
        public static float ManySmallPenalty = 40f;   // par groupe fermé trop petit
        public static float ManyGrowthWeight = 25f;   // par élément vers la taille cible (plafonné par groupe)
        public static float ManyCloseWeight = 40f;    // par bord refermé sur un groupe déjà assez grand

        public static float ManyScore(PlacementEval e) =>
            ManyClosedWeight * e.ManyClosedQualifying
            - ManySmallPenalty * e.ManyClosedSmall
            + ManyGrowthWeight * e.ManyGrowth
            + ManyCloseWeight * e.ManyCloseProgress;

        /// <summary>
        /// Note de l'objectif de biome. Les bonus (croissance, perspectives, fermeture) sont dosés par la réserve de
        /// tuiles (e.GoalScale) ; les protections du groupe (ne pas le fermer trop tôt, ne pas lui retirer de bords
        /// ouverts) restent toujours actives, sinon le bot détruit son propre groupe en période de disette.
        /// </summary>
        public static float GoalScore(PlacementEval e)
        {
            QuestState before = e.GoalBefore, after = e.GoalAfter;
            int target = Math.Max(1, e.GoalTarget);
            float bonus = 0f, protection = 0f;
            if (after.Value < target)
            {
                bonus += GoalGrowWeight / e.GoalElementsPerSegment * (after.Value - before.Value); // par "segment" ajouté
                bonus += GoalProspectWeight * e.GoalProspectDelta;
                bonus += 4f * (Math.Min(after.Open, 8) - Math.Min(before.Open, 8)); // garder de la marge pour grandir

                if (after.Open == 0) protection -= 3000f;                           // fermé avant la cible
                else if (after.Open <= GoalSafeOpenEdges && after.Open < before.Open)
                    protection -= 80f * (GoalSafeOpenEdges + 1 - after.Open);       // de plus en plus en danger
                // Chaque bord ouvert perdu sans croissance est une occasion de grandir en moins.
                if (after.Value == before.Value && after.Open < before.Open) protection -= GoalEdgeLossWeight * (before.Open - after.Open);
            }
            else if (!e.GoalClose)
            {
                if (before.Value < target) bonus += 3000f;                          // "former" : objectif atteint
            }
            else
            {
                if (before.Value < target) bonus += 300f;                           // cible atteinte
                bonus += GoalCloseWeight * (before.Open - after.Open);
                if (after.Open == 0) bonus += 3000f;                                // groupe fermé : objectif réussi
            }
            return e.GoalScale * bonus + protection;
        }

        /// <summary>
        /// Calcul complet (thread d'arrière-plan) : toutes les poses de la tuile en main, puis pour les meilleures,
        /// simulation gloutonne de la suite : les `depth` tuiles visibles de la pioche, puis `simulated` tuiles
        /// probables tirées parmi les tuiles déjà vues. Plusieurs tirages (les mêmes pour tous les candidats),
        /// dont on fait la moyenne ; leur nombre s'adapte au budget de temps.
        /// </summary>
        public static SearchResult Run(GameSnapshot snap, Objective objective, int depth, int simulated, CancellationToken ct)
        {
            const int MaxSamples = 16;
            const long BudgetMs = 1500;

            var sw = Stopwatch.StartNew();
            var result = new SearchResult();
            Board board = snap.Board;
            if (snap.Hand == null) return result;

            // Échantillon de tuiles "typiques" pour estimer le potentiel des cases vides.
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
                // Tirages communs à tous les candidats (comparaison équitable).
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
                        // La quête de la tuile en main devient une quête normale, ancrée sur ses segments posés.
                        b.Quests = b.Quests.Select(q => q.HandSegs != null ? q.AnchoredOn(placed) : q).ToList();
                        b.Hand = null;
                        totals[i] += Rollout(b, sequences[sample], sequences[sample].Count, objective, ct);
                    });
                }

                // Premier tirage pour mesurer le coût, puis autant que le budget le permet.
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
            result.Stats = $"{first.Count} poses, suite = {known} visibles + {extra} probables × {samplesDone} tirages " +
                           $"× {candidates.Count} candidats (réserve {snap.Pool.Count} tuiles) en {sw.ElapsedMilliseconds} ms";
            return result;
        }

        /// <summary>Joue les tuiles suivantes "au mieux immédiat" et renvoie la somme actualisée des notes.</summary>
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
                    total -= 50f * weight; // tuile impossible à poser
                    continue;
                }
                total += weight * bestScore;
                b.Apply(t, best.Pos, best.Rotation);
            }
            return total;
        }
    }
}
