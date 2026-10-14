using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace DorfHelper
{
    /// <summary>
    /// Records drawn tiles (full content: edges, segments, elements) and encountered quests,
    /// to feed the offline simulator (tools/sim). Files go in BepInEx/DorfHelper/.
    /// </summary>
    internal class DataRecorder
    {
        private readonly string tilesPath;
        private readonly string questsPath;
        private readonly HashSet<int> recordedTiles = new HashSet<int>();
        private readonly HashSet<string> recordedQuests = new HashSet<string>();

        public DataRecorder(string dir)
        {
            tilesPath = Path.Combine(dir, "tiles.jsonl");
            questsPath = Path.Combine(dir, "quests.jsonl");
        }

        /// <summary>Drawn tile (in hand): this gives the actual draw distribution.</summary>
        public void RecordDrawnTile(Tile tile)
        {
            if (!tile || !recordedTiles.Add(tile.GetInstanceID())) return;
            try
            {
                File.AppendAllText(tilesPath, JsonConvert.SerializeObject(Describe(tile)) + Environment.NewLine);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Failed to record tile: " + e.Message);
            }
        }

        /// <summary>Active quests: type, terrain, comparison and target (to generate realistic quests).</summary>
        public void RecordQuests(IEnumerable<QuestWatcher> watchers, QuestManager questManager)
        {
            foreach (QuestWatcher q in watchers)
            {
                try
                {
                    QuestConditionWatcher cw = q ? q.GetConditionWatcher(0) : null;
                    QuestCondition c = cw?.Condition;
                    if (c == null) continue;
                    string key = q.GetInstanceID() + "/" + cw.TargetValue;
                    if (!recordedQuests.Add(key)) continue;
                    // Game reference value when the quest is first seen: target ≈ reference + increment
                    QuestTile questTile = q.GetComponentInParent<QuestTile>() ?? q.QuestTile;
                    int reference = -1;
                    try { if (questTile && questManager) reference = questManager.ReferenceGroupCount(c, questTile); } catch (Exception) { }
                    var entry = new
                    {
                        kind = c.conditionType.ToString(),
                        group = c.groupType ? c.groupType.id.ToString() : null,
                        comparison = c.equalityComparer.ToString(),
                        target = cw.TargetValue,
                        element = c.elementType ? c.elementType.name : null,
                        reference,
                        minTarget = questTile ? questTile.minTargetCount : 0,
                    };
                    File.AppendAllText(questsPath, JsonConvert.SerializeObject(entry) + Environment.NewLine);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Failed to record quest: " + e.Message);
                }
            }
        }

        private static object Describe(Tile tile)
        {
            var edges = new List<object>();
            for (int i = 0; i < 6; i++)
            {
                edges.Add(new
                {
                    any = tile.GetEdgeTypes(i, Space.Self).Where(g => g).Select(g => (int)g.id).ToArray(),
                    nonHybrid = tile.GetEdgeTypes(i, Space.Self, TileEdgeType.NonHybrid).Where(g => g).Select(g => (int)g.id).ToArray(),
                    hybrid = tile.GetHybridEdges(i, Space.Self).Count > 0,
                    constraining = tile.GetEdgeTypes(i, Space.Self).Where(g => g && g.constraining).Select(g => (int)g.id).ToArray(),
                });
            }

            var segs = (tile.AllElementGroupSegments ?? new List<ElementGroupSegment>())
                .Where(s => s)
                .Select(s => new
                {
                    type = s.GroupType ? (int)s.GroupType.id : 0,
                    edges = s.Edges.ToArray(),
                    elements = (s.Elements ?? new Dictionary<ElementType, int>())
                        .Where(kv => kv.Key)
                        .ToDictionary(kv => kv.Key.name, kv => kv.Value),
                })
                .ToList();

            return new { name = tile.name, quest = tile is QuestTile, edges, segs };
        }
    }
}
