namespace Altruist.Authentication;

using Microsoft.AspNetCore.Authorization;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class ShieldAttribute : AuthorizeAttribute
{
    public ShieldAttribute(string policy) : base(policy)
    {
    }
}


