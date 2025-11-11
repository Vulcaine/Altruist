namespace Altruist.SimpleGame
{
    /// <summary>
    /// Minimal 3D input DTO for server. Matches MovementIntent3D.FromButtons signature.
    /// </summary>
    public sealed class MoveRequest3D
    {
        // Movement (XZ for ground; set FlyUp/FlyDown for flight/jetpack)
        public bool? Forward { get; set; }
        public bool? Back { get; set; }
        public bool? Left { get; set; }
        public bool? Right { get; set; }
        public bool? FlyUp { get; set; }
        public bool? FlyDown { get; set; }

        // Turning (signed scalar in [-1..+1], Right = +1)
        public float? TurnYaw { get; set; }

        // Actions
        public bool? Jump { get; set; }
        public bool? Boost { get; set; }
        public bool? Dash { get; set; }
    }
}
