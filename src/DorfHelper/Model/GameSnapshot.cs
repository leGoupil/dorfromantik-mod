using System.Collections.Generic;
using UnityEngine;

namespace DorfHelper.Model
{
    /// <summary>Photo de la partie, traduite en modèle pur (partagée avec le simulateur).</summary>
    internal sealed partial class GameSnapshot
    {
        public Board Board;
        public TileSpec Hand;
        public List<TileSpec> Stack = new List<TileSpec>(); // tuiles suivantes, dans l'ordre
        public List<Vector2Int> ValidSlots = new List<Vector2Int>();
        public int PlacedCount; // nombre de tuiles posées au moment de la photo
        public List<TileSpec> Pool = new List<TileSpec>(); // tuiles déjà vues, pour tirer des tuiles "probables"
    }
}
