// MovementManager3D.cs
using System.Numerics;

namespace Altruist.Gaming.Movement.ThreeD
{
    public interface IMovementManager3D : IMovementManager
    {
        bool AddPlayer(string playerId, IPhysxBody3D body, MovementProfile3D profile, MovementState3D initialState, IMovementPipeline3D pipeline);
        bool RemovePlayer(string playerId);

        void SetPlayerIntent(string playerId, in MovementIntent3D intent);
        void ClearPlayerIntent(string playerId);

        bool SetPlayerProfile(string playerId, MovementProfile3D profile);
        bool SetPlayerPipeline(string playerId, IMovementPipeline3D pipeline);

        bool TryGetPlayerState(string playerId, out MovementState3D state);
    }

    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "3D")]
    [Service(typeof(IMovementManager3D))]
    public sealed class MovementManager3D : IMovementManager3D
    {
        private readonly IPhysxMovementEngine3D _physxMovement;

        private sealed class Entry
        {
            public IPhysxBody3D Body = default!;
            public MovementDriver3D Driver = default!;
            public MovementIntent3D Intent = new MovementIntent3D(
                Move: Vector3.Zero,
                TurnYaw: 0f,
                Jump: false,
                Boost: false,
                Dash: false,
                AimDirection: Vector3.UnitZ,
                Knockback: Vector3.Zero
            );
        }

        private readonly Dictionary<string, Entry> _players = new(StringComparer.Ordinal);

        public MovementManager3D(IPhysxMovementEngine3D physxMovement, IMovementPipeline3D defaultPipeline)
        {
            _physxMovement = physxMovement;
        }

        public void Step(float dt)
        {
            foreach (var kv in _players)
            {
                var e = kv.Value;
                e.Driver.Step(e.Intent, dt);
            }
        }

        public bool AddPlayer(string playerId, IPhysxBody3D body, MovementProfile3D profile, MovementState3D initialState, IMovementPipeline3D pipeline)
        {
            if (string.IsNullOrWhiteSpace(playerId) || _players.ContainsKey(playerId)) return false;

            var driver = new MovementDriver3D(body, profile, initialState, pipeline, _physxMovement);

            _players[playerId] = new Entry
            {
                Body = body,
                Driver = driver,
                Intent = new MovementIntent3D(
                    Move: Vector3.Zero,
                    TurnYaw: 0f,
                    Jump: false,
                    AimDirection: ForwardFrom(initialState.Orientation)
                )
            };
            return true;
        }

        public bool RemovePlayer(string playerId) => _players.Remove(playerId);

        public void SetPlayerIntent(string playerId, in MovementIntent3D intent)
        {
            if (_players.TryGetValue(playerId, out var e))
                e.Intent = intent;
        }

        public void ClearPlayerIntent(string playerId)
        {
            if (_players.TryGetValue(playerId, out var e))
            {
                e.Intent = new MovementIntent3D(
                    Move: Vector3.Zero,
                    TurnYaw: 0f,
                    Jump: false,
                    Boost: false,
                    Dash: false,
                    AimDirection: ForwardFrom(e.Driver.State.Orientation),
                    Knockback: Vector3.Zero
                );
            }
        }

        public bool SetPlayerProfile(string playerId, MovementProfile3D profile)
        {
            if (!_players.TryGetValue(playerId, out var e)) return false;
            e.Driver.Profile = profile;
            return true;
        }

        public bool SetPlayerPipeline(string playerId, IMovementPipeline3D pipeline)
        {
            if (!_players.TryGetValue(playerId, out var e)) return false;
            e.Driver.Pipeline = pipeline;
            return true;
        }

        public bool TryGetPlayerState(string playerId, out MovementState3D state)
        {
            if (_players.TryGetValue(playerId, out var e))
            {
                state = e.Driver.State;
                return true;
            }
            state = default;
            return false;
        }

        private static Vector3 ForwardFrom(Quaternion q)
        {
            // Forward is +Z in most right-handed conventions; UnitX uses X-forward
            return Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, q));
        }
    }
}
