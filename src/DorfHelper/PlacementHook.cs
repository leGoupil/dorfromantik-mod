using System;
using HarmonyLib;

namespace DorfHelper
{
    /// <summary>Notifies the plugin right BEFORE the game places the tile (preview still on its final slot).</summary>
    [HarmonyPatch(typeof(TilePlacer), nameof(TilePlacer.PlaceCurrentTile))]
    internal static class PlacementHook
    {
        public static Action<TileSlot> BeforePlace;

        private static void Prefix(TileSlot targetSlot) => BeforePlace?.Invoke(targetSlot);
    }
}
