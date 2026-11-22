namespace Altruist.Gaming.ThreeD
{
    /// <summary>Integer (column, row, slice) key of a partition in the world's partition grid; value equality.</summary>
    public class PartitionIndex3D
    {
        /// <summary>Column (along X).</summary>
        public int X { get; }
        /// <summary>Row (along Y).</summary>
        public int Y { get; }
        /// <summary>Slice (along Z).</summary>
        public int Z { get; }

        /// <summary>Creates a partition key.</summary>
        /// <param name="x">Column.</param>
        /// <param name="y">Row.</param>
        /// <param name="z">Slice.</param>
        public PartitionIndex3D(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) =>
            obj is PartitionIndex3D other && X == other.X && Y == other.Y && Z == other.Z;

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    }
}
