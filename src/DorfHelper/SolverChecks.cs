using System.Collections.Generic;
using System.Linq;
using DorfHelper.Model;
using UnityEngine;

namespace DorfHelper
{
    internal partial class Suggestion
    {
        public TileSlot Slot; // résolu sur le thread principal
    }

    internal static partial class Solver
    {
        // ---------- Auto-vérifications (thread principal) ----------

        /// <summary>
        /// Compare le modèle avec le jeu, sur la tuile d'aperçu actuellement posée sur une case :
        /// bords parfaits / vides, et valeur de chaque quête (le jeu l'a déjà recalculée avec l'aperçu).
        /// </summary>
        public static string CheckAgainstPreview(GameState state, GameSnapshot snap)
        {
            Tile tile = state.TilePlacer.CurrentTile;
            TileSlot slot = state.TilePlacer.CurrentTileSlot;
            if (!tile || !slot || !tile.gameObject.activeSelf || snap?.Hand == null) return null;

            var questsAfter = new List<QuestState>();
            PlacementEval ours = snap.Board.Evaluate(snap.Hand, slot.GridPos, tile.RotationIndex, questsAfter);

            int perfect = 0, empty = 0;
            for (int i = 0; i < 6; i++)
            {
                Tile neighbor = tile.GetNeighbor(i, Space.World);
                if (neighbor == null) { empty++; continue; }
                List<GroupType> a = tile.GetEdgeTypes(i, Space.World);
                List<GroupType> b = neighbor.GetEdgeTypes((i + 3) % 6, Space.World);
                if ((a.Count == 0 && b.Count == 0)
                    || (tile.GetHybridEdges(i, Space.World).Count > 0 && b.Count == 0)
                    || (a.Count == 0 && neighbor.GetHybridEdges((i + 3) % 6, Space.World).Count > 0)
                    || a.Any(b.Contains))
                    perfect++;
            }

            var diffs = new List<string>();
            if (ours.Perfect != perfect || ours.Empty != empty)
            {
                diffs.Add($"bords : nous {ours.Perfect} parfaits/{ours.Empty} vides, jeu {perfect}/{empty}");
                for (int i = 0; i < 6; i++)
                {
                    Tile n = tile.GetNeighbor(i, Space.World);
                    string game = n ? $"{n.GridPos} {n.name} rot{n.RotationIndex}: [{string.Join(",", n.GetEdgeTypes((i + 3) % 6, Space.World).Select(g => (int)g.id))}]" : "vide";
                    diffs.Add($"  dir{i} modèle {snap.Board.DescribeNeighborEdge(slot.GridPos, i)} | jeu {game}");
                }
            }

            for (int i = 0; i < snap.Board.Quests.Count; i++)
            {
                QuestModel q = snap.Board.Quests[i];
                if (!snap.QuestSources.TryGetValue(q, out QuestWatcher w) || !w) continue;
                QuestConditionWatcher cw = w.GetConditionWatcher(0);
                if (cw == null) continue;
                // CloseGroup : le jeu ne rafraîchit sa valeur qu'à l'ajout d'un segment ; on compare aux bords ouverts réels.
                int gameValue = q.Kind == QuestKind.CloseGroup && cw is GroupConditionWatcher closeWatcher && closeWatcher.WatchedGroups != null
                    ? closeWatcher.WatchedGroups.Where(g => g).Distinct().Sum(g => g.OpenEdges)
                    : cw.CurrentValue;
                if (gameValue == questsAfter[i].Value) continue;
                diffs.Add($"quête [{q.Label}] : nous {questsAfter[i].Value}, jeu {gameValue}");
                if (q.Kind == QuestKind.CloseGroup && cw is GroupConditionWatcher gw && gw.WatchedGroups != null)
                {
                    // Diagnostic : groupes surveillés côté jeu (avec l'aperçu posé) et côté modèle (avant la pose)
                    diffs.Add("  jeu : " + string.Join(", ", gw.WatchedGroups.Where(g => g).Select(g =>
                        $"[{(g.GroupType ? g.GroupType.id.ToString() : "?")} ouverts {g.OpenEdges} segs {g.Segments.Count}" +
                        $" hybrides {g.Segments.Count(sg => sg && sg.HybridSegment)}]")));
                    diffs.Add("  modèle (avant pose) : " + snap.Board.DescribeAnchors(q.AnchorSegs));
                }
            }

            return diffs.Count == 0 ? null : $"case {slot.GridPos} rot {tile.RotationIndex} → " + string.Join("\n", diffs);
        }
    }
}
