
using Altruist;
using Altruist.Auth;


public interface ILoginVault : IVault<Account>
{
    bool Login<TLoginToken>(TLoginToken token) where TLoginToken : ILoginToken;
}
