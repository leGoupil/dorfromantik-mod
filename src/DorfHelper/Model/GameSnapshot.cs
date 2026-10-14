using System.Collections.Generic;
using UnityEngine;

namespace DorfHelper.Model
{
    /// <summary>Snapshot of the game, translated into the pure model (shared with the simulator).</summary>
    internal sealed partial class GameSnapshot
    {
        public Board Board;
        public TileSpec Hand;
        public List<TileSpec> Stack = new List<TileSpec>(); // upcoming tiles, in order
        public List<Vector2Int> ValidSlots = new List<Vector2Int>();
        public int PlacedCount; // number of tiles placed when the snapshot was taken
        public List<TileSpec> Pool = new List<TileSpec>(); // tiles seen so far, used to draw "likely" tiles
    }
}
