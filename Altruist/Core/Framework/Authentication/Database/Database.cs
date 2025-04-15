
using Altruist;
using Altruist.Auth;

public interface ILoginVault : IVault<AccountVault>
{
    bool Login<TLoginToken>(TLoginToken token) where TLoginToken : ILoginToken;
}
