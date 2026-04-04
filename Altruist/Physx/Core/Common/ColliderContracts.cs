namespace Altruist.Physx.Contracts
{

    public interface IPhysxCollider
    {
        string Id { get; }
        bool IsTrigger { get; set; }
        object? UserData { get; set; }
        event Action<IPhysxCollider, IPhysxCollider>? OnTriggerEnter;
        event Action<IPhysxCollider, IPhysxCollider>? OnTriggerExit;
    }



}
