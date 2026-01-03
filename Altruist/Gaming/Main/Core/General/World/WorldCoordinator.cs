namespace Altruist.Gaming
{
    public interface IGameWorldOrganizer
    {
        Task StepAsync(float deltaTime);
    }
}
