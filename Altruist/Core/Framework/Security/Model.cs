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


/// <summary>Marker for a credential payload a login checks (<see cref="UsernamePasswordLoginRequest"/>, <see cref="EmailPasswordLoginRequest"/>, or your own).</summary>
public interface ILoginToken
{

}

/// <summary>
/// Base persisted account. Derive one of the ready-made shapes (<see cref="UsernamePasswordAccountModel"/>,
/// <see cref="EmailPasswordAccountModel"/>, <see cref="HybridAccountModel"/>) or your own, and give it a
/// <c>[Vault]</c> attribute to store it. <see cref="StorageId"/> is a new GUID by default and is the principal id
/// put in tokens (<c>sub</c>).
/// </summary>
public abstract class AccountModel : VaultModel
{
    /// <summary>Account id (column <c>id</c>; a new GUID string by default).</summary>
    [VaultColumn("id")]
    public override string StorageId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>When the account was created (UTC).</summary>
    [VaultColumn("created-at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last save time (UTC).</summary>
    [VaultColumn("timestamp")]
    public override DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Discriminator naming the account shape.</summary>
    [VaultColumn("type")]
    public override string Type { get; set; } = "Account";
}


/// <summary>An account that signs in with a user name and password.</summary>
public class UsernamePasswordAccountModel : AccountModel
{

    /// <summary>Unique sign-in name.</summary>
    [VaultColumn("username")]
    public required string Username { get; set; }

    /// <summary>Password hash from <see cref="IPasswordHasher.Hash"/> (never the raw password).</summary>
    [VaultColumn("password-hash")]
    public required string PasswordHash { get; set; }

    /// <inheritdoc/>
    [VaultColumn("type")]
    public override string Type { get; set; } = "UsernamePasswordAccount";
}

/// <summary>An account that signs in with an email address and password.</summary>
public class EmailPasswordAccountModel : AccountModel
{
    /// <summary>Sign-in email address.</summary>
    [VaultColumn("email")]
    public required string Email { get; set; }

    /// <summary>Password hash from <see cref="IPasswordHasher.Hash"/> (never the raw password).</summary>
    [VaultColumn("password-hash")]
    public required string PasswordHash { get; set; }

    /// <inheritdoc/>
    [VaultColumn("type")]

    public override string Type { get; set; } = "EmailPasswordAccount";
}


/// <summary>An account with both a user name and an email address (either can be used to sign in, depending on your <see cref="Altruist.Security.Auth.ILoginService"/>).</summary>
public class HybridAccountModel : AccountModel
{
    /// <summary>Unique user name.</summary>
    [VaultColumn("username")]
    public required string Username { get; set; }

    /// <summary>Email address.</summary>
    [VaultColumn("email")]
    public required string Email { get; set; }

    /// <summary>Password hash from <see cref="IPasswordHasher.Hash"/> (never the raw password).</summary>
    [VaultColumn("password-hash")]
    public required string PasswordHash { get; set; }

    /// <inheritdoc/>
    [VaultColumn("type")]
    public override string Type { get; set; } = "HybridAccount";
}


/// <summary>Base of every login request body; carries the options shared by all credential kinds.</summary>
public class LoginRequest
{
    /// <summary>Optional client fingerprint (device id, browser hash); sessions are bound to it and a refresh must present the same one.</summary>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; set; }

    /// <summary>Client's "remember me" choice; not interpreted by Altruist itself.</summary>
    [JsonPropertyName("rememberMe")]
    public bool? RememberMe { get; set; }
}

/// <summary>Body of the signup endpoint, handed to <see cref="Altruist.Security.Auth.ILoginService.SignupAsync"/>.</summary>
public class SignupRequest
{
    /// <summary>Requested user name.</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>Account email address.</summary>
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    /// <summary>Raw password (hash it with <see cref="IPasswordHasher"/>; never store it).</summary>
    [JsonPropertyName("password")]
    public string Password { get; set; }

    /// <summary>Creates a signup request.</summary>
    /// <param name="password">Raw password.</param>
    /// <param name="username">User name.</param>
    /// <param name="email">Email address.</param>
    /// <exception cref="BadHttpRequestException">When neither <paramref name="username"/> nor <paramref name="email"/> is given. Endpoints may require both (<see cref="Altruist.Security.Http.JwtAuthController"/>'s <c>signup</c> does).</exception>
    public SignupRequest(string password, string? username = null, string? email = null)
    {
        if (username == null && email == null)
        {
            throw new BadHttpRequestException("Username or email must be provided.");
        }

        Username = username;
        Password = password;
        Email = email;
    }
}

/// <summary>Login body for <c>POST login/unamepwd</c>: user name and password.</summary>
public class UsernamePasswordLoginRequest : LoginRequest, ILoginToken
{
    /// <summary>Sign-in name.</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; }

    /// <summary>Raw password.</summary>
    [JsonPropertyName("password")]
    public string Password { get; set; }

    /// <summary>Creates the request.</summary>
    /// <param name="username">Sign-in name.</param>
    /// <param name="password">Raw password.</param>
    public UsernamePasswordLoginRequest(string username, string password)
    {
        Username = username;
        Password = password;
    }
}

/// <summary>Login body for <c>POST login/emailpwd</c>: email and password.</summary>
public class EmailPasswordLoginRequest : LoginRequest, ILoginToken
{
    /// <summary>Sign-in email address.</summary>
    [JsonPropertyName("email")]
    public string Email { get; set; }

    /// <summary>Raw password.</summary>
    [JsonPropertyName("password")]
    public string Password { get; set; }

    /// <summary>Creates the request.</summary>
    /// <param name="email">Sign-in email address.</param>
    /// <param name="password">Raw password.</param>
    public EmailPasswordLoginRequest(string email, string password)
    {
        Email = email;
        Password = password;
    }
}

/// <summary>Response body of the login and refresh endpoints of <see cref="Altruist.Security.Http.JwtAuthController"/>.</summary>
public class AltruistLoginResponse
{
    /// <summary>The access token, including its protocol suffix (e.g. <c>"&lt;jwt&gt;;jwt"</c>).</summary>
    public string AccessToken { get; set; } = "";
    /// <summary>The refresh token, including its protocol suffix.</summary>
    public string RefreshToken { get; set; } = "";
}

/// <summary>
/// MVC model binder that reads an <c>application/json</c> body and binds it to a
/// <see cref="UsernamePasswordLoginRequest"/> when it has <c>username</c> and <c>password</c>; anything else fails
/// binding. Apply with <c>[ModelBinder(typeof(LoginRequestBinder))]</c>.
/// </summary>
public class LoginRequestBinder : IModelBinder
{
    /// <inheritdoc/>
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
