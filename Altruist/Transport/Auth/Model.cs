using Cassandra.Mapping;
using Cassandra.Mapping.Attributes;

namespace Altruist.Auth;


public interface ILoginToken
{

}

[Table("Account")]
[PrimaryKey("Id")]
public abstract class Account : IVaultModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}


public class UsernamePasswordAccount : Account
{
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
}

public class EmailPasswordAccount : Account
{
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
}


public class HybridAccount : Account
{
    public required string Username { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
}


public class LoginRequest
{
}

public class UsernamePasswordLoginRequest : LoginRequest, ILoginToken
{
    public string Username { get; set; }
    public string Password { get; set; }

    public UsernamePasswordLoginRequest(string username, string password)
    {
        Username = username;
        Password = password;
    }
}