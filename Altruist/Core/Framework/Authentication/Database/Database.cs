
using Altruist;
using Altruist.Security;

public interface ILoginVault : IVault<AccountModel>
{
    bool Login<TLoginToken>(TLoginToken token) where TLoginToken : ILoginToken;
}
