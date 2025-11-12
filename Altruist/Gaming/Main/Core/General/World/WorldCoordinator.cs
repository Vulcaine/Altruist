using Altruist.Physx.Contracts;

namespace Altruist.Gaming
{
    public interface IGameWorldCoordinator
    {
        void Step(float deltaTime);
        void AddWorld(IWorldIndex index, IPhysxWorld physx2D);
    }
}
