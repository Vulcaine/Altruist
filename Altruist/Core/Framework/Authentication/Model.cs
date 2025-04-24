/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Text.Json.Serialization;
using Altruist.UORM;
using Microsoft.AspNetCore.Http;
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


public class UsernamePasswordAccountModel : AccountModel
{

    [VaultColumn("username")]
    public required string Username { get; set; }

    [VaultColumn("passwordHash")]
    public required string PasswordHash { get; set; }

    [VaultColumn("type")]
    public override string Type { get; set; } = "UsernamePasswordAccount";
}

public class EmailPasswordAccountModel : AccountModel
{
    [VaultColumn("email")]
    public required string Email { get; set; }

    [VaultColumn("passwordHash")]
    public required string PasswordHash { get; set; }

    [VaultColumn("type")]

    public override string Type { get; set; } = "EmailPasswordAccount";
}


public class HybridAccountModel : AccountModel
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
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; set; }
}

public class SignupRequest
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("password")]
    public string Password { get; set; }

    public SignupRequest(string password, string? username = null, string? email = null)
    {
        if (username == null && email == null)
        {
            throw new BadHttpRequestException("Username or email must be provided.");
        }

        Username = username;
        Password = password;
    }
}

public class UsernamePasswordLoginRequest : LoginRequest, ILoginToken
{
    [JsonPropertyName("username")]
    public string Username { get; set; }

    [JsonPropertyName("password")]
    public string Password { get; set; }

    public UsernamePasswordLoginRequest(string username, string password)
    {
        Username = username;
        Password = password;
    }
}

public class EmailPasswordLoginRequest : LoginRequest, ILoginToken
{
    [JsonPropertyName("email")]
    public string Email { get; set; }

    [JsonPropertyName("password")]
    public string Password { get; set; }

    public EmailPasswordLoginRequest(string email, string password)
    {
        Email = email;
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
