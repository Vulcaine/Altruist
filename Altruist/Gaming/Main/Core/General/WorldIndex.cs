namespace Altruist.Gaming
{
    public interface IWorldIndex
    {
        float FixedDeltaTime { get; set; }
        public int Index { get; set; }
        public string Name { get; set; }
        public string? DataPath { get; set; }
    }
}
