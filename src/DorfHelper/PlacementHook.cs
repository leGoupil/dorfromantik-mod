using System;
using HarmonyLib;

namespace DorfHelper
{
    /// <summary>Prévient le plugin juste AVANT que le jeu pose la tuile (aperçu encore sur sa case finale).</summary>
    [HarmonyPatch(typeof(TilePlacer), nameof(TilePlacer.PlaceCurrentTile))]
    internal static class PlacementHook
    {
        public static Action<TileSlot> BeforePlace;

        private static void Prefix(TileSlot targetSlot) => BeforePlace?.Invoke(targetSlot);
    }
}
