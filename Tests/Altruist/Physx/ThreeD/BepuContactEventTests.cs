using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using FluentAssertions;

namespace Tests.Altruist.Physx.ThreeD;

public class BepuContactEventTests
{
    private sealed class Recorder
    {
        public List<PhysxCollisionInfo3D> Enter { get; } = new();
        public List<PhysxCollisionInfo3D> Stay { get; } = new();
        public List<PhysxCollisionInfo3D> Exit { get; } = new();
        public List<IPhysxCollider> TriggerEnter { get; } = new();
        public List<IPhysxCollider> TriggerExit { get; } = new();

        public Recorder(IPhysxCollider3D collider)
        {
            collider.OnCollisionEnter += Enter.Add;
            collider.OnCollisionStay += Stay.Add;
            collider.OnCollisionExit += Exit.Add;
            collider.OnTriggerEnter += (_, other) => TriggerEnter.Add(other);
            collider.OnTriggerExit += (_, other) => TriggerExit.Add(other);
        }
    }

    private sealed class Scene : IDisposable
    {
        public BepuTestWorld World { get; } = new(new Vector3(0f, -10f, 0f));
        public IPhysxBody3D Ground { get; }
        public IPhysxCollider3D GroundCollider { get; }
        public IPhysxBody3D Ball { get; }
        public IPhysxCollider3D BallCollider { get; }

        public Scene(bool groundIsTrigger = false)
        {
            Ground = World.Static(new Vector3(0f, -0.5f, 0f), new Vector3(10f, 0.5f, 10f));
            GroundCollider = World.Attach(Ground, PhysxCollider3D.CreateBox(new Vector3(10f, 0.5f, 10f), isTrigger: groundIsTrigger));
            Ball = World.Dynamic(new Vector3(0f, 2f, 0f));
            BallCollider = World.Attach(Ball, PhysxCollider3D.CreateSphere(0.5f));
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void touching_colliders_both_get_one_enter_then_stays()
    {
        using var s = new Scene();
        var ball = new Recorder(s.BallCollider);
        var ground = new Recorder(s.GroundCollider);

        s.World.StepSeconds(1.5f);

        ball.Enter.Should().ContainSingle();
        ground.Enter.Should().ContainSingle();
        ball.Stay.Should().NotBeEmpty();
        ball.Exit.Should().BeEmpty();

        var hit = ball.Enter[0];
        hit.SelfCollider.Should().BeSameAs(s.BallCollider);
        hit.OtherCollider.Should().BeSameAs(s.GroundCollider);
        hit.SelfBody.Should().BeSameAs(s.Ball);
        hit.OtherBody.Should().BeSameAs(s.Ground);
        hit.Normal.Y.Should().BeApproximately(-1f, 1e-3f);
        hit.Point.Y.Should().BeApproximately(0f, 0.05f);
        ground.Enter[0].Normal.Y.Should().BeApproximately(1f, 1e-3f);
        ground.Enter[0].SelfCollider.Should().BeSameAs(s.GroundCollider);
    }

    [Fact]
    public void separating_colliders_get_an_exit()
    {
        using var s = new Scene();
        var ball = new Recorder(s.BallCollider);
        s.World.StepSeconds(1.5f);

        s.Ball.Position = new Vector3(0f, 10f, 0f);
        s.Ball.LinearVelocity = Vector3.Zero;
        s.World.StepSeconds(2f / 60f);

        ball.Exit.Should().ContainSingle().Which.OtherCollider.Should().BeSameAs(s.GroundCollider);
    }

    [Fact]
    public void a_resting_pair_keeps_touching_while_asleep()
    {
        using var s = new Scene();
        var ball = new Recorder(s.BallCollider);

        s.World.StepSeconds(8f);

        ball.Enter.Should().ContainSingle();
        ball.Exit.Should().BeEmpty();
    }

    [Fact]
    public void removing_a_body_ends_its_contacts()
    {
        using var s = new Scene();
        var ground = new Recorder(s.GroundCollider);
        s.World.StepSeconds(1.5f);

        s.World.Engine.RemoveBody(s.Ball);
        s.World.StepSeconds(2f / 60f);

        ground.Exit.Should().ContainSingle().Which.OtherCollider.Should().BeSameAs(s.BallCollider);
    }

    [Fact]
    public void a_trigger_collider_raises_trigger_events_and_lets_bodies_through()
    {
        using var s = new Scene(groundIsTrigger: true);
        var ball = new Recorder(s.BallCollider);
        var ground = new Recorder(s.GroundCollider);

        s.World.StepSeconds(2f);

        s.Ball.Position.Y.Should().BeLessThan(-3f);
        ground.TriggerEnter.Should().ContainSingle().Which.Should().BeSameAs(s.BallCollider);
        ground.TriggerExit.Should().ContainSingle().Which.Should().BeSameAs(s.BallCollider);
        ball.TriggerEnter.Should().ContainSingle().Which.Should().BeSameAs(s.GroundCollider);
        ball.Enter.Should().BeEmpty();
    }
}
