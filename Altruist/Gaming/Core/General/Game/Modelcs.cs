namespace Altruist.Gaming;

public class World
{
    public int Width { get; set; }
    public int Height { get; set; }

    public World(int width, int height)
    {
        Width = width;
        Height = height;
    }
}

public class WorldPartition
{
    public int PartitionWidth { get; }
    public int PartitionHeight { get; }

    public WorldPartition(int partitionWidth, int partitionHeight)
    {
        PartitionWidth = partitionWidth;
        PartitionHeight = partitionHeight;
    }

    public List<Partition> CalculatePartitions(World world)
    {
        var partitions = new List<Partition>();

        int columns = (int)Math.Ceiling((double)world.Width / PartitionWidth);
        int rows = (int)Math.Ceiling((double)world.Height / PartitionHeight);

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                int x = col * PartitionWidth;
                int y = row * PartitionHeight;

                int width = Math.Min(PartitionWidth, world.Width - x);
                int height = Math.Min(PartitionHeight, world.Height - y);

                var partition = new Partition
                {
                    IndexX = col,
                    IndexY = row,
                    StartX = x,
                    StartY = y,
                    Width = width,
                    Height = height,
                    EpicenterX = x + width / 2,
                    EpicenterY = y + height / 2
                };

                partitions.Add(partition);
            }
        }

        return partitions;
    }
}

public class Partition
{
    public int IndexX { get; set; }
    public int IndexY { get; set; }
    
    public int StartX { get; set; }
    public int StartY { get; set; }
    
    public int Width { get; set; }
    public int Height { get; set; }

    public int EpicenterX { get; set; }
    public int EpicenterY { get; set; }
}
