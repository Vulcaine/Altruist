
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Joints;
using Microsoft.Xna.Framework;

namespace Altruist.Physx;

public class PhysxWorld
{
    public World World { get; }

    public PhysxWorld(Vector2 gravity) => World = new World(gravity);

    // Add a body to the world
    public void AddBreakableBody(BreakableBody body)
    {
        World.AddBreakableBody(body);
    }

    // Remove a body from the world
    public void RemoveBody(Body body)
    {
        World.RemoveBody(body);
    }

    public void RayCast(Func<Fixture, Vector2, Vector2, float, float> callback, Vector2 point1, Vector2 point2)
    {
        World.RayCast(callback, point1, point2);
    }

    public void RemoveJoint(Joint joint)
    {
        World.RemoveJoint(joint);
    }

    public void AddJoint(Joint joint)
    {
        World.AddJoint(joint);
    }

    // Step the physics world with a fixed deltaTime
    public void Step(float deltaTime)
    {
        // Update the physics simulation world step with deltaTime
        World.Step(deltaTime);
    }

    // Apply a force to a body (for example, deceleration or other forces)
    public void ApplyForce(Body body, Vector2 force)
    {
        body.ApplyForce(ref force);
    }

    // Retrieve all the bodies in the world (for querying or other purposes)
    public List<Body> GetAllBodies()
    {
        var bodies = new List<Body>();
        foreach (Body body in World.BodyList)
        {
            bodies.Add(body);
        }
        return bodies;
    }

    // Get the position of a body
    public Vector2 GetPosition(Body body)
    {
        return body.Position;
    }

    // Set the position of a body directly
    public void SetPosition(Body body, Vector2 position)
    {
        body.Position = position;
    }

    // Get the velocity of a body
    public Vector2 GetVelocity(Body body)
    {
        return body.LinearVelocity;
    }

    // Set the velocity of a body directly
    public void SetVelocity(Body body, Vector2 velocity)
    {
        body.LinearVelocity = velocity;
    }

    // Apply impulse to a body
    public void ApplyImpulse(Body body, Vector2 impulse)
    {
        body.ApplyLinearImpulse(ref impulse);
    }

    // Apply torque to a body
    public void ApplyTorque(Body body, float torque)
    {
        body.ApplyTorque(torque);
    }

    // Add a force at a specific point in the world
    public void ApplyForceAtPoint(Body body, Vector2 force, Vector2 point)
    {
        body.ApplyForce(force, point);
    }
}