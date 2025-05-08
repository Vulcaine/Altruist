
using Box2DSharp.Dynamics;
using Box2DSharp.Dynamics.Joints;
using Box2DSharp.Common;
using System.Numerics;

namespace Altruist.Physx;

public class PhysxWorld
{
    public World World { get; }

    public PhysxWorld(Vector2 gravity) => World = new World(gravity);

    // Remove a body from the world
    public void RemoveBody(Body body)
    {
        World.DestroyBody(body);
    }

    // RayCast using Box2D's callback format
    public void RayCast(in IRayCastCallback callback, in Vector2 point1, in Vector2 point2)
    {
        World.RayCast(callback, point1, point2);
    }

    public void RemoveJoint(Joint joint)
    {
        World.DestroyJoint(joint);
    }

    public void AddJoint(Joint joint)
    {
        // No explicit "AddJoint" method in Box2DSharp — joints are added when created.
        // You can leave this empty or use a factory method elsewhere.
    }

    // Step the physics world with a fixed deltaTime
    public void Step(float deltaTime)
    {
        if (World.BodyCount == 0) return;
        World.Step(deltaTime, velocityIterations: 8, positionIterations: 3);
    }

    // Apply a force to a body (e.g. for deceleration or external effects)
    public void ApplyForce(Body body, Vector2 force, bool wake)
    {
        body.ApplyForce(force, body.GetWorldCenter(), wake);
    }

    public List<Body> GetAllBodies()
    {
        var bodies = new List<Body>();

        foreach (var body in World.BodyList)
        {
            bodies.Add(body);
        }

        return bodies;
    }


    // Get and set body position
    public Vector2 GetPosition(Body body) => body.GetPosition();

    public void SetPosition(Body body, Vector2 position)
    {
        var angle = body.GetAngle();
        body.SetTransform(position, angle);
    }

    // Get and set body velocity
    public Vector2 GetVelocity(Body body) => body.LinearVelocity;

    public void SetVelocity(Body body, Vector2 velocity)
    {
        body.SetLinearVelocity(velocity);
    }

    // Apply impulse to a body
    public void ApplyImpulse(Body body, Vector2 impulse, bool wake)
    {
        body.ApplyLinearImpulse(impulse, body.GetWorldCenter(), wake);
    }

    // Apply torque to a body
    public void ApplyTorque(Body body, float torque, bool wake)
    {
        body.ApplyTorque(torque, wake);
    }

    // Apply force at a specific world point
    public void ApplyForceAtPoint(Body body, Vector2 force, Vector2 point, bool wake)
    {
        body.ApplyForce(force, point, wake);
    }
}