using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DorfHelper.Model;

namespace DorfSim
{
    /// <summary>A tile recorded by the mod, ready to be converted into a TileSpec.</summary>
    internal sealed class RecordedTile
    {
        public string Name;
        public bool Quest;
        public int[][] Any = new int[6][];
        public int[][] NonHybrid = new int[6][];
        public bool[] Hybrid = new bool[6];
        public List<(int type, int[] edges, Dictionary<string, int> elements)> Segs = new List<(int, int[], Dictionary<string, int>)>();

        /// <summary>Dominant terrain type (used to pick the quest for a quest tile).</summary>
        public int MainType => Segs.Count == 0 ? 0 : Segs.GroupBy(s => s.type).OrderByDescending(g => g.Sum(s => s.edges.Length)).First().Key;
    }

    internal sealed class RecordedQuest
    {
        public QuestKind Kind;
        public Comparison Comparison;
        public int Group;
        public int Target;
        public string Element;
        public int Reference = -1; // size of the reference group when the quest was seen (-1: unknown)

        /// <summary>Increment required beyond the reference group.</summary>
        public int Increment => Reference >= 0 && Target > Reference ? Target - Reference : -1;
    }

    internal sealed class Dataset
    {
        public static readonly string[] GroupNames = { "Village", "Forest", "Agriculture", "TrainTracks", "Water" };

        public List<RecordedTile> Tiles = new List<RecordedTile>();
        public List<RecordedQuest> Quests = new List<RecordedQuest>();
        public bool[] Constraining = new bool[Board.TypeCount];
        public List<string> ElementNames = new List<string>();
        public string Source;

        /// <summary>
        /// Loads tiles.jsonl / quests.jsonl (recorded by the mod). Otherwise falls back to snapshots.jsonl
        /// (edges only: one segment per run of same-type edges, no elements or quests).
        /// </summary>
        public static Dataset Load(string dir)
        {
            var ds = new Dataset();
            string tiles = Path.Combine(dir, "tiles.jsonl");
            if (File.Exists(tiles) && new FileInfo(tiles).Length > 0)
            {
                ds.Source = "tiles.jsonl";
                foreach (string line in File.ReadLines(tiles)) ds.Tiles.Add(ds.ParseTile(JsonDocument.Parse(line).RootElement));
                string quests = Path.Combine(dir, "quests.jsonl");
                if (File.Exists(quests))
                    foreach (string line in File.ReadLines(quests)) ds.ParseQuest(JsonDocument.Parse(line).RootElement);
            }
            else
            {
                ds.Source = "snapshots.jsonl (edges only)";
                ds.Constraining[3] = ds.Constraining[4] = true; // tracks and rivers
                var seen = new HashSet<string>();
                foreach (string line in File.ReadLines(Path.Combine(dir, "snapshots.jsonl")))
                {
                    JsonElement root = JsonDocument.Parse(line).RootElement;
                    if (!root.TryGetProperty("currentTile", out JsonElement ct) || ct.ValueKind != JsonValueKind.Object) continue;
                    string key = ct.GetProperty("name").GetString() + root.GetProperty("time").GetString();
                    if (seen.Add(key)) ds.Tiles.Add(FromEdgesOnly(ct));
                }
            }
            ds.ElementNames = ds.Quests.Where(q => q.Element != null).Select(q => q.Element).Distinct().ToList();
            return ds;
        }

