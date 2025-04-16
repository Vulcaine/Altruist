
using Altruist.UORM;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Newtonsoft.Json;

namespace Altruist.Security;


public interface ILoginToken
{

}

public abstract class AccountModel : VaultModel
{
    [VaultColumn("id")]
    public override string GenId { get; set; } = Guid.NewGuid().ToString();

    [VaultColumn("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [VaultColumn("timestamp")]
    public override DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [VaultColumn("type")]
    public override string Type { get; set; } = "Account";
}


public class UsernamePasswordAccountVault : AccountModel
{

    [VaultColumn("username")]
    public required string Username { get; set; }

    [VaultColumn("passwordHash")]
    public required string PasswordHash { get; set; }

    [VaultColumn("type")]
    public override string Type { get; set; } = "UsernamePasswordAccount";
}

public class EmailPasswordAccountVault : AccountModel
{
    [VaultColumn("email")]
    public required string Email { get; set; }

    [VaultColumn("passwordHash")]
    public required string PasswordHash { get; set; }

    [VaultColumn("type")]

    public override string Type { get; set; } = "EmailPasswordAccount";
}


public class HybridAccountVault : AccountModel
{
    [VaultColumn("username")]
    public required string Username { get; set; }

    [VaultColumn("email")]
    public required string Email { get; set; }

    [VaultColumn("passwordHash")]
    public required string PasswordHash { get; set; }

    [VaultColumn("type")]
    public override string Type { get; set; } = "HybridAccount";
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

public class AltruistLoginResponse
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
}

public class LoginRequestBinder : IModelBinder
{
    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var requestType = bindingContext.HttpContext.Request.ContentType;

        if (requestType != "application/json")
            return;

        // Read the request body asynchronously
        var body = bindingContext.HttpContext.Request.Body;
        var json = await new StreamReader(body).ReadToEndAsync();

        // Deserialize the JSON into a dynamic object
        dynamic request = JsonConvert.DeserializeObject(json);

        if (request.username != null && request.password != null)
        {
            var usernamePasswordRequest = JsonConvert.DeserializeObject<UsernamePasswordLoginRequest>(json);
            bindingContext.Result = ModelBindingResult.Success(usernamePasswordRequest);
        }
        else
        {
            bindingContext.Result = ModelBindingResult.Failed();
        }
    }
}
