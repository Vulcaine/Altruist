
using Altruist;
using Altruist.Security;

public interface ILoginVault : IVault<AccountVault>
{
    bool Login<TLoginToken>(TLoginToken token) where TLoginToken : ILoginToken;
}
