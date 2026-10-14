using System.Collections.Generic;
using System.Linq;
using DorfHelper.Model;
using UnityEngine;

namespace DorfHelper
{
    internal partial class Suggestion
    {
        public TileSlot Slot; // resolved on the main thread
    }

    internal static partial class Solver
    {
        // ---------- Self-checks (main thread) ----------

        /// <summary>
        /// Compares the model with the game, using the preview tile currently hovering over a slot:
        /// perfect / empty edges, and each quest's value (the game has already recomputed it with the preview).
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
                diffs.Add($"edges: model {ours.Perfect} perfect/{ours.Empty} empty, game {perfect}/{empty}");
                for (int i = 0; i < 6; i++)
                {
                    Tile n = tile.GetNeighbor(i, Space.World);
                    string game = n ? $"{n.GridPos} {n.name} rot{n.RotationIndex}: [{string.Join(",", n.GetEdgeTypes((i + 3) % 6, Space.World).Select(g => (int)g.id))}]" : "empty";
                    diffs.Add($"  dir{i} model {snap.Board.DescribeNeighborEdge(slot.GridPos, i)} | game {game}");
                }
            }

            for (int i = 0; i < snap.Board.Quests.Count; i++)
            {
                QuestModel q = snap.Board.Quests[i];
                if (!snap.QuestSources.TryGetValue(q, out QuestWatcher w) || !w) continue;
                QuestConditionWatcher cw = w.GetConditionWatcher(0);
                if (cw == null) continue;
                // CloseGroup: the game only refreshes its value when a segment is added; compare against the actual open edges.
                int gameValue = q.Kind == QuestKind.CloseGroup && cw is GroupConditionWatcher closeWatcher && closeWatcher.WatchedGroups != null
                    ? closeWatcher.WatchedGroups.Where(g => g).Distinct().Sum(g => g.OpenEdges)
                    : cw.CurrentValue;
                if (gameValue == questsAfter[i].Value) continue;
                diffs.Add($"quest [{q.Label}]: model {questsAfter[i].Value}, game {gameValue}");
                if (q.Kind == QuestKind.CloseGroup && cw is GroupConditionWatcher gw && gw.WatchedGroups != null)
                {
                    // Diagnostics: watched groups on the game side (with the preview in place) and on the model side (before placement)
                    diffs.Add("  game: " + string.Join(", ", gw.WatchedGroups.Where(g => g).Select(g =>
                        $"[{(g.GroupType ? g.GroupType.id.ToString() : "?")} open {g.OpenEdges} segs {g.Segments.Count}" +
                        $" hybrid {g.Segments.Count(sg => sg && sg.HybridSegment)}]")));
                    diffs.Add("  model (before placement): " + snap.Board.DescribeAnchors(q.AnchorSegs));
                }
            }

            return diffs.Count == 0 ? null : $"slot {slot.GridPos} rot {tile.RotationIndex} → " + string.Join("\n", diffs);
        }
    }
}