        private RecordedTile ParseTile(JsonElement e)
        {
            var t = new RecordedTile { Name = e.GetProperty("name").GetString(), Quest = e.GetProperty("quest").GetBoolean() };
            int i = 0;
            foreach (JsonElement edge in e.GetProperty("edges").EnumerateArray())
            {
                t.Any[i] = edge.GetProperty("any").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                t.NonHybrid[i] = edge.GetProperty("nonHybrid").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                t.Hybrid[i] = edge.GetProperty("hybrid").GetBoolean();
                foreach (JsonElement c in edge.GetProperty("constraining").EnumerateArray()) Constraining[c.GetInt32()] = true;
                i++;
            }
            foreach (JsonElement s in e.GetProperty("segs").EnumerateArray())
            {
                var elements = new Dictionary<string, int>();
                foreach (JsonProperty p in s.GetProperty("elements").EnumerateObject()) elements[p.Name] = p.Value.GetInt32();
                t.Segs.Add((s.GetProperty("type").GetInt32(), s.GetProperty("edges").EnumerateArray().Select(x => x.GetInt32()).ToArray(), elements));
            }
            return t;
        }

        private void ParseQuest(JsonElement e)
        {
            string kind = e.GetProperty("kind").GetString();
            string group = e.GetProperty("group").ValueKind == JsonValueKind.String ? e.GetProperty("group").GetString() : null;
            Quests.Add(new RecordedQuest
            {
                Kind = kind == "CountElements" ? QuestKind.CountElements : kind == "CountSegments" ? QuestKind.CountSegments : QuestKind.CloseGroup,
                Comparison = e.GetProperty("comparison").GetString() == "Exactly" ? Comparison.Exactly
                    : e.GetProperty("comparison").GetString() == "FewerThan" ? Comparison.FewerThan : Comparison.MoreThan,
                Group = group == null ? -1 : Array.IndexOf(GroupNames, group),
                Target = e.GetProperty("target").GetInt32(),
                Element = e.GetProperty("element").ValueKind == JsonValueKind.String ? e.GetProperty("element").GetString() : null,
                Reference = e.TryGetProperty("reference", out JsonElement r) ? r.GetInt32() : -1,
            });
        }

        private static RecordedTile FromEdgesOnly(JsonElement ct)
        {
            var t = new RecordedTile { Name = ct.GetProperty("name").GetString(), Quest = ct.GetProperty("quest").GetBoolean() };
            int i = 0;
            foreach (JsonElement edge in ct.GetProperty("edges").EnumerateArray())
            {
                t.Any[i] = edge.EnumerateArray().Select(x => Array.IndexOf(GroupNames, x.GetString())).Where(x => x >= 0).ToArray();
                t.NonHybrid[i] = t.Any[i];
                i++;
            }
            // One segment per run of consecutive edges sharing a type
            for (int type = 0; type < Board.TypeCount; type++)
            {
                bool[] has = Enumerable.Range(0, 6).Select(d => t.Any[d].Contains(type)).ToArray();
                if (!has.Any(x => x)) continue;
                if (has.All(x => x)) { t.Segs.Add((type, Enumerable.Range(0, 6).ToArray(), new Dictionary<string, int>())); continue; }
                int start = Array.FindIndex(has, x => !x);
                var run = new List<int>();
                for (int k = 1; k <= 6; k++)
                {
                    int d = (start + k) % 6;
                    if (has[d]) run.Add(d);
                    else if (run.Count > 0) { t.Segs.Add((type, run.ToArray(), new Dictionary<string, int>())); run = new List<int>(); }
                }
                if (run.Count > 0) t.Segs.Add((type, run.ToArray(), new Dictionary<string, int>()));
            }
            return t;
        }

        public TileSpec ToSpec(RecordedTile t)
        {
            var spec = new TileSpec { Name = t.Name };
            for (int i = 0; i < 6; i++)
                spec.Edges[i] = new EdgeSpec
                {
                    Any = t.Any[i].Select(x => (byte)x).ToArray(),
                    NonHybrid = t.NonHybrid[i].Select(x => (byte)x).ToArray(),
                    Hybrid = t.Hybrid[i],
                };
            spec.Segs = t.Segs.Select(s => new SegSpec
            {
                Type = (byte)s.type,
                Edges = s.edges,
                Elements = ElementNames.Select(n => s.elements.TryGetValue(n, out int c) ? c : 0).ToArray(),
            }).ToArray();
            return spec;
        }
    }
}
