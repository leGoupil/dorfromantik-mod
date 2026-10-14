using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DorfHelper
{
    /// <summary>
    /// After F5, prevents mouse hover (TileSlotSelector) from moving the preview tile again
    /// until the mouse has actually moved. A click then places the tile on the suggestion.
    /// </summary>
    [HarmonyPatch(typeof(InputRouter), nameof(InputRouter.MovePreviewTile))]
    internal static class PreviewLock
    {
        private const float ReleaseDistance = 40f; // pixels

        private static bool locked;
        private static bool ownMove;
        private static Vector2 lockMousePos;

        public static bool IsLocked => locked;

        /// <summary>True while the Ctrl+Cmd shortcut is held: the mouse does not unlock.</summary>
        public static bool Held;

        /// <summary>Moves the preview ourselves (bypassing the lock), then locks.</summary>
        public static void MoveAndLock(InputRouter router, TileSlot slot)
        {
            ownMove = true;
            try { router.MovePreviewTile(slot); }
            finally { ownMove = false; }
            locked = true;
            lockMousePos = MousePosition();
        }

        public static void Release() => locked = false;

        /// <summary>Call every frame: releases the lock if the mouse has moved away.</summary>
        public static void Tick()
        {
            if (locked && !Held && (MousePosition() - lockMousePos).sqrMagnitude > ReleaseDistance * ReleaseDistance)
                locked = false;
        }

        private static Vector2 MousePosition() => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;

        // Harmony prefix: returning false cancels the original call.
        // Also ignore a move to a destroyed slot (reference still set but Unity object gone),
        // which would crash the game every frame; an explicit move to null is still allowed.
        private static bool Prefix(TileSlot targetTileSlot)
        {
            if (!ReferenceEquals(targetTileSlot, null) && !targetTileSlot) return false;
            return ownMove || !locked;
        }
    }
}
