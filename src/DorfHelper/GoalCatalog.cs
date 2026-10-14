using System;
using System.Collections.Generic;
using System.Linq;
using DorfHelper.Model;
using HarmonyLib;
using UnityEngine;

namespace DorfHelper
{
    /// <summary>
    /// The game's not-yet-completed "big group" challenges (session quests that unlock biomes and achievements),
    /// offered as biome goals: close (CloseBigGroup) or form (FormBigGroup) a group of N elements/segments.
    /// </summary>
    internal static class GoalCatalog
    {
        public static List<BiomeGoal> FromGame()
        {
            var goals = new List<BiomeGoal>();
            SessionQuestWatcher watcher = UnityEngine.Object.FindObjectOfType<SessionQuestWatcher>();
            List<SessionQuest> quests = watcher && watcher.SessionQuestManager ? watcher.SessionQuestManager.sessionQuests : null;
            if (quests == null) return goals;

            foreach (SessionQuest q in quests.Where(q => q))
            {
                // "At once" challenges (D: perfect placements, E: points in a single placement)
                if (q is Dorfromantik.Challenge_PerfectPlacementsAtOnce || q is Dorfromantik.Challenge_ScorePerPlacement)
                {
                    if (q.CurrentState == RewardState.Completed) continue;
                    // One level per entry: aiming for a higher level also completes the lower ones at once
                    // (the game keeps validating levels as long as the reached value exceeds the next one).
                    for (int level = q.CurrentLevelIndex; level < q.LevelCount; level++)
                    {
                        string atOnceTitle = null;
                        try { atOnceTitle = q.GetTitle(level); } catch (Exception) { }
                        int atOnceTarget;
                        try { atOnceTarget = q.TargetCount(level); } catch (Exception) { continue; }
                        goals.Add(new BiomeGoal
                        {
                            AtOnceKind = q is Dorfromantik.Challenge_PerfectPlacementsAtOnce ? 1 : 2,
                            Target = atOnceTarget,
                            Source = q,
                            Level = level,
                            Title = string.IsNullOrWhiteSpace(atOnceTitle) ? $"{q.name} {level + 1}" : StripTags(atOnceTitle),
                        });
                    }
                    continue;
                }

                bool close, many = false;
                if (q is SessionQuest_CloseBigGroup || q is SessionQuest_CloseBigGroupExcludingSegmentTypes) close = true;
                else if (q is SessionQuest_FormBigGroup) close = false;
                else if (q is SessionQuest_CloseManyGroups || q is SessionQuest_FormManyGroups) { close = true; many = true; }
                else continue;
                if (q.CurrentState == RewardState.Completed) continue;

                // Fields shared by the three classes (countTarget, groupType, elementType)
                var t = Traverse.Create(q);
                GroupType groupType = t.Field("groupType").GetValue<GroupType>();
                CountTarget countTarget = t.Field("countTarget").GetValue<CountTarget>();
                ElementType elementType = t.Field("elementType").GetValue<ElementType>();
                if (!groupType) continue;

                string title = null;
                try { title = q.GetTitle(); } catch (Exception) { }
                int target;
                try { target = q.TargetCount(); } catch (Exception) { continue; }

                int groupSize = 0;
                if (many)
                {
                    List<int> sizes = t.Field("targetGroupSizePerLevel").GetValue<List<int>>();
                    if (sizes == null || sizes.Count == 0) continue;
                    groupSize = sizes[Mathf.Clamp(q.CurrentLevelIndex, 0, sizes.Count - 1)];
                }

                goals.Add(new BiomeGoal
                {
                    Many = many,
                    GroupSize = groupSize,
                    Source = q,
                    GroupType = groupType.id,
                    Element = countTarget == CountTarget.Segments || !elementType ? null : elementType.name,
                    Target = target,
                    Close = close,
                    Title = string.IsNullOrWhiteSpace(title) ? q.name : StripTags(title),
                });
            }
            return goals;
        }

        private static string StripTags(string s) => System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "").Trim();
    }
}
