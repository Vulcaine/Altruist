namespace Altruist.Gaming.TwoD
{
    /// <summary>Column / row of a partition in a 2D world's grid, with value equality; used as a
    /// dictionary key by <see cref="GameWorldManager2D"/>.</summary>
    public class PartitionIndex2D
    {
        /// <summary>Column (0-based, along +X).</summary>
        public int X { get; }
        /// <summary>Row (0-based, along +Y).</summary>
        public int Y { get; }

        /// <summary>Creates an index for column <paramref name="x"/>, row <paramref name="y"/>.</summary>
        public PartitionIndex2D(int x, int y)
        {
            X = x;
            Y = y;
        }

        /// <summary>True for another <see cref="PartitionIndex2D"/> with the same column and row.</summary>
        public override bool Equals(object? obj) =>
            obj is PartitionIndex2D other && X == other.X && Y == other.Y;

        /// <summary>Hash of (X, Y).</summary>
        public override int GetHashCode() => HashCode.Combine(X, Y);
    }
}
