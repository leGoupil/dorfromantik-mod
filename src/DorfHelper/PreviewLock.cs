using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DorfHelper
{
    /// <summary>
    /// Après F5, empêche le survol de la souris (TileSlotSelector) de redéplacer la tuile d'aperçu,
    /// tant que la souris n'a pas vraiment bougé. Un clic pose alors la tuile sur la suggestion.
    /// </summary>
    [HarmonyPatch(typeof(InputRouter), nameof(InputRouter.MovePreviewTile))]
    internal static class PreviewLock
    {
        private const float ReleaseDistance = 40f; // pixels

        private static bool locked;
        private static bool ownMove;
        private static Vector2 lockMousePos;

        public static bool IsLocked => locked;

        /// <summary>Vrai tant que le raccourci Ctrl+Cmd est maintenu : la souris ne déverrouille pas.</summary>
        public static bool Held;

        /// <summary>Déplace l'aperçu nous-mêmes (passe à travers le verrou) puis verrouille.</summary>
        public static void MoveAndLock(InputRouter router, TileSlot slot)
        {
            ownMove = true;
            try { router.MovePreviewTile(slot); }
            finally { ownMove = false; }
            locked = true;
            lockMousePos = MousePosition();
        }

        public static void Release() => locked = false;

        /// <summary>À appeler à chaque frame : libère le verrou si la souris s'est éloignée.</summary>
        public static void Tick()
        {
            if (locked && !Held && (MousePosition() - lockMousePos).sqrMagnitude > ReleaseDistance * ReleaseDistance)
                locked = false;
        }

        private static Vector2 MousePosition() => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;

        // Prefix Harmony : renvoyer false annule l'appel d'origine.
        // On ignore aussi un déplacement vers une case détruite (référence encore présente mais objet Unity disparu),
        // qui ferait planter le jeu à chaque frame ; un déplacement explicite vers null reste permis.
        private static bool Prefix(TileSlot targetTileSlot)
        {
            if (!ReferenceEquals(targetTileSlot, null) && !targetTileSlot) return false;
            return ownMove || !locked;
        }
    }
}
