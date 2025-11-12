
// MovementManager2D.cs (Movement module)

using System.Numerics;

using Altruist.Physx.TwoD;

namespace Altruist.Gaming.Movement.TwoD
{
    public interface IMovementManager2D : IMovementManager
    {
        // Registration (bodies are already created/owned elsewhere; we just keep drivers)
        bool AddPlayer(string playerId, IPhysxBody2D body, MovementProfile2D profile, MovementState2D initialState, IMovementPipeline2D pipeline);
        bool RemovePlayer(string playerId);

        // Input
        void SetPlayerIntent(string playerId, in MovementIntent2D intent);
        void ClearPlayerIntent(string playerId);

        // Tuning / behavior
        bool SetPlayerProfile(string playerId, MovementProfile2D profile);
        bool SetPlayerPipeline(string playerId, IMovementPipeline2D pipeline);

        // Query
        bool TryGetPlayerState(string playerId, out MovementState2D state);
    }

    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "2D")]
    [Service(typeof(IMovementManager2D))]
    public sealed class MovementManager2D : IMovementManager2D
    {
        private readonly IPhysxMovementEngine2D _physxMovement;

        private sealed class Entry
        {
            public IPhysxBody2D Body = default!;
            public MovementDriver2D Driver = default!;
            // default intent now includes Jump=false explicitly (for clarity)
            public MovementIntent2D Intent = new(
                Move: Vector2.Zero,
                AimAngleRad: 0f,
                Jump: false,
                Boost: false,
                Dash: false,
                Knockback: Vector2.Zero
            );
        }

        private readonly Dictionary<string, Entry> _players = new(StringComparer.Ordinal);

        public MovementManager2D(IPhysxMovementEngine2D physxMovement, IMovementPipeline2D defaultPipeline)
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

        public bool AddPlayer(string playerId, IPhysxBody2D body, MovementProfile2D profile, MovementState2D initialState, IMovementPipeline2D pipeline)
        {
            if (string.IsNullOrWhiteSpace(playerId) || _players.ContainsKey(playerId))
                return false;

            var driver = new MovementDriver2D(body, profile, initialState, pipeline, _physxMovement);

            _players[playerId] = new Entry
            {
                Body = body,
                Driver = driver,
                Intent = new MovementIntent2D(
                    Move: Vector2.Zero,
                    AimAngleRad: initialState.AngleRad,
                    Jump: false,
                    Boost: false,
                    Dash: false,
                    Knockback: Vector2.Zero)
            };
            return true;
        }

        public bool RemovePlayer(string playerId) => _players.Remove(playerId);

        public void SetPlayerIntent(string playerId, in MovementIntent2D intent)
        {
            if (_players.TryGetValue(playerId, out var e))
                e.Intent = intent;
        }

        public void ClearPlayerIntent(string playerId)
        {
            if (_players.TryGetValue(playerId, out var e))
            {
                e.Intent = new MovementIntent2D(
                    Move: Vector2.Zero,
                    AimAngleRad: e.Driver.State.AngleRad,
                    Jump: false,
                    Boost: false,
                    Dash: false,
                    Knockback: Vector2.Zero
                );
            }
        }

        public bool SetPlayerProfile(string playerId, MovementProfile2D profile)
        {
            if (!_players.TryGetValue(playerId, out var e))
                return false;
            e.Driver.Profile = profile;
            return true;
        }

        public bool SetPlayerPipeline(string playerId, IMovementPipeline2D pipeline)
        {
            if (!_players.TryGetValue(playerId, out var e))
                return false;
            e.Driver.Pipeline = pipeline;
            return true;
        }

        public bool TryGetPlayerState(string playerId, out MovementState2D state)
        {
            if (_players.TryGetValue(playerId, out var e))
            {
                state = e.Driver.State;
                return true;
            }
            state = default;
            return false;
        }
    }
}
