
namespace Altruist;

public interface IPlayerService
{
    Task<PlayerEntity> GetPlayerAsync(string id);
}

public interface IFactory<TType>
{
    TType Get(SupportedBackplane backplane);
}

public interface IPlayerServiceFactory : IFactory<IPlayerService>
{
    IPlayerService<TPlayerEntity> Get<TPlayerEntity>(SupportedBackplane backplane) where TPlayerEntity : PlayerEntity;
}